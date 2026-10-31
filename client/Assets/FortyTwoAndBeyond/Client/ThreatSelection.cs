using System;
using System.Linq;
using FortyTwoAndBeyond.Protocol;

namespace FortyTwoAndBeyond.Client
{
    /// <summary>Choose visible hostile contacts and server-confirmed attackers without predicting MOB aggro.</summary>
    public static class ThreatSelection
    {
        /// Returns:
        ///     uint: The nearest selectable contact, wreck, resource or known navigation object, excluding self.
        public static uint NearestSelectable(Snapshot snapshot, ContactCategoryFilter filter = ContactCategoryFilter.All) => NextSelectable(snapshot, 0, filter);

        /// Returns:
        ///     uint: The next advertised selectable object by distance, wrapping around the complete list.
        public static uint NextSelectable(Snapshot snapshot, uint selectedId, ContactCategoryFilter filter = ContactCategoryFilter.All)
        {
            if (snapshot == null || snapshot.Station != null || !snapshot.HasPosition) return 0;
            var objects = snapshot.Mobs.Concat(snapshot.Players)
                .Where(actor => ContactFiltering.Matches(filter, actor) && actor.Id != snapshot.PlayerId)
                .Select(actor => (actor.Id, actor.Position))
                .Concat(snapshot.Objects.Where(item => ContactFiltering.Matches(filter, item)).Select(item => (item.Id, item.Position)))
                .Where(item => item.Id != 0 && item.Id != snapshot.PlayerId)
                .GroupBy(item => item.Id).Select(group => group.First())
                .OrderBy(item => item.Position.Distance(snapshot.Position)).ThenBy(item => item.Id).ToArray();
            if (objects.Length == 0) return 0;
            int current = Array.FindIndex(objects, item => item.Id == selectedId);
            return objects[(current + 1) % objects.Length].Id;
        }

        /// Returns:
        ///     uint: The closest visible hostile MOB, or zero when none can be targeted.
        public static uint NearestHostile(Snapshot snapshot) => NextHostile(snapshot, 0);

        /// Returns:
        ///     uint: The next visible hostile MOB in stable distance order, wrapping to the nearest.
        public static uint NextHostile(Snapshot snapshot, uint selectedId)
        {
            if (snapshot == null || snapshot.Station != null || !snapshot.HasPosition) return 0;
            ShipActor[] hostiles = snapshot.Mobs.Where(mob => mob.Hostile && mob.HasPosition)
                .OrderBy(mob => mob.Position.Distance(snapshot.Position)).ThenBy(mob => mob.Id).ToArray();
            if (hostiles.Length == 0) return 0;
            int current = Array.FindIndex(hostiles, mob => mob.Id == selectedId);
            return hostiles[(current + 1) % hostiles.Length].Id;
        }

        /// <summary>
        /// Returns:
        ///     uint: The next visible attacker by range, or zero when none can be targeted.
        /// </summary>
        public static uint Next(Snapshot snapshot, uint selectedId)
        {
            if (snapshot == null || snapshot.Station != null || !snapshot.HasPosition) return 0;
            var attackers = snapshot.Mobs.Where(mob => mob.HasPosition &&
                    snapshot.Attackers.Contains(mob.Id))
                .OrderBy(mob => mob.Position.Distance(snapshot.Position))
                .ThenBy(mob => mob.Id).ToArray();
            if (attackers.Length == 0) return 0;
            int current = Array.FindIndex(attackers, mob => mob.Id == selectedId);
            return attackers[(current + 1) % attackers.Length].Id;
        }
    }
}
