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
        private Vector2 formulaScroll, detailScroll;
        private bool open;
        private float CanvasWidth => Screen.width / flightHud.Scale;
        private float CanvasHeight => Screen.height / flightHud.Scale;
        public bool IsOpen => open;

        /// <summary>Share the flight HUD's station styles without owning network state.</summary>
        public ProductionHud(SpaceClient client, FlightHud flightHud)
        { this.client = client; this.flightHud = flightHud; }

        /// <summary>Reset the overlay after logout or a station departure.</summary>
        public void Close() { open = false; }

        /// <summary>Draw the terminal selector and active server workbench view.</summary>
        public void Draw(Snapshot snapshot)
        {
            if (snapshot?.Station == null || snapshot.Dialogue != null) { open = false; return; }
            Rect toggle = new Rect((CanvasWidth - 180) * .5f + 100, 27, 180, 30);
            if (GUI.Button(toggle, open ? "CLOSE BENCH" : "WORKBENCH", flightHud.ButtonStyle))
            {
                open = !open;
                if (open && !snapshot.Production.Open) client.OpenProduction(4);
                else if (!open && snapshot.Production.Open) client.CloseProduction();
            }
            if (!open) return;
            Rect panel = new Rect((CanvasWidth - 780) * .5f, 91, 780,
                Mathf.Min(590, CanvasHeight - 112));
            flightHud.Panel(panel);
            GUI.Label(new Rect(panel.x + 18, panel.y + 13, 340, 26),
                "STATION WORKBENCH", flightHud.BodyStyle);
            GUI.Label(new Rect(panel.x + 18, panel.y + 40, panel.width - 36, 22),
                "Choose a formula or cargo sample. Quotes update as your inventory changes.",
                flightHud.SmallStyle);
            DrawModes(panel, snapshot.Production.Mode);
            if (!snapshot.Production.Open)
            {
                GUI.Label(new Rect(panel.x + 22, panel.y + 120, panel.width - 44, 32),
                    "Opening the station terminal…", flightHud.BodyStyle);
                return;
            }
            DrawLevelFilter(panel, snapshot.Production);
            float contentTop = panel.y + 156;
            float contentHeight = panel.height - 174;
            GUILayout.BeginArea(new Rect(panel.x + 18, contentTop, 334, contentHeight));
            DrawChoices(snapshot);
            GUILayout.EndArea();
            GUILayout.BeginArea(new Rect(panel.x + 372, contentTop, 390, contentHeight));
            DrawQuote(snapshot);
            GUILayout.EndArea();
        }

        /// <summary>Switch between four native terminal modes.</summary>
        private void DrawModes(Rect panel, uint mode)
        {
            string[] names = { "MANUFACTURE", "ANALYZE", "DISMANTLE", "REFINE" };
            for (uint index = 0; index < names.Length; index++)
            {
                Rect button = new Rect(panel.x + 18 + index * 187, panel.y + 76, 178, 31);
                bool active = mode == index + 1;
                if (GUI.Button(button, names[index], flightHud.ButtonStyle) && !active)
                {
                    client.OpenProduction(index + 1);
                    formulaScroll = detailScroll = Vector2.zero;
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
                Rect button = new Rect(panel.x + 100 + (level - 1) * 72, panel.y + 113, 67, 29);
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
            formulaScroll = GUILayout.BeginScrollView(formulaScroll);
            if (sampleMode)
            {
                foreach (ItemView item in snapshot.Cargo.Where(item => item != null))
                {
                    GUI.enabled = production.Target == null;
                    if (GUILayout.Button($"{item.Slot:00}  {item.Name} ×{item.Quantity}",
                        flightHud.ButtonStyle, GUILayout.Height(29)))
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
                    if (GUILayout.Button(label, flightHud.ButtonStyle, GUILayout.Height(29)))
                        client.SelectProductionRecipe(formula.Template);
                }
                if (production.Formulas.Length == 0)
                    GUILayout.Label(production.Mode == 1 ?
                        "No learned recipes match these levels." :
                        "Carry refining materials to list available formulas.", flightHud.SmallStyle);
            }
            GUILayout.EndScrollView();
        }

        /// <summary>Show the server's selected target, ingredients, and production action.</summary>
        private void DrawQuote(Snapshot snapshot)
        {
            ProductionView production = snapshot.Production;
            detailScroll = GUILayout.BeginScrollView(detailScroll);
            GUILayout.Label(production.Target?.Name ?? "SELECT AN ITEM", flightHud.BodyStyle);
            if (production.Target != null)
            {
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
                    GUILayout.BeginHorizontal(GUILayout.Height(29));
                    GUILayout.Label($"{required} × {first.Name}", flightHud.BodyStyle,
                        GUILayout.Width(225));
                    GUILayout.Label(availability, present == required ? flightHud.SectionStyle :
                        flightHud.SmallStyle, GUILayout.Width(155));
                    GUILayout.EndHorizontal();
                }
                GUILayout.Space(8);
                string result = production.Validity == 13 ? "Ready" :
                    production.Validity == 16 ? "Completed" :
                    production.Failure.Length > 0 ? production.Failure : "Waiting for server";
                GUILayout.Label(result, production.Ready ? flightHud.SectionStyle :
                    flightHud.SmallStyle);
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
    }
}
