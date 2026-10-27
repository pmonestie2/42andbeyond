using System.Collections.Generic;
using System.Linq;
using FortyTwoAndBeyond.Protocol;
using UnityEngine;

namespace FortyTwoAndBeyond.Client
{
    /// <summary>Present server-owned station refining and crafting terminals.</summary>
    public sealed class ProductionHud
    {
        private readonly SpaceClient client;
        private readonly FlightHud flightHud;
        private readonly HashSet<uint> requestedMarkets = new HashSet<uint>();
        private Vector2 formulaScroll, detailScroll;
        private bool open, collapsed;
        private uint selectedMode;
        private float requestAt, choicesWidth;
        private GUIStyle wrappedBody, wrappedSmall;
        private float CanvasWidth => Screen.width / flightHud.Scale;
        private float CanvasHeight => Screen.height / flightHud.Scale;
        public bool IsOpen => open;
        public uint SelectedMode => selectedMode;

        /// <summary>Share the flight HUD's station styles without owning network state.</summary>
        public ProductionHud(SpaceClient client, FlightHud flightHud)
        { this.client = client; this.flightHud = flightHud; }

        /// <summary>Reset the overlay after logout or a station departure.</summary>
        public void Close() { open = false; requestedMarkets.Clear(); }

        /// <summary>Select a directory service and request it after any contact dialogue closes.</summary>
        public void Open(uint mode)
        {
            if (mode < 1 || mode > 4) return;
            open = true; collapsed = false; selectedMode = mode; requestAt = 0;
            formulaScroll = detailScroll = Vector2.zero;
        }

        /// <summary>Draw the terminal selector and active server workbench view.</summary>
        public void Draw(Snapshot snapshot)
        {
            if (snapshot?.Station == null) { open = false; return; }
            if (snapshot.Dialogue != null || !open) return;
            if (wrappedBody == null)
            {
                wrappedBody = new GUIStyle(flightHud.BodyStyle) { wordWrap = true };
                wrappedSmall = new GUIStyle(flightHud.SmallStyle) { wordWrap = true };
            }
            if ((!snapshot.Production.Open || snapshot.Production.Mode != selectedMode) &&
                Time.unscaledTime >= requestAt)
            {
                client.OpenProduction(selectedMode); requestAt = Time.unscaledTime + 2f;
            }
            Rect panel = SingleRoomStationHud.Workspace(CanvasWidth, CanvasHeight);
            if (collapsed) panel.height = 54;
            flightHud.Panel(panel);
            GUI.Label(new Rect(panel.x + 18, panel.y + 13, 340, 26),
                "STATION WORKBENCH", flightHud.BodyStyle);
            if (GUI.Button(new Rect(panel.xMax - 324, panel.y + 12, 174, 29),
                "COMPONENT MARKET", flightHud.ButtonStyle)) client.OpenComponentMarket();
            if (GUI.Button(new Rect(panel.xMax - 100, panel.y + 12, 82, 29),
                "Close", flightHud.ButtonStyle)) { client.HideStationService(); return; }
            if (GUI.Button(new Rect(panel.xMax - 142, panel.y + 12, 34, 29),
                collapsed ? "+" : "−", flightHud.ButtonStyle)) collapsed = !collapsed;
            if (collapsed || panel.height < 100) return;
            GUI.Label(new Rect(panel.x + 18, panel.y + 40, panel.width - 36, 22),
                "Choose a formula or cargo sample. Quotes update as your inventory changes.",
                flightHud.SmallStyle);
            DrawModes(panel, selectedMode);
            if (!snapshot.Production.Open || snapshot.Production.Mode != selectedMode)
            {
                GUI.Label(new Rect(panel.x + 22, panel.y + 120, panel.width - 44, 32),
                    "Opening the station terminal…", flightHud.BodyStyle);
                return;
            }
            DrawLevelFilter(panel, snapshot.Production);
            float contentTop = panel.y + 156;
            float contentHeight = panel.height - 174;
            choicesWidth = (panel.width - 64) * .45f;
            GUILayout.BeginArea(new Rect(panel.x + 18, contentTop, choicesWidth, contentHeight));
            DrawChoices(snapshot);
            GUILayout.EndArea();
            GUILayout.BeginArea(new Rect(panel.x + 38 + choicesWidth, contentTop,
                panel.width - choicesWidth - 56, contentHeight));
            DrawQuote(snapshot);
            GUILayout.EndArea();
        }

        /// <summary>Switch between four native terminal modes.</summary>
        private void DrawModes(Rect panel, uint mode)
        {
            string[] names = { "MANUFACTURE", "ANALYZE", "DISMANTLE", "REFINE" };
            for (uint index = 0; index < names.Length; index++)
            {
                float step = (panel.width - 36) / 4;
                Rect button = new Rect(panel.x + 18 + index * step, panel.y + 76, step - 9, 31);
                bool active = mode == index + 1;
                if (GUI.Button(button, names[index], flightHud.ButtonStyle) && !active)
                {
                    Open(index + 1);
                }
                if (active)
                {
                    Accent(new Rect(button.x, button.y, 4, button.height),
                        new Color(.35f, .9f, 1f, 1));
                    Accent(new Rect(button.x, button.yMax - 3, button.width, 3),
                        new Color(.35f, .9f, 1f, 1));
                }
            }
        }

