using System;
using System.Collections.Generic;
using System.Linq;
using FortyTwoAndBeyond.Protocol;
using UnityEngine;

namespace FortyTwoAndBeyond.Client
{
    /// <summary>Search bundled public items and inspect vendor offers without sending search requests.</summary>
    public sealed class ItemBrowserHud
    {
        [Serializable]
        private sealed class Catalog { public Entry[] items = Array.Empty<Entry>(); }
        [Serializable]
        private sealed class Entry
        {
            public uint id, type, icon, price, stack;
            public int level;
            public string name, description, manufacturer, stats, effects;
            [NonSerialized] public ItemView View;
        }

        private readonly HudWindowDrag windowDrag = new HudWindowDrag();
        private readonly SpaceClient client;
        private readonly FlightHud hud;
        private Entry[] catalog, matches;
        private Dictionary<uint, Entry> byId;
        private Entry selected;
        private ItemView offer;
        private uint vendor;
        private bool open, inspecting, selling;
        private string search = "", lastSearch;
        private int category, lastCategory = -1, page;
        private Vector2 listScroll, detailScroll;
        private GUIStyle text, title, meta, searchStyle;
        public bool IsOpen => open;
        public int CatalogCount => catalog?.Length ?? 0;
        private static readonly string[] Categories = { "All", "Weapons", "Devices", "Shields", "Reactors", "Engines", "Ammo", "Other" };

        /// <summary>Bind local search and inspection to the existing HUD and native trade commands.</summary>
        public ItemBrowserHud(SpaceClient client, FlightHud hud) { this.client = client; this.hud = hud; }

        /// <summary>Load cached item identities on Unity's main thread for servers that send cache IDs.</summary>
        /// Returns:
        ///     ItemTemplateInfo[]: Immutable fallback names, categories and icons; server templates take precedence.
        public static ItemTemplateInfo[] LoadTemplateFallbacks()
        {
            TextAsset resource = Resources.Load<TextAsset>("ItemDirectory");
            Entry[] entries = resource == null ? Array.Empty<Entry>() : JsonUtility.FromJson<Catalog>(resource.text).items;
            return entries.Select(entry => new ItemTemplateInfo(entry.id, entry.type, entry.name, "",
                maxStack: entry.stack, iconId: entry.icon)).ToArray();
        }

        /// <summary>Open a local galaxy-wide item search without querying the server.</summary>
        public void Open()
        {
            LoadCatalog(); open = true; inspecting = false; offer = null;
            if (selected == null && catalog.Length > 0) selected = catalog[0];
        }

        /// <summary>Inspect an offer while deferring its purchase or sale to an explicit button.</summary>
        public void Inspect(ItemView item, bool sell)
        {
            LoadCatalog(); offer = item; selling = sell; inspecting = true; open = true;
            vendor = client.CurrentSnapshot?.Dialogue?.NpcId ?? 0;
            byId.TryGetValue(item.Template, out selected); detailScroll = Vector2.zero;
        }

        /// <summary>Release the modal input surface.</summary>
        public void Close() { open = false; }

        /// <summary>Load the immutable bundled directory once on Unity's main thread.</summary>
        private void LoadCatalog()
        {
            if (catalog != null) return;
            TextAsset resource = Resources.Load<TextAsset>("ItemDirectory");
            catalog = resource == null ? Array.Empty<Entry>() : JsonUtility.FromJson<Catalog>(resource.text).items;
            byId = catalog.ToDictionary(entry => entry.id);
            foreach (Entry entry in catalog)
            {
                var template = new ItemTemplateInfo(entry.id, entry.type, entry.name, "", maxStack: entry.stack, iconId: entry.icon);
                entry.View = new ItemView(-1, new ItemState { Template = entry.id, Quality = 1, Structure = 1 },
                    new Dictionary<uint, ItemTemplateInfo> { [entry.id] = template });
            }
        }

        /// Returns:
        ///     long: Authoritative quantity of an item in the pilot's cargo.
        public static long CargoCount(Snapshot snapshot, uint template) =>
            snapshot.Cargo.Where(item => item?.Template == template).Sum(item => (long)item.Quantity);

