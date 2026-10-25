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
        CheckExperience(); CheckHullProgression(); CheckMiningPhases(); CheckLootTractor(); CheckLootSweep(); CheckWarpPhases(); CheckNavigationSelection(); CheckWarpTargets(); CheckWeaponReady();
        CheckDamageResults(); CheckCombatEngagement(); CheckShotPresentation(); CheckContactSelection(); CheckTargetVerbs(); CheckDeviceEffects();
        CheckNativeDeviceBuff(); CheckPeerScanBuff(); CheckConcurrentRecipientBuffs(); CheckSupportObjectEffects();
        CheckAmmunitionTransfer(); CheckFlightThrottle(); CheckFormationAnchor();
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

    /// <summary>Decode exact stage-zero and upgraded native ship/RPG AUX frames.</summary>
    private static void CheckHullProgression()
    {
        const uint player = 0x40000001;
        var world = new World();
        foreach (string name in new[] { "42b-hull-stage0-ship.bin", "42b-hull-stage0-rpg.bin" })
        {
            byte[] frame = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));
            Assert(Wire.U16(frame, 0) == frame.Length && Wire.U16(frame, 2) == 0x1b,
                "Hull progression fixture is not a native AUX frame.");
            world.Apply(new Frame(0x1b, frame.Skip(4).ToArray()), player);
        }
        Snapshot before = new Snapshot(world, "ready", true);
        Assert(before.CargoCapacity == 25 && before.Experience?.HullStage == 0,
            "Native stage-zero scout capacity or hull stage was lost.");
        world.Equipment.Cargo[0] = new ItemState { Template = 1, Quantity = 1 };
        world.Equipment.Cargo[24] = new ItemState { Template = 2, Quantity = 1 };
        Assert(world.Equipment.FirstFreeCargoSlot() == 1,
            "Cargo placement ignored an empty authoritative slot before the capacity limit.");
        for (int slot = 1; slot < 24; slot++)
            world.Equipment.Cargo[slot] = new ItemState { Template = 1, Quantity = 1 };
        Assert(world.Equipment.FirstFreeCargoSlot() == -1,
            "Cargo placement offered a serialized but locked slot beyond capacity.");
        foreach (string name in new[] { "42b-hull-stage1-ship.bin", "42b-hull-stage1-rpg.bin" })
        {
            byte[] frame = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));
            Assert(Wire.U16(frame, 0) == frame.Length && Wire.U16(frame, 2) == 0x1b,
                "Upgraded hull fixture is not a native AUX frame.");
            world.Apply(new Frame(0x1b, frame.Skip(4).ToArray()), player);
        }
        Snapshot upgraded = new Snapshot(world, "ready", true);
        Assert(upgraded.CargoCapacity == 27 && upgraded.Experience?.HullStage == 1 &&
            before.CargoCapacity == 25 && before.Experience.HullStage == 0,
            "Native hull upgrade changed an earlier immutable snapshot or missed cargo growth.");
        for (int slot = 0; slot < 25; slot++)
            world.Equipment.Cargo[slot] = new ItemState { Template = 1, Quantity = 1 };
        Assert(world.Equipment.FirstFreeCargoSlot() == 25,
            "New hull stage did not unlock the next cargo slot.");
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
        byte[] nextProspect = (byte[])prospect.Clone();
        Array.Copy(BitConverter.GetBytes(12u), 0, nextProspect, 8, 4);
        world.Apply(new Frame(0x2012, nextProspect), 1);
        Snapshot overlap = new Snapshot(world, "ready", true);
        Assert(overlap.MiningProspect?.EffectId == 12 && overlap.MiningTractor?.EffectId == 11 &&
            overlap.MiningActivity?.Phase == MiningPhase.Prospect &&
            overlap.ProspectRemainingSeconds > 0 && overlap.TractorRemainingSeconds > 0,
            "A new prospect overwrote the prior server-owned ore tractor.");
        world.Apply(new Frame(0x0f, BitConverter.GetBytes(9u)), 1);
        Assert(world.MiningProspect?.EffectId == 12 && world.MiningTractor?.EffectId == 11,
            "An earlier prospect effect removal cancelled an overlapping mining phase.");
        world.Apply(new Frame(7, BitConverter.GetBytes(10u)), 1);
        Assert(world.MiningTractor == null && world.MiningProspect?.EffectId == 12 &&
            overlap.MiningTractor?.ArticleId == 10,
            "Collected ore removed the newer prospect or mutated an older snapshot.");
        world.Apply(new Frame(0x0f, BitConverter.GetBytes(12u)), 1);
        Assert(world.MiningActivity == null, "Server cancellation left prospecting active.");
        var replay = new World();
        replay.SetResourceTarget(77);
        replay.Apply(new Frame(0x2013, tractor), 1);
        replay.Apply(new Frame(0x2012, nextProspect), 1);
        Assert(replay.MiningTractor?.TargetId == 77 && replay.MiningProspect?.EffectId == 12,
            "Restart replay did not retain the older tractor and newer prospect separately.");
        replay.Apply(new Frame(0x0f, BitConverter.GetBytes(12u)), 1);
        Assert(replay.MiningProspect == null && replay.MiningTractor?.ArticleId == 10,
            "Canceling the newer prospect erased already claimed ore in flight.");
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

    /// <summary>Advance Collect All only after native tractor and wreck-stock confirmation.</summary>
    private static void CheckLootSweep()
    {
        var catalog = new System.Collections.Generic.Dictionary<uint, ItemTemplateInfo>
        {
            [11] = new ItemTemplateInfo(11, 0, "First item", ""),
            [12] = new ItemTemplateInfo(12, 0, "Second item", "")
        };
        var items = new ItemState[6];
        items[0] = new ItemState { Template = 11, Quantity = 2 };
        items[1] = new ItemState { Template = 12, Quantity = 3 };
        CorpseLoot full = new CorpseLoot(77, items, catalog);
        long now = Stopwatch.GetTimestamp();
        var sweep = new LootSweep();
        Assert(sweep.Begin(77, 77, 77, full, null, now) == 0 && sweep.Active,
            "Collect All did not request the first server-listed wreck slot.");
        Assert(sweep.Observe(77, 77, full, null, false, now + Stopwatch.Frequency) == null,
            "Collect All repeated a request before server tractor acknowledgment.");
        byte[] body = Wire.Body(writer =>
        {
            writer.Write(1u); writer.Write(10u); writer.Write(11u);
            writer.Write((ushort)233); writer.Write(600u); writer.Write((ushort)3);
            writer.Write(System.Text.Encoding.ASCII.GetBytes("Ore"));
            writer.Write(2000u); writer.Write(200f);
            writer.Write(10f); writer.Write(20f); writer.Write(30f);
        });
        LootActivity tractor = LootActivity.FromTractor(body, 1, 77);
        Assert(sweep.Observe(77, 77, full, tractor, false,
            now + 2 * Stopwatch.Frequency) == null && sweep.CollectedStacks == 0,
            "An active native tractor was mistaken for a completed pickup.");
        Assert(sweep.Observe(77, 77, full, null, false,
            now + 3 * Stopwatch.Frequency) == null,
            "Effect removal alone advanced Collect All before the stock changed.");
        items[0] = null;
        CorpseLoot afterFirst = new CorpseLoot(77, items, catalog);
        Assert(sweep.Observe(77, 77, afterFirst, null, false,
            now + 4 * Stopwatch.Frequency) == 1 && sweep.CollectedStacks == 1,
            "Collect All did not advance to the next stock slot after authoritative depletion.");
        sweep.Observe(77, 77, afterFirst, tractor, false, now + 5 * Stopwatch.Frequency);
        items[1] = null;
        Assert(sweep.Observe(77, 77, new CorpseLoot(77, items, catalog), null,
            false, now + 6 * Stopwatch.Frequency) == null && !sweep.Active &&
            sweep.Status == "COLLECTED 2 STACKS",
            "Collect All did not finish after both server-owned stacks were removed.");
        Assert(sweep.Begin(77, 77, 77, full, null, now) == 0 &&
            sweep.Observe(0, 0, full, null, false, now + Stopwatch.Frequency) == null &&
            !sweep.Active,
            "Closing or retargeting a wreck left Collect All issuing requests.");
        Assert(sweep.Begin(77, 77, 77, full, null, now) == 0 &&
            sweep.Observe(77, 77, full, null, false,
                now + 5 * Stopwatch.Frequency) == null && !sweep.Active,
            "Unacknowledged pickup retried instead of stopping after server rejection.");
    }

    /// <summary>Preserve explicit free flight while refreshing sector destinations.</summary>
    private static void CheckNavigationSelection()
    {
        var selection = new NavigationSelection();
        uint[] visible = { 11, 12 };
        Assert(selection.Resolve(visible, 11) == 11 && !selection.FreeFlight,
            "A sector did not choose its nearest initial destination.");
        selection.ChooseFreeFlight();
        Assert(selection.Resolve(visible, 11) == 0 && selection.FreeFlight,
            "A populated sector replaced explicit free flight with an automatic destination.");
        selection.ChooseTarget(12);
        Assert(selection.Resolve(visible, 11) == 12 && !selection.FreeFlight,
            "Choosing a waypoint did not leave free-flight mode.");
        Assert(selection.Resolve(new uint[] { 11 }, 11) == 11,
            "A departed waypoint did not fall back to the nearest destination.");
        selection.ChooseFreeFlight();
        selection.Reset();
        Assert(selection.Resolve(visible, 12) == 12 && !selection.FreeFlight,
            "Free flight leaked into the next sector.");
    }

    /// <summary>Accept native resource, wreck, and MOB warp IDs while excluding pilots.</summary>
    private static void CheckWarpTargets()
    {
        var world = new World();
        byte[] objectBody(uint id, byte kind, float x, bool navigable)
        {
            var body = new byte[63];
            Array.Copy(BitConverter.GetBytes(id), 0, body, 0, 4);
            body[4] = kind;
            Array.Copy(BitConverter.GetBytes(1f), 0, body, 7, 4);
            Array.Copy(BitConverter.GetBytes(x), 0, body, 25, 4);
            Array.Copy(BitConverter.GetBytes(1f), 0, body, 49, 4);
            body[57] = navigable ? (byte)0x10 : (byte)0;
            body[58] = 3;
            Array.Copy(System.Text.Encoding.ASCII.GetBytes("Obj"), 0, body, 60, 3);
            return body;
        }
        world.Apply(new Frame(0x2018, objectBody(77, 38, 4500, false)), 1);
        world.Apply(new Frame(0x2018, objectBody(78, 25, 4500, false)), 1);
        world.Apply(new Frame(0x2018, objectBody(79, 11, 4500, true)), 1);
        world.Apply(new Frame(0x2018, objectBody(80, 38, 21000, false)), 1);
        world.Mobs[81] = new ShipActor(81, 0, 1, "Mob", new Position(4500, 0, 0),
            new[] { 0f, 0f, 0f, 1f }, true, false, true);
        world.Players[82] = new ShipActor(82, 0, 1, "Pilot", new Position(4500, 0, 0),
            new[] { 0f, 0f, 0f, 1f }, true);
        Assert(WarpTargets.Available(world, 77) && WarpTargets.Available(world, 78) &&
            WarpTargets.Available(world, 79) && WarpTargets.Available(world, 81),
            "A supported native object or MOB route was rejected.");
        Assert(!WarpTargets.Available(world, 80) && !WarpTargets.Available(world, 82),
            "Out-of-range resources or pilot ships were accepted as warp routes.");
        var selection = new NavigationSelection();
        selection.ChooseTarget(77);
        Assert(selection.Resolve(new uint[] { 77, 79, 81 }, 79) == 77,
            "An explicitly selected non-nav world object was replaced by the default navpoint.");
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

    /// <summary>Separate armed intent and approximate guidance from actual server shots.</summary>
    private static void CheckCombatEngagement()
    {
        const uint mobId = 0x10000001;
        var world = new World();
        world.ItemCatalog[10] = new ItemTemplateInfo(10, 14, "Pulse Laser", "");
        world.ItemCatalog[11] = new ItemTemplateInfo(11, 15, "Missile Rack", "");
        world.Equipment.WeaponMounts = 2;
        world.Equipment.TargetId = mobId;
        world.Equipment.Mounted[3] = new ItemState
            { Template = 10, Quantity = 1, Range = 900, WeaponState = 0x2001 };
        world.Equipment.Mounted[4] = new ItemState
            { Template = 11, Quantity = 1, Range = 1800, WeaponState = 0x2009 };
        byte[] movement = new byte[38];
        Array.Copy(BitConverter.GetBytes(1u), 0, movement, 2, 4);
        Array.Copy(BitConverter.GetBytes(1f), 0, movement, 34, 4);
        world.Apply(new Frame(0x3e, movement), 1);
        world.Mobs[mobId] = new ShipActor(mobId, 0, 1, "Range target",
            new Position(1630, 0, 0), new[] { 0f, 0f, 0f, 1f }, true,
            false, true);
        Snapshot armed = new Snapshot(world, "ready", true);
        CombatEngagement engagement = armed.Engagement;
        Assert(engagement != null && engagement.TargetId == mobId &&
            engagement.CenterDistanceMeters == 1630 && engagement.Weapons.Length == 2 &&
            engagement.Weapons[0].Armed && engagement.Weapons[0].BeyondListedRange &&
            !engagement.Weapons[0].OutsideFiringArc && engagement.Weapons[1].OutOfAmmo &&
            !engagement.Weapons[1].OutsideFiringArc && engagement.LastShot == null,
            "Armed target guidance was confused with a server-confirmed shot.");
        byte[] shot = Wire.Body(writer =>
        {
            writer.Write((ushort)7); writer.Write(1u); writer.Write(mobId);
            writer.Write((ushort)44); writer.Write((byte)0); writer.Write(9u);
            writer.Write(1000u); writer.Write((ushort)500);
        });
        world.Apply(new Frame(0x0b, shot), 1);
        Snapshot launched = new Snapshot(world, "ready", true);
        Assert(launched.Engagement.LastShot != null &&
            launched.Engagement.LastShotAgeSeconds >= 0 &&
            launched.Engagement.LastShotAgeSeconds < 1 && armed.Engagement.LastShot == null,
            "Native 0x0b launch did not distinguish an actual shot from old armed intent.");
        byte[] miss = Wire.Body(writer =>
        {
            writer.Write(0f); writer.Write(0f); writer.Write(3u); writer.Write(0u);
            writer.Write(1u); writer.Write(mobId);
        });
        world.Apply(new Frame(0x64, miss), 1);
        Snapshot missed = new Snapshot(world, "ready", true);
        Assert(missed.Engagement.LastResult?.Outcome == DamageOutcome.Miss &&
            missed.Engagement.LastResultAgeSeconds < 1 && launched.Engagement.LastResult == null,
            "Native miss result did not reach engagement feedback.");
        world.Mobs[mobId] = new ShipActor(mobId, 0, 1, "Behind",
            new Position(-300, 0, 0), new[] { 0f, 0f, 0f, 1f }, true,
            false, true);
        Snapshot behind = new Snapshot(world, "ready", true);
        Assert(behind.Engagement.Weapons[0].OutsideFiringArc &&
            !behind.Engagement.Weapons[1].OutsideFiringArc,
            "Directional weapon arc guidance disagreed with the server's 40-degree cone or missile exception.");
    }

    /// <summary>Use native launch asset and mounted/ammo metadata for traveling rounds.</summary>
    private static void CheckShotPresentation()
    {
        ItemTemplateInfo nativeBeam = ItemTemplateInfo.Decode(FixtureBytes("weapon769.hex"));
        ItemTemplateInfo nativeRounds = ItemTemplateInfo.Decode(FixtureBytes("ammo507.hex"));
        Assert(nativeBeam.Id == 769 && nativeBeam.Type == 14 &&
            nativeBeam.Name == "Pulse Laser" && nativeBeam.UseEffect == 35 &&
            nativeRounds.Id == 507 && nativeRounds.Type == 10 &&
            nativeRounds.Name == "Dragonclaw Missile" && nativeRounds.UseEffect == 886,
            "Native 0x25 weapon or ammunition effect asset was decoded at the wrong offset.");
        var catalog = new System.Collections.Generic.Dictionary<uint, ItemTemplateInfo>
        {
            [10] = new ItemTemplateInfo(10, 14, "Beam", "", useEffect: 35),
            [11] = new ItemTemplateInfo(11, 15, "Missile launcher", "", useEffect: 36),
            [12] = new ItemTemplateInfo(12, 16, "Projectile launcher", "", useEffect: 37),
            [13] = new ItemTemplateInfo(13, 10, "Missile ammunition", "", useEffect: 886),
            [14] = new ItemTemplateInfo(14, 10, "Projectile ammunition", "", useEffect: 887)
        };
        var mounted = new ItemView[20]; var ammo = new ItemView[20];
        mounted[3] = new ItemView(3, new ItemState { Template = 10 }, catalog);
        mounted[4] = new ItemView(4, new ItemState { Template = 11 }, catalog);
        mounted[5] = new ItemView(5, new ItemState { Template = 12 }, catalog);
        ammo[4] = new ItemView(4, new ItemState { Template = 13 }, catalog);
        ammo[5] = new ItemView(5, new ItemState { Template = 14 }, catalog);
        var beam = new ShotEvent(1, 1, 2, 35, 500);
        var missile = new ShotEvent(2, 1, 2, 886, 1800);
        var projectile = new ShotEvent(3, 1, 2, 887, 900);
        Assert(ShotPresentation.Classify(beam, 1, mounted, ammo) == ShotTravelKind.Beam &&
            ShotPresentation.Classify(missile, 1, mounted, ammo) == ShotTravelKind.Missile &&
            ShotPresentation.Classify(projectile, 1, mounted, ammo) == ShotTravelKind.Projectile,
            "Native shot assets did not resolve their beam, missile and projectile mounts.");
        Assert(ShotPresentation.Classify(new ShotEvent(4, 9, 1, 999, 1000), 1,
                mounted, ammo) == ShotTravelKind.Beam &&
            ShotPresentation.Classify(new ShotEvent(5, 9, 1, 999, 1500), 1,
                mounted, ammo) == ShotTravelKind.Projectile,
            "Unknown peer effects should preserve the native NPC beam duration boundary.");
        byte[] distant = Wire.Body(writer =>
        {
            writer.Write((ushort)7); writer.Write(1u); writer.Write(2u);
            writer.Write((ushort)886); writer.Write((byte)0); writer.Write(7u);
            writer.Write(1000u); writer.Write((ushort)12000);
        });
        var world = new World();
        world.Apply(new Frame(0x0b, distant), 1);
        Assert(world.Shots.Last().Duration == 12000,
            "A valid long-range native missile was discarded before presentation.");
    }

    /// <summary>Reconcile local clicks with a target shared by the server.</summary>
    private static void CheckContactSelection()
    {
        var selection = new ContactSelection();
        uint[] visible = { 10, 20 };
        Assert(selection.Observe(10, visible, 0) == ContactSelectionChange.None &&
            selection.SelectedId == 10, "Initial authoritative actor target was hidden.");
        selection.SelectLocal(20, 1);
        selection.Observe(10, visible, 1.1);
        Assert(selection.SelectedId == 20, "An old snapshot erased a pending local click.");
        Assert(selection.Observe(20, visible, 1.2) == ContactSelectionChange.LocalAcknowledged &&
            selection.SelectedId == 20, "A local target acknowledgement looked like a group share.");
        Assert(selection.Observe(10, visible, 2) == ContactSelectionChange.ServerSelected &&
            selection.SelectedId == 10, "A server-shared target did not replace the stale HUD contact.");
        selection.ClearLocal();
        selection.Observe(10, visible, 2.1);
        Assert(selection.SelectedId == 0, "An unchanged server target reopened a dismissed panel.");
        selection.SelectLocal(20, 3);
        selection.Observe(10, visible, 6.1);
        Assert(selection.SelectedId == 10, "An unacknowledged local target stayed selected indefinitely.");
        selection.Observe(10, new uint[] { 20 }, 6.2);
        Assert(selection.SelectedId == 0, "A departed server target stayed selected.");
        selection.Reset();
        selection.Observe(20, visible, 7);
        Assert(selection.SelectedId == 20, "A new sector retained previous target history.");
    }

    /// <summary>Decode bounded native verb lists and retain immutable availability revisions.</summary>
    private static void CheckTargetVerbs()
    {
        const string fixture = "0000004d0000000202000a00020008000000000200000a0002000800";
        byte[] body = Enumerable.Range(0, fixture.Length / 2)
            .Select(index => Convert.ToByte(fixture.Substring(index * 2, 2), 16)).ToArray();
        var world = new World();
        world.Apply(new Frame(0x5c, body), 1);
        Snapshot first = new Snapshot(world, "ready", true);
        Assert(first.Verbs.TargetId == 77 && first.Verbs.Available(77, 10) == true &&
            first.Verbs.Available(77, 8) == false && first.Verbs.Available(77, 9) == null &&
            first.Verbs.Available(78, 10) == null,
            "Native gate and dock verb availability was decoded incorrectly.");
        byte[] changed = (byte[])body.Clone();
        changed[20] = 2;
        world.Apply(new Frame(0x5c, changed), 1);
        Assert(first.Verbs.Available(77, 10) == true && world.Verbs.Available(77, 10) == false,
            "A newer verb revision mutated an older snapshot.");
        Reject(() => TargetVerbs.Decode(body.Take(body.Length - 1).ToArray()));
        byte[] excessive = (byte[])body.Clone();
        excessive[7] = 65;
        Reject(() => TargetVerbs.Decode(excessive));
    }

    /// <summary>Decode the server's native buff slot and matching visual lifecycle.</summary>
    private static void CheckNativeDeviceBuff()
    {
        byte[] bytes(string hex) => Enumerable.Range(0, hex.Length / 2)
            .Select(index => Convert.ToByte(hex.Substring(index * 2, 2), 16)).ToArray();
        const uint player = 0x40000006;
        var world = new World();
        world.Apply(new Frame(0x1b, bytes(
            "060000402200010200000000400000120000d2000d00547572626f20576561706f6e7300da2c0000")), player);
        Assert(world.ActiveBuff.Name == "Turbo Weapons" && world.ActiveBuff.RemovalTick == 11482 &&
            world.ActiveDeviceLabel == "Turbo Weapons",
            "Native ship AUX field42 did not publish the Turbo Weapons buff icon name and removal tick.");
        world.Apply(new Frame(0x09, bytes("03060000405c028e920930ca050000")), player);
        Snapshot active = new Snapshot(world, "ready", true);
        Assert(active.DeviceBuff != null && active.DeviceBuff.Name == "Turbo Weapons" &&
            active.DeviceBuff.VisualActive && active.DeviceBuff.VisualDescriptor == 604 &&
            active.DeviceBuff.VisualEffectId == 0x3009928e,
            "Native ObjectEffect was not associated with the timed device buff.");
        Assert(world.ActiveBuff.RemainingSeconds(1482) == 10 &&
            world.ActiveBuff.RemainingSeconds(12482) == 0 &&
            new TimedDeviceBuff("Turbo Weapons", 200, 0, 0).RemainingSeconds(uint.MaxValue - 799) == 1,
            "Wrapping server tick did not drive a bounded buff countdown.");
        world.Apply(new Frame(0x1b, bytes(
            "060000401500010200000000400000120000d20000000000000000")), player);
        world.Apply(new Frame(0x0f, bytes("8e920930")), player);
        Assert(new Snapshot(world, "ready", true).DeviceBuff == null &&
            active.DeviceBuff.VisualActive && active.DeviceBuff.Name == "Turbo Weapons",
            "Native buff expiry mutated a previous immutable snapshot or left the buff active.");
        Reject(() => NativeObjectEffect.Decode(bytes("03060000405c028e920930ca05")));
    }

    /// <summary>Decode peer Scan Boost slot one and its independent native visual lifecycle.</summary>
    private static void CheckPeerScanBuff()
    {
        byte[] bytes(string hex) => Enumerable.Range(0, hex.Length / 2)
            .Select(index => Convert.ToByte(hex.Substring(index * 2, 2), 16)).ToArray();
        const uint player = 0x40000002;
        const string activeAux =
            "020000402800010200000000400000220000d2001300496e637265617365205363616e2052616e67650088130000";
        const string clearAux = "020000401500010200000000400000220000d20000000000000000";
        const string visual = "0302000040ce010300a01c88130000";
        var world = new World();
        world.Apply(new Frame(0x1b, bytes(activeAux)), player);
        Assert(world.PeerBuff.Name == "Increase Scan Range" && world.PeerBuff.RemovalTick == 5000 &&
            !world.ActiveBuff.Active, "Peer Scan Boost must occupy buff slot one independently.");
        world.Apply(new Frame(0x09, bytes(visual)), player);
        Snapshot first = new Snapshot(world, "ready", true);
        Assert(first.PeerBuff != null && first.PeerBuff.VisualActive &&
            first.PeerBuff.VisualDescriptor == 462 && first.PeerBuff.VisualEffectId == 0x1ca00003 &&
            first.DeviceBuff == null, "Recipient ObjectEffect462 was not matched to buff slot one.");
        world.Apply(new Frame(0x1b, bytes(clearAux)), player);
        world.Apply(new Frame(0x0f, bytes("0300a01c")), player);
        Assert(new Snapshot(world, "ready", true).PeerBuff == null && first.PeerBuff.VisualActive,
            "Peer buff expiry must clear current state without mutating prior snapshots.");
        world.Apply(new Frame(0x1b, bytes(activeAux)), player);
        world.Apply(new Frame(0x09, bytes(visual)), player);
        Assert(new Snapshot(world, "ready", true).PeerBuff?.VisualEffectId == 0x1ca00003,
            "Server replay did not restore the recipient Scan Boost visual.");
        var combined = new World();
        combined.Apply(new Frame(0x1b, bytes(
            "020000402200010200000000400000120000d2000d00547572626f20576561706f6e7300da2c0000")), player);
        combined.Apply(new Frame(0x1b, bytes(activeAux)), player);
        combined.Apply(new Frame(0x09, bytes(visual)), player);
        Assert(combined.ActiveBuff.VisualDescriptor == 0 && combined.PeerBuff.VisualDescriptor == 462,
            "A recipient visual was incorrectly assigned to the sender's own active buff.");
        combined.Apply(new Frame(0x09, bytes("03020000405c028e920930ca050000")), player);
        Assert(new Snapshot(combined, "ready", true).DeviceBuff?.VisualDescriptor == 604 &&
            new Snapshot(combined, "ready", true).PeerBuff?.VisualDescriptor == 462,
            "Concurrent native self and peer buff visuals did not remain independent.");
    }

    /// <summary>Decode exact server slot-two/three frames without replacing another recipient buff.</summary>
    private static void CheckConcurrentRecipientBuffs()
    {
        byte[] Bytes(string hex) => Enumerable.Range(0, hex.Length / 2)
            .Select(index => Convert.ToByte(hex.Substring(index * 2, 2), 16)).ToArray();
        Frame Fixture(string hex)
        {
            byte[] frame = Bytes(hex);
            Assert(Wire.U16(frame, 0) == frame.Length && Wire.U16(frame, 2) == 0x1b,
                "Server recipient-buff fixture framing changed.");
            return new Frame(0x1b, frame.Skip(4).ToArray());
        }
        const uint player = 0x40000002;
        var world = new World();
        world.Apply(new Frame(0x1b, Bytes(
            "020000402800010200000000400000220000d2001300496e637265617365205363616e2052616e67650088130000")), player);
        world.Apply(Fixture("30001b00020000402600010200000000400000420000d2001100526567656e657261746520536869656c640040e20100"), player);
        world.Apply(Fixture("30001b00020000402600010200000000400000820000d2001100526567656e657261746520456e657267790040e20100"), player);
        Assert(world.PeerBuff.Name == "Increase Scan Range" &&
            world.ShieldBuff.Name == "Regenerate Shield" && world.ReactorBuff.Name == "Regenerate Energy" &&
            world.ShieldBuff.RemovalTick == 123456 && world.ReactorBuff.RemovalTick == 123456,
            "Native recipient slots one through three were not decoded independently.");
        NativeObjectEffect serverVisual = NativeObjectEffect.Decode(Bytes(
            "030200001029020300a04c88130000"));
        Assert(serverVisual.OwnerId == 0x10000002 && serverVisual.Descriptor == 553 &&
            serverVisual.EffectId == 0x4ca00003 && serverVisual.Tick == 5000,
            "Server-encoded recipient recharge ObjectEffect fixture changed.");
        byte[] Visual(uint effectId) => Wire.Body(writer =>
        {
            writer.Write((byte)3); writer.Write(player); writer.Write((ushort)553);
            writer.Write(effectId); writer.Write(120000u);
        });
        world.Apply(new Frame(0x09, Visual(0xA0000001)), player);
        world.Apply(new Frame(0x09, Visual(0xA0000002)), player);
        Snapshot active = new Snapshot(world, "ready", true);
        Assert(active.PeerBuff != null && active.ShieldBuff?.VisualEffectId == 0xA0000001 &&
            active.ReactorBuff?.VisualEffectId == 0xA0000002 &&
            active.ShieldBuff.VisualDescriptor == 553 && active.ReactorBuff.VisualDescriptor == 553,
            "Equal visual descriptors were not matched to the correct native buff slots.");
        world.Apply(Fixture("1f001b00020000401500010200000000400000420000d20000000000000000"), player);
        world.Apply(new Frame(0x0f, Bytes("010000a0")), player);
        Snapshot ended = new Snapshot(world, "ready", true);
        Assert(ended.ShieldBuff == null && ended.ReactorBuff?.VisualEffectId == 0xA0000002 &&
            ended.PeerBuff?.Name == "Increase Scan Range" && active.ShieldBuff.VisualActive,
            "Shield expiry removed another recipient buff or mutated an old immutable snapshot.");
        world.Apply(Fixture("1f001b00020000401500010200000000400000820000d20000000000000000"), player);
        world.Apply(new Frame(0x0f, Bytes("020000a0")), player);
        Assert(new Snapshot(world, "ready", true).ReactorBuff == null &&
            new Snapshot(world, "ready", true).PeerBuff != null,
            "Reactor expiry removed the still-active recipient scan buff.");
        world.Apply(new Frame(0x1b, Bytes(
            "020000402500010200000000400000020100d2001000526564756365205369676e61747572650088130000")), player);
        Snapshot signature = new Snapshot(world, "ready", true);
        Assert(signature.SignatureBuff?.Name == "Reduce Signature" &&
            world.SignatureBuff.RemovalTick == 5000 &&
            !signature.SignatureBuff.VisualActive && signature.PeerBuff != null,
            "Native slot-four signature buff gained a nonexistent ObjectEffect or replaced Scan Boost.");
        world.Apply(new Frame(0x1b, Bytes(
            "020000401500010200000000400000020100d20000000000000000")), player);
        Assert(new Snapshot(world, "ready", true).SignatureBuff == null &&
            signature.SignatureBuff.Name == "Reduce Signature" &&
            new Snapshot(world, "ready", true).PeerBuff != null,
            "Signature expiry altered another recipient slot or an immutable older snapshot.");
    }

    /// <summary>Retain server-created support visuals until their native removal packet.</summary>
    private static void CheckSupportObjectEffects()
    {
        var world = new World();
        world.Apply(new Frame(0x09, Wire.Body(writer =>
        {
            writer.Write((byte)3); writer.Write(2u); writer.Write((ushort)216);
            writer.Write(99u); writer.Write(5000u);
        })), 1);
        Snapshot active = new Snapshot(world, "ready", true);
        Assert(active.ObjectEffects.Length == 1 && active.ObjectEffects[0].OwnerId == 2 &&
            active.ObjectEffects[0].EffectId == 99 && active.ObjectEffects[0].Descriptor == 216,
            "Native group support effect did not identify the recipient and visual descriptor.");
        world.Apply(new Frame(0x0f, Wire.Body(writer => writer.Write(99u))), 1);
        Assert(new Snapshot(world, "ready", true).ObjectEffects.Length == 0 &&
            active.ObjectEffects.Length == 1,
            "Effect removal mutated an earlier immutable support snapshot.");
    }

    /// <summary>Recognize catalog-backed active devices and their server notices.</summary>
    private static void CheckDeviceEffects()
    {
        foreach (string description in new[]
        {
            "Recharge Reactor", "Recharge Shields", "Deflect Impact", "Deflect Explosive",
            "Deflect Plasma", "Deflect Energy", "Deflect EMP", "Deflect Chemical", "Resist Psionic",
            "Turbo Warp", "Improved Beam Handling", "Improved Projectile Handling", "Shunt to Reactor",
            "Increase Maneuverability", "Maximize Shields", "Reduce Ship Signature"
        })
            Assert(DeviceEffects.LabelFor(description + " (Activated)") == description,
                "A server-supported active device is missing from the catalog allowlist.");
        Assert(DeviceEffects.LabelFor("Turbo (Activated)") == "Impulse Turbo" &&
            DeviceEffects.LabelFor("Reroute Shield Energy (Instant)") == "Reroute Shield Energy" &&
            DeviceEffects.LabelFor("Transfer Reactor Energy (Instant)") == "Transfer Reactor Energy",
            "Native instant and impulse catalog descriptions lost their UI labels.");
        foreach (string ally in new[] { "Scan Boost", "Recharge Shields",
                     "Recharge Reactor", "Reduce Ship Signature" })
            Assert(DeviceEffects.RequiresPeer(ally, true) && !DeviceEffects.RequiresPeer(ally),
                "A targeted-friendly device bypassed the selected crew recipient check.");
        Assert(DeviceEffects.MountedSlot("Transfer Reactor Energy", 11, 14, 6) &&
            DeviceEffects.MountedSlot("Reroute Shield Energy", 7, 1, 6) &&
            DeviceEffects.MountedSlot("Increase Maneuverability", 2, 0, 6) &&
            DeviceEffects.MountedSlot("Increase Maneuverability", 6, 2, 6) &&
            DeviceEffects.MountedSlot("Impulse Turbo", 7, 1, 6) &&
            !DeviceEffects.MountedSlot("Turbo Weapons", 7, 1, 6) &&
            !DeviceEffects.MountedSlot("Transfer Reactor Energy", 11, 15, 6),
            "Activation mount rules diverged from server-supported core and device slots.");
        Assert(DeviceEffects.IsInstant("Reroute Shield Energy") &&
            DeviceEffects.IsInstant("Transfer Reactor Energy") &&
            DeviceEffects.RequiresPeer("Transfer Reactor Energy") &&
            !DeviceEffects.RequiresPeer("Reroute Shield Energy") &&
            !DeviceEffects.IsInstant("Turbo Weapons"),
            "Instant and selected-peer effects need distinct control gates.");
        Assert(DeviceEffects.LabelFor("Unknown (Activated)") == "",
            "An unsupported device was offered as activatable.");
        const string fixture =
            "de012500000007120b0000006e0000000b000000070300000003000000070000000000000009000000000000000b0000000c004465766963652054656368000d00000014004c6576656c20312044657669636520546563680011000000000000001200000004000000000000011800526567656e657261746520456e65726779204974656d20431c0052656368617267652052656163746f7220284163746976617465642945002b342052656163746f7220526563686172676520666f72202576616c7565322e306625207365636f6e647320746f20746172676574207768656e206163746976617465642e000000000000000343700000437000000000000040000000010000000000000000000000a00f000000000000000000000225028b0001000007fe0000000100000000000000001700466f726e6178204175737472616c6973204920524520318800446973636f766572207468652066757475726521202053756e646172692e5c6e5768656e20616374697661746564206f6e206120746172676574656420667269656e646c792074686973206974656d20676976657320616e20696e63726561736520746f2074617267657427732072656163746f7220726567656e65726174696f6e20726174652e070053756e64617269";
        byte[] packet = Enumerable.Range(0, fixture.Length / 2)
            .Select(index => Convert.ToByte(fixture.Substring(index * 2, 2), 16)).ToArray();
        Assert(packet.Length == 478 && Wire.U16(packet, 0) == 478 && Wire.U16(packet, 2) == 0x25,
            "Native recharge-device metadata fixture is malformed.");
        ItemTemplateInfo template = ItemTemplateInfo.Decode(packet.Skip(4).ToArray());
        Assert(template.Id == 1810 && template.Type == 11 &&
            template.ActivationLabel == "Recharge Reactor" && template.TargetsPeer,
            "Native recharge device metadata was not exposed to the equipped-device control.");
        ItemTemplateInfo peerScan = ItemTemplateInfo.Decode(FixtureBytes("item1813.hex"));
        ItemTemplateInfo reactor = ItemTemplateInfo.Decode(FixtureBytes("item3337.hex"));
        Assert(peerScan.Id == 1813 && peerScan.Type == 11 && peerScan.TargetsPeer &&
            peerScan.ActivationLabel == "Scan Boost" &&
            DeviceEffects.RequiresPeer(peerScan.ActivationLabel, peerScan.TargetsPeer) &&
            reactor.Id == 3337 && reactor.Type == 7 && !reactor.TargetsPeer &&
            reactor.ActivationLabel == "Reroute Shield Energy",
            "Native catalog flags or core effects were lost during item metadata decoding.");
        var world = new World();
        Action<string> notice = message =>
        {
            byte[] text = System.Text.Encoding.UTF8.GetBytes(message + "\0");
            world.Apply(new Frame(0x1d, Wire.Body(writer =>
            { writer.Write((ushort)text.Length); writer.Write((byte)11); writer.Write(text); })), 1);
        };
        notice("Recharge Reactor active for 90 seconds.");
        Snapshot active = new Snapshot(world, "ready", true);
        Assert(active.ActiveDeviceLabel == "Recharge Reactor" &&
            active.ActiveDeviceRemainingSeconds > 89 && active.ActiveDeviceRemainingSeconds <= 90,
            "Recharge activation notice did not establish a bounded active timer.");
        notice("Recharge Reactor is already active.");
        Assert(world.ActiveDeviceLabel == "Recharge Reactor" && world.ActiveDeviceEndsAt != 0,
            "Repeat activation erased the server-acknowledged effect timer.");
        notice("Recharge Reactor ended.");
        Assert(world.ActiveDeviceLabel == "" && world.ActiveDeviceEndsAt == 0 &&
            active.ActiveDeviceLabel == "Recharge Reactor",
            "Recharge expiry changed an earlier immutable snapshot.");
        notice("Deflect Plasma active for 120 seconds.");
        Assert(world.ActiveDeviceLabel == "Deflect Plasma",
            "Typed deflect activation notice was not recognized.");
        notice("Deflect Plasma ended.");
        notice("Shunt to Reactor active for 30 seconds.");
        Assert(world.ActiveDeviceLabel == "Shunt to Reactor" && world.ActiveDeviceEndsAt != 0,
            "Shunt activation notice did not establish an active timer.");
        notice("Shunt to Reactor ended.");
        notice("Impulse Turbo active for 10 seconds.");
        Assert(world.ActiveDeviceLabel == "Impulse Turbo", "Impulse Turbo notice did not publish a timer.");
        notice("Impulse Turbo ended.");
        notice("Transfer Reactor Energy completed.");
        Assert(world.ActiveDeviceLabel == "", "Instant peer support falsely published a timed buff.");
        notice("Recharge Reactor active for seconds.");
        Assert(world.ActiveDeviceLabel == "",
            "Malformed active-device duration created a false effect state.");
    }

    /// <summary>
    /// Returns:
    ///     byte[]: An exact server-encoded item metadata body from the protocol fixtures.
    /// </summary>
    private static byte[] FixtureBytes(string name)
    {
        string hex = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name)).Trim();
        return Enumerable.Range(0, hex.Length / 2)
            .Select(index => Convert.ToByte(hex.Substring(index * 2, 2), 16)).ToArray();
    }

    /// <summary>Move a full ammunition stack while installing single equipment items.</summary>
    private static void CheckAmmunitionTransfer()
    {
        var cargo = new ItemState { Template = 100, Quantity = 120 };
        var ammunition = new ItemTemplateInfo(100, 10, "Test rounds", "");
        var device = new ItemTemplateInfo(101, 11, "Test device", "");
        Assert(cargo.EquipQuantity(ammunition) == 120 && cargo.EquipQuantity(device) == 1,
            "Cargo-to-mount transfer would load only one round or install an entire equipment stack.");
    }

    /// <summary>Decode and rotate the native leader-relative formation offset.</summary>
    private static void CheckFormationAnchor()
    {
        byte[] body = Wire.Body(writer =>
        {
            writer.Write(2u); writer.Write(1u);
            writer.Write(100f); writer.Write(0f); writer.Write(0f);
        });
        FormationAnchor anchor = FormationAnchor.Decode(body, 2);
        FormationAnchor blockWing = FormationAnchor.ForGroupMember(1, "Block", 1);
        Assert(blockWing?.Offset.X == 0 && blockWing.Offset.Y == -200 &&
            FormationAnchor.ForGroupMember(1, "Block", 6) == null,
            "Server-authored group slot did not resolve to the native Block offset.");
        var leader = new ShipActor(1, 0, 1, "Leader", new Position(1000, 20, 3000),
            new[] { 0f, 0f, .70710677f, .70710677f }, true);
        Position position = anchor.Project(leader);
        Assert(Math.Abs(position.X - 1000) < .01f && Math.Abs(position.Y - 120) < .01f &&
            Math.Abs(position.Z - 3000) < .01f,
            "Native formation offset did not rotate with the leader's quaternion.");
        var world = new World();
        world.Apply(new Frame(0x41, body), 2);
        Snapshot first = new Snapshot(world, "ready", true);
        Assert(world.Formation?.LeaderId == 1 && first.Formation?.Offset.X == 100,
            "Native 0x41 formation anchor was dropped.");
        byte[] changed = (byte[])body.Clone();
        Array.Copy(BitConverter.GetBytes(50f), 0, changed, 8, 4);
        world.Apply(new Frame(0x41, changed), 2);
        Assert(world.Formation.Offset.X == 50 && first.Formation.Offset.X == 100,
            "Formation update changed an older immutable snapshot.");
        byte[] invalid = (byte[])body.Clone();
        Array.Copy(BitConverter.GetBytes(float.NaN), 0, invalid, 8, 4);
        Reject(() => FormationAnchor.Decode(invalid, 2));
        Reject(() => FormationAnchor.Decode(body, 3));
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
            session.Connect(host, "warper", "warp");
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
            MapObject ore = session.World.Objects.Values.First(x => x.Kind == 38 &&
                x.Position.Distance(initial) >= 4000 && x.Position.Distance(initial) <= 5000);
            session.SelectTarget(ore.Id);
            Wait(session, () => session.World.Equipment.TargetId == ore.Id, 5,
                "A 4–5 km resource target did not acknowledge native selection.");
            session.Warp(ore.Id);
            Wait(session, () => session.World.Warp.Phase == WarpPhase.Charging ||
                session.World.Warp.Phase == WarpPhase.Traveling, 8,
                "A 4–5 km non-nav object did not start native warp.");
            Wait(session, () => session.World.Position.Distance(ore.Position) < 1300 &&
                Math.Abs(session.World.Speed) < 1, 35,
                "Warp did not approach the selected 4–5 km resource.");
            Wait(session, () => session.World.Warp.Phase == WarpPhase.Idle, 12,
                "Resource warp did not finish recovery before the next route.");
            Console.WriteLine("PASS: non-nav object selection and 4–5 km native warp");
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
            session.Connect(host, "warper", "warp");
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
