using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using FortyTwoAndBeyond.Protocol;

internal static class Program
{
    /// <summary>Run pure protocol checks, with explicit opt-in to a disposable local server.</summary>
    private static void Main(string[] args)
    {
        CheckReassembly(); CheckWorld(); CheckStationLayout(); CheckDemoRoster(); CheckAddress(); PeerChecks.Decode();
        Console.WriteLine("PASS: framing, ordering, fragmentation, duplicates, bounds, world decoding, private addresses");
        if (args.Length == 2 && args[0] == "--server") { PeerChecks.Live(args[1]); CheckServer(args[1]); }
        else if (args.Length != 0) throw new ArgumentException("Usage: ProtocolChecks [--server PRIVATE_IP]");
    }

    /// <summary>Fail with a useful behavior description.</summary>
    private static void Assert(bool condition, string message)
    { if (!condition) throw new Exception(message); }

    /// <summary>Require malformed input to be rejected.</summary>
    private static void Reject(Action operation)
    {
        try { operation(); }
        catch (InvalidDataException) { return; }
        throw new Exception("Expected invalid packet rejection.");
    }

    /// <summary>Exercise reordering, fragmented frames, bounded gaps, and duplicate integrity.</summary>
    private static void CheckReassembly()
    {
        byte[] frame = Wire.Encode(0x2020, new byte[] { 1, 2, 3, 4 });
        var stream = new SectorStream(123);
        byte[] later = Wire.Encode(0x201a, Wire.Slice(frame, 3, frame.Length - 3), 123, 1);
        Assert(stream.Feed(later).Count == 0 && stream.MissingCount == 1, "Gap was not retained.");
        Assert(stream.Feed(later).Count == 0, "Identical duplicate produced a frame.");
        byte[] conflict = (byte[])later.Clone(); conflict[12] ^= 1;
        Reject(() => stream.Feed(conflict));
        var frames = stream.Feed(Wire.Encode(0x2016, Wire.Slice(frame, 0, 3), 123, 0));
        Assert(frames.Count == 1 && frames[0].Opcode == 0x2020 && frames[0].Body.SequenceEqual(new byte[] { 1, 2, 3, 4 }), "Fragment reassembly failed.");
        Assert(stream.Expected == 2 && stream.Feed(later).Count == 0, "Duplicate delivery advanced the stream.");
        Reject(() => stream.Feed(Wire.Encode(0x2016, frame, 456, 2)));
        Reject(() => stream.Feed(Wire.Encode(0x2016, frame, 123, 258)));
        Reject(() => new SectorStream(1).Feed(Wire.Encode(0x2016, new byte[] { 3, 0, 5, 0 }, 1)));
        Reject(() => Wire.Datagram(new byte[4], 0));
        Reject(() => Wire.Float(BitConverter.GetBytes(float.NaN), 0));
    }

    /// <summary>Check map bounds, movement ownership, and immutable snapshot separation.</summary>
    private static void CheckWorld()
    {
        byte[] body = new byte[64]; body[0] = 77; body[4] = 11; body[57] = 0x10; body[58] = 4;
        Array.Copy(BitConverter.GetBytes(2f), 0, body, 7, 4);
        Array.Copy(BitConverter.GetBytes(1f), 0, body, 49, 4);
        Array.Copy(System.Text.Encoding.ASCII.GetBytes("Gate"), 0, body, 60, 4);
        var world = new World(); world.Apply(new Frame(0x2018, body), 1);
        var snapshot = new Snapshot(world, "test", true);
        Assert(snapshot.Objects[0].Scale == 2 && snapshot.Objects[0].Orientation[3] == 1, "Object transform was not decoded.");
        world.Apply(new Frame(7, new byte[] { 77, 0, 0, 0 }), 1);
        Assert(snapshot.Objects.Length == 1 && snapshot.Objects[0].Kind == 11 && world.Objects.Count == 0, "Snapshot retained mutable world storage.");
        body[58] = 5; Reject(() => new MapObject(body));
        byte[] movement = new byte[38]; movement[2] = 2;
        world.Apply(new Frame(0x3e, movement), 1);
        Assert(!world.HasPosition, "Accepted a different player's movement.");
        movement[2] = 1; world.Apply(new Frame(0x3e, movement), 1);
        Assert(world.HasPosition, "Ignored matching player movement.");
        byte[] turboStart = Wire.Body(w => { w.Write((ushort)37); w.Write((byte)0);
            w.Write(System.Text.Encoding.ASCII.GetBytes("Turbo Weapons active for 10 seconds.\0")); });
        world.Apply(new Frame(0x1d, turboStart), 1);
        Snapshot turboSnapshot = new Snapshot(world, "ready", true);
        Assert(turboSnapshot.TurboWeaponsActive,
            "Turbo Weapons activation feedback did not set the display state.");
        byte[] turboEnd = Wire.Body(w => { w.Write((ushort)21); w.Write((byte)0);
            w.Write(System.Text.Encoding.ASCII.GetBytes("Turbo Weapons ended.\0")); });
        world.Apply(new Frame(0x1d, turboEnd), 1);
        Assert(!world.TurboWeaponsActive && turboSnapshot.TurboWeaponsActive,
            "Turbo Weapons expiry changed an earlier immutable display snapshot.");
    }

