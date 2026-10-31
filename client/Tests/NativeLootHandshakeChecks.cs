using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Threading;
using FortyTwoAndBeyond.Protocol;

/// <summary>Exercise selection, native permission and sparse corpse inventory updates against an owned UDP peer.</summary>
internal static class NativeLootHandshakeChecks
{
    private const uint Player = 81, Wreck = 120;
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;

    /// <summary>Prove stale or denied verbs never deadlock requests or authorize collection.</summary>
    public static void Check()
    {
        using (var fixture = new Fixture())
        {
            fixture.Apply(new Frame(0x5c, Verbs(999, 0)));
            fixture.Session.OpenLoot(Wreck); fixture.Receive(0x17);
            Require(fixture.Session.World.PendingLootId == Wreck && !fixture.Session.World.LootPermissionRequested,
                "Loot selection phase was not published to the immutable HUD view.");
            fixture.Session.OpenLoot(Wreck);
            Require(fixture.Peer.Available == 0, "Repeated same-wreck open duplicated native selection.");
            fixture.Acknowledge(999);
            Require(fixture.Peer.Available == 0, "An unrelated target acknowledgement opened loot.");
            fixture.Apply(new Frame(0x5c, Verbs(Wreck, 2))); // Explicitly unavailable: server must still decide the requested action.
            fixture.Acknowledge(Wreck); fixture.Receive(0x51);
            Require(fixture.Session.World.OpenLootId == 0 && fixture.Session.World.LootPermissionRequested,
                "A selection acknowledgement or unavailable verb was mistaken for server loot permission.");
            fixture.Session.TakeLoot(Wreck, 1);
            Require(fixture.Peer.Available == 0, "Collection bypassed the native permission grant.");
            fixture.Grant(Wreck + 1);
            Require(fixture.Session.World.OpenLootId == 0, "An unrelated permission opened this wreck.");
            fixture.Grant(Wreck);
            Snapshot before = new Snapshot(fixture.Session.World, "", true);
            Require(before.OpenLootId == Wreck && before.PendingLootId == 0 && before.Loot.Single().Items[1].Quantity == 3,
                "Matching native permission did not expose the already received corpse contents.");
            fixture.Session.TakeLoot(Wreck, 1); fixture.Receive(0x27);
            Require(fixture.Session.World.Loot[Wreck][1].Quantity == 3, "A tractor request optimistically removed loot.");
            fixture.Apply(new Frame(0x1b, CargoDiff(1, false)));
            Require(fixture.Session.World.Loot[Wreck][0].Template == 301 && fixture.Session.World.Loot[Wreck][1].Template == 302 &&
                fixture.Session.World.Loot[Wreck][1].Quantity == 1 && before.Loot.Single().Items[1].Quantity == 3,
                "Sparse corpse quantity update lost another slot, its template, or snapshot immutability.");
            fixture.Apply(new Frame(0x1b, CargoDiff(0, true)));
            Require(new Snapshot(fixture.Session.World, "", true).Loot.Single().Items[1] == null &&
                fixture.Session.World.Loot[Wreck][0].Present, "Sparse native looted-slot removal was ignored.");
            fixture.Apply(new Frame(4, NativeWreckCompatibilityChecks.Create(25)));
            Require(fixture.Session.World.OpenLootId == 0 && !fixture.Session.World.Loot.ContainsKey(Wreck),
                "A reused native corpse identity retained its previous permission or inventory.");
        }
        using (var fixture = new Fixture())
        {
            fixture.Session.SelectTarget(Wreck); fixture.Receive(0x17); fixture.Acknowledge(Wreck);
            fixture.Session.World.Equipment.TargetId = Wreck; // An authoritative target AUX already arrived.
            fixture.Session.OpenLoot(Wreck); fixture.Receive(0x51);
            Require(fixture.Peer.Available == 0, "An already acknowledged current target unnecessarily waited for another selection acknowledgement.");
        }
        using (var fixture = new Fixture())
        {
            byte[] other = NativeWreckCompatibilityChecks.Create(25); WriteId(other, Wreck + 1);
            fixture.Apply(new Frame(4, other));
            fixture.Session.OpenLoot(Wreck); fixture.Receive(0x17);
            fixture.Session.OpenLoot(Wreck + 1); fixture.Receive(0x17);
            fixture.Acknowledge(Wreck); fixture.Grant(Wreck);
            Require(fixture.Peer.Available == 0 && fixture.Session.World.OpenLootId == 0 && fixture.Session.World.PendingLootId == Wreck + 1,
                "A late old-target acknowledgement or grant stole the newer wreck request.");
            fixture.Acknowledge(Wreck + 1); fixture.Receive(0x51); fixture.Grant(Wreck + 1);
            Require(fixture.Session.World.OpenLootId == Wreck + 1, "Replacing a stale pending wreck prevented the next request opening.");
        }
        using (var fixture = new Fixture())
        {
            fixture.Session.OpenLoot(Wreck); fixture.Receive(0x17);
            fixture.Apply(new Frame(0x66, Wire.Body(writer => { writer.Write(1u); writer.Write(0u); })));
            Require(fixture.Session.World.PendingLootId == 0 && fixture.Session.World.OpenLootId == 0,
                "Explicit server closure left the HUD waiting for loot permission.");
            fixture.Session.OpenLoot(Wreck); fixture.Receive(0x17);
            Require(fixture.Session.World.PendingLootId == Wreck, "Closed loot request could not be retried.");
        }
        using (var fixture = new Fixture())
        {
            fixture.Session.OpenLoot(Wreck); fixture.Receive(0x17);
            fixture.Apply(new Frame(7, Wire.Body(writer => writer.Write(Wreck))));
            fixture.Acknowledge(Wreck); fixture.Grant(Wreck);
            Require(fixture.Peer.Available == 0 && fixture.Session.World.PendingLootId == 0 && fixture.Session.World.OpenLootId == 0,
                "Despawn left a pending request or allowed a stale permission to reopen loot.");
        }
    }

