using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using FortyTwoAndBeyond.Protocol;
using UnityEngine;

namespace FortyTwoAndBeyond.Client
{
    /// <summary>Display server-advertised class abilities and activate them in space.</summary>
    public sealed class AbilityHud
    {
        private sealed class AbilityDefinition
        {
            public readonly int id, skill;
            public readonly string name, description, kind;
            public readonly int rank, range;
            public readonly float energy, charge, cooldown;

            /// <summary>Parse one generated display row from the server catalog.</summary>
            public AbilityDefinition(string[] fields)
            {
                id = int.Parse(fields[0], CultureInfo.InvariantCulture);
                skill = int.Parse(fields[1], CultureInfo.InvariantCulture);
                name = fields[2]; description = fields[3]; kind = fields[4];
                rank = int.Parse(fields[5], CultureInfo.InvariantCulture);
                energy = float.Parse(fields[6], CultureInfo.InvariantCulture);
                charge = float.Parse(fields[7], CultureInfo.InvariantCulture);
                cooldown = float.Parse(fields[8], CultureInfo.InvariantCulture);
                range = int.Parse(fields[9], CultureInfo.InvariantCulture);
            }
        }

        private readonly SpaceClient client;
        private readonly FlightHud flightHud;
        private readonly AbilityDefinition[] catalog;
        private readonly Dictionary<uint, string> skillNames = new Dictionary<uint, string>();
        private readonly AbilityIcons icons = new AbilityIcons();
        private readonly HashSet<int> expandedSkills = new HashSet<int>();
        private readonly int[] slots = { -1, -1, -1, -1, -1, -1 };
        private GUIStyle quickbarName, abilityName, abilityMeta;
        private Vector2 scroll;
        private bool open;
        private bool trainingTab;
        private bool equipmentTab;
        private int assigningSlot = -1;
        private int variantSlot = -1;
        private int dragBinding = int.MinValue;
        private bool draggingAbility, dragCanActivate;
        private Vector2 dragStart;
        private uint slotOwner;
        private uint pendingSupportTarget;
        private float pendingSupportAt;
        public bool IsOpen => open;
        private float CanvasWidth => Screen.width / flightHud.Scale;
        private float CanvasHeight => Screen.height / flightHud.Scale;

        /// <summary>Load public display text; the live server still decides availability.</summary>
        public AbilityHud(SpaceClient client, FlightHud flightHud)
        {
            this.client = client; this.flightHud = flightHud;
            TextAsset asset = Resources.Load<TextAsset>("AbilityCatalog");
            var entries = new List<AbilityDefinition>();
            if (asset != null)
                foreach (string row in asset.text.Split('\n'))
                {
                    string[] fields = row.TrimEnd('\r').Split('\t');
                    if (fields.Length == 10) entries.Add(new AbilityDefinition(fields));
                }
            catalog = entries.ToArray();
            TextAsset skills = Resources.Load<TextAsset>("SkillCatalog");
            if (skills != null)
                foreach (string row in skills.text.Split('\n'))
                {
                    string[] fields = row.TrimEnd('\r').Split('\t');
                    if (fields.Length == 2 && uint.TryParse(fields[0], out uint id))
                        skillNames[id] = fields[1];
                }
        }

        /// <returns>bool: Whether pointer input belongs to the ability button or open panel.</returns>
        public bool ContainsPointer()
        {
            Vector2 point = new Vector2(Input.mousePosition.x / flightHud.Scale,
                (Screen.height - Input.mousePosition.y) / flightHud.Scale);
            return ButtonRect().Contains(point) ||
                (open && PanelRect().Contains(point)) ||
                client.CurrentSnapshot?.Station == null &&
                (QuickbarRect().Contains(point) || VariantPickerRect(client.CurrentSnapshot).Contains(point));
        }

        /// <returns>Rect: Bounds of the ability panel's persistent toolbar button.</returns>
        private Rect ButtonRect()
        {
            return client.CurrentSnapshot?.Station != null ? new Rect(CanvasWidth - 570, 37, 110, 34) :
                new Rect(CanvasWidth - 550, 25, 110, 28);
        }

        /// <returns>Rect: Bounds of the scrollable ability panel.</returns>
        private Rect PanelRect()
        {
            float height = Mathf.Min(600, CanvasHeight - 220);
            return new Rect((CanvasWidth - 520) * .5f, 82, 520, height);
        }

        /// <returns>Rect: Bounds of the six flight ability slots.</returns>
        private Rect QuickbarRect()
        { return flightHud.RetainedVisible && flightHud.RetainedQuickbarRect.width > 0 ?
            flightHud.RetainedQuickbarRect : new Rect((CanvasWidth - 720) * .5f, CanvasHeight - 117, 720, 68); }

        /// <returns>Vector2: Pointer in the HUD's scaled, top-left canvas coordinates.</returns>
        private Vector2 CanvasPointer()
        { return new Vector2(Input.mousePosition.x / flightHud.Scale,
            (Screen.height - Input.mousePosition.y) / flightHud.Scale); }

