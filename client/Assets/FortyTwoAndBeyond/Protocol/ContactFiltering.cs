using System;

namespace FortyTwoAndBeyond.Protocol
{
    /// <summary>Compact categories shared by contact cycling and the flight-edge rim.</summary>
    public enum ContactCategoryFilter { All, Combat, Loot, Mining, Travel, NonEnemy }

    /// <summary>Classify only server-advertised, selectable space contacts.</summary>
    public static class ContactFiltering
    {
        /// Returns:
        ///     bool: Whether this visible actor belongs to the chosen contact category.
        public static bool Matches(ContactCategoryFilter filter, ShipActor actor)
        {
            if (actor == null || !actor.HasPosition) return false;
            return filter == ContactCategoryFilter.All ||
                filter == ContactCategoryFilter.Combat && actor.Hostile ||
                filter == ContactCategoryFilter.NonEnemy && !actor.Hostile;
        }

        /// Returns:
        ///     bool: Whether this server-advertised object belongs to the chosen category.
        public static bool Matches(ContactCategoryFilter filter, MapObject item)
        {
            if (item == null || !WarpTargets.WorldObject(item)) return false;
            ContactCategoryFilter category = item.Kind == 25 ? ContactCategoryFilter.Loot :
                item.Kind == 37 || item.Kind == 38 ? ContactCategoryFilter.Mining :
                ContactCategoryFilter.Travel;
            string name = item.Name ?? "";
            if (category == ContactCategoryFilter.Travel && item.Kind != 11 && item.Kind != 12 &&
                (!item.Navigable || name.IndexOf("Border Demarcation", StringComparison.OrdinalIgnoreCase) >= 0 ||
                 name.IndexOf("BorderDemarcation", StringComparison.OrdinalIgnoreCase) >= 0 ||
                 name.StartsWith("Asteroid", StringComparison.OrdinalIgnoreCase))) return false;
            return filter == ContactCategoryFilter.All || filter == ContactCategoryFilter.NonEnemy || filter == category;
        }
    }
}