    /// <summary>Check packed station room and NPC placement before interior navigation.</summary>
    private static void CheckStationLayout()
    {
        byte[] layout = new byte[8 + 2 * 28 + 4 + 4 + 265];
        Array.Copy(BitConverter.GetBytes(2u), 0, layout, 4, 4);
        Array.Copy(BitConverter.GetBytes(9u), 0, layout, 8 + 4, 4);
        Array.Copy(BitConverter.GetBytes(1u), 0, layout, 8 + 28, 4);
        Array.Copy(BitConverter.GetBytes(12u), 0, layout, 8 + 28 + 4, 4);
        int contact = 8 + 2 * 28 + 4;
        Array.Copy(BitConverter.GetBytes(1u), 0, layout, contact, 4);
        Array.Copy(BitConverter.GetBytes(1u), 0, layout, contact + 4, 4);
        Array.Copy(BitConverter.GetBytes(77u), 0, layout, contact + 12, 4);
        StationState station = new StationState(100).WithLayout(layout);
        Assert(station.Rooms == 2 && station.RoomList[0].Style == 9 && station.RoomList[1].Index == 1 &&
            station.Contacts.Count == 1 && station.Contacts[0].Room == 1 && station.Contacts[0].Id == 77,
            "Station room or placed contact was decoded from the wrong native offset.");
        Assert(station.WithRoom(1).CurrentRoom == 1 && station.CurrentRoom == uint.MaxValue,
            "Station room selection modified an earlier immutable view.");
        Reject(() => station.WithRoom(2));
        var world = new World();
        world.Apply(new Frame(0x4f, Wire.Body(w => { w.Write(100u); w.Write((byte)0); w.Write((byte)0); })), 1);
        world.Apply(new Frame(0x52, layout), 1);
        byte[] description = new byte[260];
        Array.Copy(BitConverter.GetBytes(2u), description, 4);
        Array.Copy(System.Text.Encoding.ASCII.GetBytes("Visitor"), 0, description, 4, 7);
        world.Apply(new Frame(0x61, description), 1);
        world.Apply(new Frame(0xa0, Wire.Body(w => { w.Write(2u); w.Write(1u); w.Write(uint.MaxValue); })), 1);
        world.Apply(new Frame(0x9e, Wire.Body(w =>
        {
            w.Write(2u); w.Write(0.5f); w.Write(10f); w.Write(20f); w.Write(30f);
            w.Write(0x41u); w.Write(1u);
        })), 1);
        Snapshot roomSnapshot = new Snapshot(world, "ready", true);
        Assert(roomSnapshot.StationAvatars.Length == 1 && roomSnapshot.StationAvatars[0].Name == "Visitor" &&
            roomSnapshot.StationAvatars[0].Room == 1 && roomSnapshot.StationAvatars[0].Position.Y == 20,
            "Station peer room or native pose was not decoded.");
        world.Apply(new Frame(0xa0, Wire.Body(w => { w.Write(2u); w.Write(uint.MaxValue); w.Write(1u); })), 1);
        Assert(world.StationAvatars.Count == 0 && roomSnapshot.StationAvatars.Length == 1,
            "Station peer departure changed an earlier display snapshot.");
    }

    /// <summary>Require a published roster to contain only the nine known demo classes.</summary>
    private static void CheckDemoRoster()
    {
        byte[] reply = { 1, 9, (byte)'t', (byte)'e', (byte)'t', (byte)'t',
            (byte)'t', (byte)'s', (byte)'j', (byte)'d', (byte)'j', (byte)'s',
            (byte)'j', (byte)'e', (byte)'p', (byte)'w', (byte)'p', (byte)'p',
            (byte)'p', (byte)'s' };
        DemoAccount[] accounts = DemoRoster.Decode(reply);
        Assert(accounts.Length == 9 && accounts[0].ClassName == "Terran Enforcer" &&
            accounts[0].Username == "pte" && accounts[6].Username == "ppw" &&
            accounts[8].Password == "pps",
            "Published demo classes or their documented login convention changed.");
        byte[] duplicate = (byte[])reply.Clone(); duplicate[18] = (byte)'t'; duplicate[19] = (byte)'e';
        Reject(() => DemoRoster.Decode(duplicate));
        byte[] unknown = (byte[])reply.Clone(); unknown[18] = (byte)'x';
        Reject(() => DemoRoster.Decode(unknown));
        Reject(() => DemoRoster.Decode(new byte[] { 1, 10 }));
    }