        /// <returns>int: Quick bar slot under the pointer, or minus one.</returns>
        private int QuickbarSlotAt(Vector2 point)
        {
            Rect bar = QuickbarRect();
            for (int i = 0; i < slots.Length; i++)
                if (new Rect(bar.x + 9 + i * 118, bar.y + 8, 110, 52).Contains(point)) return i;
            return -1;
        }

        /// <returns>Rect: Bounds of the active quickbar variant picker, if one is open.</returns>
        private Rect VariantPickerRect(Snapshot snapshot)
        {
            if (variantSlot < 0 || variantSlot >= slots.Length || snapshot == null)
                return Rect.zero;
            AbilityDefinition bound = catalog.FirstOrDefault(item => item.id == slots[variantSlot]);
            if (bound == null) return Rect.zero;
            int count = catalog.Count(item => item.skill == bound.skill &&
                snapshot.AvailableAbilities.Contains((uint)item.id));
            if (count == 0) return Rect.zero;
            Rect bar = QuickbarRect();
            float height = 47 + count * 36;
            float x = Mathf.Clamp(bar.x + variantSlot * 118, 8, CanvasWidth - 310);
            return new Rect(x, bar.y - height - 5, 302, height);
        }

        /// <summary>Save one per-character ability or equipped device assignment.</summary>
        private void BindSlot(int slot, int binding)
        {
            slots[slot] = binding;
            PlayerPrefs.SetInt($"42andBeyond.ability.{slotOwner}.{slot}", binding);
            PlayerPrefs.Save(); assigningSlot = -1; variantSlot = -1;
        }

        /// <returns>int: A device template encoded apart from existing ability IDs and empty slots.</returns>
        private static int DeviceBinding(uint template)
        { return -(int)template - 2; }

        /// <returns>ItemView: The currently mounted and activatable device for a saved binding.</returns>
        private static ItemView BoundDevice(Snapshot snapshot, int binding)
        {
            if (snapshot == null || binding >= -1 || binding == int.MinValue) return null;
            uint template = (uint)(-binding - 2);
            for (int slot = 0; slot < snapshot.Mounted.Length; slot++)
            {
                ItemView item = snapshot.Mounted[slot];
                if (item?.Template == template && DeviceEffects.MountedSlot(
                    item.ActivationLabel, item.Type, slot, snapshot.DeviceMounts)) return item;
            }
            return null;
        }

        /// <summary>Activate a saved ability or the matching currently mounted device.</summary>
        private void ActivateBinding(int binding, Snapshot snapshot)
        {
            if (binding < -1)
            {
                ItemView device = BoundDevice(snapshot, binding);
                if (device != null) client.ActivateDevice(device.Slot);
                return;
            }
            if (binding < 0 || !snapshot.AvailableAbilities.Contains((uint)binding)) return;
            if (!AbilityTargets.Directed((uint)binding) && SupportTargetPending(snapshot) ||
                binding == 125 && !CrewRecipientSelected(snapshot)) return;
            ActivateForSelectedTarget((uint)binding, snapshot);
        }

        /// <summary>Finish a row click or drag using the pointer's screen position.</summary>
        private void HandleAbilityDrag(Snapshot snapshot)
        {
            if (dragBinding == int.MinValue) return;
            Event current = Event.current;
            if (current.type == EventType.KeyDown && current.keyCode == KeyCode.Escape)
            { dragBinding = int.MinValue; draggingAbility = false; current.Use(); return; }
            if (current.type == EventType.MouseDrag)
            {
                if (Vector2.Distance(CanvasPointer(), dragStart) > 8) draggingAbility = true;
                current.Use(); return;
            }
            if (current.type != EventType.MouseUp || current.button != 0) return;
            int binding = dragBinding;
            if (draggingAbility)
            {
                int slot = QuickbarSlotAt(CanvasPointer());
                if (slot >= 0) BindSlot(slot, binding);
            }
            else if (assigningSlot >= 0) BindSlot(assigningSlot, binding);
            else if (dragCanActivate) ActivateBinding(binding, snapshot);
            dragBinding = int.MinValue; draggingAbility = false; current.Use();
        }

        /// <returns>string: Target and range guidance for a Hacking row.</returns>
        private static string HackingTargetHint(Snapshot snapshot)
        {
            ShipActor target = snapshot.Mobs.FirstOrDefault(actor =>
                actor.Id == snapshot.SelectedTargetId && actor.HasPosition);
            if (target == null) return "SELECT A MECHANICAL MOB";
            uint rank = snapshot.Skills.FirstOrDefault(skill => skill.Id == 26)?.Rank ?? 0;
            float range = 1000 + 250 * Mathf.Max(0, (int)rank - 1);
            float distance = snapshot.Position.Distance(target.Position);
            return distance > range ? $"OUT OF RANGE  ·  {distance:0} / {range:0} M" :
                $"TARGET {target.Name.ToUpperInvariant()}  ·  {distance:0} M";
        }

