using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Globalization;

namespace FortyTwoAndBeyond.Protocol
{
    public readonly struct Position
    {
        public readonly float X, Y, Z;
        /// <summary>Store coordinates in server meters.</summary>
        public Position(float x, float y, float z) { X = x; Y = y; Z = z; }
        /// <returns>The Euclidean distance in server meters.</returns>
        public float Distance(Position other)
        { float x = X - other.X, y = Y - other.Y, z = Z - other.Z; return (float)Math.Sqrt(x * x + y * y + z * z); }
    }

    /// <summary>One native leader-relative offset for a formed wing ship.</summary>
    public sealed class FormationAnchor
    {
        public readonly uint LeaderId;
        public readonly Position Offset;

        /// <summary>Keep one server-advertised formation anchor immutable.</summary>
        private FormationAnchor(uint leaderId, Position offset)
        { LeaderId = leaderId; Offset = offset; }

        /// <summary>
        /// Returns:
        ///     FormationAnchor: The validated native 0x41 offset for this player.
        /// Raises:
        ///     InvalidDataException: The body has an invalid owner, leader, or offset.
        /// </summary>
        public static FormationAnchor Decode(byte[] body, uint player)
        {
            if (body.Length != 20 || Wire.U32(body, 0) != player)
                throw new InvalidDataException("Invalid formation owner or length.");
            uint leader = Wire.U32(body, 4);
            float x = Wire.Float(body, 8), y = Wire.Float(body, 12), z = Wire.Float(body, 16);
            if (leader == 0 || leader == player || float.IsNaN(x) || float.IsInfinity(x) ||
                float.IsNaN(y) || float.IsInfinity(y) || float.IsNaN(z) || float.IsInfinity(z) ||
                Math.Abs(x) > 10000 || Math.Abs(y) > 10000 || Math.Abs(z) > 10000)
                throw new InvalidDataException("Invalid formation leader or offset.");
            return new FormationAnchor(leader, new Position(x, y, z));
        }

        /// <summary>
        /// Returns:
        ///     FormationAnchor: The archived offset for a server-authored formed slot, or null.
        /// </summary>
        public static FormationAnchor ForGroupMember(uint leaderId, string shape, uint slot)
        {
            if (leaderId == 0 || slot < 1 || slot > 5) return null;
            Position[] offsets = shape == "Slot Back" ? new[] {
                new Position(-300, -200, 50), new Position(-300, 200, 50),
                new Position(-300, 0, 50), new Position(-400, -200, 100),
                new Position(-400, 200, 100) } :
                shape == "Block" ? new[] {
                    new Position(0, -200, 0), new Position(0, 200, 0),
                    new Position(-300, 0, 100), new Position(-300, -200, 100),
                    new Position(-300, 200, 100) } :
                shape == "Pipe" ? new[] {
                    new Position(-300, -200, 0), new Position(-300, 200, 0),
                    new Position(-400, 0, 0), new Position(-600, -200, 0),
                    new Position(-600, 200, 0) } : null;
            return offsets == null ? null : new FormationAnchor(leaderId, offsets[(int)slot - 1]);
        }

        /// <summary>
        /// Returns:
        ///     Position: The leader-relative position rotated into sector coordinates.
        /// </summary>
        public Position Project(ShipActor leader)
        { return Project(leader.Position, leader.Orientation); }

        /// <summary>
        /// Returns:
        ///     Position: The offset projected from an authoritative leader pose.
        /// </summary>
        public Position Project(Position leaderPosition, float[] orientation)
        {
            float[] q = orientation;
            float x = Offset.X, y = Offset.Y, z = Offset.Z;
            float ux = q[1] * z - q[2] * y;
            float uy = q[2] * x - q[0] * z;
            float uz = q[0] * y - q[1] * x;
            float vx = q[1] * uz - q[2] * uy;
            float vy = q[2] * ux - q[0] * uz;
            float vz = q[0] * uy - q[1] * ux;
            return new Position(leaderPosition.X + x + 2 * (q[3] * ux + vx),
                leaderPosition.Y + y + 2 * (q[3] * uy + vy),
                leaderPosition.Z + z + 2 * (q[3] * uz + vz));
        }
    }

    public sealed class MapObject
    {
        public readonly uint Id;
        public readonly string Name;
        public readonly byte Kind;
        public readonly ushort Asset;
        public readonly Position Position;
        public readonly bool Navigable;
        public readonly float Scale;
        public readonly float[] Orientation;

        /// <summary>Decode a validated map-object record.</summary>
        public MapObject(byte[] body)
        {
            Wire.Require(body, 0, 60);
            int length = Wire.U16(body, 58);
            if (length == 0 || body.Length != 60 + length) throw new InvalidDataException("Invalid map-object name.");
            Id = Wire.U32(body, 0); Kind = body[4]; Asset = Wire.U16(body, 5);
            Name = Wire.Text(body, 60, length);
            Scale = Wire.Float(body, 7);
            if (Scale <= 0 || Scale > 10000) throw new InvalidDataException("Invalid object scale.");
            Orientation = new[] { Wire.Float(body, 37), Wire.Float(body, 41), Wire.Float(body, 45), Wire.Float(body, 49) };
            Position = new Position(Wire.Float(body, 25), Wire.Float(body, 29), Wire.Float(body, 33));
            if (Math.Abs(Position.X) >= 1e9 || Math.Abs(Position.Y) >= 1e9 || Math.Abs(Position.Z) >= 1e9)
                throw new InvalidDataException("Map coordinates exceed limits.");
            Navigable = (body[57] & 0x10) != 0;
        }
    }

    /// <summary>One station room advertised by the native interior layout.</summary>
    public sealed class StationRoom
    {
        public readonly uint Index, Style;
        /// <summary>Keep a room's native index and visual style.</summary>
        public StationRoom(uint index, uint style) { Index = index; Style = style; }
    }

    /// <summary>One NPC and its room in the server's station layout.</summary>
    public sealed class StationContact
    {
        public readonly uint Id, Room;
        /// <summary>Keep the contact's server identity and placement.</summary>
        public StationContact(uint id, uint room) { Id = id; Room = room; }
    }

    /// <summary>A player avatar visible in the current station interior.</summary>
    public sealed class StationAvatar
    {
        public readonly uint Id, Room;
        public readonly string Name;
        public readonly Position Position;
        public readonly float Orientation;

        /// <summary>Keep one immutable room and pose report for an interior peer.</summary>
        public StationAvatar(uint id, string name, uint room = uint.MaxValue, Position position = default,
            float orientation = 0)
        { Id = id; Name = name; Room = room; Position = position; Orientation = orientation; }

        /// <returns>
        /// StationAvatar: The same avatar after a room or pose update.
        /// </returns>
        public StationAvatar WithRoom(uint room)
        { return new StationAvatar(Id, Name, room, Position, Orientation); }

        /// <returns>
        /// StationAvatar: The same avatar after a native station pose report.
        /// </returns>
        public StationAvatar WithPose(byte[] body)
        {
            if (body.Length != 28 || Wire.U32(body, 0) != Id)
                throw new InvalidDataException("Invalid station avatar pose.");
            return new StationAvatar(Id, Name, Wire.U32(body, 24),
                new Position(Wire.Float(body, 8), Wire.Float(body, 12), Wire.Float(body, 16)),
                Wire.Float(body, 4));
        }
    }

    /// <summary>Server-owned station identity and available interior services.</summary>
    public sealed class StationState
    {
        public readonly uint Id, Kind, Rooms, Terminals;
        public readonly IReadOnlyList<uint> NpcIds;
        public readonly IReadOnlyList<StationRoom> RoomList;
        public readonly IReadOnlyList<StationContact> Contacts;
        public readonly uint CurrentRoom;
        public readonly string Name;

        /// <summary>Keep station details immutable across display snapshots.</summary>
        public StationState(uint id, uint kind = 0, uint terminals = 0, StationRoom[] rooms = null,
            StationContact[] contacts = null, uint currentRoom = uint.MaxValue, string name = "")
        {
            Id = id; Kind = kind; Terminals = terminals;
            RoomList = Array.AsReadOnly((StationRoom[])(rooms ?? Array.Empty<StationRoom>()).Clone());
            Contacts = Array.AsReadOnly((StationContact[])(contacts ?? Array.Empty<StationContact>()).Clone());
            Rooms = (uint)RoomList.Count; NpcIds = Array.AsReadOnly(Contacts.Select(contact => contact.Id).ToArray());
            CurrentRoom = currentRoom; Name = name;
        }

