using System.Collections.Generic;
using System.Linq;
using FortyTwoAndBeyond.Protocol;
using UnityEngine;

namespace FortyTwoAndBeyond.Client
{
    /// <summary>Aggregate the station's native rooms into one client directory and service workspace.</summary>
    public sealed class SingleRoomStationHud
    {
        private readonly SpaceClient client;
        private readonly FlightHud hud;
        private Vector2 directoryScroll;
        private uint pendingContact;
        private float enterRoomAt;
        private bool directoryCollapsed;
        /// <returns>int: Completed directory repaints for the station visual check.</returns>
        public int DirectoryRenderCount { get; private set; }
        /// <returns>int: Contact buttons populated in the current directory draw.</returns>
        public int RenderedContacts { get; private set; }
        /// <returns>int: Industry service buttons populated in the current directory draw.</returns>
        public int RenderedServices { get; private set; }


        /// <summary>Share the existing HUD presentation and station command controller.</summary>
        public SingleRoomStationHud(SpaceClient client, FlightHud hud)
        { this.client = client; this.hud = hud; }

        /// <summary>
        /// Returns:
        ///     Rect: The shared docked workspace beside the persistent station directory.
        /// </summary>
        public static Rect Workspace(float width, float height)
        { return new Rect(344, 112, Mathf.Max(580, width - 368), Mathf.Max(420, height - 142)); }

        /// <summary>Present every native room's contacts and terminals through one client concourse.</summary>
        public void Draw(Snapshot snapshot, IReadOnlyDictionary<uint, string> names)
        {
            StationState station = snapshot.Station;
            float width = Screen.width / hud.Scale, height = Screen.height / hud.Scale;
            if (station.CurrentRoom == uint.MaxValue && station.RoomList.Any(room => room.Index == 0) &&
                Time.unscaledTime >= enterRoomAt)
            { client.EnterStationRoom(0); enterRoomAt = Time.unscaledTime + 1f; }
            if (pendingContact != 0 && snapshot.Dialogue == null && station.CurrentRoom != uint.MaxValue)
            { uint id = pendingContact; pendingContact = 0; client.OpenNpc(id); }

            hud.Panel(new Rect(24, 20, width - 48, 76));
            GUI.Label(new Rect(42, 30, width - 370, 30),
                string.IsNullOrEmpty(station.Name) ? "STATION CONCOURSE" : station.Name.ToUpperInvariant(), hud.BodyStyle);
            GUI.Label(new Rect(42, 62, width - 370, 20),
                "ONE CONCOURSE  /  VENDORS  /  INDUSTRY  /  SHIP SERVICES", hud.SmallStyle);
            if (GUI.Button(new Rect(width - 700, 37, 128, 34), "Galaxy · " + client.Bindings.Display(ControlAction.Galaxy), hud.ButtonStyle)) client.OpenGalaxyTravel();
            if (GUI.Button(new Rect(width - 330, 37, 144, 34), "Inventory  ·  I", hud.ButtonStyle)) hud.ToggleEquipment();
            if (GUI.Button(new Rect(width - 176, 37, 134, 34), "Launch", hud.ButtonStyle)) client.Launch();

            Rect directory = new Rect(24, 112, 304, height - 142);
            if (directoryCollapsed) directory.height = 54;
            hud.Panel(directory);
            GUI.Label(new Rect(40, 128, 230, 24), "STATION DIRECTORY", hud.BodyStyle);
            if (GUI.Button(new Rect(278, 124, 34, 30), directoryCollapsed ? "+" : "−", hud.ButtonStyle))
                directoryCollapsed = !directoryCollapsed;
            if (!directoryCollapsed)
            {
                RenderedContacts = RenderedServices = 0;
                if (Event.current.type == EventType.Repaint) DirectoryRenderCount++;
                GUILayout.BeginArea(new Rect(40, 158, 272, Mathf.Max(180, height - 268)));
                directoryScroll = GUILayout.BeginScrollView(directoryScroll);
                GUILayout.Label("INDUSTRY SERVICES", hud.SmallStyle);
                GUILayout.Space(7);
                string[] modes = { "Manufacturing", "Analyze", "Dismantle", "Refine" };
                for (int index = 0; index < modes.Length; index++)
                {
                    uint mode = (uint)index + 1;
                    string caption = client.StationServiceOpen && client.StationServiceMode == mode ? "●  " + modes[index] : modes[index] + "   ›";
                    if (GUILayout.Button(caption, hud.ButtonStyle, GUILayout.Height(34)))
                    { pendingContact = 0; client.ShowStationService(mode); }
                    RenderedServices++;
                }
                GUILayout.Space(12);
                GUILayout.Label("VENDORS & CONTACTS", hud.SmallStyle);
                GUILayout.Space(7);
                foreach (StationContact contact in station.Contacts.OrderBy(contact =>
                    names.TryGetValue(contact.Id, out string name) ? name : contact.Id.ToString()))
                {
                    string label = names.TryGetValue(contact.Id, out string name) ? name : "Contact " + contact.Id;
                    float rowHeight = Mathf.Max(38, hud.ButtonStyle.CalcHeight(new GUIContent(label), 250) + 10);
                    if (GUILayout.Button(label + "   ›", hud.ButtonStyle, GUILayout.Height(rowHeight)))
                    {
                        client.HideStationService();
                        if (snapshot.Dialogue != null) client.ChooseNpc(230);
                        pendingContact = contact.Id;
                    }
                    RenderedContacts++;
                }
                if (station.Contacts.Count == 0) GUILayout.Label("No contacts advertised here.", hud.SmallStyle);
                GUILayout.EndScrollView(); GUILayout.EndArea();
                GUI.Label(new Rect(40, height - 94, 272, 21), $"{snapshot.Credits:N0} CREDITS", hud.BodyStyle);
                GUI.Label(new Rect(40, height - 68, 272, 20), station.Contacts.Count + " contacts in this concourse", hud.SmallStyle);
            }

            if (!client.StationServiceOpen && snapshot.Dialogue == null)
            {
                Rect workspace = Workspace(width, height);
                hud.Panel(workspace);
                GUI.Label(new Rect(workspace.x + 24, workspace.y + 22, workspace.width - 48, 30), "WELCOME TO THE CONCOURSE", hud.BodyStyle);
                GUI.Label(new Rect(workspace.x + 24, workspace.y + 63, workspace.width - 48, 60),
                    "Choose a vendor or industry service from the directory.\nEvery station service is available in this room.", hud.SmallStyle);
                GUI.Label(new Rect(workspace.x + 24, workspace.y + 149, workspace.width - 48, 30),
                    $"SHIELD  {snapshot.Shield:N0} / {snapshot.MaxShield:N0}     HULL  {snapshot.Hull:N0} / {snapshot.MaxHull:N0}", hud.SmallStyle);
                GUI.Label(new Rect(workspace.x + 24, workspace.yMax - 94, workspace.width - 48, 64),
                    string.Join("\n", snapshot.Messages.Reverse().Take(3).Reverse()), hud.SmallStyle);
            }
        }
    }
}