        /// <summary>Build readable text styles from the shared packaged HUD font.</summary>
        private void EnsureStyles()
        {
            if (quickbarName != null) return;
            quickbarName = new GUIStyle(flightHud.SectionStyle) { fontSize = 12, wordWrap = true };
            abilityName = new GUIStyle(flightHud.BodyStyle) { fontSize = 16, wordWrap = true };
            abilityMeta = new GUIStyle(flightHud.SmallStyle) { fontSize = 14, wordWrap = true };
        }

        /// <summary>Load a separate ability layout for each server character identity.</summary>
        private void LoadSlots(uint playerId)
        {
            if (slotOwner == playerId) return;
            slotOwner = playerId; assigningSlot = -1; pendingSupportTarget = 0;
            variantSlot = -1; expandedSkills.Clear();
            trainingTab = equipmentTab = false;
            dragBinding = int.MinValue; draggingAbility = false;
            for (int i = 0; i < slots.Length; i++)
                slots[i] = PlayerPrefs.GetInt($"42andBeyond.ability.{playerId}.{i}", -1);
        }

        /// <summary>Activate a bound slot while leaving target and resource checks to the server.</summary>
        public void ActivateSlot(int index, Snapshot snapshot)
        {
            if (snapshot == null || index < 0 || index >= slots.Length) return;
            LoadSlots(snapshot.PlayerId);
            ActivateBinding(slots[index], snapshot);
        }

        /// <summary>Preserve the intended support recipient across target selection.</summary>
        private void ActivateForSelectedTarget(uint id, Snapshot snapshot)
        {
            if (!AbilityTargets.Directed(id)) { client.ActivateAbility(id); return; }
            uint selected = snapshot.SelectedTargetId;
            ShipActor peer = snapshot.Players.FirstOrDefault(actor => actor.Id == selected && actor.HasPosition);
            uint recipient = pendingSupportTarget != 0 && Time.unscaledTime - pendingSupportAt <= 3 ?
                pendingSupportTarget : peer?.Id ?? snapshot.PlayerId;
            if (AbilityTargets.CrewOnly(id) && recipient != snapshot.PlayerId &&
                !snapshot.Group.Members.Any(member => member.Id == recipient))
                recipient = snapshot.PlayerId;
            pendingSupportTarget = 0;
            client.ActivateAbilityFor(id, recipient);
        }

        /// <returns>bool: Whether a support selection still awaits server target acknowledgment.</returns>
        private bool SupportTargetPending(Snapshot snapshot)
        {
            if (pendingSupportTarget == 0) return false;
            if (snapshot.SelectedTargetId == pendingSupportTarget || Time.unscaledTime - pendingSupportAt > 3)
            { pendingSupportTarget = 0; return false; }
            return true;
        }

        /// <returns>ShipActor: The visible server-selected crew member, or null.</returns>
        private static ShipActor SelectedCrew(Snapshot snapshot)
        { return snapshot.Players.FirstOrDefault(actor => actor.Id == snapshot.SelectedTargetId &&
            actor.HasPosition && snapshot.Group.Members.Any(member => member.Id == actor.Id)); }

        /// <returns>bool: Whether the selected or newly requested recipient is visible crew.</returns>
        private bool CrewRecipientSelected(Snapshot snapshot)
        {
            if (SelectedCrew(snapshot) != null) return true;
            return pendingSupportTarget != 0 && Time.unscaledTime - pendingSupportAt <= 3 &&
                snapshot.Players.Any(actor => actor.Id == pendingSupportTarget && actor.HasPosition) &&
                snapshot.Group.Members.Any(member => member.Id == pendingSupportTarget);
        }

