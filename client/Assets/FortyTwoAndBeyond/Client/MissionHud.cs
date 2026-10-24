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
        private Vector2 missionScroll, historyScroll;
        private int selectedSlot = -1;
        private bool open;
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
            float x = snapshot?.Station == null ? CanvasWidth - 670 : CanvasWidth - 570;
            return new Rect(x, 25, 110, 28);
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
            GUI.Label(new Rect(panel.x + 19, panel.y + 15, 450, 27), "MISSION JOURNAL", flightHud.BodyStyle);
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
                if (GUILayout.Button(label, flightHud.ButtonStyle, GUILayout.Height(38)))
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

        /// <summary>Present the accepted mission's current and earlier stage text.</summary>
        private void DrawMission(MissionState mission)
        {
            GUILayout.Label(mission.Name, flightHud.BodyStyle);
            if (mission.Summary.Length > 0)
            {
                GUILayout.Label(mission.Summary, detailText);
                GUILayout.Space(12);
            }
            uint playableStages = mission.StageCount > 0 ? mission.StageCount - 1 : 0;
            GUILayout.Label($"CURRENT OBJECTIVE  ·  STAGE {mission.Stage} OF {playableStages}",
                flightHud.BodyStyle);
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
