using System;
using System.Linq;
using FortyTwoAndBeyond.Protocol;
using UnityEngine;

namespace FortyTwoAndBeyond.Client
{
    /// <summary>Show native mission journal entries and server-owned objectives.</summary>
    public sealed class MissionHud
    {
        private readonly SpaceClient client;
        private readonly FlightHud flightHud;
        private Vector2 missionScroll, historyScroll, factionScroll;
        private int selectedSlot = -1;
        private bool open, factionsTab;
        private GUIStyle detailText;
        private float CanvasWidth => Screen.width / flightHud.Scale;
        private float CanvasHeight => Screen.height / flightHud.Scale;

        /// <summary>Bind the journal to the foreground controller and shared HUD styles.</summary>
        public MissionHud(SpaceClient client, FlightHud flightHud)
        { this.client = client; this.flightHud = flightHud; }

        /// <summary>Close the journal when another overlay takes focus.</summary>
        public void Close() { open = false; }

        /// <summary>
        /// Returns:
        ///     Rect: The toolbar button's scaled canvas bounds.
        /// </summary>
        private Rect ButtonRect(Snapshot snapshot)
        {
            if (snapshot?.Station != null && CanvasWidth < 1450)
                return new Rect(CanvasWidth * .5f + 86, 25, 136, 28);
            float x = snapshot?.Station == null ? CanvasWidth - 696 : CanvasWidth - 596;
            return new Rect(x, 25, 136, 28);
        }

        /// <summary>
        /// Returns:
        ///     Rect: The journal panel's scaled canvas bounds.
        /// </summary>
        private Rect PanelRect()
        { return new Rect((CanvasWidth - 720) * .5f, 86, 720, Mathf.Min(490, CanvasHeight - 115)); }

        /// <summary>
        /// Returns:
        ///     bool: Whether the pointer is over the journal button or open panel.
        /// </summary>
        public bool ContainsPointer()
        {
            Snapshot snapshot = client.CurrentSnapshot;
            if (snapshot?.Ready != true) return false;
            Vector2 point = new Vector2(Input.mousePosition.x / flightHud.Scale,
                (Screen.height - Input.mousePosition.y) / flightHud.Scale);
            return ButtonRect(snapshot).Contains(point) || open && PanelRect().Contains(point);
        }

        /// <summary>Draw accepted missions, the current objective, and reached stages.</summary>
        public void Draw(Snapshot snapshot)
        {
            if (snapshot?.Ready != true || snapshot.Dialogue != null) { open = false; return; }
            MissionState[] visible = snapshot.Missions.Where(mission => mission?.Visible == true).ToArray();
            if (GUI.Button(ButtonRect(snapshot), "MISSIONS  " + visible.Length, flightHud.ButtonStyle))
                open = !open;
            if (!open) return;
            if (detailText == null)
                detailText = new GUIStyle(flightHud.SmallStyle) { wordWrap = true, fontSize = 14 };
            Rect panel = PanelRect();
            flightHud.Panel(panel);
            GUI.Label(new Rect(panel.x + 19, panel.y + 15, 315, 27),
                factionsTab ? "FACTION STANDINGS" : "MISSION JOURNAL", flightHud.BodyStyle);
            if (GUI.Button(new Rect(panel.x + 438, panel.y + 14, 124, 31), "MISSIONS", flightHud.ButtonStyle))
                factionsTab = false;
            if (GUI.Button(new Rect(panel.x + 570, panel.y + 14, 130, 31), "FACTIONS", flightHud.ButtonStyle))
                factionsTab = true;
            if (factionsTab) { DrawFactions(snapshot, panel); return; }
            GUI.Label(new Rect(panel.x + 19, panel.y + 43, panel.width - 38, 21),
                "Objectives and progress update as you play.", flightHud.SmallStyle);
            MissionState selected = visible.FirstOrDefault(mission => mission.Slot == selectedSlot);
            if (selected == null && visible.Length > 0)
            { selected = visible[0]; selectedSlot = (int)selected.Slot; }
            float contentHeight = panel.height - 90;
            GUILayout.BeginArea(new Rect(panel.x + 18, panel.y + 75, 258, contentHeight));
            GUILayout.Label("ACTIVE MISSIONS", flightHud.BodyStyle);
            missionScroll = GUILayout.BeginScrollView(missionScroll);
            foreach (MissionState mission in visible)
            {
                string label = $"{mission.Slot + 1:00}  {mission.Name}";
                float height = Mathf.Max(38, flightHud.ButtonStyle.CalcHeight(new GUIContent(label), 224) + 7);
                if (GUILayout.Button(label, flightHud.ButtonStyle, GUILayout.Height(height)))
                { selectedSlot = (int)mission.Slot; historyScroll = Vector2.zero; }
            }
            if (visible.Length == 0)
                GUILayout.Label("No active missions. Speak with station contacts to find one.", detailText);
            GUILayout.EndScrollView();
            GUILayout.EndArea();
            GUILayout.BeginArea(new Rect(panel.x + 299, panel.y + 75, 401, contentHeight));
            historyScroll = GUILayout.BeginScrollView(historyScroll);
            if (selected != null) DrawMission(selected);
            GUILayout.EndScrollView();
            GUILayout.EndArea();
        }