        /// <summary>Choose a self or visible crew recipient using native target selection.</summary>
        private void SupportTargetBar(Snapshot snapshot, Rect panel)
        {
            ShipActor selectedCrew = SelectedCrew(snapshot);
            bool peerSupport = snapshot.AvailableAbilities.Contains(89u) ||
                snapshot.AvailableAbilities.Contains(95u) ||
                snapshot.AvailableAbilities.Contains(31u) ||
                snapshot.AvailableAbilities.Contains(32u) ||
                snapshot.AvailableAbilities.Contains(122u) ||
                snapshot.AvailableAbilities.Contains(130u) ||
                snapshot.AvailableAbilities.Contains(74u);
            ShipActor selectedPeer = peerSupport ?
                snapshot.Players.FirstOrDefault(actor => actor.Id == snapshot.SelectedTargetId &&
                    actor.HasPosition) : selectedCrew;
            uint recipient = selectedPeer?.Id ?? snapshot.PlayerId;
            bool pending = SupportTargetPending(snapshot);
            bool shielded = snapshot.ObjectEffects.Any(effect =>
                effect.OwnerId == recipient && effect.Descriptor == 216);
            string target = selectedPeer?.Name ?? "YOUR SHIP";
            GUI.Label(new Rect(panel.x + 18, panel.y + 205, panel.width - 36, 23),
                pending ? "SUPPORT TARGET  ·  WAITING FOR SERVER" :
                "SUPPORT TARGET  ·  " + target.ToUpperInvariant() +
                (shielded ? "  ·  ENV SHIELD ACTIVE" : ""), flightHud.SmallStyle);
            if (GUI.Button(new Rect(panel.x + 18, panel.y + 229, 86, 29), "SELF", flightHud.ButtonStyle))
            { pendingSupportTarget = snapshot.PlayerId; pendingSupportAt = Time.unscaledTime; client.SelectAbilitySelf(); }
            ShipActor[] crew = snapshot.Players.Where(actor => actor.HasPosition &&
                (peerSupport ||
                 snapshot.Group.Members.Any(member => member.Id == actor.Id)))
                .OrderByDescending(actor => snapshot.Group.Members.Any(member => member.Id == actor.Id))
                .Take(4).ToArray();
            for (int i = 0; i < crew.Length; i++)
            {
                ShipActor actor = crew[i];
                string label = actor.Name.Length > 9 ? actor.Name.Substring(0, 8) + "…" : actor.Name;
                if (GUI.Button(new Rect(panel.x + 109 + i * 96, panel.y + 229, 91, 29),
                    label, flightHud.ButtonStyle))
                { pendingSupportTarget = actor.Id; pendingSupportAt = Time.unscaledTime; client.SelectContact(actor.Id); }
            }
        }

        internal bool VariantsOpen => variantSlot >= 0;

        /// <summary>Open or close the skill library from the retained toolbar.</summary>
        internal void Toggle() { open = !open; }

        /// Returns:
        ///     ValueTuple: The bound action's display name, key, and optional skill icon.
        internal (string Name, string Key, Texture2D Icon) QuickSlotDisplay(int index, Snapshot snapshot)
        {
            LoadSlots(snapshot.PlayerId);
            int id = slots[index];
            AbilityDefinition ability = id >= 0 ? catalog.FirstOrDefault(item => item.id == id) : null;
            ItemView device = BoundDevice(snapshot, id);
            return (ability?.name ?? device?.ActivationLabel ?? "+ Assign",
                client.Bindings.Display((ControlAction)((int)ControlAction.AbilityOne + index)),
                ability != null ? icons.ForSkill(ability.skill) : null);
        }

        /// <summary>Use the existing quickbar activation, assignment, and rank-selection behavior.</summary>
        internal void PressQuickSlot(int index, bool rightClick, Snapshot snapshot)
        {
            LoadSlots(snapshot.PlayerId);
            int id = slots[index];
            AbilityDefinition ability = id >= 0 ? catalog.FirstOrDefault(item => item.id == id) : null;
            ItemView device = BoundDevice(snapshot, id);
            if (rightClick)
            { if (ability != null) variantSlot = variantSlot == index ? -1 : index; return; }
            if (ability == null && device == null)
            { open = true; equipmentTab = id < -1; trainingTab = false; assigningSlot = index; }
            else ActivateSlot(index, snapshot);
        }

        /// <summary>Show bound abilities and their configured keys during flight.</summary>
        private void Quickbar(Snapshot snapshot)
        {
            if (flightHud.RetainedVisible && !open && variantSlot < 0) return;
            EnsureStyles();
            Rect bar = QuickbarRect(); flightHud.Panel(bar);
            Event current = Event.current;
            for (int i = 0; i < slots.Length; i++)
            {
                int id = slots[i];
                AbilityDefinition ability = id >= 0 ? catalog.FirstOrDefault(item => item.id == id) : null;
                ItemView device = BoundDevice(snapshot, id);
                string key = client.Bindings.Display((ControlAction)((int)ControlAction.AbilityOne + i));
                string name = ability?.name ?? device?.ActivationLabel ??
                    (id < -1 ? "Equip device" : "Assign action");
                Rect slot = new Rect(bar.x + 9 + i * 118, bar.y + 8, 110, 52);
                if (ability != null && current.type == EventType.MouseDown &&
                    current.button == 1 && slot.Contains(current.mousePosition))
                { variantSlot = variantSlot == i ? -1 : i; current.Use(); }
                if (GUI.Button(slot, new GUIContent("", name), flightHud.ButtonStyle))
                {
                    if (ability == null && device == null)
                    { open = true; equipmentTab = id < -1; trainingTab = false; assigningSlot = i; }
                    else ActivateSlot(i, snapshot);
                }
                if (ability != null)
                    GUI.DrawTexture(new Rect(slot.x + 5, slot.y + 17, 30, 30),
                        icons.ForSkill(ability.skill));
                GUI.Label(new Rect(slot.x + 7, slot.y + 2, 96, 18), key +
                    (ability != null ? "  ·  RMB" : ""), flightHud.SmallStyle);
                GUI.Label(new Rect(slot.x + (ability == null ? 7 : 38), slot.y + 19,
                    ability == null ? 96 : 69, 32), name,
                    ability == null && device == null ? flightHud.SmallStyle : quickbarName);
                if (draggingAbility && QuickbarSlotAt(CanvasPointer()) == i)
                    GUI.Box(new Rect(slot.x + 1, slot.y + 1, slot.width - 2, slot.height - 2),
                        GUIContent.none, flightHud.SectionStyle);
            }
            if (draggingAbility)
            {
                AbilityDefinition dragged = dragBinding >= 0 ?
                    catalog.FirstOrDefault(item => item.id == dragBinding) : null;
                ItemView device = BoundDevice(snapshot, dragBinding);
                Vector2 point = CanvasPointer();
                GUI.Label(new Rect(point.x + 14, point.y - 15, 195, 34),
                    dragged?.name ?? device?.ActivationLabel ?? "Action", flightHud.BodyStyle);
            }
            DrawVariantPicker(snapshot, bar);
        }

