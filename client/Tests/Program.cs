using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using FortyTwoAndBeyond.Client;
using FortyTwoAndBeyond.Protocol;

internal static class Program
{
    /// <summary>Run pure protocol checks, with explicit opt-in to a disposable local server.</summary>
    private static void Main(string[] args)
    {
        if (args.Length == 1 && args[0] == "--galaxy-only")
        { GalaxyTravelChecks.Check(); return; }
        if (args.Length == 2 && args[0] == "--galaxy-server")
        { GalaxyTravelChecks.Check(); GalaxyTravelChecks.Live(args[1]); return; }
        if (args.Length == 1 && args[0] == "--transport-only")
        {
            SectorTransportChecks.Check(exerciseLoopback: true);
            Console.WriteLine("PASS: bounded replay, manufacture burst loss/reorder/fragmentation, UDP receive queue, unchanged ACK echo");
            return;
        }
        CheckReassembly(); SectorTransportChecks.Check(exerciseLoopback: false);
        CheckWorld(); CheckMissionJournal(); CheckMissionDialogue();
        CheckExperience(); NativeExperienceProgressChecks.Check(); CheckHullProgression(); CheckReputation(); CheckMiningPhases(); CheckLootTractor(); CheckLootSweep(); CheckWarpPhases(); CheckNavigationSelection(); CheckWarpTargets(); CheckWeaponReady();
        CheckContactCapacities(); CheckDamageResults(); CheckCombatEngagement(); CheckAttackerSelection(); CheckHostileSelection(); CheckSelectableObjects(); CheckShotPresentation(); CheckContactSelection(); CheckTargetVerbs(); CheckDeviceEffects();
        CheckNativeDeviceBuff(); CheckPeerScanBuff(); CheckConcurrentRecipientBuffs();
        CheckAllNativeBuffSlots(); CheckFullActiveDeviceSnapshot(); CheckSupportObjectEffects(); CheckSectorTransit(); CheckShipDefense();
        CheckAmmunitionTransfer(); CheckLauncherAmmo(); CheckVaultStackPlacement(); CheckEquippedEffects(); CheckFlightThrottle(); CheckFormationAnchor(); SectorLivenessChecks.Check();
        CheckNativeChat();
        CheckAbilityTargets(); CheckSkillTraining(); CheckCharacterRoster();
        TradeChecks.Check(); GlobalAuxPrefixChecks.Check(); NativeChatCompatibilityChecks.Check(); NativeShipCompatibilityChecks.Check(); StationContactCompatibilityChecks.Check();
        NativeDialogueCompatibilityChecks.Check(); RemotePoseTimingChecks.Check(); MovementTelemetryChecks.Check(); TimedPercentChecks.Check(); NativeMotionReportChecks.Check();
        NativeWreckCompatibilityChecks.Check();
        NativeLootHandshakeChecks.Check();
        NativePriorityNoticeChecks.Check();
        SectorHandoffPeerChecks.Check(); NativeMountCapacityChecks.Check();
        NativeSkillRankCompatibilityChecks.Check();
        CheckStationLayout(); CheckStationWalk(); CheckDemoRoster(); CheckAddress(); ServerHistoryChecks.Check(); PeerChecks.Decode();
        Console.WriteLine("PASS: framing, ordering, fragmentation, duplicates, bounds, world decoding, server hostnames and IPv4 addresses");
        if (args.Length == 2 && args[0] == "--server") { PeerChecks.Live(args[1]); CheckServer(args[1]); }
        else if (args.Length == 2 && args[0] == "--handoff-server") SectorHandoffPeerChecks.Live(args[1]);
        else if (args.Length == 2 && args[0] == "--area-recharge-server") RechargeAbilityChecks.AreaLive(args[1]);
        else if (args.Length == 2 && args[0] == "--recharge-server") RechargeAbilityChecks.Live(args[1]);
        else if (args.Length == 2 && args[0] == "--support-server") SupportAbilityChecks.Live(args[1]);
        else if (args.Length == 2 && args[0] == "--enemy-ability-server") EnemyAbilityChecks.Live(args[1]);
        else if (args.Length == 2 && args[0] == "--skill-server") SkillTrainingChecks.Live(args[1], false);
        else if (args.Length == 2 && args[0] == "--skill-resume-server") SkillTrainingChecks.Live(args[1], true);
        else if (args.Length == 2 && args[0] == "--roster-server") CharacterRosterChecks.Live(args[1]);
        else if (args.Length == 2 && args[0] == "--space-mission-server") SpaceMissionChecks.Live(args[1]);
        else if (args.Length == 2 && args[0] == "--space-turnin-server") SpaceMissionChecks.TurnIn(args[1]);
        else if (args.Length == 2 && args[0] == "--cleaner-turnin-server") SpaceMissionChecks.CleanerTurnIn(args[1]);
        else if (args.Length == 2 && args[0] == "--te-message-accept-server") SpaceMissionChecks.TeMessageAccept(args[1]);
        else if (args.Length == 2 && args[0] == "--te-message-complete-server") SpaceMissionChecks.TeMessageComplete(args[1]);
        else if (args.Length == 2 && args[0] == "--native-wreck-capture")
            Console.WriteLine("PASS: captured native corpse inventories=" + NativeWreckCompatibilityChecks.CheckCapturedWrecks(args[1]));
        else if (args.Length != 0) throw new ArgumentException("Usage: ProtocolChecks [--server PRIVATE_IP|--area-recharge-server PRIVATE_IP|--recharge-server PRIVATE_IP|--support-server PRIVATE_IP|--enemy-ability-server PRIVATE_IP|--skill-server PRIVATE_IP|--skill-resume-server PRIVATE_IP]");
    }

    /// <summary>Keep server-selected support abilities distinct from self and combat casts.</summary>
    private static void CheckAbilityTargets()
    {
        foreach (uint id in new uint[] { 9, 14, 60, 61, 31, 32, 44, 45, 48, 68, 89, 95, 125, 128,
            122, 130, 74 })
            Assert(AbilityTargets.Directed(id), "Directed support ability lost target acknowledgment.");
        Assert(AbilityTargets.CrewOnly(44) && AbilityTargets.CrewOnly(125) &&
            !AbilityTargets.CrewOnly(89) && !AbilityTargets.CrewOnly(95) &&
            !AbilityTargets.CrewOnly(14) && !AbilityTargets.CrewOnly(61) &&
            !AbilityTargets.CrewOnly(122) &&
            AbilityTargets.RequiresPeer(125) && !AbilityTargets.RequiresPeer(44) &&
            !AbilityTargets.Directed(77) && !AbilityTargets.Directed(91) &&
            !AbilityTargets.Directed(124),
            "Ability recipient policy mismatches the native server contract.");
        foreach (uint id in new uint[] { 33, 52, 53, 106, 108, 109, 124,
            112, 114, 54, 98, 55, 111, 113, 105, 107, 110 })
            Assert(AbilityTargets.Enemy(id) && !AbilityTargets.Directed(id),
                "Enemy ability lost its selected-MOB requirement.");
        Assert(!AbilityTargets.Enemy(32) && !AbilityTargets.Enemy(125),
            "Support ability was classified as hostile targeting.");
    }

