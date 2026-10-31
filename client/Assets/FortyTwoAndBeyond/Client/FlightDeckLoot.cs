using System;
using System.Linq;
using FortyTwoAndBeyond.Protocol;
using UnityEngine;
using UnityEngine.UIElements;

namespace FortyTwoAndBeyond.Client
{
    public sealed partial class FlightHud
    {
        private sealed partial class DeckView
        {
            private VisualElement lootPanel, lootHover;
            private ScrollView lootRows;
            private Label lootTitle, lootRange, lootHold, lootStatus, lootHoverName, lootHoverStats;
            private Button lootAll;
            private uint shownLootId;
            private string lootKey = "";

            /// <summary>Create a movable wreck inventory with individual native pickup controls.</summary>
            private void CreateLootPanel()
            {
                lootPanel = Box(root, "panel loot-window"); lootPanel.name = "wreck-inventory";
                lootPanel.style.position = UnityEngine.UIElements.Position.Absolute;
                lootPanel.style.width = 390; lootPanel.style.height = 300;
                lootPanel.style.display = DisplayStyle.None;
                var header = Box(lootPanel, "row spread");
                lootTitle = Text(header, "WRECK INVENTORY", "eyebrow");
                lootTitle.style.whiteSpace = WhiteSpace.NoWrap;
                lootTitle.style.textOverflow = TextOverflow.Ellipsis;
                lootTitle.style.overflow = Overflow.Hidden;
                lootTitle.style.flexGrow = 1;
                MakeMovable(lootPanel, lootTitle, "wreck-inventory");
                ActionButton(header, "Close  ×", () =>
                {
                    if (shownLootId != 0) Client.CloseLoot(shownLootId);
                }).name = "close-wreck-inventory";
                lootRange = Text(lootPanel, "", "subtle");
                lootHold = Text(lootPanel, "", "subtle");
                lootRows = Scroll(lootPanel); lootRows.name = "wreck-item-list";
                lootRows.style.flexGrow = 1; lootRows.style.minHeight = 0;
                var actions = Box(lootPanel, "row spread");
                lootAll = ActionButton(actions, "Loot all", () =>
                {
                    if (Client.LootAllActive) Client.StopCollectAllLoot();
                    else if (shownLootId != 0) Client.CollectAllLoot(shownLootId);
                });
                lootAll.name = "wreck-loot-all";
                lootStatus = Text(actions, "", "subtle"); lootStatus.style.flexGrow = 1;
                lootHover = Box(root, "panel item-hover"); lootHover.name = "wreck-item-hover";
                lootHover.style.position = UnityEngine.UIElements.Position.Absolute;
                lootHover.style.width = 300; lootHover.style.display = DisplayStyle.None;
                lootHover.pickingMode = PickingMode.Ignore;
                lootHoverName = Text(lootHover, "", "title");
                lootHoverStats = Text(lootHover, "", "inspector");
                lootHoverStats.style.whiteSpace = WhiteSpace.Normal;
                lootHoverName.pickingMode = lootHoverStats.pickingMode = PickingMode.Ignore;
            }

