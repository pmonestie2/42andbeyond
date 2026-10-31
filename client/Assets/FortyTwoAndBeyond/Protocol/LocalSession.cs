using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace FortyTwoAndBeyond.Protocol
{
    /// <summary>Single-worker local-server session; never calls an engine API.</summary>
    public sealed partial class LocalSession : IDisposable
    {
        public const float StationEntryZ = -16f;
        private const int UdpReceiveBufferBytes = 1024 * 1024;
        private const int MaximumDatagramsPerPoll = 128;
        private const double ReceiveBatchSeconds = .004;
        private readonly CancellationToken cancel;
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private readonly System.Collections.Generic.HashSet<string> subscribedChatChannels =
            new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private UdpClient comms, sector;
        private IPAddress address;
        private IPEndPoint sectorPeer;
        private SectorStream stream;
        private uint sequence = 1;
        private double keepaliveAt, resendAt, gateAt, dockAt, enteredAt;
        private double nextMotionReportAt, motionReportInterval = .5;
        private double lastStreamProgressAt, gapStartedAt;
        private uint gate, dock, combatTarget, followTarget, pendingLoot, pendingResource;
        private uint pendingDeviceTarget;
        private uint pendingDeviceTemplate;
        private int pendingDeviceSlot = -1;
        private double pendingDeviceAt;
        private uint pendingAbilityTarget, pendingAbilityId;
        private double pendingAbilityAt;
        private uint requestedTarget, acknowledgedTarget;
        private double requestedTargetAt;
        private int pendingSkillId = -1;
        private double pendingSkillAt;
        private double combatTargetAt, followTargetAt, pendingLootAt, pendingResourceAt, selfJumpstartAt, rescueRequestAt;
        private bool gateActivated, gateCamera, gateFinished, dockRequested, lootRequested, selfJumpstartPending, rescuePending, disposed, failed;
        private byte[] handoff;
        private const int MaximumAggregationAttempts = 3;
        private double aggregateStationAt;
        private int aggregateStationAttempts;
        private bool nativeStationContacts;
        private uint pendingStationContact;
        public uint Player { get; private set; }
        public World World { get; private set; } = new World();
        public bool Ready { get; private set; }
        public string Status { get; private set; } = "Connecting";

        /// <summary>Bind session lifetime to a worker cancellation token.</summary>
        public LocalSession(CancellationToken token, byte[] navigationData = null, ItemTemplateInfo[] fallbackItems = null)
        {
            cancel = token; navigation = GalaxyNavigationCatalog.Decode(navigationData);
            if (fallbackItems != null)
                foreach (ItemTemplateInfo item in fallbackItems)
                    if (item != null && !World.ItemCatalog.ContainsKey(item.Id)) World.ItemCatalog.Add(item.Id, item);
        }

        /// <summary>Authenticate through the existing plaintext local bridge and enter space.</summary>
        public void Connect(string host, string username, string password, int characterSlot = 0)
        {
            cancel.ThrowIfCancellationRequested();
            address = ServerEndpoint.Resolve(host, cancel);
            comms = NewUdp(3806);
            SendUdp(comms, 0x1000, Wire.Body(w => w.Write(246u)), 0, 0);
            byte[] registration = ReceiveUdp(comms, 10);
            if (Wire.Datagram(registration, 0) != 0x1001) throw new InvalidDataException("UDP registration rejected.");
            using (var tcp = new TcpClient(AddressFamily.InterNetwork))
            {
                IAsyncResult connecting = tcp.BeginConnect(address, 3807, null, null);
                using (WaitHandle handle = connecting.AsyncWaitHandle)
                {
                    double deadline = clock.Elapsed.TotalSeconds + 10;
                    while (!handle.WaitOne(50)) { cancel.ThrowIfCancellationRequested(); if (clock.Elapsed.TotalSeconds > deadline) throw new TimeoutException("Local TCP bridge timed out."); }
                }
                tcp.EndConnect(connecting);
                tcp.SendTimeout = 1000;
                NetworkStream network = tcp.GetStream();
                byte[] user = Encoding.UTF8.GetBytes(username), secret = Encoding.UTF8.GetBytes(password);
                if (user.Length == 0 || user.Length > 256 || secret.Length > 1024) throw new ArgumentException("Invalid credential length.");
                byte[] credentials = Wire.Body(w => { w.Write((ushort)user.Length); w.Write(user); w.Write((ushort)secret.Length); w.Write(secret); });
                try { Write(network, 0x2000, credentials); }
                finally { Array.Clear(secret, 0, secret.Length); Array.Clear(credentials, 0, credentials.Length); }
                Frame auth = Read(tcp);
                if (auth.Opcode != 0x2001) throw new InvalidDataException("Unexpected authentication reply.");
                string ticket = null;
                bool valid = false;
                foreach (string line in Encoding.ASCII.GetString(auth.Body).Split('\n'))
                {
                    string field = line.Trim();
                    if (field == "Valid=TRUE") valid = true;
                    if (field.StartsWith("Ticket=", StringComparison.Ordinal)) ticket = field.Substring(7);
                }
                if (!valid || string.IsNullOrEmpty(ticket) || ticket.Length > 512) throw new InvalidDataException("Local account authentication rejected.");
                Write(network, 0x2002, Encoding.ASCII.GetBytes(ticket));
                Frame characters = Read(tcp);
                if (characters.Opcode != 0x70 || characters.Body.Length < 1874) throw new InvalidDataException("Character list rejected.");
                if (!CharacterInfo.DecodeRoster(characters.Body).Any(character => character.Slot == characterSlot))
                    throw new InvalidDataException("Choose an available character.");
                int characterOffset = 133 + characterSlot * 374;
                if (Wire.Text(characters.Body, characterOffset, 20).Length == 0)
                    throw new InvalidDataException("The selected character slot is empty.");
                Write(network, 0x2004, Wire.Body(w => Wire.Big(w, (uint)characterSlot)));
                Frame selected = Read(tcp);
                if (selected.Opcode != 0x6f || selected.Body.Length != 68 || Wire.U32(selected.Body, 0, true) != 0)
                    throw new InvalidDataException("Character selection rejected; it may already be logged in.");
                byte[] join = Wire.Slice(selected.Body, 4, 64);
                Player = Wire.U32(join, 16, true);
                if (Player == 0) throw new InvalidDataException("Missing player identity.");
                SendUdp(comms, 0x200f, Array.Empty<byte>(), Player, 0);
                StartPacketJournal();
                Enter(join);
            }
        }

        /// <returns>A connected UDP socket with short cancellation-aware polling.</returns>
        private UdpClient NewUdp(int port)
        {
            var socket = new UdpClient(AddressFamily.InterNetwork);
            try
            {
                socket.Client.ReceiveBufferSize = UdpReceiveBufferBytes;
                socket.Client.ReceiveTimeout = 100;
                socket.Client.SendTimeout = 1000;
                socket.Connect(address, port);
                return socket;
            }
            catch { socket.Dispose(); throw; }
        }

        /// <summary>Send an encoded datagram through its owning socket.</summary>
        private static void SendUdp(UdpClient socket, ushort opcode, byte[] body, uint player, uint sequence)
        { byte[] packet = Wire.Encode(opcode, body, player, sequence); socket.Send(packet, packet.Length); }

        /// <returns>A datagram received before the deadline or cancellation.</returns>
        private byte[] ReceiveUdp(UdpClient socket, double timeout)
        {
            double deadline = clock.Elapsed.TotalSeconds + timeout;
            while (true)
            {
                cancel.ThrowIfCancellationRequested();
                if (clock.Elapsed.TotalSeconds >= deadline) throw new TimeoutException("Local server response timed out.");
                try { if (ReceiveCurrentPeer(socket, out byte[] packet)) return packet; }
                catch (SocketException error) when (error.SocketErrorCode == SocketError.TimedOut || error.SocketErrorCode == SocketError.WouldBlock)
                { if (clock.Elapsed.TotalSeconds >= deadline) throw new TimeoutException("Local server response timed out."); }
            }
        }

        /// Returns:
        ///     bool: Whether this datagram came from the socket's currently connected peer, rather than a queued previous-sector source.
        private bool ReceiveCurrentPeer(UdpClient socket, out byte[] packet, IPEndPoint expectedPeer = null)
        {
            var expected = expectedPeer ?? socket.Client.RemoteEndPoint as IPEndPoint;
            if (expected == null) throw new InvalidOperationException("UDP peer is not connected.");
            IPEndPoint remote = null;
            packet = socket.Receive(ref remote);
            if (remote != null && remote.Port == expected.Port && remote.Address.Equals(expected.Address)) return true;
            RecordTransportLine("ignore-foreign bytes=" + packet.Length + " source=" + remote + " expected-peer=" + expected);
            packet = null;
            return false;
        }

        /// <summary>Write a framed request to the local TCP bridge.</summary>
        private static void Write(NetworkStream network, ushort opcode, byte[] body)
        { byte[] packet = Wire.Encode(opcode, body); network.Write(packet, 0, packet.Length); }

        /// <returns>Exactly the requested bytes, honoring TCP fragmentation and cancellation.</returns>
        private byte[] ReadExact(TcpClient tcp, int size)
        {
            byte[] result = new byte[size]; int offset = 0;
            double deadline = clock.Elapsed.TotalSeconds + 10;
            while (offset < size)
            {
                cancel.ThrowIfCancellationRequested();
                if (clock.Elapsed.TotalSeconds > deadline) throw new TimeoutException("Local bridge response timed out.");
                if (!tcp.Client.Poll(100000, SelectMode.SelectRead)) continue;
                int count = tcp.Client.Receive(result, offset, size - offset, SocketFlags.None);
                if (count == 0) throw new EndOfStreamException("Local bridge closed the connection.");
                offset += count;
            }
            return result;
        }

        /// <returns>A bounded complete TCP frame.</returns>
        private Frame Read(TcpClient tcp)
        {
            byte[] header = ReadExact(tcp, 4); int size = Wire.U16(header, 0);
            if (size < 4 || size > 32767) throw new InvalidDataException("Invalid TCP frame size.");
            return new Frame(Wire.U16(header, 2), ReadExact(tcp, size - 4));
        }

        /// <summary>Register a fresh sector socket before replacing the previous connection and reliable stream.</summary>
        private void Enter(byte[] join) { EnterSector(join, 3808); }

        /// <summary>Complete a sector entry through its explicit master endpoint before replacing the old socket.</summary>
        private void EnterSector(byte[] join, int masterPort)
        {
            uint owner = Wire.U32(join, 16, true), destination = Wire.U32(join, 20, true);
            if (owner != Player || destination == 0) throw new InvalidDataException("Invalid sector handoff identity.");
            int transferKind = World.TransferKind();
            UdpClient incoming = NewUdp(masterPort);
            int sectorPort;
            try
            {
                SendUdp(incoming, 0x2008, Wire.Body(w => { w.Write(destination); w.Write((byte)1); }), Player, 0);
                byte[] reply = ReceiveUdp(incoming, 15);
                if (Wire.Datagram(reply, Player) != 0x2009 || reply.Length < 22 || Wire.U32(reply, 18) != Player)
                    throw new InvalidDataException("Sector handoff identity mismatch.");
                sectorPort = Wire.U16(reply, 16);
                if (sectorPort == 0) throw new InvalidDataException("Missing sector port.");
                incoming.Connect(address, sectorPort);
            }
            catch { incoming.Dispose(); throw; }
            UdpClient previousSocket = sector;
            sector = incoming;
            sectorPeer = new IPEndPoint(address, sectorPort);
            RecordTransportLine("sector-peer local=" + sector.Client.LocalEndPoint + " remote=" + sectorPeer +
                " previous-local=" + previousSocket?.Client.LocalEndPoint);
            previousSocket?.Dispose();
            stream = new SectorStream(Player);
            World previousWorld = World;
            World = new World(destination) { EntryGeneration = sectorGeneration + 1, ArrivalKind = transferKind, GalaxyCatalog = World.GalaxyCatalog, GalaxyRoute = World.GalaxyRoute, GalaxyTravel = galaxyPilot.View };
            World.InheritCharacterProgress(previousWorld);
            foreach (var item in previousWorld.ItemCatalog) World.ItemCatalog[item.Key] = item.Value;
            foreach (var cooldown in previousWorld.AbilityCooldownEnds)
                World.AbilityCooldownEnds[cooldown.Key] = cooldown.Value;
            sectorGeneration++; sequence = 1; Ready = false;
            aggregateStationAt = 0; aggregateStationAttempts = 0; nativeStationContacts = false; pendingStationContact = 0;
            gate = dock = followTarget = pendingResource = pendingDeviceTarget = 0;
            pendingDeviceSlot = -1; pendingDeviceTemplate = 0;
            pendingAbilityTarget = pendingAbilityId = 0;
            requestedTarget = acknowledgedTarget = 0;
            pendingSkillId = -1;
            selfJumpstartPending = rescuePending = false; handoff = null;
            byte[] body = Wire.Body(w =>
            {
                w.Write(join); w.Write(unchecked((uint)Environment.TickCount)); w.Write(new byte[40]);
                w.Write(Encoding.ASCII.GetBytes(DateTime.Now.ToString("MM/dd/yy HH:mm:ss", CultureInfo.InvariantCulture) + "\0"));
                w.Write(new byte[11]);
            });
            Send(2, body);
            enteredAt = keepaliveAt = lastStreamProgressAt = clock.Elapsed.TotalSeconds;
            gapStartedAt = 0;
            Status = "Receiving sector";
        }

        /// <summary>Send one input with this session's monotonically increasing sequence.</summary>
        private void Send(ushort opcode, byte[] body)
        {
            cancel.ThrowIfCancellationRequested();
            RecordTransport("send", opcode, body.Length + 12, sequence);
            uint sentSequence = sequence++;
            SendUdp(sector, opcode, body, Player, sentSequence);
            if (opcode == 0x14 && body.Length == 5)
                RecordTransportLine("sent-thrust14 command=" + body[4] +
                    " sequence=" + sentSequence);
        }

        /// <summary>Submit forward, reverse, or brake input.</summary>
        public void Thrust(byte command)
        {
            if (!Ready || gate != 0) return;
            if (command != 2 && command != 3 && command != 4) throw new ArgumentOutOfRangeException(nameof(command));
            followTarget = 0;
            Send(0x14, Wire.Body(w => { w.Write(Player); w.Write(command); }));
        }

        /// <summary>Send a bounded local chat or developer command.</summary>
        public void Chat(string text, byte channel = 4)
        {
            if (!Ready || gate != 0) return;
            if (string.IsNullOrWhiteSpace(text) || text.Length > 180 || text.IndexOf('\0') >= 0)
                throw new ArgumentException("Enter a chat line up to 180 characters.", nameof(text));
            if (channel != 1 && channel != 4) throw new ArgumentOutOfRangeException(nameof(channel));
            if (text.Trim().Equals("/accept", StringComparison.OrdinalIgnoreCase) || text.Trim().Equals("/decline", StringComparison.OrdinalIgnoreCase)) World.ClearConfirmation();
            byte[] encoded = Encoding.UTF8.GetBytes(text);
            Send(0x33, Wire.Body(w => { w.Write(Player); w.Write(channel); w.Write((ushort)encoded.Length); w.Write(encoded); }));
        }

        /// <summary>Subscribe once to a canonical public chat channel on this session.</summary>
        public void SubscribeChatChannel(string channel)
        {
            if (!Ready || gate != 0 || !NativeChatChannel(channel) ||
                !subscribedChatChannels.Add(channel)) return;
            SendNativeChat(6, channel, "");
        }

        /// <summary>Send a named native chat line after subscribing to its channel.</summary>
        public void ChatNamed(string text, string channel)
        {
            if (!Ready || gate != 0 || !NativeChatChannel(channel)) return;
            if (string.IsNullOrWhiteSpace(text) || text.Length > 180 || text.IndexOf('\0') >= 0)
                throw new ArgumentException("Enter a chat line up to 180 characters.", nameof(text));
            SubscribeChatChannel(channel);
            SendNativeChat(0, channel, text);
        }

        /// <returns>bool: Whether a named channel is supported by the native chat service.</returns>
        private static bool NativeChatChannel(string channel)
        { return channel == "Local" || channel == "General Chat" || channel == "Market" || channel == "NewPlayers"; }

        /// <summary>Write the native three-string chat request without hidden terminators.</summary>
        private void SendNativeChat(uint kind, string channel, string message)
        {
            Send(0xa3, Wire.Body(writer =>
            {
                writer.Write(Player); writer.Write(kind);
                WriteChatText(writer, ""); WriteChatText(writer, channel); WriteChatText(writer, message);
                writer.Write(0u);
            }));
        }

        /// <summary>Write one bounded native length-prefixed UTF-8 field.</summary>
        private static void WriteChatText(BinaryWriter writer, string value)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(value);
            if (bytes.Length > ushort.MaxValue) throw new InvalidDataException("Chat field too long.");
            writer.Write((ushort)bytes.Length); writer.Write(bytes);
        }

        /// <summary>Submit yaw and pitch intensities in the range minus one to one.</summary>
        public void Steer(float yaw, float pitch)
        {
            if (!Ready || gate != 0) return;
            if (float.IsNaN(yaw) || float.IsNaN(pitch) || Math.Abs(yaw) > 1 || Math.Abs(pitch) > 1) throw new ArgumentOutOfRangeException(nameof(yaw));
            followTarget = 0;
            Send(0x12, Wire.Body(w => { w.Write(Player); w.Write(yaw); }));
            Send(0x13, Wire.Body(w => { w.Write(Player); w.Write(pitch); }));
        }

        /// <summary>Report the rendered native MVAS pose from the worker-owned comms socket.</summary>
        public void MotionReport(Position position, Position heading, float speedMetersPerSecond,
            int entryGeneration)
        {
            if (!Ready || comms == null || World.Station != null || World.Warp.Phase != WarpPhase.Idle ||
                World.Formation != null && World.Group.Formed ||
                entryGeneration != World.EntryGeneration ||
                clock.Elapsed.TotalSeconds < nextMotionReportAt) return;
            byte[] body = NativeMotionReport.Encode(position, heading, speedMetersPerSecond);
            byte[] packet = Wire.Encode(0x1004, body, Player, 1);
            comms.Send(packet, packet.Length);
            RecordMotionPacket(packet);
            nextMotionReportAt = clock.Elapsed.TotalSeconds + motionReportInterval;
        }

        /// <summary>Warp toward a server-visible object or MOB, fly ahead, or cancel warp.</summary>
        public void Warp(uint target)
        {
            if (!Ready || gate != 0 || World.Station != null || World.Warp.Phase == WarpPhase.Recovering) return;
            bool active = World.Warp.Phase == WarpPhase.Charging || World.Warp.Phase == WarpPhase.Traveling;
            if (!active && !WarpTargets.Available(World, target)) return;
            followTarget = 0;
            Send(0x9b, WarpState.RouteRequest(Player, active ? 0 : target));
            Status = active ? "Cancelling warp" : target == 0 ? "Warping ahead" :
                "Warp requested: " + (World.Objects.TryGetValue(target, out MapObject item) ? item.Name :
                    World.Mobs[target].Name);
        }

        /// <summary>Select a gate once, waiting for its matching acknowledgment before activation.</summary>
        public void Gate(uint target)
        {
            if (!Ready || gate != 0 || !World.Objects.TryGetValue(target, out MapObject item) || item.Kind != 11) return;
            followTarget = 0;
            World.ClearTransferIntent();
            gate = target; gateActivated = gateCamera = gateFinished = false; gateAt = clock.Elapsed.TotalSeconds;
            Send(0x17, Wire.Body(w => { w.Write(Player); w.Write(target); }));
            Send(0x5a, Wire.Body(w => { Wire.Big(w, Player); Wire.Big(w, target); w.Write(1u); }));
            Status = "Selecting gate: " + item.Name;
        }

        /// <summary>Select a nearby station and wait for clearance before docking.</summary>
        public void Dock(uint target, float radius = 0)
        {
            if (!Ready || World.Station != null || gate != 0 || dock != 0 ||
                !World.Objects.TryGetValue(target, out MapObject item) || item.Kind != 12 ||
                !World.HasPosition || World.Position.Distance(item.Position) > 5000 + Math.Max(0, radius))
                return;
            followTarget = 0;
            World.ClearTransferIntent();
            dock = target; dockAt = clock.Elapsed.TotalSeconds; dockRequested = false;
            Send(0x17, Wire.Body(w => { w.Write(Player); w.Write(target); }));
            Status = "Requesting docking clearance: " + item.Name;
        }

        /// <summary>Leave the current station through its native launch request.</summary>
        public void Launch()
        {
            if (!Ready || World.Station == null || gate != 0 || dock != 0) return;
            Send(0x4e, Wire.Body(w => { w.Write(Player); w.Write(World.Station.Id); w.Write((byte)1); }));
            Status = "Launching from " + World.Sector;
        }

        /// <summary>Negotiate the Unity directory's cross-room access without changing native room layouts.</summary>
        private void RequestStationAggregation()
        {
            if (!Ready || World.AggregateStation || nativeStationContacts || aggregateStationAttempts >= MaximumAggregationAttempts) return;
            Send(0x203a, Wire.Body(w => { w.Write(Player); w.Write(1u); }));
            aggregateStationAttempts++;
            aggregateStationAt = clock.Elapsed.TotalSeconds + 1;
        }

        /// <summary>Enter a server-advertised station room and publish this avatar's arrival.</summary>
        public void EnterStationRoom(uint room)
        {
            StationState station = World.Station;
            if (!Ready || station == null || station.CurrentRoom == room ||
                World.Dialogue != null && station.RoomList.Count != 1 ||
                !station.RoomList.Any(entry => entry.Index == room)) return;
            uint previous = station.CurrentRoom;
            Send(0x9f, Wire.Body(w => { w.Write(Player); w.Write(room); w.Write(previous); }));
            Send(0x9d, Wire.StationAvatarPose(Player, room, 0, 0, StationEntryZ, 0x41));
            World.SetStationRoom(room);
            Status = "Entered station room " + (room + 1);
        }

        /// <summary>Publish a bounded local station avatar pose to same-room peers.</summary>
        public void StationPose(uint room, float orientation, float x, float z, uint action)
        {
            if (!Ready || World.Station?.CurrentRoom != room) return;
            if (float.IsNaN(orientation) || float.IsInfinity(orientation) ||
                float.IsNaN(x) || float.IsInfinity(x) || float.IsNaN(z) || float.IsInfinity(z) ||
                Math.Abs(x) > 30 || Math.Abs(z) > 40 || (action != 0 && action != 1))
                throw new ArgumentOutOfRangeException(nameof(x), "Invalid station avatar pose.");
            Send(0x9d, Wire.StationAvatarPose(Player, room, orientation, x, z, action));
        }

        /// <summary>Open an advertised contact through negotiated aggregation or its native station room.</summary>
        public void OpenNpc(uint npcId)
        {
            if (!Ready || World.Station == null || World.Dialogue != null) return;
            StationContact contact = World.Station.Contacts.FirstOrDefault(entry => entry.Id == npcId);
            if (contact == null) return;
            if (!World.AggregateStation && World.Station.CurrentRoom != contact.Room)
            {
                if (!World.Station.RoomList.Any(room => room.Index == contact.Room))
                { pendingStationContact = 0; Status = "Station contact room is unavailable"; return; }
                if (!nativeStationContacts)
                {
                    pendingStationContact = npcId;
                    if (aggregateStationAttempts == 0) RequestStationAggregation();
                    Status = "Opening station contact";
                    return;
                }
                EnterStationRoom(contact.Room);
                if (World.Station.CurrentRoom != contact.Room)
                { pendingStationContact = 0; Status = "Could not enter station contact room"; return; }
            }
            pendingStationContact = 0;
            World.StartDialogue(npcId);
            Send(0x4e, Wire.Body(w => { w.Write(Player); w.Write(npcId); w.Write((byte)4); }));
            Status = "Talking to station contact";
        }

        /// <summary>Bound optional aggregation negotiation and release pending contacts through native room movement.</summary>
        private void AdvanceStationContacts(double now)
        {
            if (!Ready || World.Station == null) return;
            if (!World.AggregateStation && !nativeStationContacts && now >= aggregateStationAt)
            {
                if (aggregateStationAttempts < MaximumAggregationAttempts) RequestStationAggregation();
                else nativeStationContacts = true;
            }
            if (pendingStationContact != 0 && (World.AggregateStation || nativeStationContacts))
                OpenNpc(pendingStationContact);
        }

        /// <summary>Choose only a branch offered by an authoritative talk tree.</summary>
        public void ChooseNpc(byte node)
        {
            if (!Ready || World.Dialogue == null ||
                node != 230 && !World.Dialogue.Choices.Any(choice => choice.Node == node)) return;
            Send(0x55, Wire.Body(w => { w.Write(Player); w.Write(node); }));
            Status = node == 0 || node == 230 ? "Conversation closed" : "Waiting for contact";
        }

        /// <summary>Buy a bounded quantity from a server-listed vendor slot.</summary>
        public void BuyVendor(int slot, uint quantity = 1)
        {
            if (!Ready || World.Station == null || World.Dialogue?.VendorOpen != true ||
                slot < 0 || slot >= World.Vendor.Length || World.Vendor[slot]?.Present != true ||
                quantity == 0 || quantity > 99) return;
            Send(0x27, Wire.Body(w =>
            {
                Wire.Big(w, Player); Wire.Big(w, 4u); Wire.Big(w, (uint)slot);
                Wire.Big(w, 1u); Wire.Big(w, uint.MaxValue); Wire.Big(w, quantity);
            }));
            Status = "Buying from station vendor";
        }

        /// <summary>Sell an owned cargo stack through the active vendor.</summary>
        public void SellVendor(int slot, uint quantity = 1)
        {
            if (!Ready || World.Station == null || World.Dialogue?.VendorOpen != true ||
                slot < 0 || slot >= World.Equipment.Cargo.Length ||
                World.Equipment.Cargo[slot]?.Present != true || quantity == 0 ||
                quantity > World.Equipment.Cargo[slot].Quantity) return;
            Send(0x27, Wire.Body(w =>
            {
                Wire.Big(w, Player); Wire.Big(w, 1u); Wire.Big(w, (uint)slot);
                Wire.Big(w, 4u); Wire.Big(w, 0u); Wire.Big(w, quantity);
            }));
            Status = "Selling cargo to station vendor";
        }

        /// <summary>Install equipment or load the owned ammunition stack into a launcher.</summary>
        public void EquipCargo(int cargoSlot, int mountSlot)
        {
            if (!Ready || gate != 0 || cargoSlot < 0 || cargoSlot >= World.Equipment.Cargo.Length ||
                mountSlot < 0 || mountSlot >= World.Equipment.Mounted.Length ||
                World.Equipment.Cargo[cargoSlot]?.Present != true) return;
            ItemState cargo = World.Equipment.Cargo[cargoSlot];
            bool ammunition = World.ItemCatalog.TryGetValue(cargo.Template, out ItemTemplateInfo template) &&
                template.Type == 10;
            SendInventoryTransfer(1, (uint)cargoSlot, 2, (uint)mountSlot,
                cargo.EquipQuantity(template));
            Status = ammunition ? "Loading launcher ammunition" : "Installing ship equipment";
        }

        /// <summary>Move an installed item into the first empty cargo slot.</summary>
        public void UnequipMount(int mountSlot)
        {
            if (!Ready || gate != 0 || mountSlot < 0 || mountSlot >= World.Equipment.Mounted.Length ||
                World.Equipment.Mounted[mountSlot]?.Present != true) return;
            int cargoSlot = World.Equipment.FirstFreeCargoSlot();
            if (cargoSlot < 0) { Status = "Cargo hold is full"; return; }
            SendInventoryTransfer(2, (uint)mountSlot, 1, (uint)cargoSlot, 1);
            Status = "Moving equipment to cargo";
        }

        /// <summary>Request a cargo, hardpoint, or magazine move without changing local inventory.</summary>
        public void MoveInventory(InventoryMove move)
        {
            if (!Ready || gate != 0 || move.Quantity == 0 ||
                move.SourceSlot < 0 || move.DestinationSlot < 0 ||
                !InventoryContainer(move.Source) || !InventoryContainer(move.Destination)) return;
            if ((move.Source == 16 || move.Destination == 16) &&
                (!World.Trade.Open ||
                 !(move.Source == 1 && move.Destination == 16 ||
                   move.Source == 16 && move.Destination == 1))) return;
            ItemState[] source = InventorySlots(move.Source);
            ItemState[] destination = InventorySlots(move.Destination);
            if (move.SourceSlot >= source.Length || move.DestinationSlot >= destination.Length ||
                source[move.SourceSlot]?.Present != true ||
                move.Quantity > source[move.SourceSlot].Quantity) return;
            if (move.Source == 1 && move.Destination == 2)
            { EquipCargo(move.SourceSlot, move.DestinationSlot); return; }
            SendInventoryTransfer(move.Source, (uint)move.SourceSlot, move.Destination,
                (uint)move.DestinationSlot, move.Quantity);
            Status = "Moving inventory · awaiting server";
        }

        /// <summary>Request one server-owned launcher reload preference.</summary>
        public void SetLauncherAutoReload(int mount, bool enabled)
        {
            if (!Ready || gate != 0 || mount < 3 || mount > 8) return;
            Send(0x203d, Wire.Body(writer =>
            { writer.Write(Player); writer.Write((uint)mount); writer.Write(enabled ? 1u : 0u); }));
            Status = "Updating launcher reload preference";
        }

        /// <summary>Subscribe to authoritative compatible ammunition and reload revisions.</summary>
        private void QueryLauncherAmmo()
        { Send(0x203d, Wire.Body(writer =>
            { writer.Write(Player); writer.Write(uint.MaxValue); writer.Write(0u); })); }

        /// <returns>bool: Whether a container belongs to cargo, gear, vault, magazines, or an open trade.</returns>
        private static bool InventoryContainer(uint container)
        { return container >= 1 && container <= 3 || container == 8 || container == 16; }

        /// <returns>ItemState[]: The live worker-owned slots for a native inventory container.</returns>
        private ItemState[] InventorySlots(uint container)
        { return container == 1 ? World.Equipment.Cargo :
            container == 2 ? World.Equipment.Mounted :
            container == 3 ? World.Equipment.Vault :
            container == 16 ? World.Trade.Own.Slots : World.Equipment.Ammo; }

        /// <summary>Toggle one mounted weapon with the native item activation packet.</summary>
        public void ActivateWeapon(int slot)
        {
            if (!Ready || gate != 0 || World.Station != null || World.Equipment.Hull <= 0 ||
                slot < 3 || slot >= 3 + World.Equipment.WeaponMounts || slot >= World.Equipment.Mounted.Length)
                return;
            ItemState mounted = World.Equipment.Mounted[slot];
            if (mounted?.Present != true ||
                !World.ItemCatalog.TryGetValue(mounted.Template, out ItemTemplateInfo template) ||
                template.Type < 14 || template.Type > 16) return;
            Send(0x5d, Wire.Body(w => { w.Write(Player); w.Write((byte)2); w.Write((byte)slot); }));
            Status = (mounted.WeaponState & 1) != 0 ? "Stopping weapon slot " + slot :
                "Firing weapon slot " + slot;
        }

        /// <summary>Activate a mounted effect for the acknowledged selected crew pilot or self.</summary>
        public void ActivateDevice(int slot)
        {
            ItemState item = slot >= 0 && slot < World.Equipment.Mounted.Length ?
                World.Equipment.Mounted[slot] : null;
            if (item?.Present != true ||
                !World.ItemCatalog.TryGetValue(item.Template, out ItemTemplateInfo template)) return;
            uint target = World.Equipment.TargetId;
            bool selectedPeer = World.Players.TryGetValue(target, out ShipActor peer) && peer.HasPosition &&
                World.Group.Members.Any(member => member.Id == target);
            if (DeviceEffects.RequiresPeer(template.ActivationLabel))
            { ActivateDeviceFor(slot, target); return; }
            if (template.TargetsPeer)
            {
                if (selectedPeer || World.Players.ContainsKey(target))
                    ActivateDeviceFor(slot, selectedPeer ? target : Player);
                else if (Ready && gate == 0 && World.Station == null &&
                    World.Warp.Phase == WarpPhase.Idle && World.Equipment.Hull > 0 &&
                    DeviceEffects.MountedSlot(template.ActivationLabel, template.Type, slot,
                        World.Equipment.DeviceMounts))
                    SendDeviceActivation(slot, Player, template.ActivationLabel);
                return;
            }
            if (Ready && gate == 0 && World.Station == null && World.Equipment.Hull > 0 &&
                World.Warp.Phase != WarpPhase.Charging && World.Warp.Phase != WarpPhase.Traveling &&
                DeviceEffects.MountedSlot(template.ActivationLabel, template.Type, slot,
                    World.Equipment.DeviceMounts))
                SendDeviceActivation(slot, Player, template.ActivationLabel);
        }

        /// <summary>Wait for the native target acknowledgment before sending one device activation.</summary>
        public void ActivateDeviceFor(int slot, uint target)
        {
            if (!Ready || gate != 0 || World.Station != null || World.Equipment.Hull <= 0 ||
                World.Warp.Phase == WarpPhase.Charging || World.Warp.Phase == WarpPhase.Traveling ||
                slot < 0 || slot >= World.Equipment.Mounted.Length) return;
            ItemState item = World.Equipment.Mounted[slot];
            if (item?.Present != true ||
                !World.ItemCatalog.TryGetValue(item.Template, out ItemTemplateInfo template) ||
                !DeviceEffects.MountedSlot(template.ActivationLabel, template.Type, slot,
                    World.Equipment.DeviceMounts))
                return;
            bool transfer = DeviceEffects.RequiresPeer(template.ActivationLabel);
            if ((target == Player && transfer) ||
                (target != Player && !template.TargetsPeer && !transfer))
            { Status = "This effect cannot target that pilot"; return; }
            if (target != Player && (!World.Players.TryGetValue(target, out ShipActor peer) ||
                !peer.HasPosition || template.TargetsPeer &&
                !World.Group.Members.Any(member => member.Id == target)))
            { Status = "Select a visible crew pilot for " + template.ActivationLabel; return; }
            pendingDeviceTarget = pendingDeviceTemplate = 0; pendingDeviceSlot = -1;
            if (World.Equipment.TargetId == target && requestedTarget == 0)
            { SendDeviceActivation(slot, target, template.ActivationLabel); return; }
            SelectTarget(target);
            pendingDeviceTarget = target; pendingDeviceSlot = slot;
            pendingDeviceTemplate = item.Template;
            pendingDeviceAt = clock.Elapsed.TotalSeconds;
            Status = "Waiting for device target acknowledgment";
        }

        /// <summary>Submit one mounted-device activation after the target is server-confirmed.</summary>
        private void SendDeviceActivation(int slot, uint target, string label)
        {
            Send(0x5d, Wire.Body(w => { w.Write(Player); w.Write((byte)2); w.Write((byte)slot); }));
            string recipient = target == Player ? "your ship" :
                World.Players.TryGetValue(target, out ShipActor peer) ? peer.Name : "the selected pilot";
            Status = "Activating " + label + " for " + recipient;
        }

        /// <summary>Activate the equipped Turbo Weapons device through native item control.</summary>
        public void ActivateTurboWeapons()
        {
            if (!Ready || gate != 0 || World.Station != null || World.Equipment.Hull <= 0 ||
                World.Warp.Phase == WarpPhase.Charging || World.Warp.Phase == WarpPhase.Traveling) return;
            int first = 9, last = Math.Min(World.Equipment.Mounted.Length, first + (int)World.Equipment.DeviceMounts);
            for (int slot = first; slot < last; slot++)
            {
                ItemState item = World.Equipment.Mounted[slot];
                if (item?.Present != true || item.Template != 2363 && item.Template != 2412 &&
                    item.Template != 2265) continue;
                ActivateDevice(slot);
                return;
            }
        }

        /// <summary>Submit a bounded owned-inventory move through the server's durable transfer path.</summary>
        private void SendInventoryTransfer(uint source, uint sourceSlot, uint destination, uint destinationSlot, uint quantity)
        {
            Send(0x27, Wire.Body(w =>
            {
                Wire.Big(w, Player); Wire.Big(w, source); Wire.Big(w, sourceSlot);
                Wire.Big(w, destination); Wire.Big(w, destinationSlot); Wire.Big(w, quantity);
            }));
        }

        /// <summary>Send a native station workbench command with network-order owner and value.</summary>
        private void SendProduction(ushort opcode, uint value)
        {
            Send(opcode, Wire.Body(w => { Wire.Big(w, Player); Wire.Big(w, value); }));
        }

        /// <summary>Open manufacturing, analysis, dismantling, or refining at a station.</summary>
        public void OpenProduction(uint mode)
        {
            if (!Ready || World.Station == null || World.Equipment.Hull <= 0 || mode < 1 || mode > 4) return;
            SendProduction(0x79, mode);
            Status = "Opening production terminal";
        }

        /// <summary>Close the workbench and cancel its pending operation without consuming inputs.</summary>
        public void CloseProduction()
        {
            if (!Ready || World.Station == null || !World.Production.Open) return;
            SendProduction(0x79, 0);
            Status = "Closing production terminal";
        }

        /// <summary>Select one server-advertised manufacturing or refinery formula.</summary>
        public void SelectProductionRecipe(uint template)
        {
            if (!Ready || World.Station == null || World.Production.Mode != 1 && World.Production.Mode != 4 ||
                !World.Production.Formulas.Any(formula => formula?.Template == template)) return;
            SendProduction(World.Production.Mode == 4 ? (ushort)0x7c : (ushort)0x7b, template);
            Status = "Selecting production recipe";
        }

        /// <summary>Filter known manufacturing formulas by native category ID.</summary>
        public void SelectProductionCategory(uint category)
        {
            if (!Ready || World.Station == null || World.Production.Mode != 1) return;
            SendProduction(0x7a, category);
        }

        /// <summary>Request a bounded page of the server's eligible recipe list.</summary>
        public void SelectProductionPage(uint page)
        {
            ProductionState production = World.Production;
            if (!Ready || World.Station == null || !production.Open || production.PageSize == 0 ||
                production.TotalFormulas == 0 || page > (production.TotalFormulas - 1) / production.PageSize) return;
            Send(0x2033, Wire.Body(w => { w.Write(Player); w.Write(page); }));
            Status = "Loading recipe page";
        }

        /// <summary>Request current station component stock without opening a conversation.</summary>
        public void QueryComponentStock()
        {
            if (!Ready || gate != 0) return;
            Send(0x2038, Wire.Body(w => w.Write(Player)));
        }

        /// <summary>Ask the server for real station suppliers and buyers of one component.</summary>
        public void QueryComponentMarket(uint template)
        {
            if (!Ready || gate != 0 || template == 0) return;
            Send(0x2034, Wire.Body(w => { w.Write(Player); w.Write(template); }));
        }

        /// <summary>Submit a quoted local component trade for authoritative validation.</summary>
        public void TradeComponentMarket(uint npc, uint template, bool sell, int cargoSlot, uint quantity)
        {
            if (!Ready || gate != 0 || World.Station == null || quantity == 0 || quantity > 10000 ||
                !World.ComponentMarkets.TryGetValue(template, out ComponentMarketView market) ||
                !market.Sources.Any(source => source.Npc == npc && source.Station == World.Station.Id &&
                    (sell ? source.SellPrice > 0 : source.BuyPrice > 0))) return;
            if (sell && (cargoSlot < 0 || cargoSlot >= World.Equipment.Cargo.Length ||
                World.Equipment.Cargo[cargoSlot]?.Template != template)) return;
            Send(0x2036, Wire.Body(w =>
            {
                w.Write(Player); w.Write(npc); w.Write(template); w.Write(sell ? 1u : 0u);
                w.Write((uint)Math.Max(0, cargoSlot)); w.Write(quantity);
            }));
        }

        /// <summary>Use the server's explicitly enabled developer transport to a quoted station.</summary>
        public void TravelComponentMarket(uint station)
        {
            if (!Ready || gate != 0 || station == 0 || !World.ComponentMarkets.Values.Any(market =>
                market.DevEnabled && market.Sources.Any(source => source.Station == station))) return;
            Send(0x2037, Wire.Body(w => { w.Write(Player); w.Write(station); }));
        }

        /// <summary>Change one bounded native level-filter mask.</summary>
        public void SetProductionLevels(uint mask, bool enabled)
        {
            if (!Ready || World.Station == null || !World.Production.Open || mask == 0 || (mask & ~0x1ffu) != 0) return;
            Send(0x80, Wire.Body(w => { Wire.Big(w, Player); w.Write((byte)(enabled ? 1 : 0)); Wire.Big(w, mask); }));
        }

        /// <summary>Start one production operation or the refinery's available stack.</summary>
        public void StartProduction(bool stack)
        {
            if (!Ready || World.Station == null || !World.Production.Open || World.Production.Validity != 13 ||
                stack && World.Production.Mode != 4) return;
            SendProduction(0x7e, stack ? 3u : 2u);
            Status = "Starting production";
        }

        /// <summary>Move one owned cargo item into the analyzer's escrow slot.</summary>
        public void InsertAnalysisSample(int cargoSlot)
        {
            if (!Ready || World.Station == null || World.Production.Mode != 2 && World.Production.Mode != 3 ||
                cargoSlot < 0 || cargoSlot >= World.Equipment.Cargo.Length ||
                World.Equipment.Cargo[cargoSlot]?.Present != true || World.Production.Target[0]?.Present == true) return;
            SendInventoryTransfer(1, (uint)cargoSlot, 12, 0, 1);
            Status = "Placing analyzer sample";
        }

        /// <summary>Return the analyzer's owned sample to an empty cargo slot.</summary>
        public void ReturnAnalysisSample()
        {
            if (!Ready || World.Station == null || World.Production.Target[0]?.Present != true) return;
            int cargoSlot = Array.FindIndex(World.Equipment.Cargo, item => item?.Present != true);
            if (cargoSlot < 0) { Status = "Cargo hold is full"; return; }
            SendInventoryTransfer(12, 0, 1, (uint)cargoSlot, 1);
            Status = "Returning analyzer sample";
        }

        /// <summary>Store cargo in a compatible vault stack or an empty server slot.</summary>
        public void StoreCargo(int slot)
        {
            if (!Ready || World.Station == null || slot < 0 || slot >= World.Equipment.Cargo.Length ||
                World.Equipment.Cargo[slot]?.Present != true) return;
            ItemState source = World.Equipment.Cargo[slot];
            World.ItemCatalog.TryGetValue(source.Template, out ItemTemplateInfo template);
            InventoryTransferPlan? plan = InventoryTransferPlan.Choose(source,
                World.Equipment.Vault, (uint)World.Equipment.Vault.Length, template);
            if (!plan.HasValue) { Status = "Vault has no compatible stack space"; return; }
            SendInventoryTransfer(1, (uint)slot, 3, (uint)plan.Value.Slot, plan.Value.Quantity);
            Status = plan.Value.Merge ? $"Merging {plan.Value.Quantity} of {source.Quantity} cargo into vault" :
                "Storing cargo in vault";
        }

        /// <summary>Retrieve vault stock into a compatible or empty unlocked cargo slot.</summary>
        public void RetrieveVault(int slot)
        {
            if (!Ready || World.Station == null || slot < 0 || slot >= World.Equipment.Vault.Length ||
                World.Equipment.Vault[slot]?.Present != true) return;
            ItemState source = World.Equipment.Vault[slot];
            World.ItemCatalog.TryGetValue(source.Template, out ItemTemplateInfo template);
            InventoryTransferPlan? plan = InventoryTransferPlan.Choose(source,
                World.Equipment.Cargo, World.Equipment.CargoCapacity, template);
            if (!plan.HasValue) { Status = "Cargo hold has no compatible stack space"; return; }
            SendInventoryTransfer(3, (uint)slot, 1, (uint)plan.Value.Slot, plan.Value.Quantity);
            Status = plan.Value.Merge ? $"Merging {plan.Value.Quantity} of {source.Quantity} vault items into cargo" :
                "Retrieving vault item";
        }

        /// <summary>Select a visible actor or world object to receive native target verbs.</summary>
        public void SelectTarget(uint target)
        {
            pendingDeviceTarget = pendingDeviceTemplate = 0; pendingDeviceSlot = -1;
            pendingAbilityTarget = pendingAbilityId = 0;
            bool selectableObject = World.Objects.TryGetValue(target, out MapObject item) &&
                WarpTargets.WorldObject(item);
            if (!Ready || gate != 0 || (target != Player && !World.Mobs.ContainsKey(target) &&
                !World.Players.ContainsKey(target) && !World.StationAvatars.ContainsKey(target) && !selectableObject)) return;
            if (followTarget != target) followTarget = 0;
            if (World.Equipment.TargetId != target) World.SetOpenLoot(0);
            if (pendingLoot != 0 && pendingLoot != target) { pendingLoot = 0; lootRequested = false; World.SetLootRequest(0); }
            acknowledgedTarget = 0;
            requestedTarget = target; requestedTargetAt = clock.Elapsed.TotalSeconds;
            Send(0x17, Wire.Body(w => { w.Write(Player); w.Write(target); }));
        }

        /// <summary>Follow a visible ship after its matching target acknowledgment.</summary>
        public void FollowTarget(uint target)
        {
            bool worldObject = World.Objects.TryGetValue(target, out MapObject item) &&
                WarpTargets.WorldObject(item);
            if (!Ready || gate != 0 || (!World.Mobs.ContainsKey(target) &&
                !World.Players.ContainsKey(target) && !worldObject)) return;
            followTarget = target; followTargetAt = clock.Elapsed.TotalSeconds;
            Send(0x17, Wire.Body(w => { w.Write(Player); w.Write(target); }));
            Status = "Selecting target to follow";
        }

        /// <summary>Invite one visible online player through the native group action.</summary>
        public void InvitePlayer(uint target)
        {
            if (!Ready || gate != 0 || !World.Players.ContainsKey(target)) return;
            Send(0x2c, Wire.Body(w =>
            { w.Write(Player); w.Write(10u); w.Write(target); w.Write(0u); }));
            Status = "Requesting group invitation";
        }

        /// <summary>Request a server-offered trade with the currently selected peer.</summary>
        public void OpenTrade(uint peer)
        {
            if (!Ready || gate != 0 || World.Trade.Open ||
                (!World.Players.ContainsKey(peer) && !World.StationAvatars.ContainsKey(peer)) ||
                World.Equipment.TargetId != peer || World.Verbs.Available(peer, 6) != true) return;
            SendTradeAction(20, peer, 0);
            Status = "Opening peer trade";
        }

        /// <summary>Confirm the current native offer once; edits may reset this approval.</summary>
        public void ConfirmTrade()
        {
            if (!Ready || gate != 0 || !World.Trade.Open || World.Trade.OwnConfirmed) return;
            SendTradeAction(21, World.Trade.PeerId, 0);
            Status = "Confirming peer trade";
        }

        /// <summary>Cancel the current native trade without moving its reserved offers.</summary>
        public void CancelTrade()
        {
            if (!Ready || gate != 0 || !World.Trade.Open) return;
            SendTradeAction(22, World.Trade.PeerId, 0);
            Status = "Cancelling peer trade";
        }

        /// <summary>Set an offered credit amount, leaving admission to the server.</summary>
        public void SetTradeCredits(uint credits)
        {
            if (!Ready || gate != 0 || !World.Trade.Open) return;
            SendTradeAction(24, World.Trade.PeerId, credits);
            Status = "Updating trade credits";
        }

        /// <summary>Send one native four-integer peer-trade action.</summary>
        private void SendTradeAction(uint action, uint peer, uint optional)
        { Send(0x2c, Wire.Body(w =>
            { w.Write(Player); w.Write(action); w.Write(peer); w.Write(optional); })); }

        /// <summary>Answer the native group or ability confirmation currently offered by the server.</summary>
        public void AnswerConfirmation(bool accept)
        {
            if (!Ready || gate != 0 || World.Confirmation == null) return;
            Send(0x2c, Wire.Body(w =>
            { w.Write(Player); w.Write(accept ? 11u : 12u); w.Write(0u); w.Write(0u); }));
            World.ClearConfirmation();
            Status = accept ? "Confirmation accepted" : "Confirmation declined";
        }

        /// <summary>Stop active weapons even after the target leaves visibility.</summary>
        public void StopWeapons()
        {
            if (!Ready || gate != 0 || !World.Equipment.Mounted.Any(item => item != null && (item.WeaponState & 1) != 0)) return;
            Send(0x2c, Wire.Body(w => { w.Write(Player); w.Write(9u); w.Write(0u); w.Write(0u); }));
        }

        /// <summary>Request native loot, reusing an acknowledged current selection without a redundant target request.</summary>
        public void OpenLoot(uint target)
        {
            if (!Ready || gate != 0 || !World.Objects.TryGetValue(target, out MapObject corpse) || corpse.Kind != 25) return;
            if (World.OpenLootId == target) { Status = "Wreck open"; return; }
            if (pendingLoot == target) return;
            followTarget = 0;
            pendingLoot = target; pendingLootAt = clock.Elapsed.TotalSeconds; lootRequested = false;
            World.SetOpenLoot(0); World.SetLootRequest(target);
            if (acknowledgedTarget == target && World.Equipment.TargetId == target) RequestLootPermission();
            else { SelectTarget(target); Status = "Selecting wreck: " + corpse.Name; }
        }

        /// <summary>Ask for server permission after selection; only its grant opens the wreck.</summary>
        private void RequestLootPermission()
        {
            lootRequested = true; World.SetLootRequest(pendingLoot, true);
            Send(0x51, Wire.Body(w => { w.Write(Player); w.Write(0u); }));
            Status = "Opening wreck";
        }

        /// <summary>Close the server-owned wreck window and cancel any active tractor.</summary>
        public void CloseLoot(uint corpse)
        {
            if (!Ready || gate != 0 || corpse == 0 || World.OpenLootId != corpse ||
                World.Equipment.TargetId != corpse) return;
            Send(0x2c, Wire.Body(w =>
            { w.Write(Player); w.Write(1u); w.Write(corpse); w.Write(0u); }));
            Status = "Closing wreck";
        }

        /// <summary>Begin the server's timed tractor collection for one visible wreck slot.</summary>
        public void TakeLoot(uint corpse, int slot)
        {
            if (!Ready || gate != 0 || World.OpenLootId != corpse || World.LootActivity != null ||
                slot < 0 || slot >= 40 ||
                !World.Loot.TryGetValue(corpse, out ItemState[] contents) || contents[slot]?.Present != true)
                return;
            Send(0x27, Wire.Body(w =>
            {
                Wire.Big(w, Player); Wire.Big(w, 6u); Wire.Big(w, (uint)slot);
                Wire.Big(w, 1u); Wire.Big(w, 0u); Wire.Big(w, 1u);
            }));
            Status = "Tractor beam: slot " + slot;
        }

        /// <summary>Select a visible asteroid before opening its native prospecting window.</summary>
        public void OpenResource(uint target)
        {
            if (!Ready || gate != 0 || pendingResource != 0 ||
                !World.Objects.TryGetValue(target, out MapObject resource) || resource.Kind != 38) return;
            followTarget = 0; pendingResource = target; pendingResourceAt = clock.Elapsed.TotalSeconds;
            World.SetResourceTarget(target);
            Send(0x17, Wire.Body(w => { w.Write(Player); w.Write(target); }));
            Status = "Selecting asteroid: " + resource.Name;
        }

        /// <summary>Close the currently open prospecting window.</summary>
        public void CloseResource()
        {
            if (!Ready || gate != 0 || !World.ResourceWindowOpen || World.ResourceTargetId == 0) return;
            Send(0x2c, Wire.Body(w =>
            { w.Write(Player); w.Write(17u); w.Write(World.ResourceTargetId); w.Write(0u); }));
            Status = "Closing prospecting window";
        }

        /// <summary>Prospect one server-listed ore slot through native inventory transfer.</summary>
        public void ProspectResource(int slot)
        {
            if (!Ready || gate != 0 || !World.ResourceWindowOpen ||
                !World.Resources.TryGetValue(World.ResourceTargetId, out ResourceState resource) ||
                slot < 0 || slot >= resource.Items.Length || resource.Items[slot]?.Present != true) return;
            SendInventoryTransfer(18, (uint)slot, 0, 0, 0);
            Status = "Prospecting " + resource.Items[slot].Template;
        }

        /// <summary>Activate an ability listed in this character's authoritative RPG state.</summary>
        public void ActivateAbility(uint id)
        {
            if (AbilityTargets.Enemy(id))
            {
                uint target = requestedTarget != 0 && clock.Elapsed.TotalSeconds - requestedTargetAt <= 5 ?
                    requestedTarget : World.Equipment.TargetId;
                ActivateAbilityFor(id, target);
                return;
            }
            if (AbilityTargets.Directed(id))
            {
                uint selected = World.Equipment.TargetId;
                ActivateAbilityFor(id, World.Players.TryGetValue(selected, out ShipActor peer) &&
                    peer.HasPosition ? selected : Player);
                return;
            }
            if (!Ready || gate != 0 || World.Station != null ||
                !World.AvailableAbilities.Contains(id)) return;
            World.ClearTransferIntent();
            Send(0x58, Wire.Body(w => { w.Write(Player); w.Write(0u); w.Write(id); }));
            Status = "Activating ability " + id;
        }

        /// <summary>Cast a target-dependent ability after the server acknowledges its target.</summary>
        public void ActivateAbilityFor(uint id, uint target)
        {
            if (!AbilityTargets.Directed(id) && !AbilityTargets.Enemy(id) ||
                !Ready || gate != 0 || World.Station != null ||
                World.Equipment.Hull <= 0 || !World.AvailableAbilities.Contains(id)) return;
            string label = AbilityTargets.Name(id);
            if (AbilityTargets.Enemy(id))
            {
                if (!World.Mobs.TryGetValue(target, out ShipActor mob) || !mob.HasPosition)
                { Status = "Select a visible MOB for " + label; return; }
            }
            else
            {
                if (target == Player && AbilityTargets.RequiresPeer(id))
                { Status = "Select a visible crew pilot for " + label; return; }
                if (target != Player && (!World.Players.TryGetValue(target, out ShipActor peer) ||
                    !peer.HasPosition || AbilityTargets.CrewOnly(id) &&
                    !World.Group.Members.Any(member => member.Id == target)))
                { Status = label + " recipient is no longer visible"; return; }
            }
            pendingAbilityTarget = pendingAbilityId = 0;
            if (World.Equipment.TargetId == target && requestedTarget == 0)
            { Send(0x58, Wire.Body(w => { w.Write(Player); w.Write(0u); w.Write(id); }));
              Status = "Activating " + label; return; }
            SelectTarget(target);
            pendingAbilityTarget = target; pendingAbilityId = id;
            pendingAbilityAt = clock.Elapsed.TotalSeconds;
            Status = "Waiting for " + label + " target acknowledgment";
        }

        /// <returns>bool: Whether an acknowledged ability target remains visible and eligible.</returns>
        private bool AbilityRecipientValid(uint ability, uint target)
        {
            if (AbilityTargets.Enemy(ability))
                return World.Mobs.TryGetValue(target, out ShipActor mob) && mob.HasPosition;
            if (target == Player) return !AbilityTargets.RequiresPeer(ability);
            return World.Players.TryGetValue(target, out ShipActor peer) && peer.HasPosition &&
                (!AbilityTargets.CrewOnly(ability) ||
                 World.Group.Members.Any(member => member.Id == target));
        }

        /// <summary>Request one durable skill rank and await its authoritative RPG refresh.</summary>
        public void TrainSkill(uint id)
        {
            if (!Ready || pendingSkillId >= 0 || id > ushort.MaxValue ||
                World.Experience == null || World.Experience.AvailableSkillPoints == 0) return;
            SkillRank skill = World.Skills.FirstOrDefault(entry => entry.Id == id);
            if (skill?.CanTrain != true || World.Experience.AvailableSkillPoints < skill.Cost) return;
            Send(0x57, Wire.Body(w =>
            { w.Write(Player); w.Write(World.Experience.AvailableSkillPoints); w.Write((ushort)id); }));
            pendingSkillId = (int)id; pendingSkillAt = clock.Elapsed.TotalSeconds;
            Status = "Training skill " + id;
        }

        /// <summary>Ask rescue control for the native incapacitation choices.</summary>
        public void RequestRescue()
        {
            if (!Ready || gate != 0 || World.Station != null || World.Equipment.Hull > 0 ||
                World.Equipment.MaxHull <= 0 || World.Dialogue != null || rescuePending) return;
            rescuePending = true; rescueRequestAt = clock.Elapsed.TotalSeconds;
            Send(0x8d, Wire.Body(w => w.Write(Player)));
            Status = "Requesting rescue options";
        }

        /// <summary>Choose only an offered native rescue action.</summary>
        public void ChooseRescue(byte node)
        {
            if (!Ready || World.Station != null || World.Equipment.Hull > 0 ||
                World.Dialogue == null || !World.Dialogue.Choices.Any(choice => choice.Node == node)) return;
            Send(0x55, Wire.Body(w => { w.Write(Player); w.Write(node); }));
            Status = node == 0 ? "Requesting tow to station" :
                node == 1 ? "Sending distress call" : "Waiting for jumpstart";
        }

        /// <summary>Select the incapacitated ship itself before activating Jumpstart.</summary>
        public void SelfJumpstart()
        {
            if (!Ready || gate != 0 || World.Station != null || World.Equipment.Hull > 0 ||
                !World.AvailableAbilities.Contains(65u) || selfJumpstartPending) return;
            selfJumpstartPending = true; selfJumpstartAt = clock.Elapsed.TotalSeconds;
            Send(0x17, Wire.Body(w => { w.Write(Player); w.Write(Player); }));
            Status = "Selecting own ship for jumpstart";
        }

        /// <summary>Send one native group formation action after local validation.</summary>
        public void Formation(uint action, uint target = 0)
        {
            if (!Ready || gate != 0 || !((action >= 4 && action <= 9) || action == 12)) return;
            Send(0xbc, Wire.Body(w => { w.Write(Player); w.Write(target); w.Write(action); }));
        }

        /// <summary>Select a visible MOB and toggle equipped weapons only after its matching acknowledgment.</summary>
        public void ToggleWeapons(uint target)
        {
            if (!Ready || gate != 0 || combatTarget != 0) return;
            if (!World.Equipment.Mounted.Skip(3).Take((int)Math.Min(6u, World.Equipment.WeaponMounts))
                .Any(item => item?.Present == true))
            { Status = "No weapons equipped"; return; }
            if (!World.Mobs.TryGetValue(target, out ShipActor mob) || !mob.HasPosition)
                return;
            combatTarget = target; combatTargetAt = clock.Elapsed.TotalSeconds;
            Send(0x17, Wire.Body(w => { w.Write(Player); w.Write(target); }));
            Status = "Selecting " + mob.Name;
        }

        /// <summary>Send a gate-stage action without repeating an uncertain activation.</summary>
        private void GateAction(uint action)
        { Send(0x2c, Wire.Body(w => { w.Write(Player); w.Write(action); w.Write(gate); w.Write(0u); })); }

        /// <summary>Apply a world frame and answer required session control messages.</summary>
        private void Apply(Frame frame)
        {
            RecordSectorFrame(frame);
            bool wasLootWindow = World.OpenLootId != 0;
            SkillRank[] previousSkills = World.Skills;
            World.Apply(frame, Player); byte[] b = frame.Body;
            if (pendingSkillId >= 0 && !ReferenceEquals(previousSkills, World.Skills))
                pendingSkillId = -1;
            switch (frame.Opcode)
            {
                case 7:
                    if (b.Length == 4)
                    {
                        uint removed = Wire.U32(b, 0);
                        if (pendingLoot == removed) { pendingLoot = 0; lootRequested = false; World.SetLootRequest(0); }
                        if (acknowledgedTarget == removed) acknowledgedTarget = 0;
                    }
                    break;
                case 4:
                    if (b.Length == 23 && b[10] == 25)
                    {
                        uint created = Wire.U32(b, 0);
                        if (pendingLoot == created) { pendingLoot = 0; lootRequested = false; World.SetLootRequest(0); }
                        if (acknowledgedTarget == created) acknowledgedTarget = 0;
                    }
                    break;
                case 0x20:
                case 0x22:
                    Status = NativePriorityNotice.Decode(b).Message;
                    break;
                case 0x2020: if (b.Length == 4) Send(0x2021, b); break;
                case 5:
                    if (b.Length != 4) throw new InvalidDataException("Invalid sector start.");
                    Send(6, b); Send(0x3004, Wire.Body(w => w.Write(Player))); Ready = true; Status = "Connected";
                    RequestStationAggregation();
                    QueryLauncherAmmo();
                    if (World.Station?.RoomList.Count > 0 && World.Station.CurrentRoom == uint.MaxValue)
                        EnterStationRoom(World.Station.RoomList[0].Index);
                    break;
                case 0x203b:
                    nativeStationContacts = !World.AggregateStation;
                    if (pendingStationContact != 0) OpenNpc(pendingStationContact);
                    break;
                case 0x52:
                    if (Ready && World.Station?.RoomList.Count > 0 && World.Station.CurrentRoom == uint.MaxValue)
                        EnterStationRoom(World.Station.RoomList[0].Index);
                    break;
                case 0x54:
                    if (rescuePending) { rescuePending = false; Status = "Rescue options received"; }
                    break;
                case 0x19:
                    if (b.Length == 8 && Wire.U32(b, 4) == uint.MaxValue) acknowledgedTarget = Wire.U32(b, 0);
                    if (b.Length == 8 && Wire.U32(b, 0) == requestedTarget &&
                        Wire.U32(b, 4) == uint.MaxValue) requestedTarget = 0;
                    if (b.Length == 8 && pendingAbilityId != 0 &&
                        Wire.U32(b, 0) == pendingAbilityTarget && Wire.U32(b, 4) == uint.MaxValue)
                    {
                        uint target = pendingAbilityTarget, ability = pendingAbilityId;
                        pendingAbilityTarget = pendingAbilityId = 0;
                        if (Ready && gate == 0 && World.Station == null && World.Equipment.Hull > 0 &&
                            World.AvailableAbilities.Contains(ability) &&
                            AbilityRecipientValid(ability, target))
                        { Send(0x58, Wire.Body(w => { w.Write(Player); w.Write(0u); w.Write(ability); }));
                          Status = "Activating " + AbilityTargets.Name(ability); }
                    }
                    if (b.Length == 8 && pendingDeviceSlot >= 0 &&
                        Wire.U32(b, 0) == pendingDeviceTarget && Wire.U32(b, 4) == uint.MaxValue)
                    {
                        int slot = pendingDeviceSlot;
                        uint target = pendingDeviceTarget;
                        uint template = pendingDeviceTemplate;
                        pendingDeviceSlot = -1; pendingDeviceTarget = pendingDeviceTemplate = 0;
                        if (Ready && gate == 0 && World.Station == null && World.Equipment.Hull > 0 &&
                            World.Warp.Phase == WarpPhase.Idle &&
                            World.Equipment.Mounted[slot] is ItemState mounted &&
                            mounted.Present && mounted.Template == template &&
                            World.ItemCatalog.TryGetValue(mounted.Template, out ItemTemplateInfo metadata))
                            SendDeviceActivation(slot, target, metadata.ActivationLabel);
                    }
                    if (b.Length == 8 && selfJumpstartPending && Wire.U32(b, 0) == Player && Wire.U32(b, 4) == uint.MaxValue)
                    {
                        selfJumpstartPending = false;
                        Send(0x58, Wire.Body(w => { w.Write(Player); w.Write(0u); w.Write(65u); }));
                        Status = "Charging self jumpstart";
                    }
                    if (b.Length == 8 && dock != 0 && !dockRequested && Wire.U32(b, 0) == dock && Wire.U32(b, 4) == uint.MaxValue)
                    {
                        dockRequested = true;
                        Send(0x2c, Wire.Body(w => { w.Write(Player); w.Write(28u); w.Write(dock); w.Write(0u); }));
                        Status = "Waiting for docking clearance";
                    }
                    if (b.Length == 8 && pendingLoot != 0 && !lootRequested && Wire.U32(b, 0) == pendingLoot && Wire.U32(b, 4) == uint.MaxValue)
                    {
                        RequestLootPermission();
                    }
                    if (b.Length == 8 && pendingResource != 0 && Wire.U32(b, 0) == pendingResource && Wire.U32(b, 4) == uint.MaxValue)
                    {
                        Send(0x2c, Wire.Body(w =>
                        { w.Write(Player); w.Write(17u); w.Write(pendingResource); w.Write(0u); }));
                        pendingResource = 0; Status = "Opening prospecting window";
                    }
                    if (b.Length == 8 && combatTarget != 0 && Wire.U32(b, 0) == combatTarget && Wire.U32(b, 4) == uint.MaxValue)
                    {
                        Send(0x2c, Wire.Body(w => { w.Write(Player); w.Write(9u); w.Write(combatTarget); w.Write(0u); }));
                        combatTarget = 0; Status = "Weapon toggle sent";
                    }
                    if (b.Length == 8 && followTarget != 0 && Wire.U32(b, 0) == followTarget && Wire.U32(b, 4) == uint.MaxValue)
                    {
                        Send(0x14, Wire.Body(w => { w.Write(Player); w.Write((byte)12); }));
                        followTarget = 0; Status = "Following selected target";
                    }
                    if (b.Length == 8 && gate != 0 && Wire.U32(b, 0) == gate && Wire.U32(b, 4) == uint.MaxValue && !gateActivated)
                    { GateAction(18); gateActivated = true; gateAt = clock.Elapsed.TotalSeconds; Status = "Gate sequence running"; }
                    break;
                case 0x8c:
                    if (b.Length == 4 && pendingLoot != 0 && Wire.U32(b, 0, true) == pendingLoot)
                    { World.SetOpenLoot(pendingLoot); pendingLoot = 0; World.SetLootRequest(0); Status = "Wreck open"; }
                    break;
                case 0x92:
                    if (b.Length == 8 && dock != 0 && dockRequested && Wire.U32(b, 0, true) == 5 && Wire.U32(b, 4, true) == dock)
                    {
                        Send(0x2c, Wire.Body(w => { w.Write(Player); w.Write(7u); w.Write(dock); w.Write(0u); }));
                        dock = 0; Status = "Entering station";
                    }
                    if (b.Length == 8 && gate != 0 && Wire.U32(b, 0, true) == 4 && Wire.U32(b, 4, true) == gate) gateCamera = true;
                    break;
                case 0x1d:
                    if (b.Length > 3)
                    {
                        Status = Wire.Text(b, 3, b.Length - 3);
                        string lower = Status.ToLowerInvariant();
                        if (lower.Contains("cannot gate") || lower.Contains("unable to gate") || lower.Contains("gate inoperative") || lower.Contains("gate malfunction") || lower.Contains("sector is offline")) gate = 0;
                    }
                    break;
                case 0x66:
                    if (b.Length == 8 && Wire.U32(b, 0) == 1 && pendingLoot != 0)
                    { pendingLoot = 0; lootRequested = false; World.SetLootRequest(0); Status = "Wreck request closed by server"; break; }
                    Status = World.OpenLootId != 0 ? "Wreck open" :
                        World.ResourceWindowOpen ? "Prospecting window open" :
                        wasLootWindow ? "Wreck closed" : "Prospecting window closed";
                    break;
                case 0x3a: handoff = Wire.Slice(b, 0, 64); Ready = false; break;
                case 0xba: throw new IOException("Server ended the character session.");
            }
        }

        /// <summary>Receive one batch and service retransmission, gate, and keepalive deadlines.</summary>
        public void Poll()
        {
            cancel.ThrowIfCancellationRequested();
            try
            {
                ReceiveCommsControl();
                ReceiveSectorBatch();
                double now = clock.Elapsed.TotalSeconds;
                if (handoff != null) { Enter(handoff); return; }
                AdvanceGalaxyTravel(now);
                AdvanceStationContacts(now);
                if (pendingLoot != 0 && now - pendingLootAt > 5)
                { pendingLoot = 0; World.SetLootRequest(0); Status = "Wreck did not open; retry or move closer"; }
                if (pendingResource != 0 && now - pendingResourceAt > 5)
                { pendingResource = 0; World.SetResourceTarget(0); Status = "Asteroid did not acknowledge selection"; }
                if (selfJumpstartPending && now - selfJumpstartAt > 5)
                { selfJumpstartPending = false; Status = "Self jumpstart target timed out"; }
                if (rescuePending && now - rescueRequestAt > 5)
                { rescuePending = false; Status = "Rescue control did not respond"; }
                if (dock != 0 && now - dockAt > 8)
                { dock = 0; Status = "Docking timed out"; }
                if (combatTarget != 0 && now - combatTargetAt > 5)
                { combatTarget = 0; Status = "Target did not acknowledge weapon command"; }
                if (followTarget != 0 && now - followTargetAt > 5)
                { followTarget = 0; Status = "Follow target did not acknowledge"; }
                if (pendingDeviceSlot >= 0 && now - pendingDeviceAt > 5)
                { pendingDeviceSlot = -1; pendingDeviceTarget = pendingDeviceTemplate = 0;
                  Status = "Device target did not acknowledge; activation cancelled"; }
                if (pendingAbilityId != 0 && now - pendingAbilityAt > 5)
                { pendingAbilityTarget = pendingAbilityId = 0;
                  Status = "Ability target did not acknowledge; cast cancelled"; }
                if (requestedTarget != 0 && now - requestedTargetAt > 5) requestedTarget = 0;
                if (pendingSkillId >= 0 && now - pendingSkillAt > 5)
                { pendingSkillId = -1; Status = "Skill training did not confirm; check the server result"; }
                if (gate != 0)
                {
                    double elapsed = now - gateAt;
                    if (gateActivated && gateCamera && !gateFinished && elapsed >= 15)
                    { GateAction(19); gateFinished = true; Status = "Waiting for destination"; }
                    else if (elapsed > (gateActivated ? 45 : 5)) { gate = 0; Status = "Gate timed out; no activation retry sent"; }
                }
                if (stream.MissingCount > 0 && now - resendAt >= .25)
                { Send(0x2017, Wire.Body(w => { w.Write(stream.Expected); w.Write(stream.MissingCount); })); resendAt = now; }
                if (now - keepaliveAt >= 15)
                { Send(0x3005, Array.Empty<byte>()); SendUdp(comms, 0x3005, Array.Empty<byte>(), Player, 0); keepaliveAt = now; }
                if (SectorTimedOut(Ready, now, enteredAt, stream.MissingCount,
                    gapStartedAt, lastStreamProgressAt))
                    throw new TimeoutException("Sector stopped responding.");
            }
            catch (OperationCanceledException) { throw; }
            catch { failed = true; throw; }
        }

        /// <summary>Honor the native MVAS report frequency sent on the comms socket.</summary>
        private void ReceiveCommsControl()
        {
            if (comms == null) return;
            for (int count = 0; count < 8 && comms.Available > 0; count++)
            {
                IPEndPoint remote = null;
                byte[] packet = comms.Receive(ref remote);
                if (packet.Length != 16 || Wire.U16(packet, 0) != 16 ||
                    Wire.U16(packet, 2) != 0x1007 || Wire.U32(packet, 4) != Player) continue;
                uint frequency = Wire.U32(packet, 12);
                if (frequency == 1 || frequency == 2)
                    motionReportInterval = frequency == 1 ? .5 : 1.0;
            }
        }

        /// <summary>Drain queued datagrams without making commands or snapshot publication wait for an unbounded burst.</summary>
        /// <returns>bool: Whether a valid sector datagram arrived, including an already applied duplicate.</returns>
        private bool ReceiveSectorBatch()
        {
            bool received = false;
            double deadline = double.PositiveInfinity;
            for (int count = 0; count < MaximumDatagramsPerPoll; count++)
            {
                cancel.ThrowIfCancellationRequested();
                if (count > 0 && sector.Available == 0) return received;
                byte[] packet;
                bool currentPeer;
                try { currentPeer = ReceiveCurrentPeer(sector, out packet, expectedPeer: sectorPeer); }
                catch (SocketException error) when (error.SocketErrorCode == SocketError.TimedOut ||
                    error.SocketErrorCode == SocketError.WouldBlock) { return received; }
                double now = clock.Elapsed.TotalSeconds;
                if (count == 0) deadline = now + ReceiveBatchSeconds;
                if (!currentPeer)
                {
                    if (clock.Elapsed.TotalSeconds >= deadline) return received;
                    continue;
                }
                ushort opcode = Wire.Datagram(packet, Player);
                RecordTransport("receive", opcode, packet.Length, Wire.U32(packet, 8));
                if (opcode == 0x2016 || opcode == 0x201a)
                {
                    received = true;
                    uint before = stream.Expected;
                    foreach (Frame frame in stream.Feed(packet))
                    { Apply(frame); if (handoff != null) return received; }
                    bool progressed = stream.Expected != before;
                    if (progressed) lastStreamProgressAt = now;
                    if (stream.MissingCount == 0) gapStartedAt = 0;
                    else if (gapStartedAt == 0 || progressed) gapStartedAt = now;
                }
                if (clock.Elapsed.TotalSeconds >= deadline) return received;
            }
            return received;
        }

        /// Returns:
        ///     bool: Whether startup or a known missing reliable range has stopped progressing.
        private static bool SectorTimedOut(bool ready, double now, double entered,
            uint missingCount, double gapStarted, double lastProgress) =>
            !ready && now - entered > 30 ||
            ready && missingCount > 0 && gapStarted > 0 &&
            now - Math.Max(gapStarted, lastProgress) > 45;

        /// <summary>Log out on deliberate closure and release worker-owned sockets.</summary>
        public void Dispose()
        {
            if (disposed) return; disposed = true;
            try
            {
                if (!failed && Player != 0 && comms != null)
                    SendUdp(comms, 0x1008, Wire.Body(w => w.Write(Player)), Player, 0);
            }
            catch (SocketException) { }
            finally
            {
                sector?.Dispose(); comms?.Dispose(); packetJournal?.Dispose(); packetJournal = null;
                transportJournal?.Dispose(); transportJournal = null;
            }
        }
    }
}
