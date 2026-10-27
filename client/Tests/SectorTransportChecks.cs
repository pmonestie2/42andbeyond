using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Threading;
using FortyTwoAndBeyond.Protocol;

/// <summary>Exercise loss repair and burst receive without a game server or user client.</summary>
internal static class SectorTransportChecks
{
    /// <summary>Run transport checks, optionally including a privately owned loopback socket pair.</summary>
    public static void Check(bool exerciseLoopback)
    {
        CheckBoundedReplay();
        CheckManufactureBurst();
        if (exerciseLoopback) CheckSocketBurst();
    }

    /// <summary>Repair a long compatibility gap through bounded requests rather than one invalid request.</summary>
    private static void CheckBoundedReplay()
    {
        const uint player = 42;
        var stream = new SectorStream(player);
        byte[][] packets = Enumerable.Range(0, 201).Select(index =>
            Wire.Encode(0x2016, Wire.Encode(0x25, BitConverter.GetBytes(index)), player, (uint)index)).ToArray();
        var frames = new List<Frame>();
        frames.AddRange(stream.Feed(packets[200]));
        Require(stream.MissingCount == 128, "A compatibility gap requested more than the replay cache.");
        int requests = 0;
        while (stream.MissingCount > 0)
        {
            uint expected = stream.Expected, count = stream.MissingCount;
            Require(count <= 128, "A replay range exceeded the server's 128 datagrams.");
            for (uint index = expected; index < expected + count; index++) frames.AddRange(stream.Feed(packets[index]));
            requests++;
        }
        Require(requests == 2 && stream.Expected == 201 && frames.Count == 201,
            "Chunked recovery failed to drain an old replay gap.");
        Require(frames.Select(frame => BitConverter.ToInt32(frame.Body, 0)).SequenceEqual(Enumerable.Range(0, 201)),
            "Chunked recovery reordered a decoded frame.");
        bool rejected = false;
        try { stream.Feed(Wire.Encode(0x2016, Array.Empty<byte>(), player, stream.Expected + 256)); }
        catch (InvalidDataException) { rejected = true; }
        Require(rejected, "The existing unrecoverable sequence bound was raised.");
    }

    /// <summary>Recover a manufacture-sized stream across paced windows with losses, duplicates, and split headers.</summary>
    private static void CheckManufactureBurst()
    {
        const uint player = 43;
        var expected = new List<Frame>();
        var encoded = new List<byte>();
        for (int index = 0; index < 720; index++)
        {
            int length = index == 350 ? 32763 : 800 + index % 23;
            byte[] body = Enumerable.Range(0, length).Select(offset => (byte)(index + offset)).ToArray();
            var frame = new Frame(0x25, body);
            expected.Add(frame); encoded.AddRange(Wire.Encode(frame.Opcode, body));
        }
        var packets = new List<byte[]>();
        for (int offset = 0; offset < encoded.Count;)
        {
            // The first inner header is deliberately split after its first two bytes.
            int count = Math.Min(offset == 0 ? 2 : 997, encoded.Count - offset);
            packets.Add(Wire.Encode(packets.Count % 2 == 0 ? (ushort)0x2016 : (ushort)0x201a,
                encoded.GetRange(offset, count).ToArray(), player, (uint)packets.Count));
            offset += count;
        }
        Require(packets.Count > 512, "Manufacture regression did not span multiple reorder windows.");
        var stream = new SectorStream(player);
        var actual = new List<Frame>();
        for (int start = 0; start < packets.Count; start += 64)
        {
            int end = Math.Min(start + 64, packets.Count);
            var lost = new HashSet<int>(new[] { start + 2, start + 7, start + 37 });
            for (int index = end - 1; index >= start; index--)
            {
                if (lost.Contains(index)) continue;
                actual.AddRange(stream.Feed(packets[index]));
                if (index % 5 == 0) actual.AddRange(stream.Feed(packets[index]));
            }
            while (stream.MissingCount > 0)
            {
                uint first = stream.Expected, count = stream.MissingCount;
                Require(count <= 128, "A manufacture repair requested an oversized replay range.");
                for (uint index = first; index < first + count; index++) actual.AddRange(stream.Feed(packets[(int)index]));
            }
            // A lost tail has no later sequence to expose it; this is the server's proactive replay.
            while (stream.Expected < end) actual.AddRange(stream.Feed(packets[(int)stream.Expected]));
            Require(stream.Expected == end, "A paced transport window failed to recover.");
        }
        Require(actual.Count == expected.Count && stream.Expected == packets.Count,
            "Manufacture burst lost or duplicated an inner frame.");
        for (int index = 0; index < expected.Count; index++)
            Require(actual[index].Opcode == expected[index].Opcode && actual[index].Body.SequenceEqual(expected[index].Body),
                "Manufacture payload changed during loss/reorder recovery.");
    }