            /// <summary>Show exactly the opened corpse's authoritative stacks and cast state.</summary>
            private void RefreshLoot(Snapshot snapshot)
            {
                uint id = snapshot?.OpenLootId ?? 0;
                bool visible = id != 0 && snapshot.Station == null && !hud.equipmentOpen;
                lootPanel.style.display = visible ? DisplayStyle.Flex : DisplayStyle.None;
                if (!visible)
                {
                    lootHover.style.display = DisplayStyle.None;
                    if (id == 0) shownLootId = 0;
                    return;
                }
                if (shownLootId != id)
                {
                    shownLootId = id; lootKey = "";
                    if (!movedPanels.Contains(lootPanel))
                    {
                        lootPanel.style.left = Mathf.Max(8, (root.resolvedStyle.width - 390) * .5f);
                        lootPanel.style.top = Mathf.Clamp(root.resolvedStyle.height * .38f, 170,
                            Mathf.Max(170, root.resolvedStyle.height - 314));
                    }
                    lootPanel.BringToFront();
                }
                MapObject wreck = snapshot.Objects.FirstOrDefault(item => item.Id == id && item.Kind == 25);
                CorpseLoot contents = snapshot.Loot.FirstOrDefault(item => item.Id == id);
                ItemView[] items = contents?.Items.Where(item => item != null).ToArray() ?? Array.Empty<ItemView>();
                bool tractoring = snapshot.LootActivity?.CorpseId == id;
                bool sweeping = Client.LootAllActive;
                bool canClaim = Client.Ready && !tractoring && !sweeping;
                lootTitle.text = wreck?.Name ?? "WRECK INVENTORY";
                lootTitle.tooltip = lootTitle.text;
                lootRange.text = wreck == null ? "SERVER WRECK WINDOW OPEN" :
                    $"RANGE {wreck.Position.Distance(snapshot.Position) / 1000:0.00} km  ·  SELECT AN ITEM TO TRACTOR";
                int used = snapshot.Cargo.Count(item => item != null);
                lootHold.text = $"CARGO {used}/{snapshot.CargoCapacity} SLOTS  ·  STACKING CHECKED BY SERVER";
                lootStatus.text = tractoring ? $"TRACTOR {snapshot.LootProgress * 100:0}%  ·  {snapshot.LootRemainingSeconds:0.0}s" :
                    sweeping ? Client.LootAllStatus : items.Length == 0 ? "EMPTY" : "ONE CAST AT A TIME";
                lootAll.text = sweeping ? "Stop queue" : "Loot all";
                lootAll.SetEnabled(Client.Ready && (sweeping || items.Length > 0 && !tractoring));
                string key = id + ":" + canClaim + ":" + string.Join("|", items.Select(item =>
                    item.Slot + "/" + item.Template + "/" + item.Quantity));
                if (key == lootKey) return;
                lootKey = key; lootRows.Clear();
                if (contents == null) Text(lootRows, "Reading wreck contents…", "subtle");
                else if (items.Length == 0) Text(lootRows, "No items remain in this wreck.", "subtle");
                foreach (ItemView item in items)
                {
                    int slot = item.Slot;
                    var row = new Button(() => TryClaimLoot(id, slot)) { name = "wreck-item-" + slot };
                    row.AddToClassList("loot-item-row"); lootRows.Add(row);
                    row.EnableInClassList("unavailable", !canClaim);
                    var icon = new Image { image = hud.icons.ForItem(item), pickingMode = PickingMode.Ignore };
                    icon.AddToClassList("loot-item-icon"); row.Add(icon);
                    var labels = Box(row, "loot-item-labels");
                    var name = Text(labels, item.Name, "item-name");
                    name.style.whiteSpace = WhiteSpace.NoWrap; name.style.overflow = Overflow.Hidden;
                    name.style.textOverflow = TextOverflow.Ellipsis;
                    Text(labels, $"×{item.Quantity}  ·  quality {item.Quality * 100:0}%", "subtle");
                    Text(row, canClaim ? "CLAIM" : "WAIT", "loot-claim");
                    row.tooltip = item.Name + " ×" + item.Quantity + "\n" + item.Info;
                    row.RegisterCallback<PointerEnterEvent>(evt => ShowLootHover(item, evt.position));
                    row.RegisterCallback<PointerMoveEvent>(evt => ShowLootHover(item, evt.position));
                    row.RegisterCallback<PointerLeaveEvent>(_ => lootHover.style.display = DisplayStyle.None);
                }
            }

            /// <summary>Request one selected stack while leaving server admission and inventory writes authoritative.</summary>
            private void TryClaimLoot(uint corpseId, int slot)
            {
                lootHover.style.display = DisplayStyle.None;
                Snapshot current = Client.CurrentSnapshot;
                if (!Client.Ready || current?.OpenLootId != corpseId || Client.LootAllActive ||
                    current.LootActivity?.CorpseId == corpseId ||
                    !current.Loot.Any(wreck => wreck.Id == corpseId && slot >= 0 && slot < wreck.Items.Length &&
                        wreck.Items[slot] != null)) return;
                Client.TakeLoot(corpseId, slot);
            }

            /// <summary>Show complete server item metadata on a wreck-row hover.</summary>
            private void ShowLootHover(ItemView item, Vector2 pointer)
            {
                if (lootPanel.resolvedStyle.display == DisplayStyle.None) return;
                lootHoverName.text = item.Name;
                lootHoverStats.text = $"×{item.Quantity}  ·  quality {item.Quality * 100:0}%  ·  structure {item.Structure * 100:0}%" +
                    (item.Range > 0 ? $"\nRange {item.Range:N0} m" : "") +
                    (item.Price > 0 ? $"\nValue {item.Price:N0} credits" : "") +
                    (string.IsNullOrWhiteSpace(item.BuilderName) ? "" : "\nBuilt by " + item.BuilderName) +
                    (string.IsNullOrWhiteSpace(item.Info) ? "" : "\n\n" + item.Info);
                Vector2 local = root.WorldToLocal(pointer);
                lootHover.style.left = Mathf.Clamp(local.x + 15, 4, Mathf.Max(4, root.resolvedStyle.width - 304));
                lootHover.style.top = Mathf.Clamp(local.y + 15, 4, Mathf.Max(4, root.resolvedStyle.height - 250));
                lootHover.style.display = DisplayStyle.Flex; lootHover.BringToFront();
            }

        }
    }
}
