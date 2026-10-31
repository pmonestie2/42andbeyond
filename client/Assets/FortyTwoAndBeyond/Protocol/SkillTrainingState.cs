using System;
using System.Collections.Generic;
using System.IO;

namespace FortyTwoAndBeyond.Protocol
{
    /// <summary>One server-published rank, ceiling, and next-rank training gate.</summary>
    public sealed class SkillRank
    {
        public readonly uint Id, Rank, Maximum, RequiredLevel, Cost, Gate, Availability, QuestOnlyLevel;
        public readonly bool MaximumKnown;

        /// <summary>Keep a native skill entry immutable across worker snapshots.</summary>
        public SkillRank(uint id, uint rank, uint maximum, uint availability,
            uint gate, uint requiredLevel, uint cost, uint questOnlyLevel = 9, bool maximumKnown = true)
        {
            Id = id; Rank = rank; Maximum = maximum; Availability = availability;
            Gate = gate; RequiredLevel = requiredLevel; Cost = cost;
            QuestOnlyLevel = questOnlyLevel; MaximumKnown = maximumKnown;
        }

        public bool CanTrain => MaximumKnown && Availability == 4 && Gate == 0 && Rank < Maximum && Cost > 0;

        /// <returns>string: The server-advertised reason this rank cannot be bought.</returns>
        public string GateReason()
        {
            if (!MaximumKnown) return "WAITING FOR SKILL DATA";
            if (Rank >= Maximum || (Gate & 0x40) != 0) return "MAX RANK";
            if (Availability == 3) return "MISSION UNLOCK";
            if ((Gate & 0x02) != 0) return "NEED SKILL POINTS";
            if ((Gate & 0x08) != 0) return "COMBAT LEVEL " + RequiredLevel;
            if ((Gate & 0x10) != 0) return "TRADE LEVEL " + RequiredLevel;
            if ((Gate & 0x20) != 0) return "EXPLORE LEVEL " + RequiredLevel;
            if ((Gate & 0x01) != 0) return "TOTAL LEVEL " + RequiredLevel;
            return Gate == 0 ? "READY" : "UNAVAILABLE";
        }
    }

    internal sealed partial class AuxState
    {
        /// Returns:
        ///     bool: Whether a nested native skill record explicitly resets to its schema defaults.
        private bool ReadSkillReset()
        {
            Wire.Require(body, offset, 1);
            if (body[offset] != 5) return false;
            offset++; return true;
        }

        /// Returns:
        ///     SkillRank[]: An immutable merge of changed skill slots, or null for an unrelated AUX revision.
        public static SkillRank[] ReadSkillRanks(byte[] body, SkillRank[] previous = null)
        {
            var reader = new AuxState(body);
            if (reader.ReadSkillReset()) return Array.Empty<SkillRank>();
            Flags global = reader.Mask(18);
            if (!global.Present[6] && !global.Default[6]) return null;
            reader.SkipPlayerPrefix(global, 6);
            if (global.Default[6])
            {
                if (reader.Byte() != 5) throw new InvalidDataException("Invalid RPG skill reset.");
                return Array.Empty<SkillRank>();
            }
            if (reader.ReadSkillReset()) return Array.Empty<SkillRank>();
            Flags rpg = reader.Mask(16);
            if (!rpg.Present[2] && !rpg.Default[2]) return null;
            if (rpg.Present[0]) reader.UInt();
            if (rpg.Present[1]) reader.UInt();
            if (rpg.Default[2])
            {
                if (reader.Byte() != 5) throw new InvalidDataException("Invalid skill collection reset.");
                return Array.Empty<SkillRank>();
            }
            if (reader.ReadSkillReset()) return Array.Empty<SkillRank>();
            Flags slots = reader.Mask(64);
            var skills = new SortedDictionary<uint, SkillRank>();
            foreach (SkillRank skill in previous ?? Array.Empty<SkillRank>()) skills[skill.Id] = skill;
            for (uint id = 0; id < 64; id++)
            {
                if (slots.Present[id])
                {
                    if (reader.ReadSkillReset()) { skills.Remove(id); continue; }
                    Flags fields = reader.Mask(7);
                    skills.TryGetValue(id, out SkillRank prior);
                    uint rank = prior?.Rank ?? 0, maximum = prior?.Maximum ?? 0;
                    uint availability = prior?.Availability ?? 1, gate = prior?.Gate ?? 0;
                    uint required = prior?.RequiredLevel ?? 0, cost = prior?.Cost ?? 0;
                    uint questOnlyLevel = prior?.QuestOnlyLevel ?? 9;
                    bool maximumKnown = prior?.MaximumKnown ?? false;
                    if (fields.Default[0]) rank = 0;
                    if (fields.Default[3]) { availability = 1; gate = required = cost = 0; }
                    if (fields.Default[4]) questOnlyLevel = 9;
                    if (fields.Default[5]) { maximum = 0; maximumKnown = true; }
                    for (int field = 0; field < 7; field++)
                    {
                        if (!fields.Present[field]) continue;
                        switch (field)
                        {
                            case 0: rank = reader.UInt(); break;
                            case 3:
                                availability = reader.UInt(); gate = reader.UInt();
                                required = reader.UInt(); cost = reader.UInt(); break;
                            case 4: questOnlyLevel = reader.UInt(); break;
                            case 5: maximum = reader.UInt(); maximumKnown = true; break;
                            case 6: reader.Text(); break;
                            default: reader.UInt(); break;
                        }
                    }
                    if (maximum > 9 || rank > 9 || maximumKnown && rank > maximum || cost > 9 || questOnlyLevel > 9)
                        throw new InvalidDataException("Invalid native skill rank or cost.");
                    skills[id] = new SkillRank(id, rank, maximum, availability, gate, required, cost, questOnlyLevel, maximumKnown);
                }
                else if (slots.Default[id])
                {
                    if (reader.Byte() != 5) throw new InvalidDataException("Invalid skill slot reset.");
                    skills.Remove(id);
                }
            }
            return new List<SkillRank>(skills.Values).ToArray();
        }
    }
}
