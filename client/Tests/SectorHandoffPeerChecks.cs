using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FortyTwoAndBeyond.Protocol;

/// <summary>Verify fresh sector sockets isolate queued previous-sector datagrams without weakening sequencing.</summary>
internal static class SectorHandoffPeerChecks
{
    private const uint Player = 91;
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;

    /// <summary>Exercise real Enter handshakes with old sequence471 queued before new sequence0.</summary>
    public static void Check()
    {
        using (var master = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)))
        using (var initial = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)))
        using (var session = new LocalSession(CancellationToken.None))
        {
            master.Client.ReceiveTimeout = 2000;
            typeof(LocalSession).GetField("address", Hidden).SetValue(session, IPAddress.Loopback);
            typeof(LocalSession).GetProperty("Player").SetValue(session, Player);
            var receiver = (UdpClient)typeof(LocalSession).GetMethod("NewUdp", Hidden).Invoke(session,
                new object[] { ((IPEndPoint)initial.Client.LocalEndPoint).Port });
            typeof(LocalSession).GetField("sector", Hidden).SetValue(session, receiver);
            for (int transfer = 0; transfer < 4; transfer++)
            {
                using (var next = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)))
                {
                    next.Client.ReceiveTimeout = 2000;
                    var oldEndpoint = new IPEndPoint(IPAddress.Loopback, ((IPEndPoint)receiver.Client.LocalEndPoint).Port);
                    Socket oldSocket = receiver.Client;
                    byte[] stale = Wire.Encode(0x2016, Wire.Encode(0x7ffe, Array.Empty<byte>()), Player, 471);
                    if (transfer == 0) initial.Send(stale, stale.Length, oldEndpoint);
                    Require(SpinWait.SpinUntil(() => receiver.Available > 0, 1000), "Old-sector sequence471 was not queued before Enter.");
                    Task server = Task.Run(() =>
                    {
                        IPEndPoint registration = null;
                        Require(Wire.Datagram(master.Receive(ref registration), Player) == 0x2008, "Fresh sector source did not register with its master.");
                        byte[] response = Wire.Encode(0x2009, Wire.Body(writer =>
                        { writer.Write(0u); writer.Write((ushort)((IPEndPoint)next.Client.LocalEndPoint).Port); writer.Write(Player); }), Player);
                        master.Send(response, response.Length, registration);
                        IPEndPoint login = null;
                        Require(Wire.Datagram(next.Receive(ref login), Player) == 2 && login.Port == registration.Port,
                            "Master registration and sector login used different fresh socket identities.");
                        byte[] fresh = Wire.Encode(0x2016, Wire.Encode(0x7ffe, Array.Empty<byte>()), Player, 0);
                        next.Send(fresh, fresh.Length, login);
                    });
                    typeof(LocalSession).GetMethod("EnterSector", Hidden).Invoke(session, new object[] { Join((uint)(100 + transfer)), ((IPEndPoint)master.Client.LocalEndPoint).Port });
                    server.GetAwaiter().GetResult();
                    UdpClient freshSocket = (UdpClient)typeof(LocalSession).GetField("sector", Hidden).GetValue(session);
                    Require(!ReferenceEquals(receiver, freshSocket) && oldSocket.SafeHandle.IsClosed &&
                        ((IPEndPoint)freshSocket.Client.LocalEndPoint).Port != oldEndpoint.Port,
                        "Enter reused the old queued socket or failed to close its previous owner.");
                    for (int pass = 0; pass < 4; pass++)
                    {
                        typeof(LocalSession).GetMethod("ReceiveSectorBatch", Hidden).Invoke(session, null);
                        if (((SectorStream)typeof(LocalSession).GetField("stream", Hidden).GetValue(session)).Expected == 1) break;
                    }
                    Require(((SectorStream)typeof(LocalSession).GetField("stream", Hidden).GetValue(session)).Expected == 1,
                        "The new sector did not begin at valid sequence0 after an old queued sequence471.");
                    bool strict = false;
                    try { new SectorStream(Player).Feed(stale); } catch (InvalidDataException) { strict = true; }
                    Require(strict, "Handoff weakened the strict fresh-sector sequence window.");
                    receiver = freshSocket;
                    // The next iteration only needs a sender still matching the current
                    // endpoint; keep the current peer alive until its successor is registered.
                    if (transfer < 3)
                    {
                        byte[] queued = Wire.Encode(0x2016, Wire.Encode(0x7ffe, Array.Empty<byte>()), Player, 471);
                        next.Send(queued, queued.Length, (IPEndPoint)receiver.Client.LocalEndPoint);
                        Require(SpinWait.SpinUntil(() => receiver.Available > 0, 1000), "New peer did not queue the following handoff's stale data.");
                    }
                }
            }
            int failedPort = 0;
            Task rejected = Task.Run(() =>
            {
                IPEndPoint registration = null; master.Receive(ref registration); failedPort = registration.Port;
                byte[] mismatch = Wire.Encode(0x2009, Wire.Body(writer =>
                { writer.Write(0u); writer.Write((ushort)12345); writer.Write(Player + 1); }), Player);
                master.Send(mismatch, mismatch.Length, registration);
            });
            bool refused = false;
            try { typeof(LocalSession).GetMethod("EnterSector", Hidden).Invoke(session,
                new object[] { Join(200), ((IPEndPoint)master.Client.LocalEndPoint).Port }); }
            catch (TargetInvocationException error) when (error.InnerException is InvalidDataException) { refused = true; }
            rejected.GetAwaiter().GetResult();
            Require(refused && ReferenceEquals(receiver, typeof(LocalSession).GetField("sector", Hidden).GetValue(session)) &&
                !receiver.Client.SafeHandle.IsClosed, "Failed master registration replaced or disposed the previous socket.");
            using (var reclaimed = new UdpClient(new IPEndPoint(IPAddress.Loopback, failedPort)))
                Require(reclaimed.Client.IsBound, "Failed registration leaked the fresh socket's local port.");
        }
        CheckMotionJournal();
        Console.WriteLine("PASS: fresh sector socket handoffs isolate queued sequence471, retain master/login identity and strict sequence0; private motion capture matches actual sends");
    }

    /// <summary>Verify actual launch/dock round trips against an explicitly supplied disposable server.</summary>
    public static void Live(string host)
    {
        using (var session = new LocalSession(CancellationToken.None))
        {
            session.Connect(host, "local", "local");
            Wait(session, () => session.Ready && session.World.Station != null, "Initial disposable station did not arrive.");
            int generation = session.World.EntryGeneration;
            for (int trip = 0; trip < 3; trip++)
            {
                session.Launch();
                Wait(session, () => session.Ready && session.World.Station == null && session.World.HasPosition && session.World.Objects.Values.Any(item => item.Kind == 12), "Station launch handoff failed.");
                Require(session.World.EntryGeneration > generation, "Launch did not replace the connection generation."); generation = session.World.EntryGeneration;
                MapObject station = session.World.Objects.Values.Where(item => item.Kind == 12).OrderBy(item => item.Position.Distance(session.World.Position)).First();
                if (station.Position.Distance(session.World.Position) > 4900)
                {
                    session.Warp(station.Id);
                    Wait(session, () => station.Position.Distance(session.World.Position) < 4900 && session.World.Warp.Phase == WarpPhase.Idle, "Disposable launch did not approach docking range.");
                }
                session.Dock(station.Id);
                Wait(session, () => session.Ready && session.World.Station != null, "Dock handoff failed.");
                Require(session.World.EntryGeneration > generation, "Dock did not replace the connection generation."); generation = session.World.EntryGeneration;
            }
        }
        Console.WriteLine("PASS: three disposable native station-to-space-to-station handoffs with fresh sector sockets");
    }

    /// <summary>Wait only on the explicitly supplied disposable session.</summary>
    private static void Wait(LocalSession session, Func<bool> ready, string failure)
    { var timeout = Stopwatch.StartNew(); while (!ready()) { if (timeout.Elapsed.TotalSeconds > 30) throw new TimeoutException(failure); session.Poll(); } }

    /// <summary>Check an armed private journal captures both successful motion sends exactly.</summary>
    private static void CheckMotionJournal()
    {
        using (var peer = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)))
        using (var session = new LocalSession(CancellationToken.None))
        using (var trace = new MemoryStream())
        {
            peer.Client.ReceiveTimeout = 1000;
            typeof(LocalSession).GetField("address", Hidden).SetValue(session, IPAddress.Loopback);
            typeof(LocalSession).GetProperty("Player").SetValue(session, Player);
            typeof(LocalSession).GetProperty("Ready").SetValue(session, true);
            var socket = (UdpClient)typeof(LocalSession).GetMethod("NewUdp", Hidden).Invoke(session, new object[] { ((IPEndPoint)peer.Client.LocalEndPoint).Port });
            typeof(LocalSession).GetField("comms", Hidden).SetValue(session, socket);
            typeof(LocalSession).GetField("transportJournal", Hidden).SetValue(session, new StreamWriter(trace) { AutoFlush = true });
            session.MotionReport(new Position(1, 2, 3), new Position(1, 0, 0), 90, 0);
            IPEndPoint from = null; byte[] sent = peer.Receive(ref from);
            typeof(LocalSession).GetField("nextMotionReportAt", Hidden).SetValue(session, -1d);
            session.MotionReport(new Position(4, 5, 6), new Position(1, 0, 0), 120, 0); byte[] second = peer.Receive(ref from);
            string log = Encoding.UTF8.GetString(trace.ToArray());
            Require(log.Split(new[] { "sent-motion1004" }, StringSplitOptions.None).Length == 3 &&
                log.Contains("hex=" + BitConverter.ToString(sent).Replace("-", "")) && sent.Length == 40 &&
                log.Contains("hex=" + BitConverter.ToString(second).Replace("-", "")) && second.Length == 40,
                "Private motion trace did not match both exact datagrams sent.");
        }
    }

    /// Returns:
    ///     byte[]: A synthetic sector handoff identity accepted by the actual Enter path.
    private static byte[] Join(uint destination)
    { byte[] body = new byte[64]; byte[] owner = Wire.Body(writer => Wire.Big(writer, Player)); byte[] sector = Wire.Body(writer => Wire.Big(writer, destination)); Buffer.BlockCopy(owner, 0, body, 16, 4); Buffer.BlockCopy(sector, 0, body, 20, 4); return body; }

    /// <summary>Fail a concrete sector socket ownership or diagnostics contract.</summary>
    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}