        /// <summary>Change one assigned button to another server-available rank or variant.</summary>
        private void DrawVariantPicker(Snapshot snapshot, Rect bar)
        {
            if (variantSlot < 0 || variantSlot >= slots.Length) return;
            AbilityDefinition bound = catalog.FirstOrDefault(item => item.id == slots[variantSlot]);
            if (bound == null) { variantSlot = -1; return; }
            AbilityDefinition[] variants = catalog.Where(item => item.skill == bound.skill &&
                snapshot.AvailableAbilities.Contains((uint)item.id))
                .OrderBy(item => item.rank).ThenBy(item => item.name).ToArray();
            if (variants.Length == 0) { variantSlot = -1; return; }
            Rect menu = VariantPickerRect(snapshot);
            flightHud.Panel(menu);
            string family = skillNames.TryGetValue((uint)bound.skill, out string known) ? known :
                bound.kind.Replace('_', ' ');
            GUI.Label(new Rect(menu.x + 12, menu.y + 7, 274, 25),
                family.ToUpperInvariant() + "  ·  CHOOSE VARIANT", flightHud.SmallStyle);
            for (int i = 0; i < variants.Length; i++)
            {
                AbilityDefinition option = variants[i];
                Rect row = new Rect(menu.x + 8, menu.y + 36 + i * 36, menu.width - 16, 32);
                if (GUI.Button(row, new GUIContent("", option.description), flightHud.ButtonStyle))
                    BindSlot(variantSlot, option.id);
                GUI.DrawTexture(new Rect(row.x + 4, row.y + 2, 27, 27),
                    icons.ForSkill(option.skill));
                GUI.Label(new Rect(row.x + 36, row.y + 5, row.width - 43, 23),
                    $"R{option.rank}  {option.name}" +
                    (option.id == bound.id ? "  ✓" : ""), flightHud.SmallStyle);
            }
            Event current = Event.current;
            if (current.type == EventType.MouseDown && current.button == 0 &&
                !menu.Contains(current.mousePosition) && !bar.Contains(current.mousePosition))
                variantSlot = -1;
        }

        /// <summary>Draw server-owned combat, trade, and explore progress beside the skill list.</summary>
        private void DrawExperience(Rect panel, ExperienceState progress)
        {
            Rect area = new Rect(panel.x + 18, panel.y + 112, panel.width - 36, 86);
            flightHud.Panel(area);
            if (progress == null)
            {
                GUI.Label(new Rect(area.x + 12, area.y + 12, area.width - 24, 23),
                    "Waiting for pilot progression…", flightHud.SmallStyle);
                return;
            }
            GUI.Label(new Rect(area.x + 12, area.y + 5, area.width - 24, 23),
                $"PILOT PROGRESSION  ·  {progress.AvailableSkillPoints} SKILL POINTS", flightHud.BodyStyle);
            DrawTrack(area, 0, "COMBAT", progress.CombatLevel, progress.CombatFraction,
                new Color(.94f, .38f, .28f));
            DrawTrack(area, 1, "TRADE", progress.TradeLevel, progress.TradeFraction,
                new Color(.98f, .75f, .34f));
            DrawTrack(area, 2, "EXPLORE", progress.ExploreLevel, progress.ExploreFraction,
                new Color(.37f, .82f, .95f));
        }

        /// <summary>Draw one authoritative level and fraction of its next-level bar.</summary>
        private void DrawTrack(Rect area, int column, string name, uint level, float fraction, Color fill)
        {
            float width = (area.width - 32) / 3f;
            float x = area.x + 12 + column * (width + 4);
            GUI.Label(new Rect(x, area.y + 32, width, 21),
                $"{name}  L{level}  {fraction * 100:0}%", flightHud.SmallStyle);
            Rect track = new Rect(x, area.y + 61, width - 8, 7);
            Color previous = GUI.color;
            GUI.color = new Color(.12f, .21f, .28f); GUI.DrawTexture(track, Texture2D.whiteTexture);
            GUI.color = fill;
            GUI.DrawTexture(new Rect(track.x, track.y, track.width * Mathf.Clamp01(fraction), track.height),
                Texture2D.whiteTexture);
            GUI.color = previous;
        }

