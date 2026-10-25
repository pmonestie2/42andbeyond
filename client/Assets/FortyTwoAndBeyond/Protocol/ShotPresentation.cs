using System;

namespace FortyTwoAndBeyond.Protocol
{
    /// <summary>Visual motion inferred from the server's launch and public loadout metadata.</summary>
    public enum ShotTravelKind { Beam, Projectile, Missile }

    /// <summary>Classify a confirmed native launch without deciding its hit result.</summary>
    public static class ShotPresentation
    {
        /// <summary>
        /// Returns:
        ///     ShotTravelKind: The public weapon class, or a duration-based cue for unseen ships.
        /// </summary>
        public static ShotTravelKind Classify(ShotEvent shot, uint playerId,
            ItemView[] mounted, ItemView[] ammo)
        {
            if (shot.Source == playerId && mounted != null)
            {
                bool beam = false, projectile = false, missile = false;
                for (int slot = 3; slot < Math.Min(mounted.Length, 9); slot++)
                {
                    ItemView weapon = mounted[slot];
                    if (weapon == null || weapon.Type < 14 || weapon.Type > 16) continue;
                    ItemView rounds = ammo != null && slot < ammo.Length ? ammo[slot] : null;
                    uint effect = weapon.Type == 14 || rounds == null || rounds.UseEffect == 0 ?
                        weapon.UseEffect : rounds.UseEffect;
                    if (effect == 0 || effect != shot.Asset) continue;
                    beam |= weapon.Type == 14;
                    missile |= weapon.Type == 15;
                    projectile |= weapon.Type == 16;
                }
                if (missile && !projectile && !beam) return ShotTravelKind.Missile;
                if (projectile && !missile && !beam) return ShotTravelKind.Projectile;
                if (beam && !missile && !projectile) return ShotTravelKind.Beam;
                if ((missile || projectile) && shot.Duration != 500)
                    return missile ? ShotTravelKind.Missile : ShotTravelKind.Projectile;
            }
            // NPC beams last 1,000 ms; longer unknown effects are launcher travel.
            return shot.Duration > 1100 ? ShotTravelKind.Projectile : ShotTravelKind.Beam;
        }
    }
}
