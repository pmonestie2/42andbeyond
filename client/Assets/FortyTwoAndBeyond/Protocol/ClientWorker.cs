using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;

namespace FortyTwoAndBeyond.Protocol
{
    public enum CommandKind { Thrust, Steer, Warp, Gate, Dock, Launch, EnterStationRoom, StationPose, OpenNpc, ChooseNpc, BuyVendor, SellVendor, EquipCargo, UnequipMount, StoreCargo, RetrieveVault, OpenProduction, CloseProduction, SelectProductionRecipe, SelectProductionCategory, SetProductionLevels, StartProduction, InsertAnalysisSample, ReturnAnalysisSample, ActivateWeapon, ActivateDevice, ActivateTurboWeapons, Chat, ToggleWeapons, Formation, SelectTarget, FollowTarget, InvitePlayer, AnswerConfirmation, StopWeapons, OpenLoot, CloseLoot, TakeLoot, OpenResource, CloseResource, ProspectResource, ActivateAbility, RequestRescue, ChooseRescue, SelfJumpstart }

    public readonly struct ClientCommand
    {
        public readonly CommandKind Kind;
        public readonly uint Target;
        public readonly int Slot;
        public readonly float Yaw, Pitch;
        public readonly string Text;
        public readonly uint StationRoom, StationAction;
        public readonly float StationOrientation, StationX, StationZ;

        /// <summary>Store one immutable main-thread input for the network worker.</summary>
        public ClientCommand(CommandKind kind, uint target = 0, float yaw = 0, float pitch = 0,
            string text = "", int slot = 0, uint stationRoom = uint.MaxValue, uint stationAction = 0,
            float stationOrientation = 0, float stationX = 0, float stationZ = 0)
        {
            Kind = kind; Target = target; Yaw = yaw; Pitch = pitch; Text = text; Slot = slot;
            StationRoom = stationRoom; StationAction = stationAction;
            StationOrientation = stationOrientation; StationX = stationX; StationZ = stationZ;
        }
    }

    /// <summary>Own one session thread; publish immutable snapshots without engine calls.</summary>
    public sealed class ClientWorker : IDisposable
    {
        private readonly CancellationTokenSource stop = new CancellationTokenSource();
        private readonly ConcurrentQueue<ClientCommand> commands = new ConcurrentQueue<ClientCommand>();
        private readonly Thread thread;
        private readonly string host, username;
        private readonly int characterSlot;
        private string password;
        private Snapshot latest;
        private string error;
        private int disposed;
        public Snapshot Latest => Volatile.Read(ref latest);
        public string Error => Volatile.Read(ref error);
        public bool Running => thread.IsAlive;

        /// <summary>Start an isolated connection worker with explicit local credentials.</summary>
        public ClientWorker(string host, string username, string password, int characterSlot = 0)
        {
            this.host = host; this.username = username; this.password = password;
            this.characterSlot = characterSlot;
            thread = new Thread(Run) { IsBackground = true, Name = "42 and Beyond local client" }; thread.Start();
        }

        /// <summary>Queue bounded input only while this worker remains active.</summary>
        public void Submit(ClientCommand command)
        {
            if (Volatile.Read(ref disposed) != 0 || !Running) return;
            if (commands.Count >= 64) { Volatile.Write(ref error, "Input queue overflow; disconnected."); stop.Cancel(); return; }
            commands.Enqueue(command);
        }