        /// <returns>The same station with its authoritative interior layout counts.</returns>
        public StationState WithLayout(byte[] body)
        {
            Wire.Require(body, 0, 8);
            uint kind = Wire.U32(body, 0), rooms = Wire.U32(body, 4);
            if (rooms == 0 || rooms > 5) throw new InvalidDataException("Invalid station room count.");
            var roomList = new StationRoom[rooms];
            for (int i = 0; i < roomList.Length; i++)
            {
                int roomOffset = 8 + i * 28;
                Wire.Require(body, roomOffset, 28);
                roomList[i] = new StationRoom(Wire.U32(body, roomOffset), Wire.U32(body, roomOffset + 4));
            }
            int offset = checked(8 + (int)rooms * 28);
            Wire.Require(body, offset, 4);
            uint terminals = Wire.U32(body, offset);
            if (terminals > 15) throw new InvalidDataException("Invalid station terminal count.");
            offset += 4 + (int)terminals * 16;
            Wire.Require(body, offset, 4);
            uint npcs = Wire.U32(body, offset);
            if (npcs > 30 || offset + 4 + (int)npcs * 265 != body.Length)
                throw new InvalidDataException("Invalid station NPC layout.");
            var contacts = new StationContact[(int)npcs];
            for (int i = 0; i < contacts.Length; i++)
            {
                int contactOffset = offset + 4 + i * 265;
                contacts[i] = new StationContact(Wire.U32(body, contactOffset + 8), Wire.U32(body, contactOffset));
            }
            uint currentRoom = roomList.Any(room => room.Index == CurrentRoom) ? CurrentRoom : uint.MaxValue;
            return new StationState(Id, kind, terminals, roomList, contacts, currentRoom, Name);
        }

        /// <returns>The same station with its announced name.</returns>
        public StationState WithName(string name)
        { return new StationState(Id, Kind, Terminals, RoomList.ToArray(), Contacts.ToArray(), CurrentRoom, name); }

        /// <returns>
        /// StationState: A station view after entering an advertised room.
        /// </returns>
        public StationState WithRoom(uint room)
        {
            if (!RoomList.Any(entry => entry.Index == room)) throw new InvalidDataException("Unknown station room.");
            return new StationState(Id, Kind, Terminals, RoomList.ToArray(), Contacts.ToArray(), room, Name);
        }
    }

    /// <summary>Worker-owned contents and depletion state for a mineable resource.</summary>
    public sealed class ResourceState
    {
        public readonly uint Id, Level;
        public readonly string Name;
        public readonly float Fraction;
        public readonly ItemState[] Items;

        /// <summary>Keep one authoritative resource revision together.</summary>
        public ResourceState(uint id, string name, uint level, float fraction, ItemState[] items)
        { Id = id; Name = name; Level = level; Fraction = fraction; Items = items; }
    }

    /// <summary>Immutable resource rows for the main-thread prospecting panel.</summary>
    public sealed class ResourceView
    {
        public readonly uint Id, Level;
        public readonly string Name;
        public readonly float Fraction;
        public readonly ItemView[] Items;

        /// <summary>Resolve server-owned ore templates for display.</summary>
        public ResourceView(ResourceState state, IReadOnlyDictionary<uint, ItemTemplateInfo> catalog)
        {
            Id = state.Id; Name = state.Name; Level = state.Level; Fraction = state.Fraction;
            Items = new ItemView[state.Items.Length];
            for (int i = 0; i < Items.Length; i++)
                if (state.Items[i]?.Present == true) Items[i] = new ItemView(i, state.Items[i], catalog);
        }
    }

    /// <summary>One server-offered NPC conversation branch.</summary>
    public sealed class DialogueChoice
    {
        public readonly byte Node;
        public readonly string Text;
        /// <summary>Keep the native node identity with its displayed choice.</summary>
        public DialogueChoice(byte node, string text) { Node = node; Text = text; }
    }

    /// <summary>Server-announced mission state for the current NPC conversation.</summary>
    public enum MissionDialogueKind { None, Offer, Objective, Updated, Completed }

    /// <summary>The active station conversation and its authoritative choices.</summary>
    public sealed class DialogueState
    {
        public readonly uint NpcId;
        public readonly string Text;
        public readonly DialogueChoice[] Choices;
        public readonly bool VendorOpen;
        public readonly MissionDialogueKind MissionKind;
        public readonly string MissionName;
        public readonly uint MissionStage;

        /// <summary>Store one immutable conversation view.</summary>
        public DialogueState(uint npcId, string text = "", DialogueChoice[] choices = null,
            bool vendorOpen = false, MissionDialogueKind missionKind = MissionDialogueKind.None,
            string missionName = "", uint missionStage = 0)
        {
            NpcId = npcId; Text = text; Choices = choices ?? Array.Empty<DialogueChoice>();
            VendorOpen = vendorOpen; MissionKind = missionKind; MissionName = missionName;
            MissionStage = missionStage;
        }

        /// <returns>A conversation updated from the native talk-tree packet.</returns>
        public DialogueState WithTree(byte[] body)
        {
            int end = Array.IndexOf(body, (byte)0);
            if (end < 0 || end + 2 >= body.Length || body[end + 1] != 0)
                throw new InvalidDataException("Invalid NPC dialogue text.");
            int count = body[end + 2], offset = end + 3;
            if (count > 20) throw new InvalidDataException("Too many NPC choices.");
            var choices = new DialogueChoice[count];
            for (int i = 0; i < count; i++)
            {
                Wire.Require(body, offset, 4);
                uint node = Wire.U32(body, offset); offset += 4;
                if (node > byte.MaxValue) throw new InvalidDataException("Invalid NPC choice identity.");
                int labelEnd = Array.IndexOf(body, (byte)0, offset);
                if (labelEnd < 0) throw new InvalidDataException("Unterminated NPC choice.");
                choices[i] = new DialogueChoice((byte)node, Wire.Text(body, offset, labelEnd - offset));
                offset = labelEnd + 1;
            }
            if (offset != body.Length) throw new InvalidDataException("Unexpected NPC dialogue data.");
            return new DialogueState(NpcId, Wire.Text(body, 0, end), choices, VendorOpen,
                MissionKind, MissionName, MissionStage);
        }

        /// <returns>The same conversation with its vendor status changed.</returns>
        public DialogueState WithVendor(bool open)
        { return new DialogueState(NpcId, Text, Choices, open, MissionKind, MissionName, MissionStage); }

        /// <summary>
        /// Returns:
        ///     DialogueState: This conversation with an authoritative mission notice, if present.
        /// </summary>
        public DialogueState WithMissionMessage(string message)
        {
            MissionDialogueKind kind;
            string name;
            uint stage = 0;
            if (message.StartsWith("Mission offer: ", StringComparison.Ordinal) && message.EndsWith(".", StringComparison.Ordinal))
            { kind = MissionDialogueKind.Offer; name = message.Substring(15, message.Length - 16); }
            else if (message.StartsWith("Mission objective: ", StringComparison.Ordinal) && message.EndsWith(".", StringComparison.Ordinal))
            { kind = MissionDialogueKind.Objective; name = message.Substring(19, message.Length - 20); }
            else if (message.StartsWith("Mission completed: ", StringComparison.Ordinal) && message.EndsWith(".", StringComparison.Ordinal))
            { kind = MissionDialogueKind.Completed; name = message.Substring(19, message.Length - 20); }
            else if (message.StartsWith("Mission ", StringComparison.Ordinal) && message.EndsWith(".", StringComparison.Ordinal))
            {
                int marker = message.LastIndexOf(": stage ", StringComparison.Ordinal);
                if (marker < 9 || !uint.TryParse(message.Substring(marker + 8, message.Length - marker - 9), out stage))
                    return this;
                kind = MissionDialogueKind.Updated;
                name = message.Substring(8, marker - 8);
            }
            else return this;
            if (name.Length == 0) return this;
            return new DialogueState(NpcId, Text, Choices, VendorOpen, kind, name, stage);
        }
    }

    /// <summary>An immutable visible ship contact copied across the network boundary.</summary>
    public sealed class ShipActor
    {
        public readonly uint Id;
        public readonly ushort Asset;
        public readonly float Scale;
        public readonly string Name;
        public readonly Position Position;
        public readonly float[] Orientation;
        public readonly bool HasPosition;
        public readonly bool IsPlayer, Hostile;
        public readonly float Shield, Hull, MaxHull;

        /// <summary>Store a server actor and its latest known pose.</summary>
        public ShipActor(uint id, ushort asset, float scale, string name, Position position, float[] orientation, bool hasPosition, bool isPlayer = true, bool hostile = false, float shield = -1, float hull = -1, float maxHull = -1)
        {
            Id = id; Asset = asset; Scale = scale; Name = name; Position = position;
            Orientation = (float[])orientation.Clone(); HasPosition = hasPosition;
            IsPlayer = isPlayer; Hostile = hostile; Shield = shield; Hull = hull; MaxHull = maxHull;
        }

