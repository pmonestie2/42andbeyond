using System;
using System.Collections.Generic;
using FortyTwoAndBeyond.Client;
using FortyTwoAndBeyond.Protocol;

internal static class RecipeFilterCheck
{
    /// <summary>Verify combined local recipe filters with cargo, magazines and vault ownership.</summary>
    private static void Main()
    {
        var catalog = new Dictionary<uint, ItemTemplateInfo>();
        foreach (uint template in new uint[] { 101, 102, 103, 104 })
            catalog[template] = new ItemTemplateInfo(template, 10, "Ammo " + template, "");
        catalog[201] = new ItemTemplateInfo(201, 11, "Device", "");
        catalog[301] = new ItemTemplateInfo(301, 13, "Component", "");
        var cargo = new[] { Item(101, 12, catalog), Item(101, 8, catalog), Item(201, 1, catalog), Item(103, 0, catalog), null };
        var magazines = new[] { Item(101, 3, catalog), Item(102, 17, catalog), null };
        var vault = new[] { Item(103, 50, catalog), Item(101, 7, catalog), Item(301, 2, catalog), null };
        var filter = new ProductionRecipeFilter(cargo, magazines, vault);
        Require(filter.AboardTypes == 2 && filter.VaultTypes == 2, "Distinct ammunition templates must include loaded magazines and exclude other cargo.");
        Require(filter.AboardQuantity(101) == 23 && filter.AboardQuantity(102) == 17 && filter.VaultQuantity(101) == 7, "Quantities must aggregate duplicate cargo stacks and magazines independently of vault.");
        var aboard = new ProductionFormula(102, 4, "Plasma Missile", type: 10);
        var vaulted = new ProductionFormula(103, 7, "Plasma Slug", type: 10);
        var absent = new ProductionFormula(104, 4, "Plasma Missile", type: 10);
        var device = new ProductionFormula(201, 4, "Plasma Device", type: 11);
        const uint levels = 1u << 3;
        Require(filter.Matches(aboard, levels, "  pLaSmA  ", RecipeOwnership.Aboard, RecipeTypes.Ammo), "Aboard must match ammunition held only in a loaded magazine with level, type and case-insensitive trimmed text together.");
        Require(!filter.Matches(aboard, levels, "Plasma", RecipeOwnership.Vault, RecipeTypes.Ammo), "Loaded magazines must not count as vault ownership.");
        Require(filter.Matches(vaulted, 0x1ff, "slug", RecipeOwnership.Vault, RecipeTypes.Ammo), "Vault-only ammunition must match its output template.");
        Require(filter.Matches(vaulted, 0x1ff, "", RecipeOwnership.AboardOrVault, RecipeTypes.Ammo) && filter.Matches(aboard, levels, "", RecipeOwnership.AboardOrVault), "Aboard plus vault is a union, not an intersection.");
        Require(!filter.Matches(vaulted, levels, "Plasma", RecipeOwnership.Vault) && !filter.Matches(aboard, levels, "laser", RecipeOwnership.Aboard), "Level and text criteria must both remain active with ownership.");
        Require(!filter.Matches(absent, levels, "", RecipeOwnership.AboardOrVault), "Owning different ammunition with the same recipe name must not count.");
        Require(filter.Matches(device, levels, "device", RecipeOwnership.All, RecipeTypes.Devices | RecipeTypes.Reactors), "Multiple type categories must combine with search and level.");
        Require(!filter.Matches(device, levels, "", RecipeOwnership.Aboard) && !filter.Matches(aboard, levels, "", RecipeOwnership.All, RecipeTypes.Devices), "Owned equipment must not count as owned ammunition and a type mismatch must fail.");
        Require(filter.Matches(absent, levels, "", RecipeOwnership.All) && !filter.Matches(absent, 0, "", RecipeOwnership.All) && !filter.Matches(absent, levels, "", RecipeOwnership.All, RecipeTypes.None), "All recipes preserves unowned outputs; empty level/type masks show none.");
        Require(ProductionRecipeFilter.Category(14) == RecipeTypes.Weapons && ProductionRecipeFilter.Category(15) == RecipeTypes.Weapons && ProductionRecipeFilter.Category(16) == RecipeTypes.Weapons && ProductionRecipeFilter.Category(99) == RecipeTypes.Other, "Weapon families and unknown item types retain reachable categories.");
        Console.WriteLine("PASS: local recipe level/type/text/ownership combinations, loaded magazines, vault and quantity summaries.");
    }

    /// Returns:
    ///     ItemView: A disposable immutable item resolved through the real item catalog path.
    private static ItemView Item(uint template, uint quantity, IReadOnlyDictionary<uint, ItemTemplateInfo> catalog) =>
        new ItemView(0, new ItemState { Template = template, Quantity = quantity, Quality = 1, Structure = 1 }, catalog);

    /// <summary>Fail with the behavior that the local filter violated.</summary>
    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}
