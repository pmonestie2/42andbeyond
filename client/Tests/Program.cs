using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using FortyTwoAndBeyond.Client;
using FortyTwoAndBeyond.Protocol;

internal static class Program
{
    /// <summary>Run pure protocol checks, with explicit opt-in to a disposable local server.</summary>
    private static void Main(string[] args)
    {
        CheckReassembly(); CheckWorld(); CheckMissionJournal(); CheckMissionDialogue();
        CheckExperience(); CheckMiningPhases(); CheckLootTractor(); CheckWarpPhases(); CheckWeaponReady();
        CheckDamageResults(); CheckFlightThrottle();
        CheckStationLayout(); CheckStationWalk(); CheckDemoRoster(); CheckAddress(); PeerChecks.Decode();
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
        byte[] scanStart = Wire.Body(w => { w.Write((ushort)34); w.Write((byte)0);
            w.Write(System.Text.Encoding.ASCII.GetBytes("Scan Boost active for 60 seconds.\0")); });
        world.Apply(new Frame(0x1d, scanStart), 1);
        Snapshot scanSnapshot = new Snapshot(world, "ready", true);
        Assert(scanSnapshot.ActiveDeviceLabel == "Scan Boost" && !scanSnapshot.TurboWeaponsActive,
            "Generic device acknowledgement was not distinguished from Turbo Weapons.");
        byte[] scanEnd = Wire.Body(w => { w.Write((ushort)18); w.Write((byte)0);
            w.Write(System.Text.Encoding.ASCII.GetBytes("Scan Boost ended.\0")); });
        world.Apply(new Frame(0x1d, scanEnd), 1);
        Assert(world.ActiveDeviceLabel.Length == 0 && scanSnapshot.ActiveDeviceLabel == "Scan Boost",
            "Generic device expiry changed an earlier display snapshot.");
    }

    /// <summary>Decode an actual server mission AUX fixture and preserve snapshot ownership.</summary>
    private static void CheckMissionJournal()
    {
        const string fixture =
            "1600fe0f76001efc0f0300000000140042617369632053686f6f74696e672052616e6765" +
            "44004c6561726e2061626f757420636f6d6261742066726f6d204c742e205261696e652e20" +
            "285265776172643a20456e7261676520736b696c6c2c20436f6d62617420585029010500000001000000" +
            "160000feff0f96a0004c742e205261696e65206861732073656e7420796f7520746f2064657374726f79" +
            "20736f6d65207065736b7920726f67756520547261736820436f6c6c6563746f7220426f747320696e20" +
            "4c756e6120536563746f722e20546865792063616e20626520666f756e64206e6561722074686520426173" +
            "69632053686f6f74696e672052616e6765732e2028302f33207461726765747320646566656174656429" +
            "050505050505050505050505050505050505050505050505050505050505";
        byte[] missions = Enumerable.Range(0, fixture.Length / 2)
            .Select(index => Convert.ToByte(fixture.Substring(index * 2, 2), 16)).ToArray();
        byte[] aux = Wire.Body(writer =>
        {
            writer.Write(0u); writer.Write((ushort)(missions.Length + 4)); writer.Write((byte)1);
            writer.Write(new byte[] { 2, 0x20, 0 }); writer.Write(missions);
        });
        var world = new World(); world.Apply(new Frame(0x1b, aux), 1);
        Snapshot first = new Snapshot(world, "ready", true);
        Assert(first.Missions[0]?.Name == "Basic Shooting Range" &&
            first.Missions[0].StageCount == 5 && first.Missions[0].Stage == 1 &&
            first.Missions[0].Objective.Contains("(0/3 targets defeated)"),
            "Native mission journal did not decode its first active stage.");
        Assert(first.Missions.Skip(1).All(mission => mission == null),
            "Native mission reset slots remained populated.");
        world.Apply(new Frame(0x1b, Wire.Body(writer =>
        {
            writer.Write(0u); writer.Write((ushort)7); writer.Write((byte)1);
            writer.Write(new byte[] { 6, 0, 0, 0x80, 0, 5 });
        })), 1);
        Assert(world.Missions[0] == null && first.Missions[0] != null,
            "Mission journal reset changed an earlier immutable snapshot.");
    }