        /// <returns>A new actor with an authoritative pose, leaving old snapshots unchanged.</returns>
        public ShipActor WithPose(byte[] body)
        {
            var position = new Position(Wire.Float(body, 10), Wire.Float(body, 14), Wire.Float(body, 18));
            if (Math.Abs(position.X) >= 1e9 || Math.Abs(position.Y) >= 1e9 || Math.Abs(position.Z) >= 1e9)
                throw new InvalidDataException("Player coordinates exceed limits.");
            var orientation = new[] { Wire.Float(body, 22), Wire.Float(body, 26), Wire.Float(body, 30), Wire.Float(body, 34) };
            return new ShipActor(Id, Asset, Scale, Name, position, orientation, true, IsPlayer, Hostile, Shield, Hull, MaxHull);
        }

        /// <summary>
        /// Returns:
        ///     ShipActor: A visual pose derived from the leader's authoritative group slot.
        /// </summary>
        public ShipActor WithFormationPose(Position position, float[] orientation)
        { return new ShipActor(Id, Asset, Scale, Name, position, orientation, true, IsPlayer, Hostile, Shield, Hull, MaxHull); }

        /// <returns>A new actor with its public avatar name.</returns>
        public ShipActor WithName(string name)
        { return new ShipActor(Id, Asset, Scale, name, Position, Orientation, HasPosition, IsPlayer, Hostile, Shield, Hull, MaxHull); }
    }

    /// <summary>A server-published shot between visible ship identities.</summary>
    public sealed class ShotEvent
    {
        public readonly long Sequence;
        public readonly uint Source, Target;
        public readonly ushort Asset;
        public readonly ushort Duration;
        public readonly long ReceivedAt;
        /// <summary>Store the native weapon-effect identity and display duration.</summary>
        public ShotEvent(long sequence, uint source, uint target, ushort asset, ushort duration)
        {
            Sequence = sequence; Source = source; Target = target; Asset = asset; Duration = duration;
            ReceivedAt = System.Diagnostics.Stopwatch.GetTimestamp();
        }
    }

    /// <summary>Native damage category reported by the combat server.</summary>
    public enum DamageKind { Impact, Explosive, Plasma, Energy, Emp, Chemical, Absolute, System, Psionic }

    /// <summary>Authoritative hit result; normal and missed shots both use inflicted zero.</summary>
    public enum DamageOutcome { Miss, Hit, Critical }

    /// <summary>A server-reported combat result for transient damage feedback.</summary>
    public sealed class DamageEvent
    {
        public readonly long Sequence;
        public readonly uint Source, Target;
        public readonly float Amount, Modifier;
        public readonly DamageKind Kind;
        public readonly DamageOutcome Outcome;
        public readonly long ReceivedAt;

        /// <summary>Store one bounded native damage report, including misses and critical hits.</summary>
        public DamageEvent(long sequence, uint source, uint target, float amount,
            float modifier, DamageKind kind, DamageOutcome outcome)
        {
            Sequence = sequence; Source = source; Target = target;
            Amount = amount; Modifier = modifier; Kind = kind; Outcome = outcome;
            ReceivedAt = System.Diagnostics.Stopwatch.GetTimestamp();
        }
    }

    public enum ConfirmationKind { Group, Jumpstart, Other }

    /// <summary>A native yes/no prompt retained independently of group state.</summary>
    public sealed class ConfirmationPrompt
    {
        public readonly ConfirmationKind Kind;
        public readonly string Text;

        /// <summary>Store the immutable prompt shown to the player.</summary>
        public ConfirmationPrompt(ConfirmationKind kind, string text)
        { Kind = kind; Text = text; }

        /// <summary>Decode a native yes/no packet, preserving its prompt type.</summary>
        /// <returns>
        /// ConfirmationPrompt: Validated group or ability confirmation.
        /// </returns>
        public static ConfirmationPrompt Decode(byte[] body)
        {
            Wire.Require(body, 0, 4);
            int length = Wire.U16(body, 0);
            if (body[2] != 1 || length < 2 || length != body.Length - 3 || body[body.Length - 1] != 0)
                throw new InvalidDataException("Invalid native confirmation.");
            string message = Wire.Text(body, 3, length - 1);
            if (message.Length == 0) throw new InvalidDataException("Empty native confirmation.");
            ConfirmationKind kind = message.StartsWith("Do you want to accept a jumpstart level ", StringComparison.OrdinalIgnoreCase)
                ? ConfirmationKind.Jumpstart
                : message.IndexOf("join their group", StringComparison.OrdinalIgnoreCase) >= 0
                    ? ConfirmationKind.Group : ConfirmationKind.Other;
            return new ConfirmationPrompt(kind, message);
        }
    }

    public sealed class World
    {
        public uint PlayerId { get; private set; }
        private long nextShot, nextDamage;
        public readonly Queue<DamageEvent> Damages = new Queue<DamageEvent>();
        private readonly HashSet<uint> attackerIds = new HashSet<uint>();
        public uint[] AttackerIds => attackerIds.OrderBy(id => id).ToArray();
        public Dictionary<uint, ItemState[]> Loot { get; } = new Dictionary<uint, ItemState[]>();
        public uint OpenLootId { get; private set; }
        /// <summary>Record the server acknowledgement of a wreck window.</summary>
        public void SetOpenLoot(uint id)
        {
            OpenLootId = id;
            if (id == 0) LootActivity = null;
            else { ResourceTargetId = 0; ResourceWindowOpen = false; MiningProspect = null; }
        }
        public readonly Queue<ShotEvent> Shots = new Queue<ShotEvent>();
        public readonly Dictionary<ulong, NativeObjectEffect> ObjectEffects =
            new Dictionary<ulong, NativeObjectEffect>();
        public readonly HashSet<uint> ShieldChargingPeers = new HashSet<uint>();
        public GroupState Group { get; private set; } = new GroupState(false, Array.Empty<GroupMember>());
        public FormationAnchor Formation { get; private set; }
        public TargetVerbs Verbs { get; private set; } = TargetVerbs.Empty;
        private ConfirmationPrompt confirmation;
        private long confirmationAt;
        public ConfirmationPrompt Confirmation =>
            (System.Diagnostics.Stopwatch.GetTimestamp() - confirmationAt) / (double)System.Diagnostics.Stopwatch.Frequency < 30
                ? confirmation : null;
        public string Invitation => Confirmation?.Kind == ConfirmationKind.Group ? Confirmation.Text : "";
        /// <summary>Dismiss a server prompt after a local answer.</summary>
        public void ClearConfirmation() { confirmation = null; }
        /// <summary>Dismiss a group invite when joining a group.</summary>
        public void ClearInvitation() { if (Confirmation?.Kind == ConfirmationKind.Group) ClearConfirmation(); }
        public readonly Queue<string> Messages = new Queue<string>();
        public EquipmentState Equipment { get; } = new EquipmentState();
        public readonly uint SectorId;
        public GalaxyCatalogPage GalaxyCatalog;
        public GalaxyRoutePlan GalaxyRoute;
        public GalaxyTravelView GalaxyTravel = new GalaxyTravelView(false, false, 0, "Choose a destination.");

        /// <summary>Bind a fresh worker world to the authenticated sector handoff identity.</summary>
        public World(uint sectorId = 0) { SectorId = sectorId; }

        public bool AggregateStation { get; private set; }
        public uint[] MarketItemTemplates = Array.Empty<uint>();
        public Dictionary<uint, ComponentMarketView> ComponentMarkets { get; } = new Dictionary<uint, ComponentMarketView>();
        public Dictionary<uint, ItemTemplateInfo> ItemCatalog { get; } = new Dictionary<uint, ItemTemplateInfo>();
        public Dictionary<uint, ShipActor> Mobs { get; } = new Dictionary<uint, ShipActor>();
        public string Sector { get; private set; } = "Connecting";
        public StationState Station { get; private set; }
        public ProductionState Production { get; private set; } = new ProductionState();
        public DialogueState Dialogue { get; private set; }
        public ulong Credits { get; private set; }
        public MissionState[] Missions { get; private set; } = new MissionState[12];
        public ReputationState Reputation { get; private set; }
        public ReputationChange RecentReputationChange { get; private set; }
        public ExperienceState Experience { get; private set; }
        public SkillRank[] Skills { get; private set; } = Array.Empty<SkillRank>();
        public uint[] AvailableAbilities { get; private set; } = Array.Empty<uint>();
        public uint CastingAbilityId { get; private set; } = uint.MaxValue;
        public string ActiveDeviceLabel { get; private set; } = "";
        public long ActiveDeviceEndsAt { get; private set; }
        private readonly TimedDeviceBuff[] buffs =
            Enumerable.Repeat(TimedDeviceBuff.Empty, 16).ToArray();
        private ActiveDeviceStatus[] activeDevices = Array.Empty<ActiveDeviceStatus>();
        public bool HasActiveDeviceSnapshot { get; private set; }
        public IReadOnlyList<ActiveDeviceStatus> ActiveDevices => activeDevices;
        private readonly System.Collections.Generic.List<int> pendingBuffVisuals =
            new System.Collections.Generic.List<int>();
        public TimedDeviceBuff ActiveBuff => buffs[0];
        public TimedDeviceBuff PeerBuff => buffs[1];
        public TimedDeviceBuff ShieldBuff => buffs[2];
        public TimedDeviceBuff ReactorBuff => buffs[3];
        public TimedDeviceBuff SignatureBuff => buffs[4];
        public TimedDeviceBuff RallyBuff => buffs[5];
        /// Returns:
        ///     TimedDeviceBuff: The immutable revision in one server-owned status slot.
        public TimedDeviceBuff BuffAt(int slot) { return buffs[slot]; }