        /// <summary>Own all protocol state until the session and its sockets are closed.</summary>
        private void Run()
        {
            try
            {
                using (var session = new LocalSession(stop.Token))
                {
                    try { session.Connect(host, username, password, characterSlot); }
                    finally { password = null; }
                    var displayClock = Stopwatch.StartNew();
                    long nextDisplayAt = 0;
                    while (!stop.IsCancellationRequested)
                    {
                        while (commands.TryDequeue(out ClientCommand command))
                        {
                            stop.Token.ThrowIfCancellationRequested();
                            switch (command.Kind)
                            {
                                case CommandKind.Thrust: session.Thrust((byte)command.Target); break;
                                case CommandKind.Steer: session.Steer(command.Yaw, command.Pitch); break;
                                case CommandKind.Warp: session.Warp(command.Target); break;
                                case CommandKind.Gate: session.Gate(command.Target); break;
                                case CommandKind.Dock: session.Dock(command.Target); break;
                                case CommandKind.Launch: session.Launch(); break;
                                case CommandKind.EnterStationRoom: session.EnterStationRoom(command.Target); break;
                                case CommandKind.StationPose: session.StationPose(command.StationRoom,
                                    command.StationOrientation, command.StationX, command.StationZ,
                                    command.StationAction); break;
                                case CommandKind.OpenNpc: session.OpenNpc(command.Target); break;
                                case CommandKind.ChooseNpc: session.ChooseNpc((byte)command.Target); break;
                                case CommandKind.BuyVendor: session.BuyVendor(command.Slot); break;
                                case CommandKind.SellVendor: session.SellVendor(command.Slot); break;
                                case CommandKind.EquipCargo: session.EquipCargo((int)command.Target, command.Slot); break;
                                case CommandKind.UnequipMount: session.UnequipMount((int)command.Target); break;
                                case CommandKind.StoreCargo: session.StoreCargo((int)command.Target); break;
                                case CommandKind.RetrieveVault: session.RetrieveVault((int)command.Target); break;
                                case CommandKind.OpenProduction: session.OpenProduction(command.Target); break;
                                case CommandKind.CloseProduction: session.CloseProduction(); break;
                                case CommandKind.SelectProductionRecipe: session.SelectProductionRecipe(command.Target); break;
                                case CommandKind.SelectProductionCategory: session.SelectProductionCategory(command.Target); break;
                                case CommandKind.SetProductionLevels: session.SetProductionLevels(command.Target, command.Slot != 0); break;
                                case CommandKind.StartProduction: session.StartProduction(command.Target != 0); break;
                                case CommandKind.InsertAnalysisSample: session.InsertAnalysisSample((int)command.Target); break;
                                case CommandKind.ReturnAnalysisSample: session.ReturnAnalysisSample(); break;
                                case CommandKind.ActivateWeapon: session.ActivateWeapon((int)command.Target); break;
                                case CommandKind.ActivateDevice: session.ActivateDevice((int)command.Target); break;
                                case CommandKind.ActivateTurboWeapons: session.ActivateTurboWeapons(); break;
                                case CommandKind.Chat: session.Chat(command.Text, (byte)command.Target); break;
                                case CommandKind.ToggleWeapons: session.ToggleWeapons(command.Target); break;
                                case CommandKind.Formation: session.Formation(command.Target); break;
                                case CommandKind.SelectTarget: session.SelectTarget(command.Target); break;
                                case CommandKind.FollowTarget: session.FollowTarget(command.Target); break;
                                case CommandKind.InvitePlayer: session.InvitePlayer(command.Target); break;
                                case CommandKind.AnswerConfirmation: session.AnswerConfirmation(command.Target != 0); break;
                                case CommandKind.StopWeapons: session.StopWeapons(); break;
                                case CommandKind.OpenLoot: session.OpenLoot(command.Target); break;
                                case CommandKind.CloseLoot: session.CloseLoot(command.Target); break;
                                case CommandKind.TakeLoot: session.TakeLoot(command.Target, command.Slot); break;
                                case CommandKind.OpenResource: session.OpenResource(command.Target); break;
                                case CommandKind.CloseResource: session.CloseResource(); break;
                                case CommandKind.ProspectResource: session.ProspectResource(command.Slot); break;
                                case CommandKind.ActivateAbility: session.ActivateAbility(command.Target); break;
                                case CommandKind.RequestRescue: session.RequestRescue(); break;
                                case CommandKind.ChooseRescue: session.ChooseRescue((byte)command.Target); break;
                                case CommandKind.SelfJumpstart: session.SelfJumpstart(); break;
                            }
                        }
                        session.Poll();
                        // Consume every packet, but copy display state at most 30 times per second.
                        // NPC traffic can arrive much faster than the display can use it.
                        if (displayClock.ElapsedMilliseconds >= nextDisplayAt)
                        {
                            Volatile.Write(ref latest, new Snapshot(session.World, session.Status, session.Ready));
                            nextDisplayAt = displayClock.ElapsedMilliseconds + 34;
                        }
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception exception)
            {
                // Credential packets and their values must never appear in UI errors.
                Volatile.Write(ref error, exception is System.Net.Sockets.SocketException
                    ? "Cannot reach the local server. Check its address and that it is running."
                    : exception.Message);
            }
            finally { password = null; }
        }

        /// <summary>Cancel and join before another client can replace this session.</summary>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            stop.Cancel(); thread.Join(); stop.Dispose();
        }
    }
}