    /// <summary>Verify a queued UDP burst is drained and a native acknowledgement body is echoed unchanged.</summary>
    private static void CheckSocketBurst()
    {
        const uint player = 44;
        BindingFlags privateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
        using (var sender = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)))
        using (var session = new LocalSession(CancellationToken.None))
        {
            sender.Client.ReceiveTimeout = 1000;
            typeof(LocalSession).GetField("address", privateInstance).SetValue(session, IPAddress.Loopback);
            var receiver = (UdpClient)typeof(LocalSession).GetMethod("NewUdp", privateInstance)
                .Invoke(session, new object[] { ((IPEndPoint)sender.Client.LocalEndPoint).Port });
            typeof(LocalSession).GetField("sector", privateInstance).SetValue(session, receiver);
            typeof(LocalSession).GetProperty("Player").SetValue(session, player);
            var stream = new SectorStream(player);
            typeof(LocalSession).GetField("stream", privateInstance).SetValue(session, stream);
            Require(receiver.Client.ReceiveBufferSize >= 1024 * 1024, "The OS socket receive queue is smaller than one MiB.");
            var endpoint = new IPEndPoint(IPAddress.Loopback, ((IPEndPoint)receiver.Client.LocalEndPoint).Port);
            byte[] ignored = Wire.Encode(0x7ffe, new byte[1000]);
            const string sentinel = "transport retry sentinel";
            byte[] lastPacket = null;
            for (uint sequence = 0; sequence < 96; sequence++)
            {
                byte[] payload = sequence == 95 ? Wire.Encode(0x1d,
                    new byte[3].Concat(System.Text.Encoding.ASCII.GetBytes(sentinel)).ToArray()) : ignored;
                byte[] packet = Wire.Encode(0x2016, payload, player, sequence);
                sender.Send(packet, packet.Length, endpoint);
                lastPacket = packet;
            }
            Stopwatch deadline = Stopwatch.StartNew();
            while (stream.Expected < 96 && deadline.Elapsed.TotalSeconds < 3) session.Poll();
            Require(stream.Expected == 96, "A queued UDP burst lost datagrams before the worker could drain it.");
            IPEndPoint from = null;
            uint acknowledged = 0;
            do
            {
                byte[] prefix = sender.Receive(ref from);
                Require(Wire.Datagram(prefix, player) == 0x2021, "Unexpected loopback transport response.");
                acknowledged = Math.Max(acknowledged, Wire.U32(prefix, 12));
            } while (sender.Available > 0);
            Require(acknowledged == 96, "No cumulative acknowledgement covered the received burst.");
            // Discarded ACKs simulate their loss. A byte-identical replay must provoke
            // another cumulative ACK even though SectorStream delivers no new frame.
            sender.Send(lastPacket, lastPacket.Length, endpoint); session.Poll();
            byte[] repeatedAck = sender.Receive(ref from);
            Require(Wire.Datagram(repeatedAck, player) == 0x2021 && Wire.U32(repeatedAck, 12) == 96 &&
                stream.Expected == 96 && session.World.Messages.Count(message => message == sentinel) == 1,
                "A duplicate failed to repair a lost ACK or applied its message twice.");
            byte[] acknowledgement = BitConverter.GetBytes(97u);
            byte[] ping = Wire.Encode(0x2016, Wire.Encode(0x2020, acknowledgement), player, 96);
            sender.Send(ping, ping.Length, endpoint); session.Poll();
            byte[] echo = sender.Receive(ref from);
            Require(Wire.Datagram(echo, player) == 0x2021 && Wire.Slice(echo, 12, echo.Length - 12).SequenceEqual(acknowledgement),
                "The native batch acknowledgement body was changed.");
        }
    }

    /// <summary>Fail one transport assertion with a concrete failure reason.</summary>
    private static void Require(bool valid, string message)
    { if (!valid) throw new InvalidOperationException(message); }
}