        /// <summary>Show native trained ranks and one-rank-at-a-time purchase controls.</summary>
        private void DrawTraining(Snapshot snapshot, Rect panel, float top = 300)
        {
            GUILayout.BeginArea(new Rect(panel.x + 18, panel.y + top,
                panel.width - 36, panel.height - top - 49));
            scroll = GUILayout.BeginScrollView(scroll);
            if (snapshot.Skills.Length == 0)
                GUILayout.Label("Waiting for server skill ranks…", flightHud.SmallStyle);
            foreach (SkillRank skill in snapshot.Skills.OrderBy(entry =>
                skillNames.TryGetValue(entry.Id, out string name) ? name : entry.Id.ToString()))
            {
                string name = skillNames.TryGetValue(skill.Id, out string known) ? known :
                    "Skill " + skill.Id;
                GUILayout.BeginHorizontal(GUILayout.Height(52));
                Rect badge = GUILayoutUtility.GetRect(38, 38, GUILayout.Width(38),
                    GUILayout.Height(38));
                GUI.DrawTexture(badge, icons.ForSkill((int)skill.Id));
                GUILayout.BeginVertical(GUILayout.Width(272));
                GUILayout.Label(name, flightHud.BodyStyle);
                GUILayout.Label($"RANK {skill.Rank}/{skill.Maximum}  ·  " +
                    (skill.CanTrain ? $"NEXT {skill.Cost} POINTS" : skill.GateReason()),
                    flightHud.SmallStyle);
                GUILayout.EndVertical();
                GUI.enabled = skill.CanTrain && snapshot.Experience != null &&
                    snapshot.Experience.AvailableSkillPoints >= skill.Cost;
                if (GUILayout.Button("TRAIN", flightHud.ButtonStyle, GUILayout.Width(110),
                    GUILayout.Height(34))) client.TrainSkill(skill.Id);
                GUI.enabled = true;
                GUILayout.EndHorizontal();
            }
            GUILayout.EndScrollView(); GUILayout.EndArea();
        }

        /// <summary>Keep server-owned pilot training accessible while docked.</summary>
        private void DrawDockedTraining(Snapshot snapshot)
        {
            if (GUI.Button(ButtonRect(), open ? "Close skills" : "Skills", flightHud.ButtonStyle))
                open = !open;
            if (!open) return;
            Rect panel = PanelRect(); flightHud.Panel(panel);
            GUI.Label(new Rect(panel.x + 18, panel.y + 15, panel.width - 72, 28),
                "CLASS SKILLS", flightHud.BodyStyle);
            if (GUI.Button(new Rect(panel.xMax - 48, panel.y + 12, 30, 28), "×", flightHud.ButtonStyle))
            { open = false; return; }
            GUI.Label(new Rect(panel.x + 18, panel.y + 47, panel.width - 36, 40),
                "Train one rank at a time. The server sets costs and requirements.",
                flightHud.SmallStyle);
            DrawExperience(panel, snapshot.Experience);
            DrawTraining(snapshot, panel, 218);
            GUI.Label(new Rect(panel.x + 18, panel.yMax - 43, panel.width - 36, 35),
                client.Status, flightHud.SmallStyle);
        }

        /// <summary>Offer only currently mounted devices with server-supported activation effects.</summary>
        private void DrawEquipment(Snapshot snapshot, Rect panel)
        {
            GUILayout.BeginArea(new Rect(panel.x + 18, panel.y + 300,
                panel.width - 36, panel.height - 349));
            scroll = GUILayout.BeginScrollView(scroll);
            int shown = 0;
            for (int slot = 0; slot < snapshot.Mounted.Length; slot++)
            {
                ItemView item = snapshot.Mounted[slot];
                if (item == null || !DeviceEffects.MountedSlot(item.ActivationLabel,
                    item.Type, slot, snapshot.DeviceMounts)) continue;
                shown++;
                int binding = DeviceBinding(item.Template);
                Rect row = GUILayoutUtility.GetRect(0, 56, GUILayout.ExpandWidth(true),
                    GUILayout.Height(56));
                GUI.Box(row, new GUIContent("", item.Name), flightHud.ButtonStyle);
                if (GUI.enabled && Event.current.type == EventType.MouseDown &&
                    Event.current.button == 0 && row.Contains(Event.current.mousePosition))
                {
                    dragBinding = binding; dragStart = CanvasPointer();
                    dragCanActivate = true; draggingAbility = false;
                    Event.current.Use();
                }
                GUI.Label(new Rect(row.x + 12, row.y + 5, row.width - 24, 24),
                    item.ActivationLabel, abilityName);
                GUI.Label(new Rect(row.x + 12, row.y + 31, row.width - 24, 20),
                    item.Name + "  ·  MOUNT " + (slot + 1), abilityMeta);
            }
            if (shown == 0)
                GUILayout.Label("No activatable equipment is mounted on this ship.",
                    flightHud.SmallStyle);
            GUILayout.EndScrollView(); GUILayout.EndArea();
        }

