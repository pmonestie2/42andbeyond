using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using FortyTwoAndBeyond.Protocol;

internal static class PeerChecks
{
    /// <summary>Verify player lifecycle packets without opening any network connection.</summary>
    public static void Decode()
    {
        const uint self = 0x40000001, peer = 0x40000002;
        var world = new World();
        world.Apply(Create(self, 1), self);
        world.Apply(Create(200, 0), self);
        world.Apply(Move(peer, 100), self);
        Require(world.Players.Count == 0, "Self, NPC, or unknown movement created a player.");
        Require(world.Mobs.Count == 1 && !world.Mobs[200].HasPosition, "Mob creation was not separated from players.");
        world.Apply(Move(200, 700), self);
        world.Apply(new Frame(0x89, Wire.Body(w => { Wire.Big(w, 200u); w.Write(0u); w.Write((byte)0); })), self);
        Require(world.Mobs[200].Hostile, "Hostile relation was not decoded with its big-endian identity.");
        Snapshot mobSnapshot = new Snapshot(world, "ready", true);
        world.Apply(Move(200, 800), self);
        Require(mobSnapshot.Mobs[0].Position.X == 700 && world.Mobs[200].Position.X == 800, "Mob snapshots share mutable poses.");
        world.Apply(new Frame(7, BitConverter.GetBytes(200u)), self);
        world.Apply(Move(200, 900), self);
        Require(world.Mobs.Count == 0, "Removed mob was resurrected by late movement.");
        world.Apply(Create(peer, 1), self);
        Require(!world.Players[peer].HasPosition, "A new player was placed at the origin before movement.");
        byte[] avatar = new byte[260];
        Array.Copy(BitConverter.GetBytes(peer), avatar, 4);
        Array.Copy(Encoding.ASCII.GetBytes("Visitor"), 0, avatar, 4, 7);
        world.Apply(new Frame(0x61, avatar), self);
        world.Apply(Move(peer, 100), self);
        Snapshot old = new Snapshot(world, "ready", true);
        world.Apply(Move(peer, 250), self);
        Require(old.Players[0].Name == "Visitor" && old.Players[0].Position.X == 100,
            "An existing snapshot was modified by later player movement.");
        Require(!world.HasPosition && world.Players[peer].Position.X == 250,
            "Remote movement changed the local player or was discarded.");
        world.Apply(new Frame(7, BitConverter.GetBytes(peer)), self);
        world.Apply(Move(peer, 999), self);
        Require(world.Players.Count == 0, "Movement resurrected a removed player.");
        world.Apply(Create(peer, 1), self);
        Require(!world.Players[peer].HasPosition, "Reentry retained an old pose.");
        byte[] sector = Wire.Body(w => { w.Write(4u); w.Write(0u); w.Write(1u); w.Write(Encoding.ASCII.GetBytes("Sol\0Saturn\0\0")); });
        Array.Copy(BitConverter.GetBytes((uint)sector.Length - 8), 0, sector, 4, 4);
        world.Apply(new Frame(0x97, sector), self);
        Require(world.Players.Count == 0 && old.Players.Length == 1, "Sector transition leaked player models or changed an old snapshot.");
        byte[] rescue = Wire.Body(writer =>
        {
            writer.Write(Encoding.UTF8.GetBytes("Rescue control: do you need assistance?\0\0"));
            writer.Write((byte)3);
            string[] labels = { "I need a tow", "Send distress call", "Wait for jumpstart" };
            for (int choice = 0; choice < labels.Length; choice++)
            {
                writer.Write((uint)choice);
                writer.Write(Encoding.UTF8.GetBytes(labels[choice] + "\0"));
            }
        });
        world.Apply(new Frame(0x54, rescue), self);
        Require(world.Dialogue?.Choices.Length == 3 && world.Dialogue.Choices[0].Node == 0 &&
            world.Dialogue.Choices[2].Node == 2, "Native rescue choices were not decoded.");
        world.Apply(new Frame(0x56, BitConverter.GetBytes(-32)), self);
        Require(world.Dialogue == null, "Native rescue close did not dismiss the panel.");
        byte[] groupPrompt = Confirmation("SandboxPilot is requesting you to join their group");
        world.Apply(new Frame(0x1e, groupPrompt), self);
        Require(world.Invitation.Contains("SandboxPilot") && world.Confirmation?.Kind == ConfirmationKind.Group,
            "Group invitation was not classified as a group prompt.");
        byte[] jumpstartPrompt = Confirmation("Do you want to accept a jumpstart level 7 from Explorer");
        world.Apply(new Frame(0x1e, jumpstartPrompt), self);
        Snapshot recoverySnapshot = new Snapshot(world, "ready", true);
        Require(world.Invitation == "" && recoverySnapshot.Confirmation?.Kind == ConfirmationKind.Jumpstart,
            "Jumpstart confirmation was confused with a group invitation.");
        world.ClearInvitation();
        Require(world.Confirmation?.Kind == ConfirmationKind.Jumpstart,
            "A group update cleared the pending Jumpstart confirmation.");
        world.ClearConfirmation();
        Require(world.Confirmation == null && recoverySnapshot.Confirmation?.Kind == ConfirmationKind.Jumpstart,
            "Answering a confirmation changed an existing display snapshot.");
        byte[] invalidPrompt = (byte[])jumpstartPrompt.Clone(); invalidPrompt[0]--;
        bool malformedPromptRejected = false;
        try { world.Apply(new Frame(0x1e, invalidPrompt), self); }
        catch (InvalidDataException) { malformedPromptRejected = true; }
        Require(malformedPromptRejected, "A confirmation with a false native length was accepted.");
        bool rejected = false;
        try { world.Apply(new Frame(4, new byte[10]), self); } catch (InvalidDataException) { rejected = true; }
        Require(rejected, "Truncated creation was accepted.");
        Console.WriteLine("PASS: peer creation, identity, movement, immutable snapshots, despawn, reentry, sector cleanup");
    }

