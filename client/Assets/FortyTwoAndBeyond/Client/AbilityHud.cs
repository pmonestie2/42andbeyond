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
        private readonly int[] slots = { -1, -1, -1, -1, -1, -1 };
        private Vector2 scroll;
        private bool open;
        private int assigningSlot = -1;
        private uint slotOwner;
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
        }

        /// <returns>bool: Whether pointer input belongs to the ability button or open panel.</returns>
        public bool ContainsPointer()
        {
            Vector2 point = new Vector2(Input.mousePosition.x / flightHud.Scale,
                (Screen.height - Input.mousePosition.y) / flightHud.Scale);
            return ButtonRect().Contains(point) ||
                (open ? PanelRect().Contains(point) : QuickbarRect().Contains(point));
        }

        /// <returns>Rect: Bounds of the ability panel's persistent toolbar button.</returns>
        private Rect ButtonRect() { return new Rect(CanvasWidth - 550, 25, 110, 28); }

        /// <returns>Rect: Bounds of the scrollable ability panel.</returns>
        private Rect PanelRect()
        {
            float height = Mathf.Min(600, CanvasHeight - 145);
            return new Rect((CanvasWidth - 520) * .5f, 82, 520, height);
        }

        /// <returns>Rect: Bounds of the six flight ability slots.</returns>
        private Rect QuickbarRect()
        { return new Rect((CanvasWidth - 560) * .5f, CanvasHeight - 112, 560, 60); }

        /// <summary>Load a separate ability layout for each server character identity.</summary>
        private void LoadSlots(uint playerId)
        {
            if (slotOwner == playerId) return;
            slotOwner = playerId; assigningSlot = -1;
            for (int i = 0; i < slots.Length; i++)
                slots[i] = PlayerPrefs.GetInt($"42andBeyond.ability.{playerId}.{i}", -1);
        }

        /// <summary>Activate a bound slot while leaving target and resource checks to the server.</summary>
        public void ActivateSlot(int index, Snapshot snapshot)
        {
            if (snapshot == null || index < 0 || index >= slots.Length) return;
            LoadSlots(snapshot.PlayerId);
            if (slots[index] < 0 || !snapshot.AvailableAbilities.Contains((uint)slots[index])) return;
            client.ActivateAbility((uint)slots[index]);
        }

        /// <summary>Show bound abilities and their configured keys during flight.</summary>
        private void Quickbar(Snapshot snapshot)
        {
            Rect bar = QuickbarRect(); flightHud.Panel(bar);
            for (int i = 0; i < slots.Length; i++)
            {
                int id = slots[i];
                AbilityDefinition ability = catalog.FirstOrDefault(item => item.id == id);
                string key = client.Bindings.Display((ControlAction)((int)ControlAction.AbilityOne + i));
                string name = ability == null ? "ASSIGN" : ability.name.Length > 9 ?
                    ability.name.Substring(0, 8) + "…" : ability.name;
                Rect slot = new Rect(bar.x + 9 + i * 90, bar.y + 10, 84, 40);
                if (GUI.Button(slot, GUIContent.none, flightHud.ButtonStyle))
                {
                    if (ability == null) { open = true; assigningSlot = i; }
                    else ActivateSlot(i, snapshot);
                }
                GUI.Label(new Rect(slot.x + 7, slot.y + 1, 70, 19), key, flightHud.BodyStyle);
                GUI.Label(new Rect(slot.x + 7, slot.y + 20, 72, 17), name,
                    ability == null ? flightHud.SmallStyle : flightHud.SectionStyle);
            }
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

        /// <summary>Show only abilities that this character received in native RPG packets.</summary>
        public void Draw(Snapshot snapshot)
        {
            if (snapshot == null || snapshot.Station != null) { open = false; return; }
            LoadSlots(snapshot.PlayerId);
            if (GUI.Button(ButtonRect(), open ? "Close skills" : "Skills", flightHud.ButtonStyle))
                open = !open;
            if (!open) { Quickbar(snapshot); return; }
            Rect panel = PanelRect(); flightHud.Panel(panel);
            GUI.Label(new Rect(panel.x + 18, panel.y + 15, 380, 28), "CLASS ABILITIES", flightHud.BodyStyle);
            if (GUI.Button(new Rect(panel.xMax - 48, panel.y + 12, 30, 28), "×", flightHud.ButtonStyle))
            { open = false; return; }
            GUI.Label(new Rect(panel.x + 18, panel.y + 42, panel.width - 36, 20),
                assigningSlot < 0 ? "Select a target first for combat or group abilities." :
                $"Choose an ability below for slot {assigningSlot + 1}.", flightHud.SmallStyle);
            for (int i = 0; i < slots.Length; i++)
            {
                int id = slots[i];
                AbilityDefinition bound = catalog.FirstOrDefault(item => item.id == id);
                string label = client.Bindings.Display((ControlAction)((int)ControlAction.AbilityOne + i)) + "  " +
                    (bound == null ? "Empty" : bound.name.Length > 8 ? bound.name.Substring(0, 8) + "…" : bound.name);
                if (GUI.Button(new Rect(panel.x + 18 + i * 81, panel.y + 68, 77, 37),
                    label, flightHud.ButtonStyle)) assigningSlot = i;
            }
            DrawExperience(panel, snapshot.Experience);
            AbilityDefinition[] available = catalog.Where(item => item.id >= 0 && snapshot.AvailableAbilities.Contains((uint)item.id))
                .OrderBy(item => item.name).ToArray();
            GUILayout.BeginArea(new Rect(panel.x + 18, panel.y + 208, panel.width - 36, panel.height - 257));
            scroll = GUILayout.BeginScrollView(scroll);
            if (available.Length == 0)
                GUILayout.Label("Waiting for class abilities from the server…", flightHud.SmallStyle);
            foreach (AbilityDefinition ability in available)
            {
                snapshot.AbilityCooldownSeconds.TryGetValue((uint)ability.skill, out float recovery);
                bool charging = snapshot.CastRemainingSeconds > 0;
                string description = ability.id == 65 ?
                    "Jumpstart an incapacitated ship. Energy depends on its hull upgrade. " +
                    "Charge 5 s · No cooldown · Hull restored: 10% per trained skill level." :
                    $"{ability.description}\nEnergy {ability.energy:0} · Charge {ability.charge:0.#} s · Cooldown {ability.cooldown:0.#} s · Range {ability.range} m";
                string label = recovery > 0 ? $"{ability.name}  ·  READY IN {recovery:0.#} S" :
                    charging && snapshot.CastingAbilityId == (uint)ability.id ?
                    $"{ability.name}  ·  CHARGING {snapshot.CastRemainingSeconds:0.#} S" :
                    ability.id == 65 ? "Jumpstart  ·  ENERGY DEPENDS ON TARGET" :
                    $"{ability.name}  ·  RANK {ability.rank}  ·  {ability.energy:0} ENERGY";
                GUI.enabled = assigningSlot >= 0 || recovery <= 0 && !charging;
                if (GUILayout.Button(new GUIContent(label, description), flightHud.ButtonStyle, GUILayout.Height(34)))
                {
                    if (assigningSlot >= 0)
                    {
                        slots[assigningSlot] = ability.id;
                        PlayerPrefs.SetInt($"42andBeyond.ability.{slotOwner}.{assigningSlot}", ability.id);
                        PlayerPrefs.Save(); assigningSlot = -1;
                    }
                    else client.ActivateAbility((uint)ability.id);
                }
                GUI.enabled = true;
            }
            GUILayout.EndScrollView(); GUILayout.EndArea();
            string tooltip = GUI.tooltip;
            GUI.Label(new Rect(panel.x + 18, panel.yMax - 43, panel.width - 36, 35),
                string.IsNullOrEmpty(tooltip) ? client.Status : tooltip, flightHud.SmallStyle);
        }
    }
}