        /// <summary>Show server-owned faction standings and a reward delta after a later revision.</summary>
        private void DrawFactions(Snapshot snapshot, Rect panel)
        {
            ReputationState reputation = snapshot.Reputation;
            if (reputation == null)
            {
                GUI.Label(new Rect(panel.x + 20, panel.y + 76, 650, 45),
                    "Waiting for faction standings from the server.", detailText);
                return;
            }
            float top = panel.y + 60;
            if (!string.IsNullOrEmpty(reputation.Affiliation))
                GUI.Label(new Rect(panel.x + 20, top, panel.width - 40, 24),
                    "AFFILIATION  " + reputation.Affiliation, flightHud.BodyStyle);
            ReputationChange change = snapshot.RecentReputationChange;
            if (change != null)
                GUI.Label(new Rect(panel.x + 20, top + 30, panel.width - 40, 26),
                    $"LATEST CHANGE  {change.Label} {change.Delta:+0;-0;0}  ·  {change.Value:0}",
                    detailText);
            float listTop = top + (change == null ? 30 : 62);
            Rect viewport = new Rect(panel.x + 20, listTop, panel.width - 40,
                Mathf.Max(50, panel.yMax - listTop - 20));
            float listHeight = reputation.Standings.Count * 28 + 4;
            factionScroll = GUI.BeginScrollView(viewport, factionScroll,
                new Rect(0, 0, viewport.width - 20, listHeight));
            for (int index = 0; index < reputation.Standings.Count; index++)
            {
                FactionStanding standing = reputation.Standings[index];
                GUI.Label(new Rect(5, index * 28, 300, 25), standing.Label, detailText);
                GUI.Label(new Rect(310, index * 28, 180, 25),
                    standing.Value.ToString("+0;-0;0"), detailText);
            }
            GUI.EndScrollView();
        }

        /// <summary>Present the accepted mission's current and earlier stage text.</summary>
        private void DrawMission(MissionState mission)
        {
            GUILayout.Label(mission.Name, detailText);
            if (mission.Summary.Length > 0)
            {
                GUILayout.Label(mission.Summary, detailText);
                GUILayout.Space(12);
            }
            uint playableStages = mission.StageCount > 0 ? mission.StageCount - 1 : 0;
            GUILayout.Label($"CURRENT OBJECTIVE  ·  STAGE {mission.Stage} OF {playableStages}",
                detailText);
            GUILayout.Label(mission.Objective.Length > 0 ? mission.Objective :
                "Awaiting the next server objective.", detailText);
            if (mission.ReachedStages.Count <= 1) return;
            GUILayout.Space(18);
            GUILayout.Label("EARLIER STAGES", flightHud.BodyStyle);
            for (int index = 0; index + 1 < mission.ReachedStages.Count; index++)
            {
                string description = mission.ReachedStages[index];
                if (!string.IsNullOrEmpty(description))
                    GUILayout.Label($"{index + 1}.  {description}", detailText);
            }
        }
    }
}
