using System;
using System.Collections.Generic;
using System.IO;

namespace FortyTwoAndBeyond.Protocol
{
    /// <summary>One server-published rank, ceiling, and next-rank training gate.</summary>
    public sealed class SkillRank
    {
        public readonly uint Id, Rank, Maximum, RequiredLevel, Cost, Gate, Availability;

        /// <summary>Keep a native skill entry immutable across worker snapshots.</summary>
        public SkillRank(uint id, uint rank, uint maximum, uint availability,
            uint gate, uint requiredLevel, uint cost)
        {
            Id = id; Rank = rank; Maximum = maximum; Availability = availability;
            Gate = gate; RequiredLevel = requiredLevel; Cost = cost;
        }

        public bool CanTrain => Availability == 4 && Gate == 0 && Rank < Maximum && Cost > 0;

        /// <returns>string: The server-advertised reason this rank cannot be bought.</returns>
        public string GateReason()
        {
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
        /// <returns>SkillRank[]: An authoritative skill revision, or null for unrelated AUX.</returns>
        public static SkillRank[] ReadSkillRanks(byte[] body)
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
                    throw new InvalidDataException("Invalid global AUX reset before skill ranks.");
            }
            Flags rpg = reader.Mask(16);
            if (!rpg.Present[2]) return null;
            if (rpg.Present[0]) reader.UInt();
            if (rpg.Present[1]) reader.UInt();
            Flags slots = reader.Mask(64);
            var skills = new List<SkillRank>();
            for (uint id = 0; id < 64; id++)
            {
                if (slots.Present[id])
                {
                    Flags fields = reader.Mask(7);
                    uint rank = 0, maximum = 0, availability = 0, gate = 0;
                    uint required = 0, cost = 0;
                    for (int field = 0; field < 7; field++)
                    {
                        if (!fields.Present[field]) continue;
                        switch (field)
                        {
                            case 0: rank = reader.UInt(); break;
                            case 3:
                                availability = reader.UInt(); gate = reader.UInt();
                                required = reader.UInt(); cost = reader.UInt(); break;
                            case 4: maximum = reader.UInt(); break;
                            default: reader.UInt(); break;
                        }
                    }
                    if (maximum > 9 || rank > maximum || cost > 9)
                        throw new InvalidDataException("Invalid native skill rank or cost.");
                    skills.Add(new SkillRank(id, rank, maximum, availability, gate, required, cost));
                }
                else if (slots.Default[id] && reader.Byte() != 5)
                    throw new InvalidDataException("Invalid skill slot reset.");
            }
            return skills.ToArray();
        }
    }
}