        /// <summary>Draw the local database or vendor inspector as a single modal overlay.</summary>
        public void Draw(Snapshot snapshot)
        {
            if (!open || snapshot == null) return;
            EnsureStyles();
            float width = Mathf.Min(inspecting ? 690 : 1080, Screen.width / hud.Scale - 32);
            float height = Mathf.Min(680, Screen.height / hud.Scale - 32);
            Rect window = new Rect((Screen.width / hud.Scale - width) / 2, (Screen.height / hud.Scale - height) / 2, width, height);
            window = windowDrag.Handle(window, Screen.width / hud.Scale, Screen.height / hud.Scale);
            Color old = GUI.color; GUI.color = new Color(0, 0, 0, .86f);
            GUI.DrawTexture(new Rect(0, 0, Screen.width / hud.Scale, Screen.height / hud.Scale), Texture2D.whiteTexture); GUI.color = old;
            hud.Panel(window); HudWindowDrag.DrawGrip(window);
            GUI.Label(new Rect(window.x + 36, window.y + 16, width - 140, 28), inspecting ? "ITEM DETAILS" : "GALAXY ITEM DATABASE", title);
            if (GUI.Button(new Rect(window.xMax - 100, window.y + 15, 80, 30), "Close", hud.ButtonStyle)) { Close(); return; }
            float detailX = window.x + 20, detailWidth = width - 40;
            if (!inspecting)
            {
                string next = GUI.TextField(new Rect(window.x + 20, window.y + 58, width - 40, 34), search, 96, searchStyle);
                int nextCategory = GUI.Toolbar(new Rect(window.x + 20, window.y + 100, width - 40, 30), category, Categories, hud.ButtonStyle);
                if (matches == null || next != lastSearch || nextCategory != lastCategory)
                {
                    search = next; category = nextCategory; lastSearch = search; lastCategory = category;
                    matches = catalog.Where(entry => MatchesCategory(entry.type, category) &&
                        (search.Length == 0 || (entry.name + " " + entry.manufacturer)
                            .IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0 || entry.id.ToString() == search)).ToArray();
                    if (selected == null || !matches.Contains(selected)) selected = matches.FirstOrDefault();
                    page = 0; listScroll = Vector2.zero;
                }
                float leftWidth = Mathf.Min(350, width * .35f);
                Rect list = new Rect(window.x + 20, window.y + 144, leftWidth, height - 212);
                GUILayout.BeginArea(list); listScroll = GUILayout.BeginScrollView(listScroll);
                foreach (Entry entry in matches.Skip(page * 40).Take(40))
                    if (hud.ItemActionRow(entry.View, $"L{entry.level}  ·  Own {CargoCount(snapshot, entry.id)} in cargo", leftWidth, true))
                    { selected = entry; detailScroll = Vector2.zero; }
                if (matches.Length == 0) GUILayout.Label("No matching items", text);
                GUILayout.EndScrollView(); GUILayout.EndArea();
                GUI.Label(new Rect(list.x, window.yMax - 56, leftWidth, 22), $"{matches.Length:N0} items  ·  Page {page + 1}/{Mathf.Max(1, (matches.Length + 39) / 40)}", meta);
                if (GUI.Button(new Rect(list.x, window.yMax - 32, 85, 25), "Previous", hud.ButtonStyle)) { page = Math.Max(0, page - 1); listScroll = Vector2.zero; }
                if (GUI.Button(new Rect(list.x + 94, window.yMax - 32, 85, 25), "Next", hud.ButtonStyle)) { page = Math.Min(Math.Max(0, (matches.Length - 1) / 40), page + 1); listScroll = Vector2.zero; }
                detailX = list.xMax + 24; detailWidth = window.xMax - detailX - 20;
            }
            Rect detail = new Rect(detailX, window.y + (inspecting ? 60 : 144), detailWidth, height - (inspecting ? 142 : 166));
            DrawDetails(snapshot, detail);
            if (inspecting) DrawTrade(snapshot, window);
        }