        /// <summary>Show only abilities that this character received in native RPG packets.</summary>
        public void Draw(Snapshot snapshot)
        {
            if (snapshot == null) { open = false; dragBinding = int.MinValue; return; }
            EnsureStyles();
            LoadSlots(snapshot.PlayerId);
            if (snapshot.Station != null)
            { dragBinding = int.MinValue; draggingAbility = false; DrawDockedTraining(snapshot); return; }
            if (!GUI.enabled) { dragBinding = int.MinValue; draggingAbility = false; }
            else HandleAbilityDrag(snapshot);
            if ((!flightHud.RetainedVisible || open) && GUI.Button(ButtonRect(), open ? "Close skills" : "Skills", flightHud.ButtonStyle))
                open = !open;
            if (!open) { Quickbar(snapshot); return; }
            Rect panel = PanelRect(); flightHud.Panel(panel);
            GUI.Label(new Rect(panel.x + 18, panel.y + 15, 380, 28),
                trainingTab ? "CLASS SKILLS" : equipmentTab ? "EQUIPPED DEVICES" :
                "CLASS ABILITIES", flightHud.BodyStyle);
            if (GUI.Button(new Rect(panel.xMax - 48, panel.y + 12, 30, 28), "×", flightHud.ButtonStyle))
            { open = false; return; }
            GUI.Label(new Rect(panel.x + 18, panel.y + 42, panel.width - 36, 20),
                assigningSlot < 0 ?
                "Drag to quickbar · click to use · right-click a bound skill for variants." :
                $"Choose an action for slot {assigningSlot + 1}.", flightHud.SmallStyle);
            for (int i = 0; i < slots.Length; i++)
            {
                int id = slots[i];
                AbilityDefinition bound = id >= 0 ? catalog.FirstOrDefault(item => item.id == id) : null;
                ItemView device = BoundDevice(snapshot, id);
                string label = client.Bindings.Display((ControlAction)((int)ControlAction.AbilityOne + i)) +
                    (bound == null && device == null ? "  +" : "  ●");
                if (GUI.Button(new Rect(panel.x + 18 + i * 81, panel.y + 68, 77, 37),
                    new GUIContent(label, bound?.name ?? device?.ActivationLabel ?? "Empty slot"),
                    flightHud.ButtonStyle))
                    assigningSlot = i;
            }
            DrawExperience(panel, snapshot.Experience);
            if (GUI.Button(new Rect(panel.x + 18, panel.y + 265, 132, 29), "ABILITIES",
                flightHud.ButtonStyle)) { trainingTab = equipmentTab = false; scroll = Vector2.zero; }
            if (GUI.Button(new Rect(panel.x + 158, panel.y + 265, 132, 29), "TRAINING",
                flightHud.ButtonStyle)) { trainingTab = true; equipmentTab = false; scroll = Vector2.zero; }
            if (GUI.Button(new Rect(panel.x + 298, panel.y + 265, 132, 29), "EQUIPMENT",
                flightHud.ButtonStyle)) { trainingTab = false; equipmentTab = true; scroll = Vector2.zero; }
            if (trainingTab)
            {
                GUI.Label(new Rect(panel.x + 18, panel.y + 209, panel.width - 36, 35),
                    "Train one rank at a time. The server sets costs and requirements.",
                    flightHud.SmallStyle);
                DrawTraining(snapshot, panel);
                GUI.Label(new Rect(panel.x + 18, panel.yMax - 43, panel.width - 36, 35),
                    client.Status, flightHud.SmallStyle);
                Quickbar(snapshot);
                return;
            }
            SupportTargetBar(snapshot, panel);
            if (equipmentTab)
            {
                DrawEquipment(snapshot, panel);
                Quickbar(snapshot);
                GUI.Label(new Rect(panel.x + 18, panel.yMax - 43, panel.width - 36, 35),
                    client.Status, flightHud.SmallStyle);
                return;
            }
            AbilityDefinition[] available = catalog.Where(item => item.id >= 0 &&
                snapshot.AvailableAbilities.Contains((uint)item.id)).ToArray();
            GUILayout.BeginArea(new Rect(panel.x + 18, panel.y + 300, panel.width - 36, panel.height - 349));
            scroll = GUILayout.BeginScrollView(scroll);
            if (available.Length == 0)
                GUILayout.Label(snapshot.AvailableAbilities.Length == 0 ?
                    "Waiting for class abilities from the server…" :
                    "Ability display catalog is out of date.", flightHud.SmallStyle);
            foreach (IGrouping<int, AbilityDefinition> family in available.GroupBy(item => item.skill)
                .OrderBy(group => skillNames.TryGetValue((uint)group.Key, out string name) ?
                    name : group.First().kind))
            {
                AbilityDefinition[] variants = family.OrderBy(item => item.rank)
                    .ThenBy(item => item.name).ToArray();
                string familyName = skillNames.TryGetValue((uint)family.Key, out string known) ?
                    known : variants[0].kind.Replace('_', ' ');
                bool expanded = expandedSkills.Contains(family.Key);
                Rect heading = GUILayoutUtility.GetRect(0, 50, GUILayout.ExpandWidth(true),
                    GUILayout.Height(50));
                if (GUI.Button(heading, GUIContent.none, flightHud.ButtonStyle))
                {
                    if (!expandedSkills.Add(family.Key)) expandedSkills.Remove(family.Key);
                }
                GUI.DrawTexture(new Rect(heading.x + 7, heading.y + 4, 42, 42),
                    icons.ForSkill(family.Key));
                GUI.Label(new Rect(heading.x + 57, heading.y + 5, heading.width - 84, 23),
                    familyName.ToUpperInvariant(), abilityName);
                GUI.Label(new Rect(heading.x + 57, heading.y + 27, heading.width - 84, 20),
                    $"{variants.Length} VARIANT{(variants.Length == 1 ? "" : "S")}  ·  " +
                    $"HIGHEST RANK {variants[variants.Length - 1].rank}  ·  " +
                    (expanded ? "COLLAPSE ▲" : "EXPAND ▼"), abilityMeta);
                if (!expanded) continue;
                foreach (AbilityDefinition ability in variants)
                    DrawAbilityVariant(snapshot, panel, ability);
            }
            GUILayout.EndScrollView(); GUILayout.EndArea();
            Quickbar(snapshot);
            string tooltip = GUI.tooltip;
            GUI.Label(new Rect(panel.x + 18, panel.yMax - 43, panel.width - 36, 35),
                string.IsNullOrEmpty(tooltip) ? client.Status : tooltip, flightHud.SmallStyle);
        }

