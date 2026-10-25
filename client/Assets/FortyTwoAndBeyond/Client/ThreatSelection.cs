using System;
using System.Linq;
using FortyTwoAndBeyond.Protocol;

namespace FortyTwoAndBeyond.Client
{
    /// <summary>Choose a visible server-confirmed attacker without predicting MOB aggro.</summary>
    public static class ThreatSelection
    {
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