    /// <summary>Keep server mission notices attached only to their current NPC talk tree.</summary>
    private static void CheckMissionDialogue()
    {
        var world = new World(); world.StartDialogue(46);
        Action<string> notice = message =>
        {
            byte[] text = System.Text.Encoding.ASCII.GetBytes(message + "\0");
            world.Apply(new Frame(0x1d, Wire.Body(writer =>
            { writer.Write((ushort)text.Length); writer.Write((byte)11); writer.Write(text); })), 1);
        };
        notice("Mission offer: Basic Shooting Range.");
        DialogueState offered = world.Dialogue;
        Assert(offered.MissionKind == MissionDialogueKind.Offer && offered.MissionName == "Basic Shooting Range",
            "Authoritative mission offer was not identified.");
        byte[] tree = Wire.Body(writer =>
        {
            writer.Write(System.Text.Encoding.ASCII.GetBytes("An assignment for you.\0"));
            writer.Write((byte)0); writer.Write((byte)1); writer.Write(2u);
            writer.Write(System.Text.Encoding.ASCII.GetBytes("Tell me more.\0"));
        });
        world.Apply(new Frame(0x54, tree), 1);
        Assert(world.Dialogue.MissionKind == MissionDialogueKind.Offer && world.Dialogue.Choices[0].Node == 2,
            "Talk-tree delivery erased the mission offer or its advertised choice.");
        notice("Mission Basic Shooting Range: stage 1.");
        Assert(world.Dialogue.MissionKind == MissionDialogueKind.Updated && world.Dialogue.MissionStage == 1 &&
            offered.MissionKind == MissionDialogueKind.Offer,
            "Mission acceptance did not create an immutable dialogue revision.");
        notice("Mission completed: Basic Shooting Range.");
        Assert(world.Dialogue.MissionKind == MissionDialogueKind.Completed,
            "Mission completion was not reflected in the active dialogue.");
        world.CloseDialogue(); world.StartDialogue(41);
        Assert(world.Dialogue.MissionKind == MissionDialogueKind.None,
            "An ordinary NPC inherited a previous mission badge.");
        notice("Mission objective: Finer Points of Negotiation.");
        Assert(world.Dialogue.MissionKind == MissionDialogueKind.Objective,
            "An active mission objective notice was not identified.");
    }

    /// <summary>Decode a server-encoded RPG AUX fixture and keep display revisions immutable.</summary>
    private static void CheckExperience()
    {
        const string fixture =
            "35001b00000000002b000102040006fff0000f050501000000010000000000003f" +
            "3200000000000000320000000000000032000000";
        byte[] packet = Enumerable.Range(0, fixture.Length / 2)
            .Select(index => Convert.ToByte(fixture.Substring(index * 2, 2), 16)).ToArray();
        Assert(packet.Length == 53 && Wire.U16(packet, 0) == packet.Length && Wire.U16(packet, 2) == 0x1b,
            "Experience fixture has an invalid native frame header.");
        byte[] body = packet.Skip(4).ToArray();
        var world = new World(); world.Apply(new Frame(0x1b, body), 1);
        Snapshot first = new Snapshot(world, "ready", true);
        Assert(first.Experience != null && first.Experience.CombatLevel == 50 &&
            first.Experience.CombatFraction == .5f && first.Experience.TradeLevel == 50 &&
            first.Experience.TradeFraction == 0 && first.Experience.ExploreLevel == 50 &&
            first.Experience.ExploreFraction == 0 && first.Experience.AvailableSkillPoints == 1 &&
            first.Experience.EarnedSkillPoints == 1,
            "Native RPG fields did not decode into the three XP tracks and skill points.");
        byte[] updated = (byte[])body.Clone(); updated[27] = 0x40;
        world.Apply(new Frame(0x1b, updated), 1);
        Assert(world.Experience.CombatFraction == .75f && first.Experience.CombatFraction == .5f,
            "An updated XP bar changed an earlier immutable snapshot.");
        byte[] invalid = (byte[])body.Clone();
        Array.Copy(BitConverter.GetBytes(1.5f), 0, invalid, 25, 4);
        Reject(() => world.Apply(new Frame(0x1b, invalid), 1));
    }

