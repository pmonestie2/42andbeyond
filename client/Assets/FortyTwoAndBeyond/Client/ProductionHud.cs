using System.Collections.Generic;
using System.Linq;
using FortyTwoAndBeyond.Protocol;
using UnityEngine;

namespace FortyTwoAndBeyond.Client
{
    /// <summary>Present server-owned station refining and crafting terminals.</summary>
    public sealed class ProductionHud
    {
        private readonly HudWindowDrag windowDrag = new HudWindowDrag();
        private readonly SpaceClient client;
        private readonly FlightHud flightHud;
        private readonly HashSet<uint> requestedMarkets = new HashSet<uint>();
        private Vector2 formulaScroll, detailScroll;
        private bool open, collapsed;
        private uint selectedMode;
        private float requestAt, choicesWidth;
        private GUIStyle wrappedBody, wrappedSmall, searchStyle;
        private string recipeSearch = "";
        private RecipeOwnership recipeOwnership;
        private bool searchFocused;
        private Snapshot countedSnapshot;
        private ProductionRecipeFilter recipeFilter;
        private RecipeTypes recipeTypes = RecipeTypes.All;
        private readonly Dictionary<uint, ProductionFormula[]> recipePages = new Dictionary<uint, ProductionFormula[]>();
        private uint bookStation, bookLab, bookLevels, bookCategory, bookTotal, originalPage, waitingPage, queuedRecipe;
        private ulong observedRevision, requestedRevision;
        private bool loadingBook, restoringPage, selectingPage;
        private float pageDeadline;
        private string bookStatus = "";
        private bool RecipePagesBusy => loadingBook || restoringPage || selectingPage;
        /// Returns:
        ///     bool: Whether every page of the current level-filtered learned recipe book is cached.
        private bool WholeBookLoaded(ProductionView production) => production.PageSize > 0 && recipePages.Count ==
            (production.TotalFormulas + production.PageSize - 1) / production.PageSize;
        /// Returns:
        ///     bool: Whether the manufacturing search field owns foreground keyboard input.
        public bool InputFocused => open && !collapsed && selectedMode == 1 && searchFocused;
        private float CanvasWidth => Screen.width / flightHud.Scale;
        private float CanvasHeight => Screen.height / flightHud.Scale;
        public bool IsOpen => open;
        public uint SelectedMode => selectedMode;

        /// <summary>Share the flight HUD's station styles without owning network state.</summary>
        public ProductionHud(SpaceClient client, FlightHud flightHud)
        { this.client = client; this.flightHud = flightHud; }

        /// <summary>Reset the overlay after logout or a station departure.</summary>
        public void Close() { open = false; searchFocused = false; requestedMarkets.Clear(); ResetRecipeBook(); }

        /// <summary>Select a directory service and request it after any contact dialogue closes.</summary>
        public void Open(uint mode)
        {
            if (mode < 1 || mode > 4) return;
            if (selectedMode != mode) ResetRecipeBook();
            open = true; collapsed = false; selectedMode = mode; requestAt = 0;
            formulaScroll = detailScroll = Vector2.zero;
        }