    /// <returns>
    /// byte[]: Frame body for a native group or ability yes/no prompt.
    /// </returns>
    private static byte[] Confirmation(string message)
    {
        byte[] encoded = Encoding.ASCII.GetBytes(message);
        return Wire.Body(w => { w.Write((ushort)(encoded.Length + 1)); w.Write((byte)1); w.Write(encoded); w.Write((byte)0); });
    }

    /// <returns>A native player or MOB creation packet.</returns>
    private static Frame Create(uint id, byte kind)
    { return new Frame(4, Wire.Body(w => { w.Write(id); w.Write(1f); w.Write((ushort)123); w.Write(kind); w.Write(new byte[12]); })); }

    /// <returns>A native position packet for a particular actor.</returns>
    private static Frame Move(uint id, float x)
    { return new Frame(0x3e, Wire.Body(w => { w.Write((ushort)0); w.Write(id); w.Write(0u); w.Write(x); w.Write(0f); w.Write(0f); w.Write(0f); w.Write(0f); w.Write(0f); w.Write(1f); w.Write(0u); })); }

    /// <summary>Fail when a peer lifecycle invariant does not hold.</summary>
    private static void Require(bool condition, string message)
    { if (!condition) throw new Exception(message); }

    /// <summary>Exercise two accounts only on the harness-owned disposable server.</summary>
    public static void Live(string host)
    {
        using (var observer = new LocalSession(CancellationToken.None))
        {
            observer.Connect(host, "local", "local");
            Wait(observer, null, () => observer.World.Equipment.Mounted[3]?.Present == true &&
                observer.World.ItemCatalog.TryGetValue(observer.World.Equipment.Mounted[3].Template, out ItemTemplateInfo template) &&
                template.Name.Length > 0, "Starter weapon or item template was not decoded.");
            Wait(observer, null, () => observer.Ready && observer.World.AvailableAbilities.Contains(44u) &&
                observer.World.Equipment.Energy > 0, "Character ability list or reactor energy did not arrive.");
            float energyBeforeAbility = observer.World.Equipment.Energy;
            observer.ActivateAbility(44);
            Wait(observer, null, () => observer.World.Equipment.Energy < energyBeforeAbility - .001f,
                "Native ability activation did not charge reactor energy.");
            Require(observer.World.CastingAbilityId == 44 && observer.World.CastEndsAt > Stopwatch.GetTimestamp(),
                "Native ability charge progress was not decoded.");
            Wait(observer, null, () => observer.World.AbilityCooldownEnds.ContainsKey(23),
                "Native skill cooldown was not decoded after the cast.");
            Console.WriteLine("PASS: server-advertised abilities, native activation, charge and cooldown");
            uint peerId;
            using (var visitor = new LocalSession(CancellationToken.None))
            {
                visitor.Connect(host, "peer", "pass"); peerId = visitor.Player;
                Wait(observer, visitor, () => observer.Ready && visitor.Ready &&
                    observer.World.Players.TryGetValue(peerId, out ShipActor p) && p.HasPosition && p.Name == "Visitor" &&
                    visitor.World.Players.ContainsKey(observer.Player), "Mutual player visibility did not arrive.");
                Require(observer.World.Equipment.Mounted[13]?.Template == 2412 &&
                    visitor.World.Equipment.Mounted[14]?.Template == 2495,
                    "Disposable support devices were not equipped in the expected mounts.");
                visitor.SelectTarget(observer.Player);
                Wait(observer, visitor, () => visitor.World.Equipment.TargetId == observer.Player,
                    "Selected recipient did not receive native target acknowledgment.");
                visitor.ActivateDevice(13);
                Wait(observer, visitor, () => visitor.World.ActiveDeviceLabel == "Turbo Weapons",
                    "Sender timed effect did not activate before instant support.");
                observer.ActivateDevice(13);
                Wait(observer, visitor, () => observer.World.ActiveDeviceLabel == "Turbo Weapons" &&
                    observer.World.Equipment.Energy < .99f,
                    "Timed device did not make room in the recipient reactor.");
                float recipientEnergy = observer.World.Equipment.Energy;
                float senderEnergy = visitor.World.Equipment.Energy;
                visitor.ActivateDevice(14);
                Wait(observer, visitor, () => visitor.World.Messages.Any(message =>
                    message == "Transfer Reactor Energy completed.") &&
                    observer.World.Messages.Any(message =>
                        message == "Reactor energy received from a nearby pilot."),
                    "Native selected-peer energy transfer was not acknowledged by both pilots.");
                Require(visitor.World.Equipment.Energy < senderEnergy - .005f &&
                    observer.World.Equipment.Energy >= recipientEnergy,
                    $"Native transfer reactor fraction mismatch: sender {senderEnergy:0.000} -> " +
                    $"{visitor.World.Equipment.Energy:0.000}; recipient {recipientEnergy:0.000} -> " +
                    $"{observer.World.Equipment.Energy:0.000}.");
                Require(visitor.World.ActiveDeviceLabel == "Turbo Weapons",
                    "Instant peer support unexpectedly cleared the sender's timed buff.");
                Console.WriteLine("PASS: selected-peer reactor transfer during a timed device buff");
                uint starterWeapon = observer.World.Equipment.Mounted[3].Template;
                observer.UnequipMount(3);
                Wait(observer, null, () => observer.World.Equipment.Mounted[3]?.Present != true &&
                    observer.World.Equipment.Cargo[0]?.Template == starterWeapon, "Server unequip update did not move the weapon to cargo.");
                observer.EquipCargo(0, 3);
                Wait(observer, null, () => observer.World.Equipment.Mounted[3]?.Template == starterWeapon &&
                    observer.World.Equipment.Cargo[0]?.Present != true, "Server equip update did not restore the weapon.");
                Console.WriteLine("PASS: item template, cargo and hardpoint decoding; authoritative unequip and equip");
                bool firingBefore = (observer.World.Equipment.Mounted[3].WeaponState & 1) != 0;
                observer.ActivateWeapon(3);
                Wait(observer, null, () => ((observer.World.Equipment.Mounted[3]?.WeaponState ?? 0) & 1) !=
                    (firingBefore ? 1u : 0u), "Native mounted-weapon activation did not update its own hardpoint.");
                observer.ActivateWeapon(3);
                Wait(observer, null, () => ((observer.World.Equipment.Mounted[3]?.WeaponState ?? 0) & 1) ==
                    (firingBefore ? 1u : 0u), "Native mounted-weapon stop did not clear its hardpoint.");
                Console.WriteLine("PASS: per-weapon native activation and authoritative firing state");
                Wait(observer, visitor, () => observer.World.Objects.Values.Any(item => item.Kind == 38),
                    "Disposable asteroid was not visible to the pilot.");
                MapObject resource = observer.World.Objects.Values.First(item => item.Kind == 38);
                observer.OpenResource(resource.Id);
                Wait(observer, visitor, () => observer.World.ResourceWindowOpen &&
                    observer.World.Resources.TryGetValue(resource.Id, out ResourceState stock) &&
                    stock.Items.Any(item => item?.Present == true),
                    "Native prospecting window or asteroid stock did not arrive.");
                ResourceState ore = observer.World.Resources[resource.Id];
                int oreSlot = Array.FindIndex(ore.Items, item => item?.Present == true);
                int nextOreSlot = Array.FindIndex(ore.Items, item => item?.Present == true &&
                    item.Template != ore.Items[oreSlot].Template);
                Require(nextOreSlot >= 0, "Disposable resource needs two distinct ore slots for overlap.");
                uint oreTemplate = ore.Items[oreSlot].Template;
                uint nextOreTemplate = ore.Items[nextOreSlot].Template;
                observer.ProspectResource(oreSlot);
                Wait(observer, visitor, () => observer.World.MiningTractor != null,
                    "First prospect did not enter its authoritative ore tractor phase.");
                uint priorArticle = observer.World.MiningTractor.ArticleId;
                observer.ProspectResource(nextOreSlot);
                Wait(observer, visitor, () => observer.World.MiningProspect != null &&
                    observer.World.MiningTractor?.ArticleId == priorArticle,
                    "The second prospect did not overlap the first ore tractor.");
                Snapshot overlapped = new Snapshot(observer.World, "ready", true);
                Require(overlapped.ProspectRemainingSeconds > 0 &&
                    overlapped.TractorRemainingSeconds > 0,
                    "Immutable mining snapshot lost one of the simultaneous phases.");
                Wait(observer, visitor, () => observer.World.Equipment.Cargo.Any(item => item?.Template == oreTemplate) &&
                    observer.World.Equipment.Cargo.Any(item => item?.Template == nextOreTemplate),
                    "Overlapped mining did not award both server-owned ore items into cargo.");
                observer.CloseResource();
                Wait(observer, visitor, () => !observer.World.ResourceWindowOpen,
                    "Native prospecting window did not close.");
                Console.WriteLine("PASS: asteroid selection, simultaneous prospect/tractor phases and two ore awards");
                Position before = observer.World.Players[peerId].Position;
                visitor.Thrust(2);
                Wait(observer, visitor, () => observer.World.Players[peerId].Position.Distance(before) > 1900,
                    "Remote ship did not move far enough to test follow.");
                visitor.Thrust(4);
                Wait(observer, visitor, () => Math.Abs(visitor.World.Speed) < 1,
                    "Remote ship did not brake before follow.");
                float followDistance = visitor.World.Position.Distance(observer.World.Position);
                visitor.FollowTarget(observer.Player);
                Wait(observer, visitor, () => visitor.World.Position.Distance(observer.World.Position) < followDistance - 150,
                    "Native follow did not approach the selected player.");
                visitor.Thrust(4);
                Wait(observer, visitor, () => Math.Abs(visitor.World.Speed) < 1,
                    "Manual braking did not cancel follow.");
                Console.WriteLine("PASS: correlated native follow and manual-flight cancellation");
                observer.InvitePlayer(peerId);
                Wait(observer, visitor, () => visitor.World.Invitation.Contains("SandboxPilot"), "Group invitation did not arrive.");
                visitor.AnswerConfirmation(false);
                Wait(observer, visitor, () => visitor.World.Invitation == "", "Declined invitation remained visible.");
                // Ensure the server consumes the decline before inviting again.
                visitor.Chat("/where");
                Wait(observer, visitor, () => visitor.World.Messages.Any(m => m.Contains("Rust Sandbox")), "Decline barrier did not arrive.");
                observer.Chat("/invite Visitor");
                Wait(observer, visitor, () => visitor.World.Invitation.Length > 0, "Second invitation did not arrive.");
                visitor.AnswerConfirmation(true);
                Wait(observer, visitor, () => observer.World.Group.IsLeader && observer.World.Group.Members.Any(m => m.Id == peerId) &&
                    visitor.World.Group.Members.Any(m => m.Id == observer.Player), "Group roster did not converge.");
                Require(visitor.World.Equipment.Mounted[12]?.Template == 1813 &&
                    visitor.World.ItemCatalog.TryGetValue(1813, out ItemTemplateInfo scanTemplate) &&
                    scanTemplate.TargetsPeer && scanTemplate.ActivationLabel == "Scan Boost",
                    "Disposable peer Scan Boost metadata was not decoded as a targeted device.");
                visitor.SelectTarget(observer.Player);
                Wait(observer, visitor, () => visitor.World.Equipment.TargetId == observer.Player,
                    "Scan Boost recipient was not acknowledged by the server.");
                visitor.ActivateDevice(12);
                Wait(observer, visitor, () => visitor.World.Messages.Any(message =>
                    message == "Scan Boost sent.") && observer.World.PeerBuff.Name == "Increase Scan Range" &&
                    observer.World.PeerBuff.VisualDescriptor == 462,
                    "Group recipient did not receive native Scan Boost buff and visual.");
                Require(new Snapshot(observer.World, "ready", true).PeerBuff?.VisualActive == true,
                    "Peer Scan Boost visual was missing from the recipient's immutable snapshot.");
                Console.WriteLine("PASS: selected crew Scan Boost, recipient slot-one buff and native visual");
                Wait(observer, visitor, () => (!observer.World.AbilityCooldownEnds.TryGetValue(23,
                    out long readyAt) || readyAt <= Stopwatch.GetTimestamp()) &&
                    observer.World.Position.Distance(visitor.World.Position) < 3000,
                    "Crew pilot did not reach Environment Barrier range after cooldown.");
                observer.SelectTarget(peerId);
                Wait(observer, visitor, () => observer.World.Equipment.TargetId == peerId,
                    "Group support recipient did not receive native target acknowledgment.");
                observer.ActivateAbility(44);
                Wait(observer, visitor, () => visitor.World.ObjectEffects.Values.Any(effect =>
                    effect.OwnerId == peerId && effect.Descriptor == 216),
                    "Environment Barrier did not publish its native recipient effect.");
                observer.SelectTarget(observer.Player);
                Wait(observer, visitor, () => observer.World.Equipment.TargetId == observer.Player,
                    "Own-ship support target was not acknowledged by the server.");
                Console.WriteLine("PASS: selected crew support cast, recipient effect, and self targeting");
                observer.Formation(4);
                Wait(observer, visitor, () => observer.World.Group.Formation == "Slot Back" &&
                    observer.World.Group.Formed && visitor.World.Group.Formation == "Slot Back" &&
                    !visitor.World.Group.Formed, "Leader formation choice was not published.");
                visitor.Formation(7);
                Wait(observer, visitor, () => visitor.World.Group.Formed &&
                    observer.World.Group.Members.Any(m => m.Id == peerId && m.Formed), "Follower did not form up.");
                Position leadBefore = observer.World.Position, wingBefore = visitor.World.Position;
                observer.Thrust(2);
                Wait(observer, visitor, () => observer.World.Position.Distance(leadBefore) > 50 &&
                    visitor.World.Position.Distance(wingBefore) > 50 && visitor.World.Group.Formed,
                    "Formed follower did not travel with leader impulse.");
                observer.Thrust(4);
                Position wingWarpStart = visitor.World.Position;
                observer.Warp(0);
                Wait(observer, visitor, () => observer.World.Warp.Phase == WarpPhase.Traveling &&
                    visitor.World.Position.Distance(wingWarpStart) > 1000 &&
                    visitor.World.Formation?.LeaderId == observer.Player && visitor.World.Group.Formed,
                    "Native 0x41 formation did not carry the follower through leader warp.");
                observer.Warp(0);
                Wait(observer, visitor, () => observer.World.Warp.Phase != WarpPhase.Traveling &&
                    visitor.World.Group.Formed, "Leader warp cancellation released the follower.");
                visitor.Formation(8);
                Wait(observer, visitor, () => !visitor.World.Group.Formed, "Follower did not leave formation.");
                observer.Formation(6);
                Wait(observer, visitor, () => visitor.World.Group.Formation == "Pipe", "Formation shape change was not published.");
                observer.Formation(9);
                Wait(observer, visitor, () => observer.World.Group.Formation == "" &&
                    visitor.World.Group.Formation == "", "Formation release was not published.");
                observer.SelectTarget(peerId);
                Wait(observer, visitor, () => observer.World.Equipment.TargetId == peerId, "Group leader target did not reach the server.");
                observer.Formation(12);
                Wait(observer, visitor, () => visitor.World.Equipment.TargetId == peerId, "Shared target did not reach the member.");
                Console.WriteLine("PASS: formation selection, form-up, leave, shape change, release and target sharing");
                visitor.Chat("group smoke message", 1);
                Wait(observer, visitor, () => observer.World.Messages.Any(m => m.Contains("group smoke message")), "Group chat was not delivered.");
                observer.Chat("/kick Visitor");
                Wait(observer, visitor, () => !visitor.World.Group.Joined && observer.World.Group.Members.Length == 0, "Kicked member remained grouped.");
                observer.Chat("/invite Visitor");
                Wait(observer, visitor, () => visitor.World.Invitation.Length > 0, "Reinvite did not arrive.");
                visitor.Chat("/accept");
                Wait(observer, visitor, () => visitor.World.Group.Joined, "Rejoin did not arrive.");
                visitor.Chat("/leave");
                Wait(observer, visitor, () => !visitor.World.Group.Joined && observer.World.Group.Members.Length == 0, "Leave did not clear membership.");
                observer.Chat("/invite Visitor");
                Wait(observer, visitor, () => visitor.World.Invitation.Length > 0, "Disband setup invitation did not arrive.");
                visitor.Chat("/accept");
                Wait(observer, visitor, () => observer.World.Group.Members.Length == 1 && visitor.World.Group.Joined, "Disband setup did not join.");
                observer.Chat("/disband");
                Wait(observer, visitor, () => !observer.World.Group.Joined && !visitor.World.Group.Joined, "Disband did not clear both clients.");
                observer.Chat("/d_spawn Ishuan Revenant Young 1");
                Wait(observer, visitor, () => observer.World.Mobs.Values.Any(m => m.HasPosition && m.Name.Contains("Ishuan Revenant Young") && m.Shield >= 0),
                    "Spawned mob identity, pose, and shield did not arrive.");
                ShipActor spawned = observer.World.Mobs.Values.First(m => m.Name.Contains("Ishuan Revenant Young") && m.HasPosition);
                observer.Thrust(2);
                Wait(observer, visitor, () => observer.World.Position.Distance(spawned.Position) < 700,
                    "Pilot did not close to starter-beam range.");
                observer.Thrust(4);
                observer.ToggleWeapons(spawned.Id);
                Wait(observer, visitor, () => observer.World.Equipment.Mounted[3] != null &&
                    (observer.World.Equipment.Mounted[3].WeaponState & 1) != 0, "Weapon toggle was not acknowledged by the server.");
                Wait(observer, visitor, () => observer.World.Shots.Any(shot =>
                    shot.Source == observer.Player && shot.Target == spawned.Id), "No server weapon effect reached the client.");
                Wait(observer, visitor, () => !observer.World.Mobs.ContainsKey(spawned.Id) ||
                    observer.World.Mobs.TryGetValue(spawned.Id, out ShipActor damaged) &&
                    (damaged.Shield < spawned.Shield || damaged.Hull < spawned.Hull), "Target health did not change after firing.");
                observer.Thrust(2);
                Wait(observer, visitor, () => observer.World.Position.Distance(spawned.Position) < 700,
                    "Pilot did not close to starter-beam range.");
                observer.Thrust(4);
                observer.ToggleWeapons(spawned.Id);
                Wait(observer, visitor, () => observer.World.Equipment.Mounted[3] != null &&
                    (observer.World.Equipment.Mounted[3].WeaponState & 1) == 0, "Weapon stop was not acknowledged by the server.");
                if (observer.World.Mobs.ContainsKey(spawned.Id) &&
                    !observer.World.Objects.Values.Any(item => item.Kind == 25))
                    observer.ToggleWeapons(spawned.Id);
                Wait(observer, visitor, () => observer.World.Objects.Values.Any(item => item.Kind == 25),
                    "The defeated MOB did not publish a wreck.");
                MapObject wreck = observer.World.Objects.Values.First(item => item.Kind == 25);
                observer.OpenLoot(wreck.Id);
                Wait(observer, visitor, () => observer.World.OpenLootId == wreck.Id &&
                    observer.World.Loot.ContainsKey(wreck.Id) ||
                    !observer.World.Objects.ContainsKey(wreck.Id) &&
                    observer.World.Messages.Any(message => message.StartsWith("Looted ")),
                    "Wreck selection, permission or contents did not arrive.");
                ItemState[] stock = observer.World.Loot.TryGetValue(wreck.Id, out ItemState[] contents)
                    ? contents : Array.Empty<ItemState>();
                int lootSlot = Array.FindIndex(stock, item => item?.Present == true);
                Require(lootSlot >= 0, "Guaranteed Ishuan Revenant Young drop was missing from the corpse inventory.");
                uint template = stock[lootSlot].Template;
                observer.CloseLoot(wreck.Id);
                Wait(observer, visitor, () => observer.World.OpenLootId == 0,
                    "Native wreck close did not clear the server interface.");
                observer.OpenLoot(wreck.Id);
                Wait(observer, visitor, () => observer.World.OpenLootId == wreck.Id,
                    "Wreck did not reopen after the native close action.");
                observer.TakeLoot(wreck.Id, lootSlot);
                Wait(observer, visitor, () => observer.World.Equipment.Cargo.Any(item => item?.Template == template),
                    "Tractor collection did not add the wreck item to cargo.");
                Console.WriteLine("PASS: wreck targeting, open/close/reopen, guaranteed item stock and tractor collection");
                Console.WriteLine("PASS: group invitation, decline, accept, roster, group chat, kick, rejoin, leave, disband; server mob spawn/status, weapon effects and damage");
            }
            Wait(observer, null, () => !observer.World.Players.ContainsKey(peerId), "Logged-out player remained visible.");
            using (var visitor = new LocalSession(CancellationToken.None))
            {
                visitor.Connect(host, "peer", "pass");
                Wait(observer, visitor, () => observer.World.Players.TryGetValue(peerId, out ShipActor p) && p.HasPosition,
                    "Reconnected player did not reappear.");
            }
        }
        Console.WriteLine("PASS: two-client visibility, player names, authoritative remote movement, logout removal and reentry");
    }

    /// <summary>Poll both independent clients until the expected server event arrives.</summary>
    private static void Wait(LocalSession observer, LocalSession visitor, Func<bool> condition, string message)
    {
        var clock = Stopwatch.StartNew();
        while (!condition())
        {
            if (clock.Elapsed.TotalSeconds > 12) throw new TimeoutException(message + " Status=" + observer.Status +
                " open=" + observer.World.OpenLootId + " loot=" + string.Join(",", observer.World.Loot.Keys) +
                " Equipment: " + string.Join(",", observer.World.Equipment.Mounted.Select((item,index) => item != null ? $"{index}:{item.Template}:{item.Quantity}" : "null")) + " Catalog=" + observer.World.ItemCatalog.Count + " Resources=" + observer.World.Equipment.Hull + "/" + observer.World.Equipment.MaxHull + " Contacts: " + string.Join("; ", observer.World.Mobs.Values.Select(m => $"{m.Id}:{m.Name} posed={m.HasPosition} hostile={m.Hostile} shield={m.Shield} hull={m.Hull}/{m.MaxHull}")) + " Messages: " + string.Join("; ", observer.World.Messages));
            observer.Poll(); visitor?.Poll();
        }
    }
}