        /// <summary>Display catalog stats, effects, and all server-published owned quantities.</summary>
        private void DrawDetails(Snapshot snapshot, Rect rect)
        {
            ItemView item = offer ?? selected?.View;
            if (item == null) return;
            uint template = item.Template;
            long cargo = CargoCount(snapshot, template);
            long fitted = snapshot.Mounted.Where(value => value?.Template == template).Sum(value => (long)value.Quantity);
            long ammo = snapshot.Ammo.Where(value => value?.Template == template).Sum(value => (long)value.Quantity);
            long vault = snapshot.Vault.Where(value => value?.Template == template).Sum(value => (long)value.Quantity);
            GUILayout.BeginArea(rect); detailScroll = GUILayout.BeginScrollView(detailScroll);
            GUILayout.BeginHorizontal(); GUILayout.Label(hud.ItemIcons.ForItem(item), GUILayout.Width(64), GUILayout.Height(64));
            GUILayout.Label(item.Name, title); GUILayout.EndHorizontal();
            GUILayout.Label($"OWNED  {cargo + fitted + ammo + vault:N0}  ·  Cargo {cargo:N0}  /  Fitted {fitted:N0}  /  Ammo {ammo:N0}  /  Vault {vault:N0}", text);
            if (selected != null)
            {
                GUILayout.Space(12);
                GUILayout.Label($"TECH LEVEL {selected.level}  ·  {selected.manufacturer}", meta);
                GUILayout.Label(selected.description, text);
                GUILayout.Space(12); GUILayout.Label("BASE STATS  ·  100% QUALITY", meta);
                GUILayout.Label(string.IsNullOrEmpty(selected.stats) ? "No base equipment stats" : selected.stats, text);
                if (!string.IsNullOrEmpty(selected.effects))
                { GUILayout.Space(12); GUILayout.Label("EFFECTS", meta); GUILayout.Label(selected.effects, text); }
            }
            if (offer != null)
                GUILayout.Label($"Offer quality {offer.Quality * 100:0}%  ·  Structure {offer.Structure * 100:0}%", text);
            GUILayout.EndScrollView(); GUILayout.EndArea();
        }

        /// <summary>Submit only an explicit, still-current server offer; inspection itself never trades.</summary>
        private void DrawTrade(Snapshot snapshot, Rect window)
        {
            ItemView current = (selling ? snapshot.Cargo : snapshot.Vendor).FirstOrDefault(item =>
                item != null && item.Slot == offer.Slot && item.Template == offer.Template);
            bool available = snapshot.Dialogue?.VendorOpen == true && snapshot.Dialogue.NpcId == vendor && current?.Price > 0;
            GUI.Label(new Rect(window.x + 20, window.yMax - 63, window.width - 40, 24),
                available ? $"{current.Price:N0} credits per item  ·  Balance {snapshot.Credits:N0}" : "No current vendor quote", text);
            bool previous = GUI.enabled;
            GUI.enabled = previous && client.Ready && available && (selling || current.Price <= snapshot.Credits);
            if (GUI.Button(new Rect(window.xMax - 180, window.yMax - 36, 160, 28), selling ? "SELL 1" : "BUY 1", hud.ButtonStyle))
            { if (selling) client.SellVendor(current.Slot); else client.BuyVendor(current.Slot); }
            GUI.enabled = previous;
        }

        /// Returns:
        ///     bool: Whether a catalog item belongs to the selected visible category.
        private static bool MatchesCategory(uint type, int selection) => selection == 0 ||
            selection == 1 && type >= 14 && type <= 16 || selection == 2 && type == 11 ||
            selection == 3 && type == 2 || selection == 4 && type == 7 || selection == 5 && type == 6 ||
            selection == 6 && type == 10 || selection == 7 && type != 2 && type != 6 && type != 7 && type != 10 && type != 11 && (type < 14 || type > 16);

        /// <summary>Create readable styles once from the shared HUD font.</summary>
        private void EnsureStyles()
        {
            if (text != null) return;
            text = new GUIStyle(hud.BodyStyle) { fontSize = 15, wordWrap = true, richText = false };
            title = new GUIStyle(text) { fontSize = 22, fontStyle = FontStyle.Bold };
            meta = new GUIStyle(text) { fontSize = 12, normal = { textColor = new Color(.5f,.77f,.88f) } };
            searchStyle = new GUIStyle(GUI.skin.textField) { font = text.font, fontSize = 17, padding = new RectOffset(10,10,6,6) };
        }
    }
}