    /// <summary>Decode server-encoded 0x57 before/after AUX without mutating prior snapshots.</summary>
    private static void CheckSkillTraining()
    {
        var world = new World();
        world.Apply(new Frame(0x1b, FixtureBytes("je-beam-training-before.hex")), 0x40000001);
        Snapshot before = new Snapshot(world, "ready", true);
        SkillRank first = before.Skills.FirstOrDefault(skill => skill.Id == 1);
        Assert(first != null && first.Rank == 1 && first.Cost == 1 && first.CanTrain &&
            before.Experience?.AvailableSkillPoints == 1,
            "Native JE Beam rank or spendable point was not decoded.");
        world.Apply(new Frame(0x1b, FixtureBytes("je-beam-training-after.hex")), 0x40000001);
        Snapshot after = new Snapshot(world, "ready", true);
        Assert(after.Skills.First(skill => skill.Id == 1).Rank == 2 &&
            after.Experience?.AvailableSkillPoints == 0 &&
            before.Skills.First(skill => skill.Id == 1).Rank == 1 &&
            before.Experience.AvailableSkillPoints == 1,
            "Training AUX lost rank/point update or changed an earlier immutable snapshot.");
    }

    /// <summary>Decode a server-encoded level-one native progression roster.</summary>
    private static void CheckCharacterRoster()
    {
        byte[] body = FixtureBytes("je-roster-progression.hex");
        CharacterInfo[] characters = CharacterInfo.DecodeRoster(body);
        Assert(characters.Length == 1 && characters[0].Slot == 0 &&
            characters[0].ClassName == "Jenquai Explorer" &&
            characters[0].CombatLevel == 1 && characters[0].TradeLevel == 0 &&
            characters[0].ExploreLevel == 0 && characters[0].SectorId == 1076 &&
            characters[0].SectorName == "Asteroid Belt Alpha",
            "Native roster lost class, earned levels, or starting sector.");
        byte[] invalidClass = (byte[])body.Clone(); invalidClass[179] = 3;
        Reject(() => CharacterInfo.DecodeRoster(invalidClass));
        Reject(() => CharacterInfo.DecodeRoster(new byte[20]));
        byte[] multiple = (byte[])body.Clone();
        Buffer.BlockCopy(body, 0, multiple, 4 * 374, 374);
        byte[] secondName = Encoding.ASCII.GetBytes("SecondPilot\0");
        Buffer.BlockCopy(secondName, 0, multiple, 4 * 374 + 133, secondName.Length);
        CharacterInfo[] sparse = CharacterInfo.DecodeRoster(multiple);
        Assert(sparse.Length == 2 && sparse[0].Slot == 0 && sparse[1].Slot == 4 && sparse[1].Name == "SecondPilot",
            "Roster treated the first pilot's galaxy ID as a count or lost a pilot after empty slots.");
        Array.Clear(multiple, 0, 374);
        CharacterInfo[] lastOnly = CharacterInfo.DecodeRoster(multiple);
        Assert(lastOnly.Length == 1 && lastOnly[0].Slot == 4,
            "An empty first slot hid a later pilot or changed its selection slot.");
        byte[] allSlots = (byte[])body.Clone();
        for (int slot = 1; slot < 5; slot++) Buffer.BlockCopy(body, 0, allSlots, slot * 374, 374);
        Assert(CharacterInfo.DecodeRoster(allSlots).Select(pilot => pilot.Slot).SequenceEqual(new[] { 0, 1, 2, 3, 4 }),
            "A full five-pilot roster was truncated.");
    }

