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
        private GUIStyle detailText, missionTitle, objectiveText, eyebrowText, selectedMission, standingValue;
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

        /// <summary>Draw a translucent accent without changing later GUI colors.</summary>
        private static void Tint(Rect rect, Color color)
        {
            Color previous = GUI.color;
            GUI.color = color;
            GUI.DrawTexture(rect, Texture2D.whiteTexture);
            GUI.color = previous;
        }

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
            {
                detailText = new GUIStyle(flightHud.SmallStyle) { wordWrap = true, fontSize = 14 };
                missionTitle = new GUIStyle(flightHud.BodyStyle) { wordWrap = true, fontSize = 21 };
                missionTitle.normal.textColor = new Color(.88f, .96f, 1f);
                objectiveText = new GUIStyle(missionTitle) { fontSize = 16,
                    padding = new RectOffset(13, 13, 11, 11) };
                eyebrowText = new GUIStyle(flightHud.SmallStyle) { fontSize = 12,
                    fontStyle = FontStyle.Bold };
                eyebrowText.normal.textColor = new Color(.43f, .86f, 1f);
                selectedMission = new GUIStyle(flightHud.ButtonStyle);
                selectedMission.normal.textColor = new Color(.48f, .88f, 1f);
                standingValue = new GUIStyle(detailText) { alignment = TextAnchor.MiddleRight,
                    fontStyle = FontStyle.Bold };
                standingValue.normal.textColor = new Color(.81f, .94f, 1f);
            }
            Rect panel = PanelRect();
            flightHud.Panel(panel);
            Tint(new Rect(panel.x + 8, panel.y + 68, 277, panel.height - 77),
                new Color(.012f, .025f, .04f, .46f));
            Tint(new Rect(panel.x + 285, panel.y + 69, 1, panel.height - 80),
                new Color(.34f, .68f, .8f, .28f));
            GUI.Label(new Rect(panel.x + 19, panel.y + 15, 315, 27),
                factionsTab ? "FACTION STANDINGS" : "MISSION JOURNAL", flightHud.BodyStyle);
            if (GUI.Button(new Rect(panel.x + 438, panel.y + 14, 124, 31), "MISSIONS", flightHud.ButtonStyle))
                factionsTab = false;
            if (GUI.Button(new Rect(panel.x + 570, panel.y + 14, 130, 31), "FACTIONS", flightHud.ButtonStyle))
                factionsTab = true;
            Tint(new Rect(panel.x + (factionsTab ? 570 : 438), panel.y + 47,
                factionsTab ? 130 : 124, 2), new Color(.36f, .83f, 1f, .9f));
            if (factionsTab) { DrawFactions(snapshot, panel); return; }
            GUI.Label(new Rect(panel.x + 19, panel.y + 43, panel.width - 38, 21),
                "SERVER JOURNAL   /   OBJECTIVES UPDATE AS YOU PLAY", eyebrowText);
            MissionState selected = visible.FirstOrDefault(mission => mission.Slot == selectedSlot);
            if (selected == null && visible.Length > 0)
            { selected = visible[0]; selectedSlot = (int)selected.Slot; }
            float contentHeight = panel.height - 90;
            GUILayout.BeginArea(new Rect(panel.x + 18, panel.y + 75, 258, contentHeight));
            GUILayout.Label("ACTIVE MISSIONS   " + visible.Length, eyebrowText);
            GUILayout.Space(8);
            missionScroll = GUILayout.BeginScrollView(missionScroll);
            foreach (MissionState mission in visible)
            {
                string label = $"{mission.Slot + 1:00}   {mission.Name}";
                float height = Mathf.Max(46, flightHud.ButtonStyle.CalcHeight(
                    new GUIContent(label), 224) + 10);
                bool active = mission.Slot == selectedSlot;
                Rect row = GUILayoutUtility.GetRect(0, height, GUILayout.ExpandWidth(true));
                if (GUI.Button(row, label, active ? selectedMission : flightHud.ButtonStyle))
                { selectedSlot = (int)mission.Slot; historyScroll = Vector2.zero; }
                if (active) Tint(new Rect(row.x, row.y + 2, 3, row.height - 4),
                    new Color(.4f, .87f, 1f, .95f));
                GUILayout.Space(4);
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
                float y = index * 28;
                if (index % 2 == 0) Tint(new Rect(0, y, viewport.width - 21, 27),
                    new Color(.08f, .19f, .25f, .35f));
                GUI.Label(new Rect(10, y + 1, 450, 25), standing.Label, detailText);
                GUI.Label(new Rect(viewport.width - 112, y + 1, 85, 25),
                    standing.Value.ToString("+0;-0;0"), standingValue);
            }
            GUI.EndScrollView();
        }

        /// <summary>Present the accepted mission's current and earlier stage text.</summary>
        private void DrawMission(MissionState mission)
        {
            GUILayout.Label("MISSION " + (mission.Slot + 1).ToString("00"), eyebrowText);
            GUILayout.Label(mission.Name, missionTitle);
            GUILayout.Space(7);
            if (mission.Summary.Length > 0)
            {
                GUILayout.Label(mission.Summary, detailText);
                GUILayout.Space(14);
            }
            uint playableStages = mission.StageCount > 0 ? mission.StageCount - 1 : 0;
            GUILayout.Label($"CURRENT OBJECTIVE   /   STAGE {mission.Stage} OF {playableStages}",
                eyebrowText);
            string objective = mission.Objective.Length > 0 ? mission.Objective :
                "Awaiting the next server objective.";
            float objectiveHeight = Mathf.Max(61,
                objectiveText.CalcHeight(new GUIContent(objective), 358) + 8);
            Rect card = GUILayoutUtility.GetRect(0, objectiveHeight, GUILayout.ExpandWidth(true));
            Tint(card, new Color(.075f, .17f, .22f, .68f));
            Tint(new Rect(card.x, card.y, 3, card.height), new Color(.39f, .83f, 1f, .95f));
            GUI.Label(new Rect(card.x + 8, card.y + 4, card.width - 16,
                card.height - 8), objective, objectiveText);
            if (mission.ReachedStages.Count <= 1) return;
            GUILayout.Space(20);
            GUILayout.Label("EARLIER STAGES", eyebrowText);
            for (int index = 0; index + 1 < mission.ReachedStages.Count; index++)
            {
                string description = mission.ReachedStages[index];
                if (!string.IsNullOrEmpty(description))
                {
                    GUILayout.Space(6);
                    GUILayout.Label($"{index + 1:00}   {description}", detailText);
                }
            }
        }
    }
}
