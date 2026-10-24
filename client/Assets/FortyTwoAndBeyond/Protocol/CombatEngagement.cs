using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace FortyTwoAndBeyond.Protocol
{
    /// <summary>One mounted weapon's display guidance for the selected MOB.</summary>
    public sealed class WeaponReadiness
    {
        public readonly int Slot;
        public readonly string Name;
        public readonly uint Type;
        public readonly bool Armed, Cooling, OutOfAmmo;
        public readonly float ListedRangeMeters;
        public readonly bool BeyondListedRange, OutsideFiringArc;

        /// <summary>Retain approximate range and aim cues beside authoritative mount flags.</summary>
        internal WeaponReadiness(int slot, ItemView item, float centerDistanceMeters,
            bool outsideFiringArc)
        {
            Slot = slot; Name = item.Name; Type = item.Type;
            Armed = (item.WeaponState & 1) != 0;
            Cooling = item.Cooling; OutOfAmmo = item.OutOfAmmo;
            ListedRangeMeters = item.Range;
            BeyondListedRange = item.Range > 0 && centerDistanceMeters > item.Range;
            OutsideFiringArc = outsideFiringArc;
        }
    }

    /// <summary>Selected-target weapon intent, guidance and recent server-confirmed results.</summary>
    public sealed class CombatEngagement
    {
        private const double ArcCosine = 0.766044443118978; // cos(40 degrees)
        public readonly uint TargetId;
        public readonly float CenterDistanceMeters;
        public readonly WeaponReadiness[] Weapons;
        public readonly ShotEvent LastShot;
        public readonly DamageEvent LastResult;
        public readonly float LastShotAgeSeconds, LastResultAgeSeconds;

        /// <summary>Build one immutable display view from server-owned target and mount state.</summary>
        internal CombatEngagement(uint playerId, Position ownPosition, float[] ownOrientation,
            ShipActor target, ItemView[] mounted, uint weaponMounts, IReadOnlyList<ShotEvent> shots,
            IReadOnlyList<DamageEvent> damages, long now)
        {
            TargetId = target.Id;
            CenterDistanceMeters = ownPosition.Distance(target.Position);
            var weapons = new List<WeaponReadiness>();
            int end = Math.Min(mounted.Length, 3 + (int)weaponMounts);
            for (int slot = 3; slot < end; slot++)
            {
                ItemView item = mounted[slot];
                if (item == null || item.Type < 14 || item.Type > 16) continue;
                weapons.Add(new WeaponReadiness(slot, item, CenterDistanceMeters,
                    item.Type != 15 && !InFiringArc(ownPosition, ownOrientation, target.Position)));
            }
            Weapons = weapons.ToArray();
            for (int index = shots.Count - 1; index >= 0; index--)
                if (shots[index].Source == playerId && shots[index].Target == TargetId)
                { LastShot = shots[index]; break; }
            for (int index = damages.Count - 1; index >= 0; index--)
                if (damages[index].Source == playerId && damages[index].Target == TargetId)
                { LastResult = damages[index]; break; }
            LastShotAgeSeconds = Age(LastShot?.ReceivedAt ?? 0, now);
            LastResultAgeSeconds = Age(LastResult?.ReceivedAt ?? 0, now);
        }

        /// <summary>
        /// Returns:
        ///     float: Elapsed display seconds, or positive infinity when no event exists.
        /// </summary>
        private static float Age(long receivedAt, long now)
        { return receivedAt == 0 ? float.PositiveInfinity : (float)Math.Max(0,
            (now - receivedAt) / (double)Stopwatch.Frequency); }

        /// <summary>
        /// Returns:
        ///     bool: Whether target center lies inside the server's 40-degree directional cone.
        /// </summary>
        private static bool InFiringArc(Position own, float[] orientation, Position target)
        {
            double x = orientation[0], y = orientation[1], z = orientation[2], w = orientation[3];
            double hx = 1 - 2 * (y * y + z * z);
            double hy = 2 * (x * y + z * w);
            double hz = 2 * (x * z - y * w);
            double dx = target.X - own.X, dy = target.Y - own.Y, dz = target.Z - own.Z;
            double headingLength = Math.Sqrt(hx * hx + hy * hy + hz * hz);
            double distance = Math.Sqrt(dx * dx + dy * dy + dz * dz);
            if (distance < .0001) return true;
            return headingLength > .0001 &&
                (hx * dx + hy * dy + hz * dz) / (headingLength * distance) > ArcCosine;
        }
    }
}