    /// <summary>Follow authoritative prospect, tractor, and cancellation packets.</summary>
    private static void CheckMiningPhases()
    {
        var world = new World();
        byte[] prospect = Wire.Body(writer =>
        { writer.Write(1u); writer.Write(77u); writer.Write(9u); writer.Write(500u); writer.Write(5000u); });
        world.Apply(new Frame(0x2012, prospect), 1);
        Snapshot first = new Snapshot(world, "ready", true);
        Assert(first.MiningActivity?.Phase == MiningPhase.Prospect &&
            first.MiningActivity.TargetId == 77 && first.MiningActivity.EffectId == 9 &&
            first.MiningActivity.DurationSeconds == 5 && first.MiningProgress >= 0 &&
            first.MiningProgress < 1,
            "Native prospect phase did not establish a bounded progress display.");
        byte[] other = (byte[])prospect.Clone();
        Array.Copy(BitConverter.GetBytes(2u), 0, other, 0, 4);
        world.Apply(new Frame(0x2012, other), 1);
        Assert(world.MiningActivity.EffectId == 9,
            "Another pilot's prospect beam replaced the local phase.");
        byte[] tractor = Wire.Body(writer =>
        {
            writer.Write(1u); writer.Write(10u); writer.Write(11u);
            writer.Write((ushort)233); writer.Write(600u); writer.Write((ushort)3);
            writer.Write(System.Text.Encoding.ASCII.GetBytes("Ore"));
            writer.Write(2000u); writer.Write(200f);
            writer.Write(10f); writer.Write(20f); writer.Write(30f);
        });
        world.Apply(new Frame(0x2013, tractor), 1);
        Assert(world.MiningActivity.Phase == MiningPhase.Tractor &&
            world.MiningActivity.TargetId == 77 && world.MiningActivity.ArticleId == 10 &&
            world.MiningActivity.EffectId == 11 && world.MiningActivity.ItemName == "Ore" &&
            world.MiningActivity.DurationSeconds == 2 && first.MiningActivity.Phase == MiningPhase.Prospect,
            "Ore tractor transition lost the selected resource or changed an older snapshot.");
        world.Apply(new Frame(0x0f, BitConverter.GetBytes(9u)), 1);
        Assert(world.MiningActivity?.EffectId == 11, "An earlier effect removal cancelled tractor progress.");
        world.Apply(new Frame(7, BitConverter.GetBytes(10u)), 1);
        Assert(world.MiningActivity == null, "Collected ore left an active tractor display.");
        world.Apply(new Frame(0x2012, prospect), 1);
        world.Apply(new Frame(0x0f, BitConverter.GetBytes(9u)), 1);
        Assert(world.MiningActivity == null, "Server cancellation left prospecting active.");
        byte[] invalid = (byte[])prospect.Clone();
        Array.Copy(BitConverter.GetBytes(100001u), 0, invalid, 16, 4);
        Reject(() => world.Apply(new Frame(0x2012, invalid), 1));
    }

    /// <summary>Separate shared wreck-window control from native ore collection.</summary>
    private static void CheckLootTractor()
    {
        var world = new World(); world.SetOpenLoot(77);
        byte[] open = Wire.Body(writer => { writer.Write(0u); writer.Write(0u); });
        byte[] close = Wire.Body(writer => { writer.Write(1u); writer.Write(0u); });
        world.Apply(new Frame(0x66, open), 1);
        Assert(world.OpenLootId == 77 && !world.ResourceWindowOpen,
            "Wreck interface was mistaken for an open prospecting window.");
        byte[] tractor = Wire.Body(writer =>
        {
            writer.Write(1u); writer.Write(10u); writer.Write(11u);
            writer.Write((ushort)233); writer.Write(600u); writer.Write((ushort)3);
            writer.Write(System.Text.Encoding.ASCII.GetBytes("Ore"));
            writer.Write(2000u); writer.Write(200f);
            writer.Write(10f); writer.Write(20f); writer.Write(30f);
        });
        world.Apply(new Frame(0x2014, tractor), 1);
        Snapshot first = new Snapshot(world, "ready", true);
        Assert(first.LootActivity?.CorpseId == 77 && first.LootActivity.ArticleId == 10 &&
            first.LootActivity.EffectId == 11 && first.LootActivity.ItemName == "Ore" &&
            first.LootActivity.Position.X == 10 && first.LootActivity.Position.Y == 20 &&
            first.LootActivity.Position.Z == 30 && first.LootActivity.DurationSeconds == 2 &&
            first.LootProgress >= 0 && first.LootProgress < 1 && first.MiningActivity == null,
            "Native wreck tractor did not retain its corpse, position and duration separately from ore mining.");
        byte[] other = (byte[])tractor.Clone();
        Array.Copy(BitConverter.GetBytes(2u), 0, other, 0, 4);
        world.Apply(new Frame(0x2014, other), 1);
        Assert(world.LootActivity?.ArticleId == 10,
            "Another pilot's wreck tractor replaced the local one.");
        world.Apply(new Frame(7, BitConverter.GetBytes(10u)), 1);
        Assert(world.LootActivity == null && first.LootActivity != null,
            "Loot completion changed an earlier immutable activity snapshot.");
        world.Apply(new Frame(0x2014, tractor), 1);
        world.Apply(new Frame(0x0f, BitConverter.GetBytes(11u)), 1);
        Assert(world.LootActivity == null, "Loot cancellation left tractor progress active.");
        world.Apply(new Frame(0x2014, tractor), 1);
        world.Apply(new Frame(0x66, close), 1);
        Assert(world.LootActivity == null && world.OpenLootId == 0 && !world.ResourceWindowOpen,
            "Closing a wreck did not clear its native interface and tractor state.");
        world.SetResourceTarget(88); world.Apply(new Frame(0x66, open), 1);
        Assert(world.ResourceWindowOpen && world.ResourceTargetId == 88 && world.OpenLootId == 0,
            "Prospecting interface failed after a wreck window closed.");
        byte[] invalid = (byte[])tractor.Clone();
        Array.Copy(BitConverter.GetBytes(600001u), 0, invalid, 23, 4);
        Reject(() => world.Apply(new Frame(0x2014, invalid), 1));
    }