    /// <summary>Keep native contact capacities through partial AUX updates and immutable pose/name revisions.</summary>
    private static void CheckContactCapacities()
    {
        var actor=new ShipActor(77,1,1,"Mob",default,new float[]{0,0,0,1},false,isPlayer:false);
        var mask=new byte[8];mask[0]=2;
        foreach(int field in new[]{0,1,6,7,8,9,26})mask[(field+4)/8]|=(byte)(1<<((field+4)%8));
        byte[] percent=new byte[]{0x42,0x00,0x00,0x00,0x3f}; // nested field 2 = 0.5f
        // AUX percent mask has seven bits (four header, three fields).
        byte[] peerName=new byte[]{4,0,(byte)'P',(byte)'e',(byte)'e',(byte)'r'};
        byte[] record=mask.Concat(peerName).Concat(peerName).Concat(percent).Concat(BitConverter.GetBytes(100000f))
            .Concat(BitConverter.GetBytes(80000f)).Concat(BitConverter.GetBytes(120000f)).Concat(BitConverter.GetBytes(50u)).ToArray();
        byte[] body=BitConverter.GetBytes(77u).Concat(BitConverter.GetBytes((ushort)(record.Length+1)))
            .Concat(new byte[]{1}).Concat(record).ToArray();
        var world=new World();world.Mobs[77]=actor;world.Apply(new Frame(0x1b,body),1);
        ShipActor decoded=world.Mobs[77];
        Assert(decoded.Name=="Peer" && decoded.MaxShield==100000 && decoded.Shield==.5f && decoded.MaxHull==120000 && decoded.CombatLevel==50,
            "Contact shield capacity or fraction was lost.");
        var moved=decoded.WithFormationPose(new Position(1,2,3),new float[]{0,0,0,1}).WithName("Renamed");
        Assert(moved.MaxShield==100000 && moved.Shield*moved.MaxShield==50000 && actor.MaxShield==-1 && moved.CombatLevel==50 && actor.CombatLevel==null,
            "Pose/name revisions lost capacity or mutated an earlier snapshot.");
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
        byte[] navigation = new byte[14]; navigation[0] = 77;
        world.Apply(new Frame(0x99, navigation), 1);
        Assert(!world.Objects[77].Navigable && snapshot.Objects[0].Navigable,
            "Navigation update mutated an earlier snapshot.");
        navigation[9] = 2;
        world.Apply(new Frame(0x99, navigation), 1);
        Assert(world.Objects[77].Navigable, "Discovered navigation was not restored.");
        navigation[0] = 78;
        world.Apply(new Frame(0x99, navigation), 1);
        Assert(world.Objects.Count == 1, "Navigation update invented an unknown object.");
        Reject(() => world.Apply(new Frame(0x99, new byte[13]), 1));
        world.Apply(new Frame(7, new byte[] { 77, 0, 0, 0 }), 1);
        Assert(snapshot.Objects.Length == 1 && snapshot.Objects[0].Kind == 11 && world.Objects.Count == 0, "Snapshot retained mutable world storage.");
        body[58] = 5; Reject(() => new MapObject(body));
        byte[] movement = new byte[38]; movement[2] = 2;
        world.Apply(new Frame(0x3e, movement), 1);
        Assert(!world.HasPosition, "Accepted a different player's movement.");
        movement[2] = 1; world.Apply(new Frame(0x3e, movement), 1);
        Assert(world.HasPosition, "Ignored matching player movement.");
        var poseSnapshot=new Snapshot(world,"pose",true);
        Assert(poseSnapshot.OwnPoseSequence==1&&poseSnapshot.OwnPoseReceivedAt>0,"Own pose has no receipt identity/time.");
        world.Apply(new Frame(0x3e,movement),1);
        Assert(world.OwnPoseSequence==2&&poseSnapshot.OwnPoseSequence==1,"Identical movement packets lost identity or mutated an earlier snapshot.");
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
        byte[] marker = System.Text.Encoding.ASCII.GetBytes("Learn about combat");
        int summaryAt = -1;
        for (int index = 2; index <= missions.Length - marker.Length; index++)
            if (missions.Skip(index).Take(marker.Length).SequenceEqual(marker))
            { summaryAt = index; break; }
        Assert(summaryAt > 2, "Native mission summary fixture was not found.");
        int oldSummaryLength = Wire.U16(missions, summaryAt - 2);
        byte[] longSummary = Enumerable.Repeat((byte)'S', 144).ToArray();
        byte[] expandedMissions = missions.Take(summaryAt - 2)
            .Concat(BitConverter.GetBytes((ushort)longSummary.Length)).Concat(longSummary)
            .Concat(missions.Skip(summaryAt + oldSummaryLength)).ToArray();
        expandedMissions[summaryAt + longSummary.Length] = 0; // Native IsFullyVisible, not acceptance.
        byte[] expandedAux = Wire.Body(writer =>
        {
            writer.Write(0u); writer.Write((ushort)(expandedMissions.Length + 4)); writer.Write((byte)1);
            writer.Write(new byte[] { 2, 0x20, 0 }); writer.Write(expandedMissions);
        });
        var expandedWorld = new World(); expandedWorld.Apply(new Frame(0x1b, expandedAux), 1);
        Assert(expandedWorld.Missions[0]?.Summary.Length == 144 &&
            !expandedWorld.Missions[0].FullyVisible,
            "A production-length, partially revealed accepted mission was rejected.");
        var migratedWorld = new World(2); migratedWorld.InheritCharacterProgress(world);
        Assert(migratedWorld.Missions[0]?.Name == first.Missions[0].Name &&
            !ReferenceEquals(migratedWorld.Missions, world.Missions),
            "A sector handoff lost or shared the accepted mission array.");
        byte[] stageTwoRecord = Wire.Body(writer =>
        {
            writer.Write(new byte[] { 0x12, 0 }); // Mission slot zero only.
            writer.Write(new byte[] { 2, 0, 0x18 }); // Stage number and reached-stage record.
            writer.Write(2u);
            writer.Write(new byte[] { 0x22, 0, 0 }); // Reached stage one only.
            writer.Write((byte)0x12); // Stage description.
            byte[] description = System.Text.Encoding.ASCII.GetBytes("Return to station");
            writer.Write((ushort)description.Length); writer.Write(description);
        });
        byte[] stageTwo = Wire.Body(writer =>
        {
            writer.Write(0u); writer.Write((ushort)(stageTwoRecord.Length + 4)); writer.Write((byte)1);
            writer.Write(new byte[] { 2, 0x20, 0 }); writer.Write(stageTwoRecord);
        });
        migratedWorld.Apply(new Frame(0x1b, stageTwo), 1);
        Assert(migratedWorld.Missions[0]?.Stage == 2 &&
            migratedWorld.Missions[0].Objective == "Return to station" &&
            first.Missions[0].Stage == 1,
            "A partial mission objective update lost prior stages or changed an earlier snapshot.");
        byte[] nativeMissions = (byte[])missions.Clone();
        nativeMissions[2] |= 1; // Populated mission slot retains its nested type bit.
        byte[] typedAux = Wire.Body(writer =>
        {
            writer.Write(0u); writer.Write((ushort)(nativeMissions.Length + 6)); writer.Write((byte)1);
            writer.Write(new byte[] { 6, 0x20, 0, 0x80, 0 }); writer.Write(nativeMissions);
        });
        var nativeWorld = new World(); nativeWorld.Apply(new Frame(0x1b, typedAux), 1);
        Assert(nativeWorld.Missions[0]?.Name == first.Missions[0].Name,
            "Populated native container type bits were mistaken for mission resets.");
        world.Apply(new Frame(0x1b, Wire.Body(writer =>
        {
            writer.Write(0u); writer.Write((ushort)7); writer.Write((byte)1);
            writer.Write(new byte[] { 6, 0, 0, 0x80, 0, 5 });
        })), 1);
        Assert(world.Missions[0] == null && first.Missions[0] != null,
            "Mission journal reset changed an earlier immutable snapshot.");
    }