        /// <summary>Draw one available rank inside its native skill family.</summary>
        private void DrawAbilityVariant(Snapshot snapshot, Rect panel, AbilityDefinition ability)
        {
                snapshot.AbilityCooldownSeconds.TryGetValue((uint)ability.skill, out float recovery);
                bool charging = snapshot.CastRemainingSeconds > 0;
                string description = ability.id == 65 ?
                    "Jumpstart an incapacitated ship. Energy depends on its hull upgrade. " +
                    "Charge 5 s · No cooldown · Hull restored: 10% per trained skill level." :
                    $"{ability.description}\nEnergy {ability.energy:0} · Charge {ability.charge:0.#} s · Cooldown {ability.cooldown:0.#} s · Range {ability.range} m";
                string status = recovery > 0 ? $"READY IN {recovery:0.#} S" :
                    charging && snapshot.CastingAbilityId == (uint)ability.id ?
                    $"CHARGING {snapshot.CastRemainingSeconds:0.#} S" :
                    ability.id == 65 ? "ENERGY DEPENDS ON TARGET" :
                    $"RANK {ability.rank}  ·  {ability.energy:0} ENERGY";
                if (ability.kind == "shield_charging")
                    status += AbilityTargets.Directed((uint)ability.id) ?
                        "  ·  SELECTED PILOT" : "  ·  SELF ONLY";
                if (ability.kind == "hacking" && recovery <= 0 && !charging)
                    status = HackingTargetHint(snapshot);
                float rowWidth = panel.width - 52;
                float nameHeight = Mathf.Max(22,
                    abilityName.CalcHeight(new GUIContent(ability.name), rowWidth - 65));
                float metaHeight = Mathf.Max(20,
                    abilityMeta.CalcHeight(new GUIContent(status), rowWidth - 24));
                float rowHeight = nameHeight + metaHeight + 16;
                bool canActivate = recovery <= 0 && !charging &&
                    (AbilityTargets.Directed((uint)ability.id) || !SupportTargetPending(snapshot)) &&
                    (ability.id != 125 || CrewRecipientSelected(snapshot));
                Rect row = GUILayoutUtility.GetRect(0, rowHeight, GUILayout.ExpandWidth(true),
                    GUILayout.Height(rowHeight));
                GUI.Box(row, new GUIContent("", description), flightHud.ButtonStyle);
                if (GUI.enabled && Event.current.type == EventType.MouseDown && Event.current.button == 0 &&
                    row.Contains(Event.current.mousePosition))
                {
                    dragBinding = ability.id; dragStart = CanvasPointer();
                    dragCanActivate = canActivate; draggingAbility = false;
                    Event.current.Use();
                }
                GUI.DrawTexture(new Rect(row.x + 8, row.y + 7, 32, 32),
                    icons.ForSkill(ability.skill));
                GUI.Label(new Rect(row.x + 47, row.y + 5, row.width - 59, nameHeight),
                    ability.name, abilityName);
                GUI.Label(new Rect(row.x + 47, row.y + nameHeight + 6,
                    row.width - 59, metaHeight), status, abilityMeta);
        }
    }
}