    /// Returns:
    ///     byte[]: A native current/default verb list for one target.
    private static byte[] Verbs(uint id, ushort attribute) => Wire.Body(writer =>
    { Wire.Big(writer, id); Wire.Big(writer, 1u); writer.Write((ushort)2); writer.Write((ushort)3);
        Wire.Big(writer, 1u); writer.Write(attribute); writer.Write((ushort)3); });

    /// Returns:
    ///     byte[]: A native cargo-only HulkIndex diff updating the second slot.
    private static byte[] CargoDiff(uint quantity, bool remove)
    {
        byte[] body = Wire.Body(writer =>
        {
            writer.Write(Wreck); writer.Write((ushort)0); writer.Write((byte)1);
            writer.Write(NativeWreckCompatibilityChecks.PresentMask(8, new[] { 7 }));
            writer.Write(NativeWreckCompatibilityChecks.PresentMask(40, new[] { 1 }));
            writer.Write(NativeWreckCompatibilityChecks.PresentMask(10, remove ? new[] { 0, 1 } : new[] { 1 }));
            if (remove) writer.Write(uint.MaxValue - 1);
            writer.Write(quantity);
        });
        ushort size = (ushort)(body.Length - 6); body[4] = (byte)size; body[5] = (byte)(size >> 8); return body;
    }

    /// <summary>Choose a second isolated object identity without changing any native shape.</summary>
    private static void WriteId(byte[] body, uint id)
    { byte[] value = BitConverter.GetBytes(id); Buffer.BlockCopy(value, 0, body, 0, 4); }

    private sealed class Fixture : IDisposable
    {
        public readonly UdpClient Peer;
        public readonly LocalSession Session;

        /// <summary>Bind only privately owned sockets and publish native wreck contents.</summary>
        public Fixture()
        {
            Peer = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)); Peer.Client.ReceiveTimeout = 1000;
            Session = new LocalSession(CancellationToken.None);
            typeof(LocalSession).GetField("address", Hidden).SetValue(Session, IPAddress.Loopback);
            var socket = (UdpClient)typeof(LocalSession).GetMethod("NewUdp", Hidden).Invoke(Session,
                new object[] { ((IPEndPoint)Peer.Client.LocalEndPoint).Port });
            typeof(LocalSession).GetField("sector", Hidden).SetValue(Session, socket);
            typeof(LocalSession).GetProperty("Player").SetValue(Session, Player);
            typeof(LocalSession).GetProperty("Ready").SetValue(Session, true);
            Apply(new Frame(4, NativeWreckCompatibilityChecks.Create(25)));
            Apply(new Frame(0x40, NativeWreckCompatibilityChecks.Pose(100, 200, 300)));
            Apply(new Frame(0x1b, NativeWreckCompatibilityChecks.CorpseAux(true)));
        }

        /// <summary>Run a received frame through the actual worker-side session handler.</summary>
        public void Apply(Frame frame) => typeof(LocalSession).GetMethod("Apply", Hidden).Invoke(Session, new object[] { frame });

        /// <summary>Deliver one native selection acknowledgement.</summary>
        public void Acknowledge(uint id) => Apply(new Frame(0x19, Wire.Body(writer => { writer.Write(id); writer.Write(uint.MaxValue); })));

        /// <summary>Deliver one native big-endian loot permission grant.</summary>
        public void Grant(uint id) => Apply(new Frame(0x8c, Wire.Body(writer => Wire.Big(writer, id))));

        /// <summary>Verify the next actual outgoing native opcode.</summary>
        public void Receive(ushort opcode)
        { IPEndPoint from = null; Require(Wire.Datagram(Peer.Receive(ref from), Player) == opcode, "Unexpected native loot handshake packet; expected " + opcode.ToString("x")); }

        /// <summary>Close only the fixture's owned transport resources.</summary>
        public void Dispose() { Session.Dispose(); Peer.Dispose(); }
    }

    /// <summary>Fail a concrete native loot handshake or sparse update contract.</summary>
    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}