    /// <summary>Keep warp visuals tied to the server's own-ship AUX phase revisions.</summary>
    private static void CheckWarpPhases()
    {
        var world = new World();
        byte[] phase(uint local, uint global) => Wire.Body(writer =>
        {
            writer.Write(1u); writer.Write((ushort)17); writer.Write((byte)1);
            writer.Write(new byte[] { 2, 0, 0, 0, 0x18, 0, 0, 0 });
            writer.Write(local); writer.Write(global);
        });
        world.Apply(new Frame(0x1b, phase(1, 1)), 1);
        Snapshot charging = new Snapshot(world, "ready", true);
        Assert(charging.WarpPhase == WarpPhase.Charging && !charging.WarpArrived,
            "Native charge AUX did not start the warp display.");
        world.Apply(new Frame(0x1b, phase(2, 2)), 1);
        Assert(world.Warp.Phase == WarpPhase.Traveling && charging.WarpPhase == WarpPhase.Charging,
            "Travel AUX did not advance warp while preserving the older snapshot.");
        byte[] unrelated = Wire.Body(writer =>
        {
            writer.Write(1u); writer.Write((ushort)13); writer.Write((byte)1);
            writer.Write(new byte[] { 2, 0, 0, 0, 0x20, 0, 0, 0 }); writer.Write(2u);
        });
        world.Apply(new Frame(0x1b, unrelated), 1);
        Assert(world.Warp.Phase == WarpPhase.Traveling,
            "An unrelated ship AUX diff erased warp travel.");
        world.Apply(new Frame(0x1b, phase(8, 3)), 1);
        Assert(world.Warp.Phase == WarpPhase.Recovering && world.Warp.Arrived,
            "Arrival recovery lost the native arrival reason.");
        world.Apply(new Frame(0x1b, phase(0, 0)), 1);
        Assert(world.Warp.Phase == WarpPhase.Idle && !world.Warp.Arrived,
            "Ready AUX did not clear warp recovery.");
        world.Apply(new Frame(0x1b, phase(3, 3)), 1);
        Assert(world.Warp.Phase == WarpPhase.Recovering && !world.Warp.Arrived,
            "Cancelled warp recovery was mistaken for arrival.");
        Reject(() => world.Apply(new Frame(0x1b, phase(2, 3)), 1));
        byte[] ahead = WarpState.RouteRequest(0x12345678, 0);
        Assert(ahead.Length == 6 && Wire.U32(ahead, 0) == 0x345678 && Wire.U16(ahead, 4) == 0,
            "Free-flight and cancel warp did not encode a zero-waypoint native route.");
        byte[] targeted = WarpState.RouteRequest(0x12345678, 77);
        Assert(targeted.Length == 10 && Wire.U16(targeted, 4) == 1 && Wire.U32(targeted, 6) == 77,
            "Targeted warp lost its native waypoint identity.");
    }

