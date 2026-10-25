using System;
using System.Collections.Generic;
using System.IO;

namespace FortyTwoAndBeyond.Protocol
{
    /// <summary>One standing advertised in the native faction PDA.</summary>
    public sealed class FactionStanding
    {
        public readonly uint Slot;
        public readonly string Label;
        public readonly float Value;

        /// <summary>Keep a faction row immutable across worker snapshots.</summary>
        public FactionStanding(uint slot, string label, float value)
        { Slot = slot; Label = label; Value = value; }
    }

    /// <summary>The current server-owned faction PDA and affiliation.</summary>
    public sealed class ReputationState
    {
        public readonly string Affiliation;
        public readonly IReadOnlyList<FactionStanding> Standings;

        /// <summary>Copy a complete faction revision before publishing it.</summary>
        public ReputationState(string affiliation, FactionStanding[] standings)
        { Affiliation = affiliation; Standings = Array.AsReadOnly((FactionStanding[])standings.Clone()); }
    }

    /// <summary>A gain or loss calculated between two authoritative revisions.</summary>
    public sealed class ReputationChange
    {
        public readonly string Label;
        public readonly float Delta, Value;

        /// <summary>Store a visible revision without retaining mutable world state.</summary>
        public ReputationChange(string label, float delta, float value)
        { Label = label; Delta = delta; Value = value; }
    }

    internal sealed partial class AuxState
    {
        /// <summary>
        /// Returns:
        ///     ReputationState: Updated faction PDA, or null for unrelated global AUX.
        /// </summary>
        public static ReputationState ReadReputation(byte[] body)
        {
            var reader = new AuxState(body);
            Flags global = reader.Mask(18);
            if (!global.Present[10] && !global.Default[10]) return null;
            for (int field = 0; field < 10; field++)
            {
                if (global.Present[field])
                {
                    switch (field)
                    {
                        case 0: reader.UInt(); reader.UInt(); break;
                        case 2: reader.SkipSlots(96, 10); break;
                        case 6: reader.SkipRpgForMissions(); break;
                        case 8: reader.UInt(); break;
                        case 9: reader.ReadMissionSlots(new MissionState[12]); break;
                        default: throw new InvalidDataException("Unknown player field before reputation.");
                    }
                }
                else if (global.Default[field] && (field == 2 || field == 3 || field == 4 ||
                    field == 5 || field == 6 || field == 9) && reader.Byte() != 5)
                    throw new InvalidDataException("Invalid player AUX reset before reputation.");
            }
            if (global.Default[10])
            {
                if (reader.Byte() != 5) throw new InvalidDataException("Invalid reputation reset.");
                return new ReputationState("", Array.Empty<FactionStanding>());
            }
            Flags fields = reader.Mask(2);
            FactionStanding[] standings = Array.Empty<FactionStanding>();
            string affiliation = "";
            if (fields.Present[0]) standings = reader.ReadFactionSlots();
            else if (fields.Default[0] && reader.Byte() != 5)
                throw new InvalidDataException("Invalid faction PDA reset.");
            if (fields.Present[1]) affiliation = reader.Text();
            return new ReputationState(affiliation, standings);
        }

        /// <summary>
        /// Returns:
        ///     FactionStanding[]: The visible native PDA standings in slot order.
        /// </summary>
        private FactionStanding[] ReadFactionSlots()
        {
            Flags slots = Mask(32);
            var standings = new List<FactionStanding>(32);
            for (int slot = 0; slot < 32; slot++)
            {
                if (!slots.Present[slot])
                    continue; // Primitive PDA slots carry no nested reset marker.
                Flags entry = Mask(3);
                if (!entry.Present[0] || !entry.Present[1] || !entry.Present[2])
                    throw new InvalidDataException("Incomplete faction PDA row.");
                string label = Text();
                float value = Float();
                uint index = UInt();
                if (index != slot || label.Length == 0 || label.Length > 63)
                    throw new InvalidDataException("Invalid faction PDA row.");
                standings.Add(new FactionStanding(index, label, value));
            }
            return standings.ToArray();
        }
    }
}