        /// <summary>Draw the active terminal accent without changing shared HUD textures.</summary>
        private static void Accent(Rect rect, Color color)
        {
            Color previous = GUI.color;
            GUI.color = color;
            GUI.DrawTexture(rect, Texture2D.whiteTexture);
            GUI.color = previous;
        }

        /// <summary>Filter recipe levels through the native nine-bit mask.</summary>
        private void DrawLevelFilter(Rect panel, ProductionView production)
        {
            if (production.Mode != 1 && production.Mode != 4) return;
            GUI.Label(new Rect(panel.x + 20, panel.y + 119, 94, 22), "LEVELS", flightHud.SmallStyle);
            for (int level = 1; level <= 9; level++)
            {
                uint bit = 1u << (level - 1);
                bool enabled = (production.Filter & bit) != 0;
                float step = (panel.width - 120) / 9;
                Rect button = new Rect(panel.x + 100 + (level - 1) * step, panel.y + 113, step - 5, 29);
                if (GUI.Button(button, "L" + level + (enabled ? "  ✓" : ""), flightHud.ButtonStyle))
                    client.SetProductionLevels(bit, !enabled);
            }
        }

        /// <summary>List server-offered formulas or owned analyzer samples.</summary>
        private void DrawChoices(Snapshot snapshot)
        {
            ProductionView production = snapshot.Production;
            bool sampleMode = production.Mode == 2 || production.Mode == 3;
            GUILayout.Label(sampleMode ? "OWNED CARGO SAMPLES" : "AVAILABLE FORMULAS",
                flightHud.BodyStyle);
            if (!sampleMode && production.TotalFormulas > production.PageSize)
            {
                GUILayout.BeginHorizontal();
                GUI.enabled = production.PageIndex > 0;
                if (GUILayout.Button("‹ Prev", flightHud.ButtonStyle, GUILayout.Width(76)))
                { client.SelectProductionPage(production.PageIndex - 1); formulaScroll = Vector2.zero; }
                GUI.enabled = true;
                uint first = production.PageIndex * production.PageSize + 1;
                uint last = first + (uint)production.Formulas.Length - 1;
                GUILayout.Label($"{first}–{last} / {production.TotalFormulas}", flightHud.SmallStyle);
                GUI.enabled = (production.PageIndex + 1) * production.PageSize < production.TotalFormulas;
                if (GUILayout.Button("Next ›", flightHud.ButtonStyle, GUILayout.Width(76)))
                { client.SelectProductionPage(production.PageIndex + 1); formulaScroll = Vector2.zero; }
                GUI.enabled = true;
                GUILayout.EndHorizontal();
            }
            formulaScroll = GUILayout.BeginScrollView(formulaScroll);
            if (sampleMode)
            {
                foreach (ItemView item in snapshot.Cargo.Where(item => item != null))
                {
                    GUI.enabled = production.Target == null;
                    string label = $"{item.Slot:00}  {item.Name} ×{item.Quantity}";
                    if (DrawItemChoice(label, flightHud.ItemIcons.ForItem(item), false))
                        client.InsertAnalysisSample(item.Slot);
                }
                GUI.enabled = true;
                if (snapshot.Cargo.All(item => item == null))
                    GUILayout.Label("Cargo is empty.", flightHud.SmallStyle);
            }
            else
            {
                foreach (ProductionFormula formula in production.Formulas)
                {
                    string label = $"L{formula.Level}  {formula.Name}";
                    if (DrawItemChoice(label, flightHud.ItemIcons.ForIcon(formula.IconId, formula.Type),
                        production.Target?.Template == formula.Template))
                        client.SelectProductionRecipe(formula.Template);
                }
                if (production.Formulas.Length == 0)
                    GUILayout.Label(production.Mode == 1 ?
                        "No learned recipes match these levels." :
                        "Carry refining materials to list available formulas.", flightHud.SmallStyle);
            }
            GUILayout.EndScrollView();
        }

        /// <summary>
        /// Returns:
        ///     bool: Whether the icon-and-name choice was clicked.
        /// </summary>
        private bool DrawItemChoice(string label, Texture2D icon, bool selected)
        {
            float textWidth = Mathf.Max(60, choicesWidth - 94);
            float height = Mathf.Max(54, wrappedBody.CalcHeight(new GUIContent(label), textWidth) + 16);
            Rect row = GUILayoutUtility.GetRect(0, height, GUILayout.ExpandWidth(true));
            bool clicked = GUI.Button(row, GUIContent.none, flightHud.ButtonStyle);
            if (selected) Accent(new Rect(row.x, row.y, 3, row.height), new Color(.35f, .9f, 1f));
            GUI.DrawTexture(new Rect(row.x + 8, row.y + (height - 40) / 2, 40, 40), icon, ScaleMode.ScaleToFit);
            GUI.Label(new Rect(row.x + 58, row.y + 8, Mathf.Max(40, row.width - 66), height - 16),
                label, wrappedBody);
            GUILayout.Space(4);
            return clicked;
        }