        /// <returns>ushort: The server's visual descriptor for a Rally buff token.</returns>
        private static ushort RallyDescriptor(string name)
        {
            switch (name)
            {
                case "Damage_Tactics": return 333;
                case "Defense_Tactics": return 675;
                case "Firing_Tactics": return 676;
                case "Stealth_Tactics": return 327;
                default: return 0;
            }
        }
        public bool TurboWeaponsActive => HasActiveDeviceSnapshot ?
            activeDevices.Any(device => device.Name == "Turbo Weapons") :
            ActiveDeviceLabel == "Turbo Weapons";
        public long CastEndsAt { get; private set; }
        public Dictionary<uint, long> AbilityCooldownEnds { get; } = new Dictionary<uint, long>();
        public ItemState[] Vendor { get; private set; } = Array.Empty<ItemState>();
        public Dictionary<uint, ResourceState> Resources { get; } = new Dictionary<uint, ResourceState>();
        public uint ResourceTargetId { get; private set; }
        public bool ResourceWindowOpen { get; private set; }
        public MiningActivity MiningProspect { get; private set; }
        public MiningActivity MiningTractor { get; private set; }
        public MiningActivity MiningActivity => MiningProspect ?? MiningTractor;
        public LootActivity LootActivity { get; private set; }
        public WarpState Warp { get; private set; } = WarpState.Decode(0, 0);
        /// <summary>Track the selected resource while awaiting its native prospecting window.</summary>
        public void SetResourceTarget(uint id)
        {
            ResourceTargetId = id; ResourceWindowOpen = false;
            if (id != 0) { OpenLootId = 0; LootActivity = null; }
        }
        /// <summary>Track the requested NPC while waiting for its talk tree.</summary>
        public void StartDialogue(uint npcId) { Dialogue = new DialogueState(npcId); }
        /// <summary>Clear the local conversation after the server closes it.</summary>
        public void CloseDialogue() { Dialogue = null; }
        /// <summary>Record the room selected through an owned station movement packet.</summary>
        public void SetStationRoom(uint room) { Station = Station?.WithRoom(room); }
        public Dictionary<uint, MapObject> Objects { get; } = new Dictionary<uint, MapObject>();
        public Dictionary<uint, ShipActor> Players { get; } = new Dictionary<uint, ShipActor>();
        public Dictionary<uint, StationAvatar> StationAvatars { get; } = new Dictionary<uint, StationAvatar>();
        public Position Position { get; private set; }
        public float[] Orientation { get; private set; } = { 0, 0, 0, 1 };
        public bool HasPosition { get; private set; }
        public float Speed { get; private set; }
        public float TargetSpeed { get; private set; }
        public float Acceleration { get; private set; }
        public float AngularY { get; private set; }
        public float AngularZ { get; private set; }
        private uint lastServerTick;
        private long lastServerTickAt;

        /// <summary>
        /// Returns:
        ///     uint?: The server tick extrapolated for display, or null before own movement arrives.
        /// </summary>
        public uint? EstimatedServerTick(long now)
        {
            if (lastServerTickAt == 0) return null;
            long elapsedMs = (long)Math.Max(0,
                (now - lastServerTickAt) * 1000d / System.Diagnostics.Stopwatch.Frequency);
            return unchecked(lastServerTick + (uint)Math.Min(elapsedMs, uint.MaxValue));
        }

        /// <summary>Apply the server's leader pose and native offset to a formed wing ship.</summary>
        private void ApplyFormationPose()
        {
            if (Formation == null || !Group.Formed || !Players.TryGetValue(Formation.LeaderId, out ShipActor leader) ||
                !leader.HasPosition) return;
            Position = Formation.Project(leader);
            Orientation = (float[])leader.Orientation.Clone();
            HasPosition = true;
        }

        /// <summary>Present formed peers at server-authored slots around this leader pose.</summary>
        private void ApplyFormedPeers()
        {
            if (!Group.IsLeader || !HasPosition || Group.Formation.Length == 0) return;
            foreach (GroupMember member in Group.Members)
            {
                if (!member.Formed || !Players.TryGetValue(member.Id, out ShipActor peer) ||
                    !peer.HasPosition) continue;
                FormationAnchor offset = FormationAnchor.ForGroupMember(PlayerId,
                    Group.Formation, member.FormationSlot);
                if (offset != null)
                    Players[member.Id] = peer.WithFormationPose(offset.Project(Position, Orientation), Orientation);
            }
        }

