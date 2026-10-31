using System;
using System.Collections.Generic;
using FortyTwoAndBeyond.Protocol;

namespace FortyTwoAndBeyond.Client
{
    /// <summary>Local ammunition ownership scopes for manufacturing recipe browsing.</summary>
    public enum RecipeOwnership { All, Aboard, Vault, AboardOrVault }

    /// <summary>Combinable local output categories; unknown native types remain available as Other.</summary>
    [Flags]
    public enum RecipeTypes { None = 0, Ammo = 1, Components = 2, Devices = 4, Reactors = 8,
        Shields = 16, Engines = 32, Weapons = 64, Other = 128, All = 255 }

    /// <summary>Match formula outputs against immutable cargo, magazine and vault snapshots.</summary>
    public sealed class ProductionRecipeFilter
    {
        private readonly Dictionary<uint, ulong> aboard = new Dictionary<uint, ulong>();
        private readonly Dictionary<uint, ulong> vault = new Dictionary<uint, ulong>();

        /// <summary>Count cargo and vault ammunition plus all rounds in loaded magazines.</summary>
        public ProductionRecipeFilter(IEnumerable<ItemView> cargo, IEnumerable<ItemView> magazines, IEnumerable<ItemView> vaultItems)
        {
            AddItems(aboard, cargo, false);
            AddItems(aboard, magazines, true);
            AddItems(vault, vaultItems, false);
        }

        /// Returns:
        ///     int: Distinct ammunition templates currently aboard, including loaded rounds.
        public int AboardTypes => aboard.Count;
        /// Returns:
        ///     int: Distinct ammunition templates currently held in the station vault.
        public int VaultTypes => vault.Count;

        /// Returns:
        ///     ulong: Rounds of the formula's output template in cargo and loaded magazines.
        public ulong AboardQuantity(uint template) => aboard.TryGetValue(template, out ulong count) ? count : 0;

        /// Returns:
        ///     ulong: Rounds of the formula's output template held in the station vault.
        public ulong VaultQuantity(uint template) => vault.TryGetValue(template, out ulong count) ? count : 0;

        /// Returns:
        ///     bool: Whether this output matches the level mask, text search and ownership scope together.
        public bool Matches(ProductionFormula formula, uint levels, string query, RecipeOwnership ownership, RecipeTypes types = RecipeTypes.All)
        {
            if (formula == null || formula.Level < 1 || formula.Level > 9 || (levels & (1u << ((int)formula.Level - 1))) == 0) return false;
            if ((types & Category(formula.Type)) == 0) return false;
            string search = (query ?? "").Trim();
            if (search.Length > 0 && (formula.Name ?? "").IndexOf(search, StringComparison.OrdinalIgnoreCase) < 0) return false;
            switch (ownership)
            {
                case RecipeOwnership.Aboard: return AboardQuantity(formula.Template) > 0;
                case RecipeOwnership.Vault: return VaultQuantity(formula.Template) > 0;
                case RecipeOwnership.AboardOrVault: return AboardQuantity(formula.Template) > 0 || VaultQuantity(formula.Template) > 0;
                default: return true;
            }
        }

        /// Returns:
        ///     RecipeTypes: The local filter category corresponding to one native item type.
        public static RecipeTypes Category(uint type)
        {
            switch (type)
            {
                case 10: return RecipeTypes.Ammo;
                case 13: return RecipeTypes.Components;
                case 11: return RecipeTypes.Devices;
                case 7: return RecipeTypes.Reactors;
                case 2: return RecipeTypes.Shields;
                case 6: return RecipeTypes.Engines;
                case 14: case 15: case 16: return RecipeTypes.Weapons;
                default: return RecipeTypes.Other;
            }
        }

        /// <summary>Accumulate real round quantities, ignoring empty slots and non-ammunition cargo.</summary>
        private static void AddItems(Dictionary<uint, ulong> counts, IEnumerable<ItemView> items, bool magazine)
        {
            if (items == null) return;
            foreach (ItemView item in items)
            {
                if (item == null || item.Quantity == 0 || !magazine && item.Type != 10) continue;
                counts.TryGetValue(item.Template, out ulong previous);
                counts[item.Template] = previous + item.Quantity;
            }
        }
    }
}
