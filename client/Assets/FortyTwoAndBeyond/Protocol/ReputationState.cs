using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

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
        /// <summary>Consume a native faction record before later PlayerIndex fields.</summary>
        private void SkipReputation()
        {
            Flags fields = Mask(2);
            if (fields.Present[0]) ReadFactionSlots(Array.Empty<FactionStanding>());
            else if (fields.Default[0] && Byte() != 5)
                throw new InvalidDataException("Invalid faction PDA reset before group.");
            if (fields.Present[1]) Text();
        }

        /// <summary>
        /// Returns:
        ///     ReputationState: Updated faction PDA, or null for unrelated global AUX.
        /// </summary>
        public static ReputationState ReadReputation(byte[] body, ReputationState previous = null)
        {
            var reader = new AuxState(body);
            Flags global = reader.Mask(18);
            if (!global.Present[10] && !global.Default[10]) return null;
            reader.SkipPlayerPrefix(global, 9);
            if (global.Present[9]) reader.ReadMissionSlots(new MissionState[12]);
            else if (global.Default[9] && reader.Byte() != 5)
                throw new InvalidDataException("Invalid mission reset before reputation.");
            if (global.Default[10])
            {
                if (reader.Byte() != 5) throw new InvalidDataException("Invalid reputation reset.");
                return new ReputationState("", Array.Empty<FactionStanding>());
            }
            Flags fields = reader.Mask(2);
            FactionStanding[] standings = previous?.Standings.ToArray() ?? Array.Empty<FactionStanding>();
            string affiliation = previous?.Affiliation ?? "";
            if (fields.Present[0]) standings = reader.ReadFactionSlots(standings);
            else if (fields.Default[0] && reader.Byte() != 5)
                throw new InvalidDataException("Invalid faction PDA reset.");
            else if (fields.Default[0]) standings = Array.Empty<FactionStanding>();
            if (fields.Present[1]) affiliation = reader.Text();
            return new ReputationState(affiliation, standings);
        }

        /// <summary>
        /// Returns:
        ///     FactionStanding[]: The visible native PDA standings in slot order.
        /// </summary>
        private FactionStanding[] ReadFactionSlots(FactionStanding[] previous)
        {
            Flags slots = Mask(32);
            var standings = new List<FactionStanding>(32);
            for (int slot = 0; slot < 32; slot++)
            {
                if (!slots.Present[slot])
                {
                    if (slots.Default[slot] && Byte() != 5)
                        throw new InvalidDataException("Invalid faction PDA reset.");
                    if (!slots.Default[slot])
                    {
                        FactionStanding retained = previous.FirstOrDefault(row => row.Slot == slot);
                        if (retained != null) standings.Add(retained);
                    }
                    continue;
                }
                Flags entry = Mask(3);
                FactionStanding prior = previous.FirstOrDefault(row => row.Slot == slot);
                string label = entry.Present[0] ? Text() : prior?.Label ?? "";
                float value = entry.Present[1] ? Float() : prior?.Value ?? 0;
                uint index = entry.Present[2] ? UInt() : (uint)slot;
                if (index != slot || label.Length > 63)
                    throw new InvalidDataException("Invalid faction PDA row.");
                if (label.Length > 0) standings.Add(new FactionStanding((uint)slot, label, value));
            }
            return standings.ToArray();
        }
    }
}