        /// <summary>Update server-authoritative display state.</summary>
        public void Apply(Frame frame, uint player)
        {
            PlayerId = player;
            byte[] b = frame.Body;
            switch (frame.Opcode)
            {
                case 0x97:
                    if (b.Length < 15 || Wire.U32(b, 0) != 4 || Wire.U32(b, 4) != b.Length - 8)
                        throw new InvalidDataException("Invalid sector description.");
                    int firstEnd = Array.IndexOf(b, (byte)0, 12);
                    if (firstEnd < 0 || firstEnd + 1 >= b.Length) throw new InvalidDataException("Missing sector name.");
                    string name = Wire.Text(b, firstEnd + 1, b.Length - firstEnd - 1);
                    if (name.Length == 0) throw new InvalidDataException("Empty sector name.");
                    if (Sector != name) { Objects.Clear(); Players.Clear(); Mobs.Clear(); ShieldChargingPeers.Clear(); attackerIds.Clear(); StationAvatars.Clear(); Loot.Clear(); Resources.Clear(); ResourceTargetId = 0; ResourceWindowOpen = false; MiningProspect = null; MiningTractor = null; LootActivity = null; Formation = null; Warp = WarpState.Decode(0, 0); lastServerTickAt = 0; OpenLootId = 0; Verbs = TargetVerbs.Empty; Shots.Clear(); Damages.Clear(); ObjectEffects.Clear(); ClearConfirmation(); nextShot = nextDamage = 0; }
                    Sector = name;
                    ComponentMarkets.Clear();
                    MarketItemTemplates = Array.Empty<uint>();
                    break;
                case 0x4f:
                    if (b.Length != 6) throw new InvalidDataException("Invalid station identity.");
                    Station = b[4] == 0 ? new StationState(Wire.U32(b, 0)) : null;
                    if (Station != null) { ActiveDeviceLabel = ""; ActiveDeviceEndsAt = 0; for (int slot = 0; slot < buffs.Length; slot++) buffs[slot] = TimedDeviceBuff.Empty; pendingBuffVisuals.Clear(); Formation = null; ObjectEffects.Clear(); ShieldChargingPeers.Clear(); attackerIds.Clear(); }
                    Production = new ProductionState();
                    StationAvatars.Clear();
                    Dialogue = null; Vendor = Array.Empty<ItemState>();
                    ResourceTargetId = 0; ResourceWindowOpen = false; MiningProspect = null; MiningTractor = null;
                    OpenLootId = 0; LootActivity = null;
                    if (Station != null) Warp = WarpState.Decode(0, 0);
                    if (Station != null) Sector = "Station " + Station.Id;
                    break;
                case 0x7f:
                    if (b.Length != 4) throw new InvalidDataException("Invalid production lab identity.");
                    uint labId = Wire.U32(b, 0, true);
                    if (Station != null && labId == (player | 0x80000000))
                        Production.LabId = labId;
                    break;
                case 0x52:
                    if (Station == null) throw new InvalidDataException("Station layout arrived before its identity.");
                    Station = Station.WithLayout(b);
                    break;
                case 0x54:
                    Dialogue = (Dialogue ?? new DialogueState(0)).WithTree(b);
                    break;
                case 0x56:
                    if (b.Length != 4) throw new InvalidDataException("Invalid NPC control.");
                    int control = unchecked((int)Wire.U32(b, 0));
                    if (control == -32) Dialogue = null;
                    else if (control == 6 && Dialogue != null) Dialogue = Dialogue.WithVendor(true);
                    break;
                case 0x0b:
                    Wire.Require(b, 0, 23);
                    if (Wire.U16(b, 0) != 7) break;
                    uint source = Wire.U32(b, 2), victim = Wire.U32(b, 6);
                    ushort duration = Wire.U16(b, 21);
                    if (duration > 0)
                    {
                        Shots.Enqueue(new ShotEvent(++nextShot, source, victim, Wire.U16(b, 10), duration));
                        while (Shots.Count > 32) Shots.Dequeue();
                    }
                    break;
                case 0x64:
                    if (b.Length != 24) throw new InvalidDataException("Invalid native damage report.");
                    float amount = Wire.Float(b, 0);
                    float modifier = Wire.Float(b, 4);
                    uint kind = Wire.U32(b, 8), inflicted = Wire.U32(b, 12);
                    if (amount < 0 || amount >= 1e9f || Math.Abs(modifier) >= 1e9f ||
                        kind > 8 || (inflicted != 0 && inflicted != 1 && inflicted != 3))
                        throw new InvalidDataException("Invalid native damage values.");
                    DamageOutcome outcome = inflicted == 3 ? DamageOutcome.Critical :
                        amount == 0 && inflicted == 0 ? DamageOutcome.Miss : DamageOutcome.Hit;
                    Damages.Enqueue(new DamageEvent(++nextDamage, Wire.U32(b, 16), Wire.U32(b, 20),
                        amount, modifier, (DamageKind)kind, outcome));
                    while (Damages.Count > 32) Damages.Dequeue();
                    break;
                case 0x8b:
                    if (b.Length != 9 || b[4] != 1 || Wire.U32(b, 0) > 1 || Wire.U32(b, 5) == 0)
                        throw new InvalidDataException("Invalid native attacker update.");
                    uint attackerId = Wire.U32(b, 5);
                    if (Wire.U32(b, 0) == 1)
                    {
                        if (!attackerIds.Contains(attackerId) && attackerIds.Count >= 256)
                            throw new InvalidDataException("Attacker list limit exceeded.");
                        attackerIds.Add(attackerId);
                    }
                    else attackerIds.Remove(attackerId);
                    break;
                case 0x2018:
                    var item = new MapObject(b);
                    if (!Objects.ContainsKey(item.Id) && Objects.Count >= 10000) throw new InvalidDataException("World object limit exceeded.");
                    Objects[item.Id] = item;
                    break;
                case 0x2012:
                    MiningActivity prospect = MiningActivity.FromProspect(b, player);
                    if (prospect != null) MiningProspect = prospect;
                    break;
                case 0x2013:
                    MiningActivity tractor = MiningActivity.FromTractor(b, player, MiningProspect,
                        ResourceTargetId);
                    if (tractor != null) { MiningTractor = tractor; MiningProspect = null; }
                    break;
                case 0x2014:
                    uint corpseId = OpenLootId;
                    if (corpseId == 0 && Objects.TryGetValue(Equipment.TargetId, out MapObject selectedCorpse) &&
                        selectedCorpse.Kind == 25) corpseId = selectedCorpse.Id;
                    LootActivity looting = LootActivity.FromTractor(b, player, corpseId);
                    if (looting != null) LootActivity = looting;
                    break;
                case 0x2030:
                    activeDevices = ActiveDeviceStatus.DecodeSnapshot(b);
                    HasActiveDeviceSnapshot = true;
                    break;
                case 0x2031:
                    if (b.Length != 5 || b[4] > 1)
                        throw new InvalidDataException("Invalid peer Shield Charging status.");
                    uint chargingPeer = Wire.U32(b, 0);
                    if (b[4] == 1 && Players.ContainsKey(chargingPeer))
                        ShieldChargingPeers.Add(chargingPeer);
                    else ShieldChargingPeers.Remove(chargingPeer);
                    break;
                case 0x203b:
                    if (b.Length != 8 || Wire.U32(b, 0) != player || Wire.U32(b, 4) > 1)
                        throw new InvalidDataException("Invalid station aggregation acknowledgement.");
                    AggregateStation = Wire.U32(b, 4) == 1;
                    break;
                case 0x2039:
                    Wire.Require(b, 0, 8);
                    uint stockStation = Wire.U32(b, 0), stockCount = Wire.U32(b, 4);
                    if (stockCount > 2048 || b.Length != 8 + stockCount * 4)
                        throw new InvalidDataException("Invalid component stock response.");
                    if (stockStation == (Station?.Id ?? 0))
                    {
                        MarketItemTemplates = new uint[stockCount];
                        for (int index = 0; index < MarketItemTemplates.Length; index++)
                            MarketItemTemplates[index] = Wire.U32(b, 8 + index * 4);
                    }
                    break;
                case 0x2035:
                    ComponentMarketView market = ComponentMarketView.Decode(b);
                    if (!ComponentMarkets.ContainsKey(market.Template) && ComponentMarkets.Count >= 1024)
                        ComponentMarkets.Clear();
                    ComponentMarkets[market.Template] = market;
                    break;
                case 0x2032:
                    if (b.Length != 16) throw new InvalidDataException("Invalid recipe page metadata.");
                    uint recipeLab = Wire.U32(b, 0), recipePage = Wire.U32(b, 4);
                    uint recipeTotal = Wire.U32(b, 8), recipePageSize = Wire.U32(b, 12);
                    if (recipePageSize != 500 || recipeTotal > 100000 ||
                        recipePage > (recipeTotal == 0 ? 0 : (recipeTotal - 1) / recipePageSize))
                        throw new InvalidDataException("Recipe page metadata exceeds its bounds.");
                    if (recipeLab == Production.LabId)
                    {
                        Production.PageIndex = recipePage; Production.TotalFormulas = recipeTotal;
                        Production.PageSize = recipePageSize;
                    }
                    break;
                case 0x09:
                    NativeObjectEffect objectEffect = NativeObjectEffect.Decode(b);
                    if (objectEffect != null)
                    {
                        if (ObjectEffects.Count >= 256) ObjectEffects.Clear();
                        ObjectEffects[((ulong)objectEffect.OwnerId << 32) | objectEffect.EffectId] = objectEffect;
                    }
                    if (objectEffect?.OwnerId == player)
                    {
                        if (buffs[5].Active && RallyDescriptor(buffs[5].Name) == objectEffect.Descriptor)
                            buffs[5] = buffs[5].WithVisual(objectEffect.EffectId,
                                objectEffect.Descriptor);
                        int slot = pendingBuffVisuals.FindIndex(index => buffs[index].Active &&
                            (index == 1 && objectEffect.Descriptor == 462 ||
                             (index == 2 || index == 3) && objectEffect.Descriptor == 553 ||
                             index == 0 && objectEffect.Descriptor == 553));
                        if (slot < 0 && objectEffect.Descriptor != 462 && objectEffect.Descriptor != 553 &&
                            objectEffect.Descriptor != 333 && objectEffect.Descriptor != 675 &&
                            objectEffect.Descriptor != 676 && objectEffect.Descriptor != 327)
                            slot = pendingBuffVisuals.FindIndex(index => index == 0 && buffs[0].Active);
                        if (slot >= 0)
                        {
                            int index = pendingBuffVisuals[slot];
                            buffs[index] = buffs[index].WithVisual(objectEffect.EffectId,
                                objectEffect.Descriptor);
                            pendingBuffVisuals.RemoveAt(slot);
                        }
                    }
                    break;
                case 0x0f:
                    if (b.Length != 4) throw new InvalidDataException("Invalid effect removal.");
                    uint removedEffect = Wire.U32(b, 0);
                    foreach (ulong key in ObjectEffects.Keys.Where(key => (uint)key == removedEffect).ToArray())
                        ObjectEffects.Remove(key);
                    if (MiningProspect?.EffectId == removedEffect) MiningProspect = null;
                    if (MiningTractor?.EffectId == removedEffect) MiningTractor = null;
                    if (LootActivity?.EffectId == Wire.U32(b, 0)) LootActivity = null;
                    for (int slot = 0; slot < buffs.Length; slot++)
                        if (buffs[slot].VisualDescriptor != 0 &&
                            buffs[slot].VisualEffectId == removedEffect)
                            buffs[slot] = buffs[slot].WithVisual(0, 0);
                    break;
                case 4:
                    Wire.Require(b, 0, 23);
                    uint actorId = Wire.U32(b, 0);
                    if (actorId == player || b[10] > 1) break;
                    float scale = Wire.Float(b, 4);
                    if (scale <= 0 || scale > 10000) throw new InvalidDataException("Invalid player scale.");
                    var actors = b[10] == 1 ? Players : Mobs;
                    if (!actors.ContainsKey(actorId) && actors.Count >= 1024)
                        throw new InvalidDataException("Visible player limit exceeded.");
                    actors[actorId] = new ShipActor(actorId, Wire.U16(b, 8), scale, (b[10] == 1 ? "Player " : "Mob ") + actorId,
                        default, new[] { 0f, 0f, 0f, 1f }, false, b[10] == 1);
                    break;
                case 0x1e:
                    confirmation = ConfirmationPrompt.Decode(b);
                    confirmationAt = System.Diagnostics.Stopwatch.GetTimestamp();
                    break;
                case 0x5c:
                    Verbs = TargetVerbs.Decode(b);
                    break;
                case 0x1d:
                    Wire.Require(b, 0, 3);
                    string message = Wire.Text(b, 3, b.Length - 3);
                    pendingBuffVisuals.Remove(0);
                    if (message.StartsWith("Prospected ", StringComparison.Ordinal)) MiningTractor = null;
                    if (message.StartsWith("Prospecting interrupted", StringComparison.Ordinal) ||
                        message.StartsWith("Reactor depleted while prospecting", StringComparison.Ordinal))
                        MiningProspect = null;
                    if (Dialogue != null) Dialogue = Dialogue.WithMissionMessage(message);
                    foreach (string label in DeviceEffects.Labels)
                    {
                        string prefix = label + " active for ";
                        const string suffix = " seconds.";
                        if (message.StartsWith(prefix, StringComparison.Ordinal) &&
                            message.Length > prefix.Length + suffix.Length &&
                            message.EndsWith(suffix, StringComparison.Ordinal) &&
                            uint.TryParse(message.Substring(prefix.Length,
                                message.Length - prefix.Length - suffix.Length), NumberStyles.None,
                                CultureInfo.InvariantCulture, out uint seconds) && seconds > 0 && seconds <= 3600)
                        {
                            ActiveDeviceLabel = label;
                            ActiveDeviceEndsAt = System.Diagnostics.Stopwatch.GetTimestamp() +
                                (long)(seconds * (double)System.Diagnostics.Stopwatch.Frequency);
                        }
                        else if (message == label + " is already active.")
                        {
                            if (ActiveDeviceLabel != label) ActiveDeviceEndsAt = 0;
                            ActiveDeviceLabel = label;
                        }
                        else if (message == label + " ended." && ActiveDeviceLabel == label)
                        { ActiveDeviceLabel = ""; ActiveDeviceEndsAt = 0; }
                    }
                    Messages.Enqueue(message);
                    if (Station != null && message.StartsWith("Welcome aboard ", StringComparison.Ordinal))
                    {
                        int end = message.IndexOf('.', 15);
                        if (end > 15) { Station = Station.WithName(message.Substring(15, end - 15)); Sector = Station.Name; }
                    }
                    while (Messages.Count > 6) Messages.Dequeue();
                    break;
                case 0x25:
                    ItemTemplateInfo template = ItemTemplateInfo.Decode(b);
                    if (!ItemCatalog.ContainsKey(template.Id) && ItemCatalog.Count >= 32768)
                        throw new InvalidDataException("Item template limit exceeded.");
                    ItemCatalog[template.Id] = template;
                    break;
                case 0x1b:
                    Wire.Require(b, 0, 8);
                    if (Wire.U32(b, 0) == 0)
                    {
                        ulong? credits = AuxState.ReadCredits(b);
                        if (credits.HasValue) Credits = credits.Value;
                        AuxState.ReadVault(b, Equipment);
                        MissionState[] missions = AuxState.ReadMissions(b, Missions);
                        if (missions != null) Missions = missions;
                        ReputationState reputation = AuxState.ReadReputation(b);
                        if (reputation != null)
                        {
                            ReputationChange change = null;
                            if (Reputation != null)
                                foreach (FactionStanding current in reputation.Standings)
                                    foreach (FactionStanding former in Reputation.Standings)
                                        if (current.Slot == former.Slot && current.Label == former.Label &&
                                            current.Value != former.Value)
                                            change = new ReputationChange(current.Label,
                                                current.Value - former.Value, current.Value);
                            RecentReputationChange = change;
                            Reputation = reputation;
                        }
                        ExperienceState experience = AuxState.ReadExperience(b, Experience);
                        if (experience != null) Experience = experience;
                        SkillRank[] skills = AuxState.ReadSkillRanks(b);
                        if (skills != null) Skills = skills;
                        uint[] abilities = AuxState.ReadAvailableAbilities(b);
                        if (abilities != null) AvailableAbilities = abilities;
                        AbilityProgress progress = AuxState.ReadAbilityProgress(b);
                        if (progress != null)
                        {
                            CastingAbilityId = progress.Id;
                            CastEndsAt = progress.Id == uint.MaxValue ? 0 :
                                System.Diagnostics.Stopwatch.GetTimestamp() +
                                (long)(progress.DurationMs * (double)System.Diagnostics.Stopwatch.Frequency / 1000);
                        }
                        SkillCooldown[] cooldowns = AuxState.ReadSkillCooldowns(b);
                        if (cooldowns != null)
                            foreach (SkillCooldown cooldown in cooldowns)
                                AbilityCooldownEnds[cooldown.SkillId] = System.Diagnostics.Stopwatch.GetTimestamp() +
                                    (long)(cooldown.DurationMs * (double)System.Diagnostics.Stopwatch.Frequency / 1000);
                        ItemState[] vendor = AuxState.ReadVendor(b);
                        if (vendor != null) Vendor = vendor;
                        GroupState group = AuxState.ReadGroup(b);
                        if (group != null)
                        {
                            Group = group;
                            if (group.Joined) ClearInvitation();
                            if (!group.Formed) Formation = null;
                            else { ApplyFormationPose(); ApplyFormedPeers(); }
                        }
                    }
                    else if (Wire.U32(b, 0) == player)
                    {
                        WarpState warp = AuxState.ReadOwn(b, Equipment,
                            out TimedDeviceBuff[] revisions);
                        if (warp != null) Warp = warp;
                        for (int slot = 0; slot < buffs.Length; slot++)
                        {
                            if (revisions[slot] == null) continue;
                            buffs[slot] = revisions[slot];
                            pendingBuffVisuals.Remove(slot);
                            if (buffs[slot].Active && slot < 4) pendingBuffVisuals.Add(slot);
                            if (slot == 5 && buffs[slot].Active)
                            {
                                ushort descriptor = RallyDescriptor(buffs[slot].Name);
                                NativeObjectEffect visual = ObjectEffects.Values.FirstOrDefault(effect =>
                                    effect.OwnerId == player && effect.Descriptor == descriptor);
                                if (visual != null)
                                    buffs[slot] = buffs[slot].WithVisual(visual.EffectId, descriptor);
                            }
                            if (slot == 0)
                            {
                                ActiveDeviceLabel = buffs[0].Active ? buffs[0].Name : "";
                                if (!buffs[0].Active) ActiveDeviceEndsAt = 0;
                            }
                        }
                    }
                    else if (Station != null && Production.LabId != 0 &&
                        Wire.U32(b, 0) == Production.LabId)
                        AuxState.ReadProduction(b, Production);
                    else
                    {
                        uint id = Wire.U32(b, 0);
                        if (Objects.TryGetValue(id, out MapObject corpse) && corpse.Kind == 25)
                        {
                            ItemState[] contents = AuxState.ReadCorpse(b);
                            if (contents != null) Loot[id] = contents;
                            break;
                        }
                        if (Objects.TryGetValue(id, out MapObject resource) && resource.Kind == 38)
                        {
                            Resources.TryGetValue(id, out ResourceState previous);
                            Resources[id] = AuxState.ReadResource(b, id, previous);
                            break;
                        }
                        var contacts = Players.ContainsKey(id) ? Players : Mobs;
                        if (contacts.TryGetValue(id, out ShipActor contact)) contacts[id] = AuxState.ReadShip(b, contact);
                    }
                    break;
                case 0x89:
                    Wire.Require(b, 0, 8);
                    uint relationId = Wire.U32(b, 0, true);
                    if (Mobs.TryGetValue(relationId, out ShipActor related))
                        Mobs[relationId] = new ShipActor(related.Id, related.Asset, related.Scale, related.Name, related.Position,
                            related.Orientation, related.HasPosition, false, Wire.U32(b, 4) == 0, related.Shield, related.Hull, related.MaxHull);
                    break;
                case 0x61:
                    Wire.Require(b, 0, 24);
                    uint avatarId = Wire.U32(b, 0);
                    if (Station != null && avatarId != player)
                    {
                        string stationAvatarName = Wire.Text(b, 4, 20);
                        if (!StationAvatars.ContainsKey(avatarId) && StationAvatars.Count >= 64)
                            throw new InvalidDataException("Station avatar limit exceeded.");
                        StationAvatars[avatarId] = new StationAvatar(avatarId, stationAvatarName);
                    }
                    if (Players.TryGetValue(avatarId, out ShipActor avatar))
                    {
                        string avatarName = Wire.Text(b, 4, 20);
                        if (avatarName.Length > 0) Players[avatarId] = avatar.WithName(avatarName);
                    }
                    break;
                case 7:
                    if (b.Length != 4) throw new InvalidDataException("Invalid removal.");
                    if (MiningTractor?.ArticleId == Wire.U32(b, 0)) MiningTractor = null;
                    if (LootActivity?.ArticleId == Wire.U32(b, 0)) LootActivity = null;
                    Objects.Remove(Wire.U32(b, 0));
                    Loot.Remove(Wire.U32(b, 0));
                    Resources.Remove(Wire.U32(b, 0));
                    if (OpenLootId == Wire.U32(b, 0)) { OpenLootId = 0; LootActivity = null; }
                    if (ResourceTargetId == Wire.U32(b, 0)) SetResourceTarget(0);
                    Players.Remove(Wire.U32(b, 0)); Mobs.Remove(Wire.U32(b, 0));
                    ShieldChargingPeers.Remove(Wire.U32(b, 0));
                    attackerIds.Remove(Wire.U32(b, 0));
                    foreach (ulong key in ObjectEffects.Keys.Where(key => key >> 32 == Wire.U32(b, 0)).ToArray())
                        ObjectEffects.Remove(key);
                    StationAvatars.Remove(Wire.U32(b, 0));
                    break;
                case 0xa0:
                    if (b.Length != 12) throw new InvalidDataException("Invalid station avatar room.");
                    uint roomAvatarId = Wire.U32(b, 0), newRoom = Wire.U32(b, 4);
                    if (Station != null && roomAvatarId != player)
                    {
                        if (newRoom == uint.MaxValue) StationAvatars.Remove(roomAvatarId);
                        else if (StationAvatars.TryGetValue(roomAvatarId, out StationAvatar roomAvatar) &&
                            Station.RoomList.Any(entry => entry.Index == newRoom))
                            StationAvatars[roomAvatarId] = roomAvatar.WithRoom(newRoom);
                    }
                    break;
                case 0x9e:
                    if (b.Length != 28) throw new InvalidDataException("Invalid station avatar pose.");
                    uint posingId = Wire.U32(b, 0);
                    if (Station != null && StationAvatars.TryGetValue(posingId, out StationAvatar posing))
                        StationAvatars[posingId] = posing.WithPose(b);
                    break;
                case 0x3e:
                    Wire.Require(b, 0, 38);
                    uint movingId = Wire.U32(b, 2);
                    if (movingId != player)
                    {
                        if (Players.TryGetValue(movingId, out ShipActor moving)) Players[movingId] = moving.WithPose(b);
                        if (Mobs.TryGetValue(movingId, out ShipActor mob)) Mobs[movingId] = mob.WithPose(b);
                        if (Formation?.LeaderId == movingId) ApplyFormationPose();
                        if (Group.IsLeader) ApplyFormedPeers();
                        break;
                    }
                    Position = new Position(Wire.Float(b, 10), Wire.Float(b, 14), Wire.Float(b, 18));
                    Orientation = new[] { Wire.Float(b, 22), Wire.Float(b, 26), Wire.Float(b, 30), Wire.Float(b, 34) };
                    HasPosition = true;
                    lastServerTick = Wire.U32(b, 6);
                    lastServerTickAt = System.Diagnostics.Stopwatch.GetTimestamp();
                    if (b.Length >= 70)
                    {
                        Speed = Wire.Float(b, 42) * 1000;
                        TargetSpeed = Wire.Float(b, 46) * 1000;
                        Acceleration = Wire.Float(b, 50) * 1000;
                        AngularY = Wire.Float(b, 54);
                        AngularZ = Wire.Float(b, 62);
                    }
                    else if (b.Length >= 46) Speed = Wire.Float(b, 42) * 1000;
                    ApplyFormedPeers();
                    break;
                case 0x41:
                    Formation = FormationAnchor.Decode(b, player);
                    ApplyFormationPose();
                    break;
                case 0x66:
                    if (b.Length != 8 || Wire.U32(b, 4) != 0 || Wire.U32(b, 0) > 1)
                        throw new InvalidDataException("Invalid prospecting window state.");
                    if (Wire.U32(b, 0) == 1)
                    {
                        ResourceWindowOpen = false; ResourceTargetId = 0; MiningProspect = null;
                        OpenLootId = 0; LootActivity = null;
                    }
                    else if (OpenLootId != 0)
                    { ResourceWindowOpen = false; ResourceTargetId = 0; MiningProspect = null; }
                    else if (ResourceTargetId != 0) ResourceWindowOpen = true;
                    break;
            }
        }
    }