        /// <summary>Draw the terminal selector and active server workbench view.</summary>
        public void Draw(Snapshot snapshot)
        {
            if (snapshot?.Station == null) { open = false; return; }
            if (snapshot.Dialogue != null || !open) { searchFocused = false; return; }
            if (wrappedBody == null)
            {
                wrappedBody = new GUIStyle(flightHud.BodyStyle) { wordWrap = true };
                wrappedSmall = new GUIStyle(flightHud.SmallStyle) { wordWrap = true };
                searchStyle = new GUIStyle(GUI.skin.textField) { font = flightHud.BodyStyle.font,
                    fontSize = 14, padding = new RectOffset(10, 8, 7, 5) };
            }
            if ((!snapshot.Production.Open || snapshot.Production.Mode != selectedMode) &&
                Time.unscaledTime >= requestAt)
            {
                // Leave room for a paced recipe book before retrying a missing response.
                client.OpenProduction(selectedMode); requestAt = Time.unscaledTime + 8f;
            }
            Rect panel = SingleRoomStationHud.Workspace(CanvasWidth, CanvasHeight);
            if (collapsed) panel.height = 54;
            panel = windowDrag.Handle(panel, CanvasWidth, CanvasHeight);
            flightHud.Panel(panel); HudWindowDrag.DrawGrip(panel);
            GUI.Label(new Rect(panel.x + 36, panel.y + 13, 322, 26),
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
            if (selectedMode == 1) UpdateRecipeBook(snapshot);
            bool previousEnabled = GUI.enabled;
            GUI.enabled = previousEnabled && !RecipePagesBusy;
            DrawLevelFilter(panel, snapshot.Production);
            GUI.enabled = previousEnabled;
            if (selectedMode == 1) DrawRecipeFilters(panel, snapshot);
            else searchFocused = false;
            float filterHeight = selectedMode == 1 ? 146 : 0;
            float contentTop = panel.y + 156 + filterHeight;
            float contentHeight = panel.height - 174 - filterHeight;
            choicesWidth = (panel.width - 64) * .45f;
            GUILayout.BeginArea(new Rect(panel.x + 18, contentTop, choicesWidth, contentHeight));
            DrawChoices(snapshot);
            GUILayout.EndArea();
            GUILayout.BeginArea(new Rect(panel.x + 38 + choicesWidth, contentTop,
                panel.width - choicesWidth - 56, contentHeight));
            GUI.enabled = previousEnabled && !RecipePagesBusy;
            DrawQuote(snapshot);
            GUI.enabled = previousEnabled;
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

        /// <summary>Discard a previous character, service or level-specific recipe cache.</summary>
        private void ResetRecipeBook()
        {
            recipePages.Clear(); loadingBook = restoringPage = selectingPage = false;
            bookStation = bookLab = bookLevels = bookCategory = bookTotal = queuedRecipe = 0;
            observedRevision = requestedRevision = 0; bookStatus = "";
        }

        /// <summary>Cache only complete authoritative pages and advance one outstanding request at a time.</summary>
        private void UpdateRecipeBook(Snapshot snapshot)
        {
            ProductionView production = snapshot.Production;
            uint station = snapshot.Station.Id;
            if (bookStation != station || bookLab != production.LabId || bookLevels != production.Filter ||
                bookCategory != production.Category || bookTotal != production.TotalFormulas)
            {
                ResetRecipeBook(); bookStation = station; bookLab = production.LabId;
                bookLevels = production.Filter; bookCategory = production.Category; bookTotal = production.TotalFormulas;
                // A new native filter can precede its formula chunks. Require a fresh
                // end-of-page marker before caching anything under the new book key.
                observedRevision = production.PageRevision;
            }
            if (production.PageRevision != observedRevision)
            {
                observedRevision = production.PageRevision;
                if (recipePages.TryGetValue(production.PageIndex, out ProductionFormula[] previous) &&
                    !previous.Select(formula => formula.Template).SequenceEqual(production.Formulas.Select(formula => formula.Template)))
                    recipePages.Clear(); // Learned formulas changed; other cached pages may have shifted too.
                recipePages[production.PageIndex] = production.Formulas;
                if (RecipePagesBusy && production.PageIndex == waitingPage && production.PageRevision > requestedRevision)
                {
                    if (restoringPage)
                    { restoringPage = false; bookStatus = WholeBookLoaded(production) ? "All known recipes ready. Original recipe page restored." : bookStatus; }
                    else if (selectingPage)
                    {
                        selectingPage = false;
                        if (queuedRecipe == 0) bookStatus = "Recipe page ready.";
                        else if (production.Formulas.Any(formula => formula.Template == queuedRecipe)) client.SelectProductionRecipe(queuedRecipe);
                        else bookStatus = "That recipe is no longer on this page. Refresh your known recipes.";
                        queuedRecipe = 0;
                    }
                    else if (loadingBook) RequestNextRecipePage(snapshot);
                }
            }
            else if (recipePages.TryGetValue(production.PageIndex, out ProductionFormula[] current) &&
                current.Select(formula => formula.Template).SequenceEqual(production.Formulas.Select(formula => formula.Template)))
                recipePages[production.PageIndex] = production.Formulas;
            if (RecipePagesBusy && Time.unscaledTime > pageDeadline)
            {
                if (restoringPage)
                { restoringPage = false; bookStatus = "Recipe page restoration timed out. Use Previous / Next to choose a page."; }
                else
                { CancelRecipePages(snapshot); bookStatus = "Loading recipes timed out; restoring your original page…"; }
            }
        }

        /// <summary>Load the learned recipe book using the existing bounded page requests.</summary>
        private void BeginRecipePages(Snapshot snapshot)
        {
            if (snapshot.Production.TotalFormulas == 0) { bookStatus = "No known recipes match these levels."; return; }
            originalPage = snapshot.Production.PageIndex;
            recipePages.TryGetValue(originalPage, out ProductionFormula[] originalFormulas);
            recipePages.Clear();
            if (originalFormulas != null) recipePages[originalPage] = originalFormulas;
            loadingBook = true; restoringPage = selectingPage = false; queuedRecipe = 0;
            RequestNextRecipePage(snapshot);
        }

        /// <summary>Request the next missing page or restore the page that was open before browsing.</summary>
        private void RequestNextRecipePage(Snapshot snapshot)
        {
            ProductionView production = snapshot.Production;
            uint totalPages = production.PageSize == 0 ? 0 : (production.TotalFormulas + production.PageSize - 1) / production.PageSize;
            for (uint page = 0; page < totalPages; page++)
                if (!recipePages.ContainsKey(page))
                {
                    bookStatus = "Loading all known recipes… " + recipePages.Count + " / " + totalPages + " pages. You can cancel safely.";
                    RequestRecipePage(snapshot, page); return;
                }
            loadingBook = false; restoringPage = true;
            bookStatus = "All known recipes loaded; restoring your original page…";
            RequestRecipePage(snapshot, originalPage);
        }

        /// <summary>Cancel loading or selection and restore the original page before enabling crafting.</summary>
        private void CancelRecipePages(Snapshot snapshot)
        {
            loadingBook = selectingPage = false; queuedRecipe = 0; restoringPage = true;
            bookStatus = "Loading cancelled; restoring your original page…";
            RequestRecipePage(snapshot, originalPage);
        }

        /// <summary>Queue a page request and require a newer end-of-page snapshot before proceeding.</summary>
        private void RequestRecipePage(Snapshot snapshot, uint page)
        {
            waitingPage = page; requestedRevision = snapshot.Production.PageRevision;
            pageDeadline = Time.unscaledTime + 20;
            client.SelectProductionPage(page);
        }

        /// <summary>Keep manual native paging available until the full recipe book has loaded.</summary>
        private void BrowseRecipePage(Snapshot snapshot, uint page)
        {
            if (snapshot.Production.Mode != 1) { client.SelectProductionPage(page); return; }
            originalPage = snapshot.Production.PageIndex; selectingPage = true; queuedRecipe = 0;
            bookStatus = "Loading recipe page…"; RequestRecipePage(snapshot, page);
        }

        /// <summary>Load a cached formula's native page before submitting its normal recipe selection.</summary>
        private void SelectCachedRecipe(Snapshot snapshot, uint template)
        {
            if (snapshot.Production.Formulas.Any(formula => formula.Template == template))
            { client.SelectProductionRecipe(template); return; }
            foreach (var page in recipePages)
                if (page.Value.Any(formula => formula.Template == template))
                {
                    originalPage = snapshot.Production.PageIndex; selectingPage = true; queuedRecipe = template;
                    bookStatus = "Opening the selected recipe…";
                    RequestRecipePage(snapshot, page.Key); return;
                }
        }

        /// <summary>Keep search and ownership local while preserving native level controls and recipe pages.</summary>
        private void DrawRecipeFilters(Rect panel, Snapshot snapshot)
        {
            if (!ReferenceEquals(countedSnapshot, snapshot))
            {
                countedSnapshot = snapshot;
                recipeFilter = new ProductionRecipeFilter(snapshot.Cargo, snapshot.Ammo, snapshot.Vault);
            }
            float x = panel.x + 18, width = panel.width - 36;
            GUI.SetNextControlName("manufacturing-recipe-search");
            string next = GUI.TextField(new Rect(x, panel.y + 151, width - 70, 31), recipeSearch, searchStyle);
            searchFocused = GUI.GetNameOfFocusedControl() == "manufacturing-recipe-search";
            if (next != recipeSearch) { recipeSearch = next; formulaScroll = Vector2.zero; }
            if (recipeSearch.Length == 0)
                GUI.Label(new Rect(x + 10, panel.y + 157, width - 100, 22), WholeBookLoaded(snapshot.Production) ? "Search all known recipe names…" : "Search recipe names on this page…", flightHud.SmallStyle);
            if (GUI.Button(new Rect(x + width - 62, panel.y + 151, 62, 31), "Clear", flightHud.ButtonStyle))
            { recipeSearch = ""; recipeOwnership = RecipeOwnership.All; recipeTypes = RecipeTypes.All; formulaScroll = Vector2.zero; GUI.FocusControl(""); searchFocused = false; }
            string[] labels = { "All recipes", "Aboard · " + recipeFilter.AboardTypes + " types",
                "Vault · " + recipeFilter.VaultTypes + " types", "Aboard + vault" };
            for (int index = 0; index < labels.Length; index++)
            {
                Rect button = new Rect(x + index * width / 4, panel.y + 190, width / 4 - 6, 29);
                if (GUI.Button(button, labels[index], flightHud.ButtonStyle))
                { recipeOwnership = (RecipeOwnership)index; formulaScroll = Vector2.zero; }
                if ((int)recipeOwnership == index)
                    Accent(new Rect(button.x, button.yMax - 3, button.width, 3), new Color(.35f, .9f, 1f));
            }
            string[] typeNames = { "All types", "Ammo", "Components", "Devices", "Reactors", "Shields", "Engines", "Weapons", "Other" };
            RecipeTypes[] categories = { RecipeTypes.All, RecipeTypes.Ammo, RecipeTypes.Components, RecipeTypes.Devices,
                RecipeTypes.Reactors, RecipeTypes.Shields, RecipeTypes.Engines, RecipeTypes.Weapons, RecipeTypes.Other };
            for (int index = 0; index < categories.Length; index++)
            {
                Rect button = new Rect(x + index % 5 * width / 5, panel.y + 227 + index / 5 * 32, width / 5 - 6, 27);
                RecipeTypes category = categories[index];
                bool active = category == RecipeTypes.All ? recipeTypes == RecipeTypes.All :
                    recipeTypes != RecipeTypes.All && (recipeTypes & category) != 0;
                if (GUI.Button(button, typeNames[index], flightHud.ButtonStyle))
                {
                    recipeTypes = category == RecipeTypes.All ? RecipeTypes.All :
                        recipeTypes == RecipeTypes.All ? category : recipeTypes ^ category;
                    formulaScroll = Vector2.zero;
                }
                if (active) Accent(new Rect(button.x, button.yMax - 3, button.width, 3), new Color(.91f, .73f, .39f));
            }
            GUI.Label(new Rect(x + width * .8f, panel.y + 262, width * .2f - 6, 24), "Combine types", flightHud.SmallStyle);
        }

        /// <summary>List server-offered formulas or owned analyzer samples.</summary>
        private void DrawChoices(Snapshot snapshot)
        {
            ProductionView production = snapshot.Production;
            bool sampleMode = production.Mode == 2 || production.Mode == 3;
            GUILayout.Label(sampleMode ? "OWNED CARGO SAMPLES" : "AVAILABLE FORMULAS",
                flightHud.BodyStyle);
            if (production.Mode == 1)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label(WholeBookLoaded(production) ? "ALL KNOWN RECIPES" : "CURRENT PAGE ONLY", wrappedSmall);
                if (RecipePagesBusy)
                {
                    if (GUILayout.Button("Cancel", flightHud.ButtonStyle, GUILayout.Width(85))) CancelRecipePages(snapshot);
                }
                else if (GUILayout.Button(WholeBookLoaded(production) ? "Refresh recipes" : "Load all recipes", flightHud.ButtonStyle, GUILayout.Width(145)))
                    BeginRecipePages(snapshot);
                GUILayout.EndHorizontal();
                if (!string.IsNullOrEmpty(bookStatus)) GUILayout.Label(bookStatus, wrappedSmall);
            }
            if (!sampleMode && production.TotalFormulas > production.PageSize &&
                (production.Mode != 1 || !WholeBookLoaded(production)))
            {
                GUILayout.BeginHorizontal();
                GUI.enabled = !RecipePagesBusy && production.PageIndex > 0;
                if (GUILayout.Button("‹ Prev", flightHud.ButtonStyle, GUILayout.Width(62)))
                { BrowseRecipePage(snapshot, production.PageIndex - 1); formulaScroll = Vector2.zero; }
                GUI.enabled = true;
                uint first = production.PageIndex * production.PageSize + 1;
                uint last = first + (uint)production.Formulas.Length - 1;
                GUILayout.Label($"{first}–{last} / {production.TotalFormulas}", flightHud.SmallStyle);
                GUI.enabled = !RecipePagesBusy && (production.PageIndex + 1) * production.PageSize < production.TotalFormulas;
                if (GUILayout.Button("Next ›", flightHud.ButtonStyle, GUILayout.Width(62)))
                { BrowseRecipePage(snapshot, production.PageIndex + 1); formulaScroll = Vector2.zero; }
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
                bool localFilters = production.Mode == 1;
                ProductionFormula[] available = localFilters && WholeBookLoaded(production) ?
                    recipePages.OrderBy(page => page.Key).SelectMany(page => page.Value).ToArray() : production.Formulas;
                ProductionFormula[] choices = localFilters ? available.Where(formula =>
                    recipeFilter.Matches(formula, production.Filter, recipeSearch, recipeOwnership, recipeTypes)).ToArray() : available;
                if (localFilters)
                    GUILayout.Label(choices.Length + " / " + available.Length +
                        (WholeBookLoaded(production) ? " matches in all known recipes" : " matches on this page") + " · Aboard includes loaded rounds", wrappedSmall);
                foreach (ProductionFormula formula in choices)
                {
                    string label = $"L{formula.Level}  {formula.Name}";
                    if (localFilters)
                    {
                        ulong aboard = recipeFilter.AboardQuantity(formula.Template), vault = recipeFilter.VaultQuantity(formula.Template);
                        if (aboard > 0 || vault > 0) label += $"\nAboard {aboard:N0}  ·  Vault {vault:N0}";
                    }
                    GUI.enabled = !RecipePagesBusy;
                    if (DrawItemChoice(label, flightHud.ItemIcons.ForIcon(formula.IconId, formula.Type),
                        production.Target?.Template == formula.Template))
                    {
                        if (localFilters) SelectCachedRecipe(snapshot, formula.Template);
                        else client.SelectProductionRecipe(formula.Template);
                    }
                    GUI.enabled = true;
                }
                if (choices.Length == 0)
                    GUILayout.Label(production.Mode == 1 ?
                        (production.Formulas.Length == 0 ? "No learned recipes match these levels." :
                        (WholeBookLoaded(production) ? "No known recipes match these combined filters. Clear filters to see every recipe." :
                        "No matches on this page. Load all recipes, clear filters, or browse another page.")) :
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
                GUI.enabled = GUI.enabled && production.Ready && !RecipePagesBusy;
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
