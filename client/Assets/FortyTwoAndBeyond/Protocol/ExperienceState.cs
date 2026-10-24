using System;
using System.IO;

namespace FortyTwoAndBeyond.Protocol
{
    /// <summary>Server-owned levels, progress fractions, and earned skill points.</summary>
    public sealed class ExperienceState
    {
        public readonly uint CombatLevel, TradeLevel, ExploreLevel;
        public readonly float CombatFraction, TradeFraction, ExploreFraction;
        public readonly uint AvailableSkillPoints, EarnedSkillPoints;

        /// <summary>Keep one RPG progress revision immutable across worker snapshots.</summary>
        public ExperienceState(uint combatLevel, float combatFraction, uint tradeLevel,
            float tradeFraction, uint exploreLevel, float exploreFraction,
            uint availableSkillPoints, uint earnedSkillPoints)
        {
            CombatLevel = combatLevel; CombatFraction = combatFraction;
            TradeLevel = tradeLevel; TradeFraction = tradeFraction;
            ExploreLevel = exploreLevel; ExploreFraction = exploreFraction;
            AvailableSkillPoints = availableSkillPoints; EarnedSkillPoints = earnedSkillPoints;
        }
    }

    internal sealed partial class AuxState
    {
        /// <summary>
        /// Returns:
        ///     ExperienceState: Updated levels and bars, or null for an unrelated AUX record.
        /// </summary>
        public static ExperienceState ReadExperience(byte[] body, ExperienceState previous)
        {
            var reader = new AuxState(body);
            Flags global = reader.Mask(18);
            if (!global.Present[6]) return null;
            for (int field = 0; field < 6; field++)
            {
                if (global.Present[field])
                {
                    if (field == 0) { reader.UInt(); reader.UInt(); }
                    else if (field == 2) reader.SkipSlots(96, 10);
                    else return null;
                }
                else if (global.Default[field] && field >= 2 && reader.Byte() != 5)
                    throw new InvalidDataException("Invalid global AUX reset before RPG progress.");
            }
            Flags rpg = reader.Mask(16);
            uint points = previous?.AvailableSkillPoints ?? 0;
            uint earned = previous?.EarnedSkillPoints ?? 0;
            uint combatLevel = previous?.CombatLevel ?? 0;
            uint tradeLevel = previous?.TradeLevel ?? 0;
            uint exploreLevel = previous?.ExploreLevel ?? 0;
            float combatFraction = previous?.CombatFraction ?? 0;
            float tradeFraction = previous?.TradeFraction ?? 0;
            float exploreFraction = previous?.ExploreFraction ?? 0;
            bool progressUpdated = false;
            for (int field = 0; field <= 11; field++)
            {
                if (rpg.Present[field])
                {
                    switch (field)
                    {
                        case 0: case 1: reader.UInt(); break;
                        case 2: reader.SkipSkillSlots(); break;
                        case 3: reader.SkipAbilitySlots(); break;
                        case 4: points = reader.UInt(); progressUpdated = true; break;
                        case 5: earned = reader.UInt(); progressUpdated = true; break;
                        case 6: combatFraction = reader.Float(); progressUpdated = true; break;
                        case 7: combatLevel = reader.UInt(); progressUpdated = true; break;
                        case 8: tradeFraction = reader.Float(); progressUpdated = true; break;
                        case 9: tradeLevel = reader.UInt(); progressUpdated = true; break;
                        case 10: exploreFraction = reader.Float(); progressUpdated = true; break;
                        case 11: exploreLevel = reader.UInt(); progressUpdated = true; break;
                    }
                }
                else if (rpg.Default[field] && (field == 2 || field == 3) && reader.Byte() != 5)
                    throw new InvalidDataException("Invalid RPG collection reset.");
            }
            if (!progressUpdated) return null;
            if (combatFraction < 0 || combatFraction > 1 || tradeFraction < 0 ||
                tradeFraction > 1 || exploreFraction < 0 || exploreFraction > 1)
                throw new InvalidDataException("Invalid experience progress fraction.");
            return new ExperienceState(combatLevel, combatFraction, tradeLevel, tradeFraction,
                exploreLevel, exploreFraction, points, earned);
        }
    }
}