    /// <summary>Display copy of the slots inside one visible wreck.</summary>
    public sealed class CorpseLoot
    {
        public readonly uint Id;
        public readonly ItemView[] Items;
        /// <summary>Resolve visible wreck items against known templates.</summary>
        public CorpseLoot(uint id, ItemState[] contents, IReadOnlyDictionary<uint, ItemTemplateInfo> catalog)
        {
            Id = id; Items = new ItemView[contents.Length];
            for (int i = 0; i < contents.Length; i++)
                if (contents[i]?.Present == true) Items[i] = new ItemView(i, contents[i], catalog);
        }
    }

    public sealed class Snapshot
    {
        public readonly string Sector, Status;
        public readonly uint SectorId;
        public readonly GalaxyCatalogPage GalaxyCatalog;
        public readonly GalaxyRoutePlan GalaxyRoute;
        public readonly GalaxyTravelView GalaxyTravel;
        public readonly Position Position;
        public readonly float[] Orientation;
        public readonly MapObject[] Objects;
        public readonly ShipActor[] Players, Mobs;
        public readonly ShotEvent[] Shots;
        public readonly DamageEvent[] Damages;
        public readonly IReadOnlyList<uint> Attackers;
        public readonly CombatEngagement Engagement;
        public readonly uint PlayerId;
        public readonly GroupState Group;
        public readonly FormationAnchor Formation;
        public readonly TargetVerbs Verbs;
        public readonly StationState Station;
        public readonly StationAvatar[] StationAvatars;
        public readonly DialogueState Dialogue;
        public readonly ulong Credits;
        public readonly MissionState[] Missions;
        public readonly ReputationState Reputation;
        public readonly ReputationChange RecentReputationChange;
        public readonly ExperienceState Experience;
        public readonly SkillRank[] Skills;
        public readonly uint[] AvailableAbilities;
        public readonly uint CastingAbilityId;
        public readonly bool TurboWeaponsActive;
        public readonly string ActiveDeviceLabel;
        public readonly DeviceBuffView DeviceBuff;
        public readonly DeviceBuffView PeerBuff;
        public readonly DeviceBuffView ShieldBuff, ReactorBuff, SignatureBuff;
        public readonly DeviceBuffView RallyBuff;
        public readonly IReadOnlyList<DeviceBuffView> ActiveBuffs;
        public readonly IReadOnlyList<uint> ShieldChargingPeers;
        public readonly IReadOnlyList<ActiveDeviceView> ActiveDeviceEffects;
        public readonly bool HasActiveDeviceSnapshot;
        public readonly float ActiveDeviceRemainingSeconds;
        public readonly float CastRemainingSeconds;
        public readonly IReadOnlyDictionary<uint, float> AbilityCooldownSeconds;
        public readonly ItemView[] Vendor;
        public readonly NativeObjectEffect[] ObjectEffects;
        public readonly ResourceView[] Resources;
        public readonly uint ResourceTargetId;
        public readonly bool ResourceWindowOpen;
        public readonly MiningActivity MiningActivity;
        public readonly float MiningProgress, MiningRemainingSeconds;
        public readonly MiningActivity MiningProspect, MiningTractor;
        public readonly float ProspectProgress, ProspectRemainingSeconds;
        public readonly float TractorProgress, TractorRemainingSeconds;
        public readonly LootActivity LootActivity;
        public readonly float LootProgress, LootRemainingSeconds;
        public readonly WarpPhase WarpPhase;
        public readonly bool WarpArrived;
        public readonly string Invitation;
        public readonly ConfirmationPrompt Confirmation;
        public readonly string[] Messages;
        public readonly bool Ready, HasPosition, AggregateStation;
        public readonly float Speed, Energy, Shield, Hull, MaxEnergy, MaxShield, MaxHull;
        public readonly ShipDefenseState Defense;
        public readonly float TargetSpeed, Acceleration, AngularY, AngularZ;
        public readonly ItemView[] Cargo, Mounted, Ammo, Vault;
        public readonly ProductionView Production;
        public readonly IReadOnlyDictionary<uint, ComponentMarketView> ComponentMarkets;
        public readonly ItemView[] MarketItems;
        public readonly CorpseLoot[] Loot;
        public readonly uint OpenLootId;
        public readonly uint WeaponMounts, DeviceMounts, SelectedTargetId, CargoCapacity;
        public readonly bool WeaponsFiring;