    /// <summary>Decode the server's nested weapon state and display-only ready clock.</summary>
    private static void CheckWeaponReady()
    {
        const string fixture =
            "27001b00010000001d00010200000000010000020882000002c0018813000000409a4489200000";
        byte[] packet = Enumerable.Range(0, fixture.Length / 2)
            .Select(index => Convert.ToByte(fixture.Substring(index * 2, 2), 16)).ToArray();
        Assert(packet.Length == 39 && Wire.U16(packet, 0) == 39 && Wire.U16(packet, 2) == 0x1b,
            "Native weapon-state fixture frame is malformed.");
        var world = new World();
        world.ItemCatalog[123] = new ItemTemplateInfo(123, 15, "Test projectile", "");
        world.Equipment.Mounted[3] = new ItemState { Template = 123, Quantity = 1 };
        byte[] movement = new byte[46];
        Array.Copy(BitConverter.GetBytes(1u), 0, movement, 2, 4);
        Array.Copy(BitConverter.GetBytes(4500u), 0, movement, 6, 4);
        world.Apply(new Frame(0x3e, movement), 1);
        world.Apply(new Frame(0x1b, packet.Skip(4).ToArray()), 1);
        Snapshot first = new Snapshot(world, "ready", true);
        ItemView weapon = first.Mounted[3];
        Assert(weapon.ReadyTick == 5000 && weapon.Cooling && weapon.OutOfAmmo &&
            (weapon.WeaponState & 1) != 0 && weapon.Range == 1234f &&
            weapon.CooldownSeconds > .25f && weapon.CooldownSeconds <= .5f,
            "Native weapon ready, cooling, ammo, or firing state did not reach the loadout.");
        var wrapped = new ItemState { Template = 123, WeaponState = 0x2080,
            ReadyTick = 244 };
        ItemView wrapView = new ItemView(3, wrapped, world.ItemCatalog, 0xffffff00);
        Assert(wrapView.CooldownSeconds == .5f,
            "Server tick wrap made the display countdown negative.");
        ItemView beam = new ItemView(3, new ItemState { Template = 124,
            WeaponState = 0x2008 }, new System.Collections.Generic.Dictionary<uint, ItemTemplateInfo>
            { [124] = new ItemTemplateInfo(124, 14, "Test beam", "") });
        Assert(!beam.OutOfAmmo && first.Mounted[3].OutOfAmmo,
            "A beam inherited the projectile empty-ammo marker.");
    }

    /// <summary>Keep the server's damage category, critical flag and misses intact.</summary>
    private static void CheckDamageResults()
    {
        const string fixture = "1c00640000002a420000803e05000000030000000200004001000010";
        byte[] packet = Enumerable.Range(0, fixture.Length / 2)
            .Select(index => Convert.ToByte(fixture.Substring(index * 2, 2), 16)).ToArray();
        Assert(packet.Length == 28 && Wire.U16(packet, 0) == 28 && Wire.U16(packet, 2) == 0x64,
            "Native damage fixture frame is malformed.");
        var world = new World();
        world.Apply(new Frame(0x64, packet.Skip(4).ToArray()), 1);
        Snapshot critical = new Snapshot(world, "ready", true);
        DamageEvent hit = critical.Damages.Single();
        Assert(hit.Source == 0x40000002 && hit.Target == 0x10000001 &&
            hit.Amount == 42.5f && hit.Modifier == .25f &&
            hit.Kind == DamageKind.Chemical && hit.Outcome == DamageOutcome.Critical,
            "Native damage type, modifier or critical result was dropped.");
        byte[] missed = Wire.Body(writer =>
        {
            writer.Write(0f); writer.Write(0f); writer.Write(3u); writer.Write(0u);
            writer.Write(1u); writer.Write(2u);
        });
        world.Apply(new Frame(0x64, missed), 1);
        Assert(world.Damages.Last().Outcome == DamageOutcome.Miss &&
            critical.Damages.Length == 1 && critical.Damages[0].Outcome == DamageOutcome.Critical,
            "Zero-damage miss was lost or changed an older immutable snapshot.");
        byte[] incoming = (byte[])missed.Clone();
        Array.Copy(BitConverter.GetBytes(1u), 0, incoming, 12, 4);
        world.Apply(new Frame(0x64, incoming), 1);
        Assert(world.Damages.Last().Outcome == DamageOutcome.Hit,
            "Legacy incoming damage result was mistaken for a miss.");
        byte[] invalid = (byte[])missed.Clone();
        Array.Copy(BitConverter.GetBytes(9u), 0, invalid, 8, 4);
        Reject(() => world.Apply(new Frame(0x64, invalid), 1));
    }