        /// <summary>Keep native item art beside the selected product or required component.</summary>
        private void DrawItemSummary(ItemView item, string title, string detail)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(flightHud.ItemIcons.ForItem(item), GUILayout.Width(48), GUILayout.Height(48));
            GUILayout.BeginVertical();
            GUILayout.Label(title, wrappedBody);
            GUILayout.Label(detail, wrappedSmall);
            GUILayout.EndVertical();
            GUILayout.EndHorizontal();
        }

        /// <summary>Show the server's selected target, ingredients, and production action.</summary>
        private void DrawQuote(Snapshot snapshot)
        {
            ProductionView production = snapshot.Production;
            detailScroll = GUILayout.BeginScrollView(detailScroll);
            if (production.Target == null) GUILayout.Label("SELECT AN ITEM", wrappedBody);
            if (production.Target != null)
            {
                DrawItemSummary(production.Target, production.Target.Name, "SELECTED ITEM");
                GUILayout.Label($"Cost  {production.Cost:N0} credits   ·   Available  {snapshot.Credits:N0}",
                    flightHud.SmallStyle);
                if (production.Mode == 2 || production.Mode == 3)
                {
                    GUILayout.Label($"Estimated chance  {production.Chance:P0}", flightHud.SmallStyle);
                    if (GUILayout.Button("RETURN SAMPLE TO CARGO", flightHud.ButtonStyle,
                        GUILayout.Height(29))) client.ReturnAnalysisSample();
                }
                GUILayout.Space(8);
                GUILayout.Label(production.Mode == 3 ? "POSSIBLE RECOVERY" : "REQUIRED COMPONENTS",
                    flightHud.BodyStyle);
                foreach (var group in production.Components.Where(item => item != null)
                    .GroupBy(item => item.Template))
                {
                    ItemView first = group.First();
                    int required = group.Count();
                    int present = group.Count(item => item.Quantity > 0);
                    string availability = production.Mode == 3 ?
                        production.Validity == 16 ? $"{present}/{required} recovered" :
                        $"{required} possible" : $"{present}/{required} available";
                    DrawItemSummary(first, $"{required} × {first.Name}", availability);
                    if (!snapshot.ComponentMarkets.ContainsKey(first.Template) && requestedMarkets.Add(first.Template))
                        client.QueryComponentMarket(first.Template);
                    GUILayout.BeginHorizontal();
                    GUILayout.Label(ComponentSourceSummary(snapshot, first.Template), wrappedSmall,
                        GUILayout.MinWidth(110), GUILayout.ExpandWidth(true));
                    if (GUILayout.Button("Find / Trade", flightHud.ButtonStyle,
                        GUILayout.Width(105), GUILayout.Height(29))) client.OpenComponentMarket(first);
                    GUILayout.EndHorizontal();
                    GUILayout.Space(3);
                }
                GUILayout.Space(8);
                string result = production.Validity == 13 ? "Ready" :
                    production.Validity == 16 ? "Completed" :
                    production.Failure.Length > 0 ? production.Failure : "Waiting for server";
                GUILayout.Label(result, production.Ready ? flightHud.SectionStyle : wrappedSmall);
                GUI.enabled = production.Ready;
                if (GUILayout.Button(production.Mode == 1 ? "MANUFACTURE ONE" :
                    production.Mode == 2 ? "ANALYZE SAMPLE" :
                    production.Mode == 3 ? "DISMANTLE SAMPLE" : "REFINE ONE",
                    flightHud.ButtonStyle, GUILayout.Height(36))) client.StartProduction(false);
                if (production.Mode == 4 && production.Additional > 0 &&
                    GUILayout.Button($"REFINE STACK  ·  {production.Additional + 1} runs",
                        flightHud.ButtonStyle, GUILayout.Height(32))) client.StartProduction(true);
                GUI.enabled = true;
            }
            else if (production.Failure.Length > 0)
                GUILayout.Label(production.Failure, flightHud.SmallStyle);
            GUILayout.EndScrollView();
        }

        /// <returns>string: A compact summary of cached station suppliers for this component.</returns>
        private static string ComponentSourceSummary(Snapshot snapshot, uint template)
        {
            if (!snapshot.ComponentMarkets.TryGetValue(template, out ComponentMarketView market))
                return "Locating station sources…";
            if (market.Sources.Count == 0) return "No station vendor source listed";
            var stations = market.Sources.OrderBy(source => source.Station == snapshot.Station?.Id ? 0 : 1)
                .GroupBy(source => source.Station).Select(group => group.First()).ToArray();
            string summary = string.Join(" · ", stations.Take(2).Select(source => source.StationName));
            return "Sources: " + summary + (stations.Length > 2 ? "  +" + (stations.Length - 2) : "");
        }
    }
}