        /// <summary>Copy worker-owned state for main-thread rendering.</summary>
        public Snapshot(World world, string status, bool ready)
        {
            SectorId = world.SectorId; GalaxyCatalog = world.GalaxyCatalog;
            GalaxyRoute = world.GalaxyRoute; GalaxyTravel = world.GalaxyTravel;
            AggregateStation = world.AggregateStation;
            Sector = world.Sector; Status = status; Position = world.Position;
            Orientation = (float[])world.Orientation.Clone(); HasPosition = world.HasPosition;
            Objects = world.Objects.Values.OrderBy(x => x.Name).ToArray(); Ready = ready;
            ObjectEffects = world.ObjectEffects.Values.ToArray();
            Speed = world.Speed;
            TargetSpeed = world.TargetSpeed; Acceleration = world.Acceleration;
            AngularY = world.AngularY; AngularZ = world.AngularZ;
            Players = world.Players.Values.ToArray(); Mobs = world.Mobs.Values.ToArray();
            ShieldChargingPeers = Array.AsReadOnly(world.ShieldChargingPeers.ToArray());
            PlayerId = world.PlayerId; Shots = world.Shots.ToArray(); Damages = world.Damages.ToArray();
            Attackers = Array.AsReadOnly(world.AttackerIds);
            Group = world.Group; Formation = world.Formation; Verbs = world.Verbs;
            Station = world.Station; StationAvatars = world.StationAvatars.Values.ToArray();
            Production = new ProductionView(world.Production, world.ItemCatalog);
            ComponentMarkets = new System.Collections.ObjectModel.ReadOnlyDictionary<uint, ComponentMarketView>(
                new Dictionary<uint, ComponentMarketView>(world.ComponentMarkets));
            MarketItems = world.MarketItemTemplates.Select(template =>
                new ItemView(-1, new ItemState { Template = template, Quantity = 0 }, world.ItemCatalog)).ToArray();
            Dialogue = world.Dialogue; Credits = world.Credits;
            Missions = (MissionState[])world.Missions.Clone();
            Reputation = world.Reputation;
            RecentReputationChange = world.RecentReputationChange;
            Experience = world.Experience;
            Skills = (SkillRank[])world.Skills.Clone();
            Vendor = new ItemView[world.Vendor.Length];
            AvailableAbilities = (uint[])world.AvailableAbilities.Clone();
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            uint? buffTick = world.EstimatedServerTick(now);
            var buffViews = new DeviceBuffView[16];
            for (int slot = 0; slot < buffViews.Length; slot++)
            {
                TimedDeviceBuff buff = world.BuffAt(slot);
                if (!buff.Active) continue;
                var view = new DeviceBuffView(buff, buffTick, slot);
                if (view.Permanent || view.RemainingSeconds != 0) buffViews[slot] = view;
            }
            ActiveBuffs = Array.AsReadOnly(buffViews.Where(view => view != null).ToArray());
            HasActiveDeviceSnapshot = world.HasActiveDeviceSnapshot;
            ActiveDeviceEffects = Array.AsReadOnly(world.ActiveDevices
                .Select(device => device.View(buffTick))
                .Where(device => device.RemainingSeconds != 0).ToArray());
            DeviceBuff = buffViews[0];
            PeerBuff = buffViews[1];
            ShieldBuff = buffViews[2];
            ReactorBuff = buffViews[3];
            SignatureBuff = buffViews[4];
            RallyBuff = buffViews[5];
            CastingAbilityId = world.CastingAbilityId;
            TurboWeaponsActive = HasActiveDeviceSnapshot ?
                ActiveDeviceEffects.Any(device => device.Name == "Turbo Weapons") :
                world.TurboWeaponsActive;
            ActiveDeviceLabel = world.ActiveDeviceLabel;
            ActiveDeviceRemainingSeconds = ActiveDeviceLabel.Length == 0 || world.ActiveDeviceEndsAt == 0 ? 0 :
                (float)Math.Max(0, (world.ActiveDeviceEndsAt - now) /
                    (double)System.Diagnostics.Stopwatch.Frequency);
            CastRemainingSeconds = (float)Math.Max(0,
                (world.CastEndsAt - now) / (double)System.Diagnostics.Stopwatch.Frequency);
            AbilityCooldownSeconds = world.AbilityCooldownEnds.ToDictionary(entry => entry.Key,
                entry => (float)Math.Max(0,
                    (entry.Value - now) / (double)System.Diagnostics.Stopwatch.Frequency));
            for (int i = 0; i < Vendor.Length; i++)
                if (world.Vendor[i]?.Present == true) Vendor[i] = new ItemView(i, world.Vendor[i], world.ItemCatalog);
            Resources = world.Resources.Values.Select(state => new ResourceView(state, world.ItemCatalog)).ToArray();
            ResourceTargetId = world.ResourceTargetId; ResourceWindowOpen = world.ResourceWindowOpen;
            MiningProspect = world.MiningProspect;
            MiningTractor = world.MiningTractor;
            ProspectProgress = MiningProspect?.Progress(now) ?? 0;
            ProspectRemainingSeconds = MiningProspect == null ? 0 :
                MiningProspect.DurationSeconds * (1 - ProspectProgress);
            TractorProgress = MiningTractor?.Progress(now) ?? 0;
            TractorRemainingSeconds = MiningTractor == null ? 0 :
                MiningTractor.DurationSeconds * (1 - TractorProgress);
            MiningActivity = world.MiningActivity;
            MiningProgress = MiningActivity?.Progress(now) ?? 0;
            MiningRemainingSeconds = MiningActivity == null ? 0 :
                MiningActivity.DurationSeconds * (1 - MiningProgress);
            LootActivity = world.LootActivity;
            LootProgress = LootActivity?.Progress(now) ?? 0;
            LootRemainingSeconds = LootActivity == null ? 0 :
                LootActivity.DurationSeconds * (1 - LootProgress);
            WarpPhase = world.Warp.Phase; WarpArrived = world.Warp.Arrived;
            Invitation = world.Invitation; Confirmation = world.Confirmation; Messages = world.Messages.ToArray();
            OpenLootId = world.OpenLootId;
            Loot = world.Loot.Select(entry => new CorpseLoot(entry.Key, entry.Value, world.ItemCatalog)).ToArray();
            EquipmentState equipment = world.Equipment;
            Energy = equipment.Energy; Shield = equipment.Shield; Hull = equipment.Hull;
            MaxEnergy = equipment.MaxEnergy; MaxShield = equipment.MaxShield; MaxHull = equipment.MaxHull;
            Defense = equipment.Defense;
            WeaponMounts = equipment.WeaponMounts; DeviceMounts = equipment.DeviceMounts;
            CargoCapacity = equipment.CargoCapacity;
            SelectedTargetId = equipment.TargetId;
            WeaponsFiring = equipment.Mounted.Any(item => item != null && (item.WeaponState & 1) != 0);
            Cargo = new ItemView[equipment.Cargo.Length]; Mounted = new ItemView[equipment.Mounted.Length];
            Vault = new ItemView[equipment.Vault.Length];
            Ammo = new ItemView[equipment.Ammo.Length];
            for (int i = 0; i < Cargo.Length; i++)
                if (equipment.Cargo[i]?.Present == true) Cargo[i] = new ItemView(i, equipment.Cargo[i], world.ItemCatalog);
            for (int i = 0; i < Vault.Length; i++)
                if (equipment.Vault[i]?.Present == true) Vault[i] = new ItemView(i, equipment.Vault[i], world.ItemCatalog);
            uint? serverTick = world.EstimatedServerTick(now);
            for (int i = 0; i < Mounted.Length; i++)
                if (equipment.Mounted[i]?.Present == true) Mounted[i] = new ItemView(i,
                    equipment.Mounted[i], world.ItemCatalog, serverTick);
            for (int i = 0; i < Ammo.Length; i++)
                if (equipment.Ammo[i]?.Present == true) Ammo[i] = new ItemView(i, equipment.Ammo[i], world.ItemCatalog);
            if (HasPosition && world.Mobs.TryGetValue(SelectedTargetId, out ShipActor target) && target.HasPosition)
                Engagement = new CombatEngagement(PlayerId, Position, Orientation, target,
                    Mounted, WeaponMounts, Shots, Damages, now);
        }
    }
}