    /// <summary>Keep held flight controls reachable after focus and text entry.</summary>
    private static void CheckFlightThrottle()
    {
        var throttle = new FlightThrottle();
        Assert(throttle.Sample(true, true, false, false) == 2, "Forward key did not start thrust.");
        Assert(throttle.Sample(true, true, false, false) == null, "Held forward key repeated a request.");
        Assert(throttle.Sample(false, true, false, false) == null,
            "A blocked control sent thrust while typing or unfocused.");
        Assert(throttle.Sample(true, true, false, false) == 2,
            "Held forward key did not resume after returning to flight.");
        Assert(throttle.Sample(true, true, false, true) == 4, "Brake did not override forward.");
        Assert(throttle.Sample(true, true, false, false) == 2,
            "Releasing brake did not restore held forward thrust.");
        Assert(throttle.Sample(true, false, false, false) == null,
            "Releasing thrust should leave the server's speed setting alone.");
        Assert(throttle.Sample(true, false, true, false) == 3, "Reverse key did not set reverse thrust.");
        throttle.Reset();
        Assert(throttle.Sample(true, false, true, false) == 3,
            "Focus restoration did not resubmit a still-held reverse key.");
        Assert(throttle.Sample(true, false, true, false) == null,
            "Unchanged reverse key repeated a request before steering.");
        throttle.Reset();
        Assert(throttle.Sample(true, false, true, false) == 3,
            "Steering away from follow did not resubmit held thrust after the steer command.");
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

    /// <summary>Bound walking and verify the native room-pose packet layout.</summary>
    private static void CheckStationWalk()
    {
        var walk = new StationWalkState(); walk.EnterRoom(2);
        Assert(walk.Room == 2 && walk.X == 0 && walk.Z == LocalSession.StationEntryZ,
            "Room entry did not reset the local avatar at the entrance.");
        Assert(walk.Advance(.1f, true, false, false, false) && walk.Moving && walk.Z > LocalSession.StationEntryZ,
            "Forward walking did not move the avatar into the room.");
        Assert(walk.Advance(.1f, true, false, false, true) && walk.Orientation > 0 && walk.X > 0,
            "Turning right did not rotate and move the avatar.");
        for (int index = 0; index < 1000; index++) walk.Advance(.1f, true, false, false, false);
        Assert(walk.X <= 30 && walk.Z <= 40 && walk.Stop() && !walk.Stop(),
            "Station walking escaped its room bounds or failed to publish an idle transition.");
        byte[] pose = Wire.StationAvatarPose(0x12345678, walk.Room, walk.Orientation,
            walk.X, walk.Z, 1);
        Assert(pose.Length == 28 && Wire.U32(pose, 0) == 0x12345678 && Wire.U32(pose, 4) == 2 &&
            Wire.Float(pose, 8) == walk.Orientation && Wire.Float(pose, 12) == walk.X &&
            Wire.Float(pose, 16) == 0 && Wire.Float(pose, 20) == walk.Z && Wire.U32(pose, 24) == 1,
            "Native station pose field order or length changed.");
        walk.EnterRoom(0);
        Assert(walk.Room == 0 && walk.Orientation == 0 && walk.X == 0 &&
            walk.Z == LocalSession.StationEntryZ && !walk.Moving,
            "Changing rooms retained the prior avatar movement.");
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
            Wait(session, () => session.World.Production.LabId == (session.Player | 0x80000000),
                10, "Station workbench identity did not arrive.");
            session.OpenProduction(4);
            Wait(session, () => session.World.Production.Mode == 4 &&
                new ProductionView(session.World.Production, session.World.ItemCatalog).Open,
                10, "Native refinery window did not open.");
            session.CloseProduction();
            Wait(session, () => !session.World.Production.Open, 10,
                "Native refinery window did not close.");
            Console.WriteLine("PASS: station workbench identity, refinery AUX window and native open/close");
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
