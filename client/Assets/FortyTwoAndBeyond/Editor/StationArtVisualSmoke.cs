using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using FortyTwoAndBeyond.Client;
using FortyTwoAndBeyond.Protocol;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace FortyTwoAndBeyond.Editor
{
    /// <summary>Capture the station art against an owned disposable station session.</summary>
    [InitializeOnLoad]
    public static class StationArtVisualSmoke
    {
        private static double deadline, captureAt;
        private static int stage;
        private static bool connected;
        private static bool compact;
        private static bool motion;
        private static bool recipes, market;
        private static ulong marketCredits;
        private static uint marketCount, marketNpc;
        private static uint firstRecipe, recipeOriginalPage, cachedRecipe, cachedRecipePage;
        private static ulong recipeRevision;
        private static uint contactId;
        private static int directoryRepaints;

        /// <returns>string: The screenshot path for the active Game View size.</returns>
        private static string CaptureName(string scene)
        { return "Builds/verification/station-" + scene + (compact ? "-compact" : "") + ".png"; }

        /// <summary>Attach the opt-in driver before the Editor enters Play mode.</summary>
        static StationArtVisualSmoke()
        {
            if (Array.IndexOf(Environment.GetCommandLineArgs(), "--42andbeyond-station-art-smoke") < 0)
                return;
            compact = Array.IndexOf(Environment.GetCommandLineArgs(), "--42andbeyond-station-compact") >= 0;
            motion = Array.IndexOf(Environment.GetCommandLineArgs(), "--42andbeyond-station-motion") >= 0;
            market = Array.IndexOf(Environment.GetCommandLineArgs(), "--42andbeyond-station-market") >= 0;
            recipes = Array.IndexOf(Environment.GetCommandLineArgs(), "--42andbeyond-station-recipes") >= 0;
            deadline = EditorApplication.timeSinceStartup + 90;
            EditorApplication.update += Tick;
        }

        /// <summary>Verify native rooms remain intact while one client directory exposes their contacts.</summary>
        private static void Tick()
        {
            if (EditorApplication.isCompiling || EditorApplication.isUpdating) return;
            if (EditorApplication.timeSinceStartup > deadline)
            { Fail("Station smoke timed out at stage " + stage); return; }
            if (!EditorApplication.isPlayingOrWillChangePlaymode)
            {
                EditorSceneManager.OpenScene("Assets/FortyTwoAndBeyond/Space.unity");
                Type gameView = typeof(EditorWindow).Assembly.GetType("UnityEditor.GameView");
                if (gameView != null)
                {
                    EditorWindow window = compact ? ScriptableObject.CreateInstance(gameView) as EditorWindow :
                        EditorWindow.GetWindow(gameView);
                    if (compact) { window.Show(); window.position = new Rect(80, 75, 1180, 700); }
                    else window.maximized = true;
                    window.Focus();
                }
                EditorApplication.EnterPlaymode(); return;
            }
            if (!EditorApplication.isPlaying) return;
            SpaceClient client = UnityEngine.Object.FindFirstObjectByType<SpaceClient>();
            if (client == null) return;
            if (!connected) { connected = true; client.StartLocalSession("127.0.0.1", "local", "local"); }
            if (client.ChoosingCharacter) client.ChooseCharacter(client.Characters[0].Slot);
            if (client.ConnectionError != null) { Fail(client.ConnectionError); return; }
            var snapshot = client.CurrentSnapshot;
            if (snapshot?.Station == null || !snapshot.Ready || snapshot.Station.RoomList.Count == 0) return;
            if (snapshot.Station.RoomList.Count <= 1 || !snapshot.Station.RoomList.Any(room => room.Index == 0) ||
                !snapshot.Station.Contacts.Any(contact => contact.Room != 0))
            { Fail("Original multi-room station layout was flattened before reaching the client."); return; }
            FlightHud hud = Hud(client);
            SingleRoomStationHud directory = DirectoryHud(hud);
            if (directory == null || directory.DirectoryRenderCount == 0 || snapshot.Station.CurrentRoom != 0) return;
            if (directory.RenderedServices != 4 || directory.RenderedContacts != snapshot.Station.Contacts.Count ||
                directory.RenderedContacts == 0)
            {
                if (stage == 0) return;
                Fail("Station directory did not populate its industry services and contacts."); return;
            }
            Directory.CreateDirectory("Builds/verification");
            if (stage == 0)
            { captureAt = EditorApplication.timeSinceStartup + 1; stage = 1; return; }
            if (EditorApplication.timeSinceStartup < captureAt) return;
            if (stage == 1)
            {
                if (snapshot.Station.Contacts.Any(contact => GameObject.Find("Station contact " + contact.Id) == null))
                { Fail("Client concourse did not aggregate contact visuals from every native room."); return; }
                ScreenCapture.CaptureScreenshot(CaptureName("hangar"));
                captureAt = EditorApplication.timeSinceStartup + .5; stage = 2; return;
            }
            if (stage == 2)
            {
                if (!File.Exists(CaptureName("hangar"))) return;
                client.ShowStationService(1); stage = 3; return;
            }
            if (stage == 3)
            {
                if (!snapshot.Production.Open || snapshot.Production.Mode != 1) return;
                if (recipes)
                {
                    if (snapshot.Production.TotalFormulas <= 500 || snapshot.Production.Formulas.Length != 500) return;
                    firstRecipe = snapshot.Production.Formulas[0].Template;
                    client.SelectProductionPage(1); stage = 12; return;
                }
                if (market) { client.QueryComponentStock(); stage = 20; return; }
                client.ShowStationService(2); stage = 4; return;
            }
            if (stage == 12)
            {
                if (snapshot.Production.PageIndex != 1 || snapshot.Production.Formulas.Length == 0) return;
                if (snapshot.Production.Formulas[0].Template == firstRecipe)
                { Fail("Second recipe page repeated the first page."); return; }
                recipeOriginalPage = snapshot.Production.PageIndex;
                RecipeMethod(client, "BeginRecipePages", snapshot);
                stage = 30; return;
            }
            if (stage == 30)
            {
                if (RecipeBusy(client)) return;
                ProductionHud production = ProductionHudFor(client);
                var pages = RecipePages(production);
                uint expectedPages = (snapshot.Production.TotalFormulas + snapshot.Production.PageSize - 1) / snapshot.Production.PageSize;
                if (snapshot.Production.PageIndex != recipeOriginalPage || pages.Count != expectedPages ||
                    pages.Values.Sum(page => page.Length) != snapshot.Production.TotalFormulas)
                { Fail("Loading all known recipes did not cache complete pages and restore the original page."); return; }
                var otherPage = pages.First(page => page.Key != recipeOriginalPage && page.Value.Length > 0);
                ProductionFormula formula = otherPage.Value[0]; cachedRecipe = formula.Template; cachedRecipePage = otherPage.Key;
                SetRecipeField(production, "recipeTypes", ProductionRecipeFilter.Category(formula.Type));
                SetRecipeField(production, "recipeSearch", formula.Name);
                SetRecipeField(production, "recipeOwnership", RecipeOwnership.All);
                var filter = new ProductionRecipeFilter(snapshot.Cargo, snapshot.Ammo, snapshot.Vault);
                if (!filter.Matches(formula, snapshot.Production.Filter, formula.Name, RecipeOwnership.All, ProductionRecipeFilter.Category(formula.Type)))
                { Fail("Combined live manufacturing type/text/level filters excluded the cached output."); return; }
                captureAt = EditorApplication.timeSinceStartup + .3; stage = 31; return;
            }
            if (stage == 31)
            {
                ScreenCapture.CaptureScreenshot(CaptureName("recipe-filters"));
                recipeRevision = snapshot.Production.PageRevision;
                RecipeMethod(client, "SelectCachedRecipe", snapshot, cachedRecipe);
                stage = 32; return;
            }
            if (stage == 32)
            {
                if (RecipeBusy(client) || snapshot.Production.PageRevision <= recipeRevision ||
                    snapshot.Production.PageIndex != cachedRecipePage || snapshot.Production.Target?.Template != cachedRecipe) return;
                // Queue cancel before this disposable worker can publish the other page:
                // restoration must still win after that in-flight response arrives.
                recipeRevision = snapshot.Production.PageRevision;
                RecipeMethod(client, "BeginRecipePages", snapshot);
                RecipeMethod(client, "CancelRecipePages", snapshot);
                stage = 33; return;
            }
            if (stage == 33)
            {
                if (RecipeBusy(client) || snapshot.Production.PageRevision <= recipeRevision || snapshot.Production.PageIndex != cachedRecipePage) return;
                if (snapshot.Production.Target?.Template != cachedRecipe)
                { Fail("Cancelled recipe loading changed the selected manufacturing output."); return; }
                RecipeMethod(client, "BrowseRecipePage", snapshot, recipeOriginalPage);
                stage = 34; return;
            }
            if (stage == 34)
            {
                if (RecipeBusy(client) || snapshot.Production.PageIndex != recipeOriginalPage) return;
                ProductionHud production = ProductionHudFor(client);
                SetRecipeField(production, "recipeTypes", RecipeTypes.All);
                SetRecipeField(production, "recipeSearch", "");
                SetRecipeField(production, "recipeOwnership", RecipeOwnership.All);
                captureAt = EditorApplication.timeSinceStartup + .3; stage = 35; return;
            }
            if (stage == 35)
            {
                if (!File.Exists(CaptureName("recipe-filters"))) return;
                ScreenCapture.CaptureScreenshot(CaptureName("recipes"));
                Debug.Log("PASS: all known manufacturing recipes cached with original-page restoration; combined local filters, authoritative off-page selection, and in-flight cancellation verified.");
                captureAt = EditorApplication.timeSinceStartup + .5; stage = 13; return;
            }
            if (stage == 13)
            {
                if (market) { client.QueryComponentStock(); stage = 20; return; }
                client.ShowStationService(2); stage = 4; return;
            }
            if (stage == 20)
            {
                var item = snapshot.MarketItems.FirstOrDefault(value => value.Template == 1520);
                if (item == null) return;
                if (item.IconId == 0) { Fail("Market stock lost native icon metadata."); return; }
                client.OpenComponentMarket(item); stage = 21; return;
            }
            if (stage == 21)
            {
                if (!snapshot.ComponentMarkets.TryGetValue(1520, out var quotes)) return;
                var source = quotes.Sources.FirstOrDefault(value => value.Station == snapshot.Station.Id &&
                    value.BuyPrice > 0 && value.SellPrice > 0);
                if (source == null || !quotes.DevEnabled ||
                    !quotes.Sources.Any(value => value.StationName.Length > 0 && value.SectorName.Length > 0))
                { Fail("Component market lacks supplier, buyer, developer travel or location labels."); return; }
                marketNpc = source.Npc;
                marketCredits = snapshot.Credits;
                marketCount = snapshot.Cargo.Where(value => value?.Template == 1520).Aggregate(0u, (sum, value) => sum + value.Quantity);
                captureAt = EditorApplication.timeSinceStartup + 1; stage = 22; return;
            }
            if (stage == 22)
            {
                ScreenCapture.CaptureScreenshot(CaptureName("market"));
                client.BuyMarketComponent(marketNpc, 1520, 1); stage = 23; return;
            }
            if (stage == 23)
            {
                uint count = snapshot.Cargo.Where(value => value?.Template == 1520).Aggregate(0u, (sum, value) => sum + value.Quantity);
                if (count != marketCount + 1 || snapshot.Credits >= marketCredits) return;
                var owned = snapshot.Cargo.First(value => value?.Template == 1520);
                marketCredits = snapshot.Credits;
                client.SellMarketComponent(marketNpc, owned.Slot, 1); stage = 24; return;
            }
            if (stage == 24)
            {
                uint count = snapshot.Cargo.Where(value => value?.Template == 1520).Aggregate(0u, (sum, value) => sum + value.Quantity);
                if (count != marketCount || snapshot.Credits <= marketCredits) return;
                if (snapshot.Dialogue != null || !snapshot.Production.Open)
                { Fail("Component trade displaced the active workbench or opened a conversation."); return; }
                var field = typeof(SpaceClient).GetField("componentMarketHud", BindingFlags.Instance | BindingFlags.NonPublic);
                ((ComponentMarketHud)field.GetValue(client)).Close();
                Debug.Log("PASS: component stock, native icons, station supplier quotes, buy/sell cargo and credits, workbench preserved.");
                client.ShowStationService(2); stage = 4; return;
            }
            if (stage == 4)
            {
                if (!snapshot.Production.Open || snapshot.Production.Mode != 2) return;
                client.ShowStationService(4); stage = 5;
                captureAt = EditorApplication.timeSinceStartup + 1; return;
            }
            if (stage == 5)
            {
                if (!snapshot.Production.Open || snapshot.Production.Mode != 4) return;
                ScreenCapture.CaptureScreenshot(CaptureName("concourse"));
                captureAt = EditorApplication.timeSinceStartup + .5; stage = 6; return;
            }
            if (stage == 6)
            {
                if (!File.Exists(CaptureName("concourse"))) return;
                if (motion)
                {
                    var otherRoomContact = snapshot.Station.Contacts.FirstOrDefault(contact =>
                        contact.Room != snapshot.Station.CurrentRoom);
                    if (otherRoomContact == null) { Fail("No cross-room contact available for the concourse test."); return; }
                    contactId = otherRoomContact.Id;
                    client.HideStationService(); client.OpenNpc(contactId);
                    stage = 7; return;
                }
                BeginInventory(hud, directory); return;
            }
            if (stage == 7)
            {
                if (snapshot.Dialogue?.NpcId != contactId) return;
                ScreenCapture.CaptureScreenshot(CaptureName("dialogue"));
                captureAt = EditorApplication.timeSinceStartup + .5; stage = 8; return;
            }
            if (stage == 8)
            {
                if (!File.Exists(CaptureName("dialogue"))) return;
                var trade = snapshot.Dialogue?.Choices.FirstOrDefault(choice =>
                    choice.Text.IndexOf("trade", StringComparison.OrdinalIgnoreCase) >= 0);
                if (trade != null) { client.ChooseNpc(trade.Node); stage = 25; return; }
                client.ChooseNpc(230); stage = 9; return;
            }
            if (stage == 25)
            {
                if (snapshot.Dialogue?.VendorOpen != true || !snapshot.Vendor.Any(item => item != null)) return;
                if (!snapshot.Vendor.Any(item => item != null && item.IconId != 0))
                { Fail("Vendor offers lost native icon metadata."); return; }
                captureAt = EditorApplication.timeSinceStartup + 1; stage = 26; return;
            }
            if (stage == 26)
            {
                ScreenCapture.CaptureScreenshot(CaptureName("vendor"));
                Debug.Log("PASS: native vendor item icons advertised for the buy/sell trade panel.");
                captureAt = EditorApplication.timeSinceStartup + .5; stage = 27; return;
            }
            if (stage == 27)
            { client.ChooseNpc(230); stage = 9; return; }
            if (stage == 9)
            {
                if (snapshot.Dialogue != null) return;
                BeginInventory(hud, directory); return;
            }
            if (stage == 10)
            {
                if (!hud.EquipmentOpen || directory.DirectoryRenderCount <= directoryRepaints)
                { Fail("Opening inventory removed the station directory."); return; }
                ScreenCapture.CaptureScreenshot(CaptureName("loadout"));
                captureAt = EditorApplication.timeSinceStartup + .5; stage = 11; return;
            }
            if (!File.Exists(CaptureName("loadout"))) return;
            Debug.Log("PASS: aggregated station concourse preserves native rooms, lists all contacts, exposes manufacturing/analyze/refine services, and keeps directory with inventory open." +
                (motion ? " Cross-room contact dialogue verified." : ""));
            EditorApplication.update -= Tick;
            EditorApplication.Exit(0);
        }

        /// <summary>Open inventory and record that the same station directory continues repainting.</summary>
        private static void BeginInventory(FlightHud hud, SingleRoomStationHud directory)
        {
            if (!hud.EquipmentOpen) hud.ToggleEquipment();
            directoryRepaints = directory.DirectoryRenderCount;
            captureAt = EditorApplication.timeSinceStartup + 1;
            stage = 10;
        }

        /// Returns:
        ///     ProductionHud: The manufacturing UI owned by the disposable client.
        private static ProductionHud ProductionHudFor(SpaceClient client) =>
            (ProductionHud)typeof(SpaceClient).GetField("productionHud", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(client);

        /// Returns:
        ///     bool: Whether the UI is still waiting for a complete page or restoring its original page.
        private static bool RecipeBusy(SpaceClient client) => (bool)typeof(ProductionHud)
            .GetProperty("RecipePagesBusy", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(ProductionHudFor(client));

        /// Returns:
        ///     Dictionary<uint, ProductionFormula[]>: The actual UI cache of fully acknowledged recipe pages.
        private static Dictionary<uint, ProductionFormula[]> RecipePages(ProductionHud production) =>
            (Dictionary<uint, ProductionFormula[]>)typeof(ProductionHud).GetField("recipePages", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(production);

        /// <summary>Exercise an actual UI paging action without bypassing its network worker.</summary>
        private static void RecipeMethod(SpaceClient client, string method, Snapshot snapshot)
        { typeof(ProductionHud).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(ProductionHudFor(client), new object[] { snapshot }); }

        /// <summary>Exercise cached selection or manual page browsing through the actual UI action.</summary>
        private static void RecipeMethod(SpaceClient client, string method, Snapshot snapshot, uint value)
        { typeof(ProductionHud).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(ProductionHudFor(client), new object[] { snapshot, value }); }

        /// <summary>Choose local filter controls while preserving the network-owned recipe book.</summary>
        private static void SetRecipeField(ProductionHud production, string field, object value)
        { typeof(ProductionHud).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(production, value); }

        /// <returns>FlightHud: The actual foreground UI owned by this disposable client.</returns>
        private static FlightHud Hud(SpaceClient client)
        { return typeof(SpaceClient).GetField("hud", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(client) as FlightHud; }

        /// <returns>SingleRoomStationHud: The directory rendered by the actual station UI.</returns>
        private static SingleRoomStationHud DirectoryHud(FlightHud hud)
        { return hud == null ? null : typeof(FlightHud).GetField("stationConcourse", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(hud) as SingleRoomStationHud; }

        /// <summary>Exit the opt-in Editor run with a meaningful failure.</summary>
        private static void Fail(string reason)
        {
            Debug.LogError(reason);
            EditorApplication.update -= Tick;
            EditorApplication.Exit(1);
        }
    }
}