    /// <summary>Decode the exact native faction reward packets without inventing a login gain.</summary>
    private static void CheckReputation()
    {
        var world = new World();
        world.Apply(new Frame(0x1b, FixtureBytes("sharim_reputation_before.hex")), 1);
        Snapshot before = new Snapshot(world, "ready", true);
        FactionStanding prior = before.Reputation.Standings.Single(row => row.Label == "JW");
        Assert(prior.Value == 5000 && before.RecentReputationChange == null &&
            before.Reputation.Affiliation == "Sharim Traders",
            "Native faction PDA baseline was decoded as a reward.");
        world.Apply(new Frame(0x1b, FixtureBytes("sharim_reputation_after.hex")), 1);
        Snapshot after = new Snapshot(world, "ready", true);
        FactionStanding awarded = after.Reputation.Standings.Single(row => row.Label == "JW");
        Assert(awarded.Value == 5500 && after.RecentReputationChange.Label == "JW" &&
            after.RecentReputationChange.Delta == 500 &&
            after.RecentReputationChange.Value == 5500,
            "Sharim mission faction reward did not publish the authoritative +500 revision.");
        Assert(prior.Value == 5000 && before.RecentReputationChange == null,
            "A later faction revision mutated an earlier snapshot.");
        byte[] malformed = FixtureBytes("sharim_reputation_after.hex");
        Array.Resize(ref malformed, malformed.Length - 1);
        Reject(() => new World().Apply(new Frame(0x1b, malformed), 1));
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
        var space = new World();
        space.Apply(new Frame(0x54, tree), 1);
        Assert(space.Station == null && space.Dialogue != null &&
            space.Dialogue.Choices[0].Node == 2,
            "An authoritative space conversation required a station identity.");
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
        world.Apply(new Frame(0x2018, objectBody(83, 37, 4500, false)), 1);
        world.Mobs[81] = new ShipActor(81, 0, 1, "Mob", new Position(4500, 0, 0),
            new[] { 0f, 0f, 0f, 1f }, true, false, true);
        world.Players[82] = new ShipActor(82, 0, 1, "Pilot", new Position(4500, 0, 0),
            new[] { 0f, 0f, 0f, 1f }, true);
        Assert(WarpTargets.Available(world, 77) && WarpTargets.Available(world, 78) &&
            WarpTargets.Available(world, 79) && WarpTargets.Available(world, 81) &&
            WarpTargets.Available(world, 83),
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
        world.Apply(new Frame(0x1b, phase(2, 3)), 1);
        Assert(world.Warp.Phase == WarpPhase.Recovering, "A valid transitional native warp pair disconnected the session.");
        byte[] partial(bool local, uint value) => Wire.Body(writer =>
        {
            writer.Write(1u); writer.Write((ushort)13); writer.Write((byte)1);
            writer.Write(new byte[] { 2, 0, 0, 0, (byte)(local ? 8 : 16), 0, 0, 0 }); writer.Write(value);
        });
        world.Apply(new Frame(0x1b, partial(false, 1)), 1);
        world.Apply(new Frame(0x1b, partial(true, 2)), 1);
        Assert(world.Warp.Phase == WarpPhase.Charging, "Private-only revision erased the retained global warp phase.");
        world.Apply(new Frame(0x1b, partial(false, 2)), 1);
        Assert(world.Warp.Phase == WarpPhase.Traveling, "Global-only warp revision did not advance the phase.");
        Reject(() => world.Apply(new Frame(0x1b, phase(12, 3)), 1));
        Reject(() => world.Apply(new Frame(0x1b, phase(2, 4)), 1));
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
        movement[0] = 1; // The trailing speed word is present in this native fixture.
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

    /// <summary>Cycle only visible MOBs in the native attacking-pilot list.</summary>
    private static void CheckAttackerSelection()
    {
        const uint nearer = 0x10000001, farther = 0x10000002;
        var world = new World();
        byte[] movement = new byte[38];
        Array.Copy(BitConverter.GetBytes(1u), 0, movement, 2, 4);
        world.Apply(new Frame(0x3e, movement), 1);
        world.Mobs[farther] = new ShipActor(farther, 0, 1, "Far attacker",
            new Position(1300, 0, 0), new[] { 0f, 0f, 0f, 1f }, true, false, true);
        world.Mobs[nearer] = new ShipActor(nearer, 0, 1, "Near attacker",
            new Position(450, 0, 0), new[] { 0f, 0f, 0f, 1f }, true, false, true);
        Func<uint, uint, byte[]> update = (id, action) => Wire.Body(writer =>
        { writer.Write(action); writer.Write((byte)1); writer.Write(id); });
        world.Apply(new Frame(0x8b, update(farther, 1)), 1);
        world.Apply(new Frame(0x8b, update(nearer, 1)), 1);
        Snapshot active = new Snapshot(world, "ready", true);
        Assert(active.Attackers.Count == 2 && ThreatSelection.Next(active, 0) == nearer &&
            ThreatSelection.Next(active, nearer) == farther &&
            ThreatSelection.Next(active, farther) == nearer,
            "Confirmed attackers were not cycled by visible range.");
        world.Apply(new Frame(0x8b, update(nearer, 0)), 1);
        Snapshot removed = new Snapshot(world, "ready", true);
        Assert(removed.Attackers.Count == 1 && ThreatSelection.Next(removed, 0) == farther &&
            active.Attackers.Count == 2,
            "Native attacker removal changed an earlier snapshot or target cycle.");
        world.Apply(new Frame(7, BitConverter.GetBytes(farther)), 1);
        Snapshot departed = new Snapshot(world, "ready", true);
        Assert(departed.Attackers.Count == 0 && ThreatSelection.Next(departed, 0) == 0,
            "A departed attacker remained in the threat list or target cycle.");
        Reject(() => world.Apply(new Frame(0x8b, new byte[] { 1, 0, 0, 0, 2, 1, 0, 0, 16 }), 1));
    }

    /// <summary>Separate nearest-hostile selection from stable next-hostile cycling and ignore neutral or unplaced actors.</summary>
    private static void CheckHostileSelection()
    {
        var world = new World();
        byte[] movement = new byte[38];
        Array.Copy(BitConverter.GetBytes(1u), 0, movement, 2, 4);
        world.Apply(new Frame(0x3e, movement), 1);
        foreach (var pair in new[] { (Id: 30u, Range: 1500f), (Id: 20u, Range: 400f), (Id: 10u, Range: 400f) })
            world.Mobs[pair.Id] = new ShipActor(pair.Id, 0, 1, "Enemy", new Position(pair.Range, 0, 0),
                new[] { 0f, 0f, 0f, 1f }, true, false, true);
        world.Mobs[40] = new ShipActor(40, 0, 1, "Neutral", new Position(10, 0, 0), new[] { 0f, 0f, 0f, 1f }, true, false, false);
        world.Mobs[50] = new ShipActor(50, 0, 1, "Unplaced", default, new[] { 0f, 0f, 0f, 1f }, false, false, true);
        var snapshot = new Snapshot(world, "ready", true);
        Assert(ThreatSelection.NearestHostile(snapshot) == 10 && ThreatSelection.NearestHostile(snapshot) == 10,
            "Nearest-hostile selection cycled or targeted a neutral/unplaced actor.");
        Assert(ThreatSelection.NextHostile(snapshot, 10) == 20 && ThreatSelection.NextHostile(snapshot, 20) == 30 &&
            ThreatSelection.NextHostile(snapshot, 30) == 10 && ThreatSelection.NextHostile(snapshot, 99) == 10,
            "Next-hostile selection did not use stable distance order and wrap.");
        world.Mobs.Clear();
        Assert(ThreatSelection.NearestHostile(new Snapshot(world, "ready", true)) == 0 &&
            ThreatSelection.NextHostile(null, 10) == 0 && ThreatSelection.NearestHostile(snapshot) == 10,
            "Empty target lists or immutable snapshots changed hostile selection.");
    }

    /// <summary>Include corpses, resources, players and navigation in nearest/next selection without targeting self or scenery.</summary>
    private static void CheckSelectableObjects()
    {
        var world = new World();
        byte[] movement = new byte[38]; Array.Copy(BitConverter.GetBytes(1u), 0, movement, 2, 4);
        world.Apply(new Frame(0x3e, movement), 1);
        foreach (var item in new[] { (Id: 100u, Kind: (byte)25, Range: 100f),
            (Id: 150u, Kind: (byte)38, Range: 150f), (Id: 400u, Kind: (byte)11, Range: 400f),
            (Id: 50u, Kind: (byte)1, Range: 1f) })
        {
            byte[] body = new byte[64]; Array.Copy(BitConverter.GetBytes(item.Id), body, 4);
            body[4] = item.Kind; Array.Copy(BitConverter.GetBytes(1f), 0, body, 7, 4);
            Array.Copy(BitConverter.GetBytes(item.Range), 0, body, 25, 4);
            Array.Copy(BitConverter.GetBytes(1f), 0, body, 49, 4); body[58] = 4;
            Array.Copy(Encoding.UTF8.GetBytes("Test"), 0, body, 60, 4);
            world.Objects[item.Id] = new MapObject(body);
        }
        world.Players[200] = new ShipActor(200, 0, 1, "Pilot", new Position(200, 0, 0), new[] {0f,0f,0f,1f}, true);
        world.Mobs[300] = new ShipActor(300, 0, 1, "Enemy", new Position(300, 0, 0), new[] {0f,0f,0f,1f}, true, false, true);
        var snapshot = new Snapshot(world, "ready", true);
        Assert(ThreatSelection.NearestSelectable(snapshot) == 100, "Nearest selection excluded a corpse or included scenery.");
        uint[] order = { 100, 150, 200, 300, 400, 100 };
        for (int i = 0; i < order.Length - 1; i++)
            Assert(ThreatSelection.NextSelectable(snapshot, order[i]) == order[i+1], "All-object cycling skipped a selectable category or failed to wrap.");
        Assert(ThreatSelection.NearestSelectable(snapshot, ContactCategoryFilter.NonEnemy) == 100 &&
            ThreatSelection.NextSelectable(snapshot, 200, ContactCategoryFilter.NonEnemy) == 400 &&
            ThreatSelection.NextSelectable(snapshot, 400, ContactCategoryFilter.NonEnemy) == 100,
            "Non-enemy cycling selected a hostile, skipped a navpoint or failed to wrap.");
        world.Objects.Remove(100);
        Assert(ThreatSelection.NearestSelectable(new Snapshot(world, "ready", true)) == 150 &&
            ThreatSelection.NearestSelectable(snapshot) == 100, "Corpse removal changed an old selection snapshot.");
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
        Assert(nativeBeam.Id == 769 && nativeBeam.Type == 14 && nativeBeam.IconId == 787 &&
            nativeBeam.Name == "Pulse Laser" && nativeBeam.UseEffect == 35 &&
            nativeRounds.Id == 507 && nativeRounds.Type == 10 && nativeRounds.IconId == 653 &&
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

    /// <summary>Keep every native skill status, permanent toggle, and timed removal independent.</summary>
    private static void CheckAllNativeBuffSlots()
    {
        const uint player = 0x40000003;
        var world = new World();
        world.Apply(NativeStatusFrame(player, 6, "Cloak", true, 0), player);
        world.Apply(NativeStatusFrame(player, 13, "Shield_Charging", false, 5000), player);
        world.Apply(NativeStatusFrame(player, 15, "Shield_Inversion", false, 8000), player);
        Snapshot active = new Snapshot(world, "ready", true);
        Assert(active.ActiveBuffs.Count == 3 &&
            active.ActiveBuffs.Any(buff => buff.Slot == 6 && buff.Permanent &&
                float.IsPositiveInfinity(buff.RemainingSeconds)) &&
            active.ActiveBuffs.Any(buff => buff.Slot == 13 && buff.Name == "Shield_Charging") &&
            active.ActiveBuffs.Any(buff => buff.Slot == 15 && buff.Name == "Shield_Inversion"),
            "Native slots six through fifteen or indefinite status were discarded.");
        world.Apply(NativeStatusFrame(player, 13, "", false, 0), player);
        Snapshot removed = new Snapshot(world, "ready", true);
        Assert(removed.ActiveBuffs.Count == 2 && active.ActiveBuffs.Count == 3 &&
            !removed.ActiveBuffs.Any(buff => buff.Slot == 13),
            "Clearing one native status changed another slot or an older snapshot.");
        world.Apply(OwnPose(player, 9000), player);
        Snapshot expired = new Snapshot(world, "ready", true);
        Assert(expired.ActiveBuffs.Count == 1 && expired.ActiveBuffs[0].Name == "Cloak",
            "Timed status remained in the display after its authoritative removal tick.");
    }

    /// <summary>Replace, renew, and expire a full server-owned active-device set.</summary>
    private static void CheckFullActiveDeviceSnapshot()
    {
        const uint player = 0x40000004;
        var world = new World();
        world.Apply(OwnPose(player, 1000), player);
        world.Apply(DeviceSnapshot((9, 2265u, 5000u, "Turbo Weapons"),
            (10, 1805u, 6000u, "Recharge Shields")), player);
        Snapshot active = new Snapshot(world, "ready", true);
        Assert(active.HasActiveDeviceSnapshot && active.ActiveDeviceEffects.Count == 2 &&
            active.ActiveDeviceEffects[0].MountSlot == 9 &&
            active.ActiveDeviceEffects[1].TemplateId == 1805 && active.TurboWeaponsActive,
            "Full active-device snapshot lost concurrent equipped effects.");
        world.Apply(DeviceSnapshot((9, 2265u, 9000u, "Turbo Weapons"),
            (10, 1805u, 6000u, "Recharge Shields")), player);
        Snapshot renewed = new Snapshot(world, "ready", true);
        Assert(renewed.ActiveDeviceEffects[0].RemainingSeconds >
            active.ActiveDeviceEffects[0].RemainingSeconds + 3,
            "Reapplying an item did not renew its displayed server lease.");
        world.Apply(OwnPose(player, 6500), player);
        Snapshot expired = new Snapshot(world, "ready", true);
        Assert(expired.ActiveDeviceEffects.Count == 1 &&
            expired.ActiveDeviceEffects[0].Name == "Turbo Weapons",
            "An expired device stayed visible or removed another device.");
        world.Apply(DeviceSnapshot(), player);
        Assert(new Snapshot(world, "ready", true).HasActiveDeviceSnapshot &&
            new Snapshot(world, "ready", true).ActiveDeviceEffects.Count == 0,
            "An empty authoritative snapshot did not clear all device rows.");
        Reject(() => ActiveDeviceStatus.DecodeSnapshot(new byte[] { 1, 0, 7 }));
        Reject(() => ActiveDeviceStatus.DecodeSnapshot(new byte[] { 2, 0 }));
    }

    /// Returns:
    ///     Frame: A native ship status diff matching packets::ship_status_buff_slot.
    private static Frame NativeStatusFrame(uint player, int slot, string name,
        bool permanent, uint removalTick)
    {
        byte[] Aux(int count, params (int Index, byte[] Value)[] fields)
        {
            var record = new byte[(4 + count + 7) / 8];
            record[0] = 2;
            foreach (var field in fields)
                record[(field.Index + 4) / 8] |= (byte)(1 << ((field.Index + 4) % 8));
            return record.Concat(fields.OrderBy(field => field.Index)
                .SelectMany(field => field.Value)).ToArray();
        }
        byte[] nameBytes = Encoding.UTF8.GetBytes(name);
        byte[] text = BitConverter.GetBytes((ushort)nameBytes.Length).Concat(nameBytes).ToArray();
        byte[] buff = Aux(5, (0, text), (2, new[] { permanent ? (byte)1 : (byte)0 }),
            (3, BitConverter.GetBytes(removalTick)));
        byte[] ship = Aux(58, (42, Aux(16, (slot, buff))));
        byte[] body = BitConverter.GetBytes(player)
            .Concat(BitConverter.GetBytes((ushort)(ship.Length + 1)))
            .Concat(new byte[] { 1 }).Concat(ship).ToArray();
        return new Frame(0x1b, body);
    }

    /// Returns:
    ///     Frame: An own-ship pose carrying an authoritative server clock sample.
    private static Frame OwnPose(uint player, uint tick)
    {
        var body = new byte[38];
        Array.Copy(BitConverter.GetBytes(player), 0, body, 2, 4);
        Array.Copy(BitConverter.GetBytes(tick), 0, body, 6, 4);
        Array.Copy(BitConverter.GetBytes(1f), 0, body, 34, 4);
        return new Frame(0x3e, body);
    }

    /// Returns:
    ///     Frame: A version-one full equipped-device status replacement.
    private static Frame DeviceSnapshot(params (byte Slot, uint Template, uint Tick, string Name)[] devices)
    {
        return new Frame(0x2030, Wire.Body(writer =>
        {
            writer.Write((byte)1);
            writer.Write((byte)devices.Length);
            foreach (var device in devices)
            {
                byte[] label = Encoding.UTF8.GetBytes(device.Name);
                writer.Write(device.Slot); writer.Write(device.Template); writer.Write(device.Tick);
                writer.Write((byte)label.Length); writer.Write(label);
            }
        }));
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

    /// <summary>Retain native gate approval and same-batch wormhole completion through handoff.</summary>
    private static void CheckSectorTransit()
    {
        var world = new World(1015);
        byte[] gate = Wire.Body(writer => { writer.Write(123u); writer.Write(1u); });
        world.Apply(new Frame(0x31, gate), 1);
        var active = new Snapshot(world, "gate", true);
        long started = active.Gates.Single().StartedAt;
        world.Apply(new Frame(0x31, gate), 1);
        Assert(world.Gates[123].StartedAt == started, "Repeated gate approval restarted the animation.");
        world.Apply(new Frame(0x92, new byte[] { 0,0,0,4, 0,0,0,123 }), 1);
        Assert(world.TransferKind() == 1 && new Snapshot(world, "gate", true).GateCameraTarget == 123,
            "Native big-endian gate camera did not identify a gate transfer.");
        gate[4] = 3; world.Apply(new Frame(0x31, gate), 1);
        Assert(world.TransferKind() == 0 && world.Gates.Count == 0 && active.Gates.Length == 1,
            "Gate cancellation leaked state or mutated the previous render snapshot.");
        Reject(() => world.Apply(new Frame(0x31, new byte[7]), 1));
        Reject(() => world.Apply(new Frame(0x92, new byte[9]), 1));
        world.Apply(new Frame(0x09, Wire.Body(writer => {
            writer.Write((byte)3); writer.Write(1u); writer.Write((ushort)689); writer.Write(99u); writer.Write(1u);
        })), 1);
        Assert(world.TransferKind() == 2, "Wormhole startup was not recognized.");
        world.Apply(new Frame(0x0f, Wire.Body(writer => writer.Write(99u))), 1);
        Assert(world.TransferKind() == 0, "Canceled casts must not masquerade as completed wormholes.");
        byte[] Aux(int count, params (int Index, byte[] Value)[] fields)
        {
            var record = new byte[(4 + count + 7) / 8]; record[0] = 2;
            foreach (var field in fields)
                record[(field.Index + 4) / 8] |= (byte)(1 << ((field.Index + 4) % 8));
            return record.Concat(fields.OrderBy(field => field.Index).SelectMany(field => field.Value)).ToArray();
        }
        for (int busy = 0; busy < 512; busy++) world.Apply(new Frame(0xffff, Array.Empty<byte>()), 1);
        byte[] cooldown = Aux(18, (6, Aux(16, (2, Aux(64, (15, Aux(7,
            (1, BitConverter.GetBytes(10000u)), (2, BitConverter.GetBytes(500u)))))))));
        world.Apply(new Frame(0x1b, BitConverter.GetBytes(0u)
            .Concat(BitConverter.GetBytes((ushort)(cooldown.Length + 1)))
            .Concat(new byte[] { 1 }).Concat(cooldown).ToArray()), 1);
        Assert(world.TransferKind() == 2, "Confirmed completion lost its handoff cause.");
        Thread.Sleep(1100);
        Assert(world.TransferKind() == 2, "Reliable handoff retransmission expired by wall time.");
        for (int index = 0; index < 512; index++) world.Apply(new Frame(0xffff, Array.Empty<byte>()), 1);
        Assert(world.TransferKind() == 2, "Busy sector traffic erased a confirmed wormhole before handoff.");
        world.ClearTransferIntent();
        Assert(world.TransferKind() == 0, "An explicit replacement action retained stale completion.");
        var reentered = new Snapshot(new World(1015) { EntryGeneration = 2, ArrivalKind = 2 }, "ready", true);
        Assert(reentered.EntryGeneration == 2 && reentered.SectorId == world.SectorId && reentered.ArrivalKind == 2,
            "Same-sector handoffs lost their distinct entry identity.");
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
            DeviceEffects.LabelFor("Recharge Shields - BOOST (Activated)") == "Recharge Shields" &&
            DeviceEffects.LabelFor("Reroute Shield Energy (Instant)") == "Reroute Shield Energy" &&
            DeviceEffects.LabelFor("Transfer Reactor Energy (Instant)") == "Transfer Reactor Energy",
            "Native instant and impulse catalog descriptions lost their UI labels.");
        foreach (string ally in new[] { "Scan Boost", "Recharge Shields",
                     "Recharge Reactor", "Reduce Ship Signature" })
            Assert(!DeviceEffects.RequiresPeer(ally, true),
                "A self-capable ally device lost its self-target activation path.");
        Assert(DeviceEffects.MountedSlot("Transfer Reactor Energy", 11, 14, 6) &&
            DeviceEffects.MountedSlot("Reroute Shield Energy", 7, 1, 6) &&
            DeviceEffects.MountedSlot("Increase Maneuverability", 2, 0, 6) &&
            DeviceEffects.MountedSlot("Recharge Shields", 2, 0, 6) &&
            DeviceEffects.MountedSlot("Increase Maneuverability", 6, 2, 6) &&
            DeviceEffects.MountedSlot("Impulse Turbo", 7, 1, 6) &&
            !DeviceEffects.MountedSlot("Turbo Weapons", 7, 1, 6) &&
            !DeviceEffects.MountedSlot("Transfer Reactor Energy", 11, 15, 6),
            "Activation mount rules diverged from server-supported core and device slots.");
        foreach (string shieldEffect in new[] { "Recharge Shields", "Turbo Weapons",
                     "Deflect Impact", "Deflect Explosive", "Shunt to Reactor" })
            Assert(DeviceEffects.MountedSlot(shieldEffect, 2, 0, 6) &&
                !DeviceEffects.MountedSlot(shieldEffect, 2, 1, 6),
                "Shield-mounted activation was hidden or offered in the wrong slot: " + shieldEffect);
        foreach (string reactorEffect in new[] { "Recharge Reactor", "Recharge Shields" })
            Assert(DeviceEffects.MountedSlot(reactorEffect, 7, 1, 6) &&
                !DeviceEffects.MountedSlot(reactorEffect, 7, 0, 6),
                "Reactor-mounted recharge was hidden or offered in the wrong slot: " + reactorEffect);
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
            !DeviceEffects.RequiresPeer(peerScan.ActivationLabel, peerScan.TargetsPeer) &&
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

    /// <summary>Reject malformed launcher snapshots and preserve authoritative cargo choices across revisions.</summary>
    private static void CheckLauncherAmmo()
    {
        const uint owner = 0x40000001;
        byte[] body = Wire.Body(writer =>
        {
            writer.Write(owner); writer.Write(1u);
            writer.Write(3u); writer.Write(1u); writer.Write(507u);
            writer.Write(2u); writer.Write(0u); writer.Write(7u);
        });
        var world = new World();
        world.Apply(new Frame(0x203c, body), owner);
        LauncherAmmoState view = new Snapshot(world, "", true).LauncherAmmo;
        Assert(view.Known && view.Launchers.Count == 1 && view.Launchers[0].Mount == 3 &&
            view.Launchers[0].AutoReload && view.Launchers[0].PreviousTemplate == 507 &&
            view.Launchers[0].CargoSlots.SequenceEqual(new[] { 0, 7 }),
            "Authoritative compatible ammunition or reload choice was lost.");
        body[body.Length - 4] = 9;
        Assert(view.Launchers[0].CargoSlots[1] == 7,
            "A published launcher choice changed with its input buffer.");
        byte[] reset = Wire.Body(writer =>
        {
            writer.Write(owner); writer.Write(1u);
            writer.Write(3u); writer.Write(0u); writer.Write(507u); writer.Write(0u);
        });
        world.Apply(new Frame(0x203c, reset), owner);
        Assert(!world.LauncherAmmo.Launchers[0].AutoReload &&
            world.LauncherAmmo.Launchers[0].CargoSlots.Count == 0 && view.Launchers[0].AutoReload,
            "A preference revision mutated the earlier immutable view.");
        Reject(() => LauncherAmmoState.Decode(reset, owner + 1));
        Reject(() => LauncherAmmoState.Decode(Wire.Body(writer =>
        { writer.Write(owner); writer.Write(1u); writer.Write(9u); writer.Write(1u); writer.Write(0u); writer.Write(0u); }), owner));
        Reject(() => LauncherAmmoState.Decode(Wire.Body(writer =>
        { writer.Write(owner); writer.Write(1u); writer.Write(3u); writer.Write(1u); writer.Write(0u); writer.Write(1u); writer.Write(40u); }), owner));
    }

    /// <summary>Decode the Rust-authored chat fixture and keep bounded scrollback revisions immutable.</summary>
    private static void CheckNativeChat()
    {
        byte[] body = FixtureBytes("native_chat_event.hex");
        NativeChatNotice notice = NativeChatNotice.Decode(body);
        Assert(notice.DisplayLine() == "[General Chat] Alice: Hello", "Native chat display lost its channel or sender.");
        var world = new World();
        world.Apply(new Frame(0xa5, body), 1);
        string[] before = new Snapshot(world, "", true).Messages;
        for (int index = 0; index < 205; index++) world.Apply(new Frame(0xa5, body), 1);
        Assert(world.Messages.Count == 200 && before.Length == 1 &&
            before[0] == "[General Chat] Alice: Hello", "Chat scrollback was unbounded or mutated a published snapshot.");
        Reject(() => NativeChatNotice.Decode(body.Take(body.Length - 1).ToArray()));
        byte[] oversized = (byte[])body.Clone(); oversized[8] = 65;
        Reject(() => NativeChatNotice.Decode(oversized));
        byte[] unicode = Wire.Body(writer =>
        {
            writer.Write(3u); writer.Write(0u);
            writer.Write((ushort)5); writer.Write(Encoding.UTF8.GetBytes("Alice"));
            writer.Write((ushort)5); writer.Write(Encoding.UTF8.GetBytes("Alice"));
            writer.Write((ushort)0);
            writer.Write((ushort)5); writer.Write(Encoding.UTF8.GetBytes("Local"));
            writer.Write((ushort)6); writer.Write(Encoding.UTF8.GetBytes("Ça va"));
            writer.Write(0u); writer.Write((ushort)0);
        });
        Assert(NativeChatNotice.Decode(unicode).DisplayLine() == "[Local] Alice: Ça va",
            "Native chat lost UTF-8 text.");
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

    /// <summary>Choose native-compatible full and partial stack transfers without an empty slot.</summary>
    private static void CheckVaultStackPlacement()
    {
        byte[] craftedFrame = FixtureBytes("crafted_cargo_aux.hex");
        var craftedWorld = new World();
        craftedWorld.Apply(new Frame(0x1b, Wire.Slice(craftedFrame, 4, craftedFrame.Length - 4)),
            0x40000001);
        Snapshot craftedSnapshot = new Snapshot(craftedWorld, "ready", true);
        ItemView crafted = craftedSnapshot.Cargo[0];
        Assert(craftedSnapshot.CargoCapacity == 25,
            "The crafted cargo fixture did not publish its authoritative hull capacity.");
        Assert(crafted?.BuilderName == "Third" && crafted.Template == 2505 &&
            crafted.Structure == .5f && crafted.Quality == 1.25f,
            "Native crafted-item builder was lost from the cargo AUX record.");
        ItemTemplateInfo ammunition = ItemTemplateInfo.Decode(FixtureBytes("ammo507.hex"));
        Assert(ammunition.MaxStack == 50, "Native item max-stack metadata was not decoded.");
        var source = new ItemState { Template = ammunition.Id, Quantity = 20,
            Quality = 1, Structure = 1, BuilderName = "Third" };
        var cargo = Enumerable.Range(0, 40).Select(_ => new ItemState
            { Template = 9000, Quantity = 1 }).ToArray();
        cargo[4] = new ItemState { Template = ammunition.Id, Quantity = 35,
            Quality = 1, Structure = 1, BuilderName = "Third" };
        InventoryTransferPlan? partial = InventoryTransferPlan.Choose(source, cargo, 25, ammunition);
        Assert(partial.HasValue && partial.Value.Slot == 4 && partial.Value.Quantity == 15 &&
            partial.Value.Merge, "A full cargo hold could not accept a bounded compatible merge.");
        cargo[4].Quantity = 25;
        InventoryTransferPlan? full = InventoryTransferPlan.Choose(source, cargo, 25, ammunition);
        Assert(full.HasValue && full.Value.Slot == 4 && full.Value.Quantity == 20,
            "A full-stack merge was not preferred over a new slot.");
        cargo[4].BuilderName = "Another crafter";
        Assert(!InventoryTransferPlan.Choose(source, cargo, 25, ammunition).HasValue,
            "Different crafted builders were merged into one item stack.");
        cargo[26] = null;
        Assert(!InventoryTransferPlan.Choose(source, cargo, 25, ammunition).HasValue,
            "A locked cargo slot beyond the hull capacity was selected.");
        cargo[3] = null;
        InventoryTransferPlan? empty = InventoryTransferPlan.Choose(source, cargo, 25, ammunition);
        Assert(empty.HasValue && empty.Value.Slot == 3 && empty.Value.Quantity == 20 && !empty.Value.Merge,
            "An empty unlocked slot was not used for an incompatible stack.");
    }

    /// <summary>Retain public equipped gear benefits from exact native item packets.</summary>
    private static void CheckEquippedEffects()
    {
        ItemTemplateInfo beam = ItemTemplateInfo.Decode(FixtureBytes("item986.hex").Skip(4).ToArray());
        ItemTemplateInfo projectile = ItemTemplateInfo.Decode(FixtureBytes("item2089.hex").Skip(4).ToArray());
        ItemTemplateInfo prospect = ItemTemplateInfo.Decode(FixtureBytes("item1803.hex").Skip(4).ToArray());
        Assert(beam.Id == 986 && beam.EquippedEffects.Count == 1 &&
            beam.EquippedEffects[0].Label == "Improved Beam Handling (Equip)" &&
            beam.EquippedEffects[0].Detail.Contains("2.30 Skill Level"),
            "Native Beam Skill equipped effect or its first catalog value was lost.");
        Assert(projectile.Id == 2089 && projectile.EquippedEffects.Count == 4 &&
            projectile.EquippedEffects.Any(effect => effect.Label == "Projectile Weapon Skill (Equip)" &&
                effect.Detail.Contains("2.20 Skill Level")),
            "Native multi-effect Projectile Skill metadata was not retained.");
        Assert(prospect.Id == 1803 && prospect.EquippedEffects.Count == 1 &&
            prospect.EquippedEffects[0].Detail.Contains("0.73 Skill Level"),
            "Native Prospect Skill benefit was not displayed from item metadata.");
        var catalog = new System.Collections.Generic.Dictionary<uint, ItemTemplateInfo> { [1803] = prospect };
        ItemView mounted = new ItemView(9, new ItemState { Template = 1803, Quantity = 1 }, catalog);
        Assert(mounted.EquippedEffects.Count == 1 && mounted.EquippedEffects[0] == prospect.EquippedEffects[0],
            "The immutable equipment snapshot lost the catalog effect label.");
        byte[] truncated = FixtureBytes("item986.hex").Skip(4).Take(70).ToArray();
        Reject(() => ItemTemplateInfo.Decode(truncated));
    }

    /// <summary>Decode native typed deflects without predicting damage reduction locally.</summary>
    private static void CheckShipDefense()
    {
        byte[] frame = FixtureBytes("deflect_ship_parameters.hex");
        Assert(Wire.U16(frame, 2) == 0x1b, "Ship defense fixture is not native AUX.");
        var world = new World();
        world.Apply(new Frame(0x1b, Wire.Slice(frame, 4, frame.Length - 4)), 0x40000001);
        Snapshot snapshot = new Snapshot(world, "ready", true);
        Assert(snapshot.Defense.Known && snapshot.Defense.Deflects.SequenceEqual(
            new[] { 13, 19, 25, 31, 37, 43, 49 }),
            "Native ship stats 44/45 typed deflects or their damage order were lost.");
        Assert(snapshot.MaxEnergy == 1000 && snapshot.MaxShield == 500 && snapshot.Hull == 900,
            "Defense parsing displaced preceding authoritative ship resources.");
        int[] original = { 1, 2, 3, 4, 5, 6, 7 };
        var immutable = new ShipDefenseState(true, original);
        original[0] = 99;
        Assert(immutable.Deflects[0] == 1,
            "The Unity defense snapshot retained mutable worker-owned values.");
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
        byte[] warpAlignment = Wire.Body(writer =>
        {
            writer.Write(100004u); writer.Write(2u);
            writer.Write(100f); writer.Write(100f); writer.Write(100f);
        });
        world.Apply(new Frame(0x41, warpAlignment), 2);
        Assert(world.Formation.LeaderId == 1 && world.Formation.Offset.X == 50,
            "Warp alignment for a navigation target replaced our own formation anchor.");
        byte[] truncatedAlignment = new byte[19];
        Buffer.BlockCopy(warpAlignment, 0, truncatedAlignment, 0, truncatedAlignment.Length);
        Reject(() => world.Apply(new Frame(0x41, truncatedAlignment), 2));
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
        throttle.Sample(true, false, false, false);
        Assert(throttle.Sample(true, false, true, false, currentSpeed: 100) == 4,
            "S did not brake forward motion before reversing.");
        Assert(throttle.Sample(true, false, true, false, currentSpeed: 0) == null,
            "Holding S accelerated backward after braking to rest.");
        throttle.Sample(true, false, false, false);
        Assert(throttle.Sample(true, false, true, false, currentSpeed: 0) == 3,
            "A fresh S press did not reverse from rest.");
        Assert(throttle.Sample(true, true, false, false, currentSpeed: -50) == 4,
            "W did not brake backward motion before accelerating forward.");
        Assert(throttle.Sample(true, true, false, false, currentSpeed: 0) == null,
            "Holding W accelerated forward after braking to rest.");
        throttle.Sample(true, false, false, false);
        Assert(throttle.Sample(true, true, false, false, currentSpeed: 0) == 2,
            "A fresh W press did not accelerate forward from rest.");
        Assert(throttle.Sample(true, true, true, false, currentSpeed: 0) == 4,
            "Opposing keys together should hold the brake.");
        throttle.Reset();
        Assert(throttle.Sample(true, false, true, false) == 3,
            "Steering away from follow did not resubmit held thrust after the steer command.");
        throttle.Sample(true, false, false, false);
        Assert(throttle.Sample(true, false, true, false, currentSpeed: 0, targetSpeed: 200) == 4,
            "S ignored the authoritative forward target before the next speed update.");
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

    /// <summary>Check public addresses, hostnames, invalid endpoints and cancelled worker resolution.</summary>
    private static void CheckAddress()
    {
        foreach (string address in new[] { "127.0.0.1", "10.1.2.3", "172.16.0.1", "192.168.47.128", "216.219.87.147", "172.32.0.1" })
            Assert(ServerEndpoint.Resolve(address).ToString() == address, "Rejected or changed a valid IPv4 endpoint.");
        Assert(ServerEndpoint.NormalizeHost("  game.example.com  ") == "game.example.com", "Hostname was not trimmed.");
        Assert(ServerEndpoint.DefaultHost == "192.168.47.1", "Local development server default changed.");
        Assert(System.Net.IPAddress.IsLoopback(ServerEndpoint.Resolve("localhost")), "Local hostname did not resolve on the worker path.");
        using (var stopped = new System.Threading.CancellationTokenSource())
        {
            stopped.Cancel(); bool cancelled = false;
            try { ServerEndpoint.Resolve("localhost", stopped.Token); }
            catch (OperationCanceledException) { cancelled = true; }
            Assert(cancelled, "Cancelled hostname resolution continued.");
        }
        foreach (string address in new[] { "", " ", "0.0.0.0", "255.255.255.255", "224.1.2.3", "https://example.com", "example.com:3807", "bad host", "::1" })
        {
            bool rejected = false;
            try { ServerEndpoint.NormalizeHost(address); } catch (ArgumentException) { rejected = true; }
            Assert(rejected, "Accepted an invalid server endpoint: " + address);
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