    /// <summary>Ensure development credentials cannot be submitted to public endpoints.</summary>
    private static void CheckAddress()
    {
        foreach (string address in new[] { "127.0.0.1", "10.1.2.3", "172.16.0.1", "192.168.47.128" }) LocalSession.LocalAddress(address);
        foreach (string address in new[] { "8.8.8.8", "0.0.0.0", "172.32.0.1", "example.com", "::1" })
        {
            bool rejected = false;
            try { LocalSession.LocalAddress(address); } catch (ArgumentException) { rejected = true; }
            Assert(rejected, "Accepted a non-private IPv4 endpoint.");
        }
    }

    /// <summary>Wait for a concrete server state with a monotonic deadline.</summary>
    private static void Wait(LocalSession session, Func<bool> predicate, double seconds, string reason)
    {
        var timer = Stopwatch.StartNew();
        while (!predicate()) { if (timer.Elapsed.TotalSeconds > seconds) throw new TimeoutException(reason); session.Poll(); }
    }

    /// <summary>Verify actual local login, motion, warp, gate handoff, logout, and worker cancellation.</summary>
    private static void CheckServer(string host)
    {
        using (var session = new LocalSession(CancellationToken.None))
        {
            session.Connect(host, "local", "local");
            Wait(session, () => session.Ready && session.World.HasPosition && session.World.Objects.Values.Any(x => x.Kind == 11), 15, "No initial map.");
            Console.WriteLine("PASS: local bridge login, character selection, UDP entry, map, position");
            Position initial = session.World.Position;
            session.Thrust(2);
            Wait(session, () => session.World.Position.Distance(initial) > 20, 5, "Thrust did not move ship.");
            session.Thrust(4);
            float previousHeading = session.World.Orientation[2];
            session.Steer(.5f, 0);
            Wait(session, () => Math.Abs(session.World.Orientation[2] - previousHeading) > .02, 5, "Steering did not rotate ship.");
            session.Steer(0, 0);
            MapObject gate = session.World.Objects.Values.First(x => x.Kind == 11);
            session.Warp(gate.Id);
            Wait(session, () => session.World.Position.Distance(gate.Position) < 1800 && Math.Abs(session.World.Speed) < 1, 40, "Warp did not approach gate.");
            Console.WriteLine("PASS: server-authoritative impulse, steering, warp");
            session.Gate(gate.Id);
            Wait(session, () => session.Status == "Receiving sector", 45, "Gate handoff did not occur.");
            Wait(session, () => session.Ready && session.World.HasPosition && session.World.Objects.Count > 0, 15, "Destination map did not arrive.");
            Console.WriteLine("PASS: correlated gate activation, completion, destination handoff");
            MapObject station = session.World.Objects.Values.First(x => x.Kind == 12);
            session.Warp(station.Id);
            Wait(session, () => session.World.Position.Distance(station.Position) < 5000 && Math.Abs(session.World.Speed) < 1,
                45, "Warp did not approach the station exterior.");
            session.Dock(station.Id);
            Wait(session, () => session.Ready && session.World.Station?.Id == 10761 && session.World.Station.Rooms > 0,
                20, "Docking handoff or station layout did not arrive.");
            Console.WriteLine("PASS: station target, docking clearance, interior handoff and layout");
            Assert(session.World.Station.NpcIds.Contains(7u), "Station NPC identities were not decoded.");
            Assert(session.World.Station.CurrentRoom == 0 &&
                session.World.Station.Contacts.Any(contact => contact.Id == 7 && contact.Room == 1),
                "Station room or NPC placement was not decoded.");
            session.EnterStationRoom(1);
            Assert(session.World.Station.CurrentRoom == 1, "Station avatar did not enter the contact room.");
            session.OpenNpc(7);
            Wait(session, () => session.World.Dialogue?.NpcId == 7 &&
                session.World.Dialogue.Choices.Any(choice => choice.Node == 2), 10, "NPC greeting or offered branches did not arrive.");
            string greeting = session.World.Dialogue.Text;
            session.ChooseNpc(2);
            Wait(session, () => session.World.Dialogue?.Text != greeting &&
                session.World.Dialogue.Choices.Any(choice => choice.Node == 0), 10,
                "The selected NPC branch did not update the conversation.");
            session.ChooseNpc(0);
            Wait(session, () => session.World.Dialogue == null, 10, "NPC conversation did not close.");
            Console.WriteLine("PASS: station NPC identity, greeting, offered branch and close");
            Assert(session.World.Station.NpcIds.Contains(16u), "Station vendor identity was not decoded.");
            session.EnterStationRoom(2);
            Assert(session.World.Station.CurrentRoom == 2, "Station avatar did not enter the vendor room.");
            session.OpenNpc(16);
            Wait(session, () => session.World.Dialogue?.NpcId == 16 &&
                session.World.Dialogue.Choices.Any(choice => choice.Node == 2), 10,
                "Vendor greeting or trade branch did not arrive.");
            session.ChooseNpc(2);
            Wait(session, () => session.World.Dialogue?.VendorOpen == true &&
                session.World.Vendor.Any(item => item?.Present == true) && session.World.Credits > 0,
                10, "Vendor stock, opening control, or credits did not arrive.");
            int offerSlot = Array.FindIndex(session.World.Vendor, item => item?.Present == true && item.Price > 0);
            Assert(offerSlot >= 0, "Vendor had no priced offer.");
            uint template = session.World.Vendor[offerSlot].Template;
            ulong credits = session.World.Credits;
            session.BuyVendor(offerSlot);
            Wait(session, () => session.World.Credits < credits &&
                session.World.Equipment.Cargo.Any(item => item?.Present == true && item.Template == template),
                10, "Vendor purchase did not update cargo and credits.");
            int cargoSlot = Array.FindIndex(session.World.Equipment.Cargo,
                item => item?.Present == true && item.Template == template);
            int vaultSlot = Array.FindIndex(session.World.Equipment.Vault, item => item?.Present != true);
            Assert(vaultSlot >= 0, "Disposable station vault had no available slot.");
            session.StoreCargo(cargoSlot);
            Wait(session, () => session.World.Equipment.Cargo[cargoSlot]?.Present != true &&
                session.World.Equipment.Vault[vaultSlot]?.Template == template, 10,
                "Storing purchased cargo did not update both authoritative containers.");
            session.RetrieveVault(vaultSlot);
            Wait(session, () => session.World.Equipment.Vault[vaultSlot]?.Present != true &&
                session.World.Equipment.Cargo.Any(item => item?.Template == template), 10,
                "Retrieving a vault item did not restore owned cargo.");
            cargoSlot = Array.FindIndex(session.World.Equipment.Cargo,
                item => item?.Present == true && item.Template == template);
            Console.WriteLine("PASS: native station vault storage, retrieval and authoritative AUX snapshots");
            credits = session.World.Credits;
            session.SellVendor(cargoSlot);
            Wait(session, () => session.World.Credits > credits, 10,
                "Vendor sale did not update the credit balance.");
            session.ChooseNpc(230);
            Wait(session, () => session.World.Dialogue == null &&
                session.World.Vendor.All(item => item?.Present != true), 10,
                "Closing the vendor did not clear the trade session.");
            Console.WriteLine("PASS: native vendor stock, purchase, sale, credit updates and close");
        }
        using (var session = new LocalSession(CancellationToken.None))
        {
            session.Connect(host, "local", "local");
            Wait(session, () => session.Ready && session.World.Station?.Id == 10761,
                15, "A character already docked at the station could not reconnect.");
            session.Launch();
            Wait(session, () => session.Ready && session.World.Station == null && session.World.HasPosition &&
                session.World.Objects.Values.Any(x => x.Kind == 12), 20, "Station launch did not restore exterior flight.");
            Console.WriteLine("PASS: docked-character login, native launch and exterior handoff");
        }
        using (var worker = new ClientWorker(host, "local", "local"))
        {
            var timer = Stopwatch.StartNew();
            while (worker.Latest == null || !worker.Latest.Ready)
            {
                if (worker.Error != null) throw new Exception(worker.Error);
                if (timer.Elapsed.TotalSeconds > 15) throw new TimeoutException("Re-login after logout failed.");
                Thread.Sleep(20);
            }
            worker.Submit(new ClientCommand(CommandKind.Thrust, 4));
            timer.Restart(); worker.Dispose();
            Assert(!worker.Running && timer.Elapsed.TotalSeconds < 3, "Worker cancellation did not join promptly.");
        }
        Console.WriteLine("PASS: explicit logout, re-login, cancellation and worker join");
        using (var cancelled = new ClientWorker(host, "local", "local"))
        {
            var timer = Stopwatch.StartNew(); cancelled.Dispose();
            Assert(!cancelled.Running && timer.Elapsed.TotalSeconds < 3, "Cancellation during connection did not join promptly.");
        }
        Console.WriteLine("PASS: cancellation during connection");
    }
}
