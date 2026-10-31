using System;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Threading;
using FortyTwoAndBeyond.Protocol;

/// <summary>Verify optional aggregation never blocks native vendor interaction.</summary>
internal static class StationContactCompatibilityChecks
{
    private const uint Player = 71;
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

    /// <summary>Exercise fallback and negotiated paths using only privately owned loopback sockets.</summary>
    public static void Check()
    {
        using (var fixture = new Fixture())
        {
            fixture.Session.OpenNpc(11);
            fixture.Receive(0x203a);
            for (int attempt = 1; attempt < 3; attempt++)
            {
                fixture.Advance(); fixture.Receive(0x203a);
            }
            Require(fixture.Session.World.Dialogue == null, "A cross-room conversation started before negotiation or native room entry.");
            fixture.Advance(); fixture.CheckNativeTalk();
            fixture.Advance();
            Require(fixture.Server.Available == 0, "Unsupported aggregation kept retrying after native fallback.");
        }
        using (var fixture = new Fixture())
        {
            fixture.Session.OpenNpc(12);
            byte[] talk = fixture.Receive(0x4e);
            Require(Wire.U32(talk, 16) == 12 && talk[20] == 4 && fixture.Server.Available == 0,
                "A same-room vendor unnecessarily waited for custom capability negotiation.");
        }
        using (var fixture = new Fixture())
        {
            fixture.Session.OpenNpc(11); fixture.Receive(0x203a);
            fixture.Acknowledge(true);
            byte[] talk = fixture.Receive(0x4e);
            Require(Wire.U32(talk, 16) == 11 && fixture.Session.World.Station.CurrentRoom == 0 && fixture.Server.Available == 0,
                "Negotiated aggregation unexpectedly moved rooms or failed to open the queued contact.");
        }
        using (var fixture = new Fixture())
        {
            fixture.Session.OpenNpc(11); fixture.Receive(0x203a);
            fixture.Acknowledge(false); fixture.CheckNativeTalk();
            fixture.Session.World.CloseDialogue();
            fixture.Session.OpenNpc(999);
            Require(fixture.Server.Available == 0 && fixture.Session.World.Dialogue == null,
                "Unadvertised NPC identity bypassed station validation.");
        }
    }

    /// <summary>Own a fake native station peer, never a game server or user session.</summary>
    private sealed class Fixture : IDisposable
    {
        public readonly UdpClient Server;
        public readonly LocalSession Session;

        /// <summary>Bind a session to an isolated UDP peer and a two-room authoritative layout.</summary>
        public Fixture()
        {
            Server = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            Server.Client.ReceiveTimeout = 1000;
            Session = new LocalSession(CancellationToken.None);
            typeof(LocalSession).GetField("address", PrivateInstance).SetValue(Session, IPAddress.Loopback);
            UdpClient socket = (UdpClient)typeof(LocalSession).GetMethod("NewUdp", PrivateInstance)
                .Invoke(Session, new object[] { ((IPEndPoint)Server.Client.LocalEndPoint).Port });
            typeof(LocalSession).GetField("sector", PrivateInstance).SetValue(Session, socket);
            typeof(LocalSession).GetProperty("Player").SetValue(Session, Player);
            typeof(LocalSession).GetProperty("Ready").SetValue(Session, true);
            typeof(World).GetProperty("Station").SetValue(Session.World, new StationState(100,
                rooms: new[] { new StationRoom(0, 0), new StationRoom(1, 0) },
                contacts: new[] { new StationContact(11, 1), new StationContact(12, 0) }, currentRoom: 0));
        }

        /// <summary>Expire one aggregation deadline without sleeps or driving the game worker.</summary>
        public void Advance()
        {
            double deadline = (double)typeof(LocalSession).GetField("aggregateStationAt", PrivateInstance).GetValue(Session);
            typeof(LocalSession).GetMethod("AdvanceStationContacts", PrivateInstance).Invoke(Session, new object[] { deadline + .001 });
        }

        /// <summary>Deliver the existing capability acknowledgement through its real frame handler.</summary>
        public void Acknowledge(bool enabled)
        {
            var frame = new Frame(0x203b, Wire.Body(writer => { writer.Write(Player); writer.Write(enabled ? 1u : 0u); }));
            typeof(LocalSession).GetMethod("Apply", PrivateInstance).Invoke(Session, new object[] { frame });
        }

        /// Returns:
        ///     byte[]: One expected native datagram captured by the isolated peer.
        public byte[] Receive(ushort opcode)
        {
            IPEndPoint endpoint = null;
            byte[] packet = Server.Receive(ref endpoint);
            Require(Wire.Datagram(packet, Player) == opcode, "Native station packet order changed; expected " + opcode.ToString("x"));
            return packet;
        }

        /// <summary>Verify room change and pose precede native talk to the advertised vendor.</summary>
        public void CheckNativeTalk()
        {
            byte[] room = Receive(0x9f), pose = Receive(0x9d), talk = Receive(0x4e);
            Require(Wire.U32(room, 12) == Player && Wire.U32(room, 16) == 1 && Wire.U32(room, 20) == 0,
                "Native room movement lost player/new-room/old-room layout.");
            Require(Wire.U32(pose, 12) == Player && Wire.U32(pose, 16) == 1 && Wire.U32(talk, 16) == 11 && talk[20] == 4 &&
                Session.World.Station.CurrentRoom == 1 && Session.World.Dialogue?.NpcId == 11 && Server.Available == 0,
                "Native station fallback did not enter the vendor room and start its conversation exactly once.");
        }

        /// <summary>Close only this fixture's owned session and sockets.</summary>
        public void Dispose() { Session.Dispose(); Server.Dispose(); }
    }

    /// <summary>Fail with the native compatibility behavior that regressed.</summary>
    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}
