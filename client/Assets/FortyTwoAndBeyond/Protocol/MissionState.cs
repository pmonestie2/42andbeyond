using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace FortyTwoAndBeyond.Protocol
{
    /// <summary>One visible server-owned mission and its current objective.</summary>
    public sealed class MissionState
    {
        public readonly uint Slot, StageCount, Stage;
        public readonly string Name, Summary, Objective;
        public readonly IReadOnlyList<string> ReachedStages;
        public readonly bool Visible;

        /// <summary>Keep one mission journal row immutable across worker snapshots.</summary>
        public MissionState(uint slot, string name, string summary, uint stageCount, uint stage,
            string[] reachedStages, bool visible)
        {
            Slot = slot; Name = name; Summary = summary; StageCount = stageCount;
            Stage = stage;
            string[] copiedStages = (string[])reachedStages.Clone();
            for (int index = 0; index < copiedStages.Length; index++)
                if (copiedStages[index] == null) copiedStages[index] = "";
            ReachedStages = Array.AsReadOnly(copiedStages);
            Objective = copiedStages.Length == 0 ? "" : copiedStages[copiedStages.Length - 1];
            Visible = visible;
        }
    }

    internal sealed partial class AuxState
    {
        /// <returns>MissionState[]: Updated journal slots, or null for unrelated global AUX.</returns>
        public static MissionState[] ReadMissions(byte[] body, MissionState[] previous)
        {
            var reader = new AuxState(body);
            Flags global = reader.Mask(18);
            if (!global.Present[9] && !global.Default[9]) return null;
            for (int field = 0; field < 9; field++)
            {
                if (global.Present[field])
                {
                    switch (field)
                    {
                        case 0: reader.UInt(); reader.UInt(); break;
                        case 2: reader.SkipSlots(96, 10); break;
                        case 6: reader.SkipRpgForMissions(); break;
                        case 8: reader.UInt(); break;
                        default: throw new InvalidDataException("Unknown player field before mission journal.");
                    }
                }
                else if (global.Default[field] && (field == 2 || field == 3 || field == 4 ||
                    field == 5 || field == 6))
                {
                    if (reader.Byte() != 5) throw new InvalidDataException("Invalid player AUX reset before missions.");
                }
            }
            if (global.Default[9])
            {
                if (reader.Byte() != 5) throw new InvalidDataException("Invalid mission journal reset.");
                return new MissionState[12];
            }
            MissionState[] missions = (MissionState[])previous.Clone();
            reader.ReadMissionSlots(missions);
            return missions;
        }

        /// <summary>Consume the player RPG record preceding a mission journal.</summary>
        private void SkipRpgForMissions()
        {
            Flags rpg = Mask(16);
            for (int field = 0; field < 16; field++)
            {
                if (rpg.Present[field])
                {
                    if (field == 2) SkipSkillSlots();
                    else if (field == 3) SkipAbilitySlots();
                    else if (field == 6 || field == 8 || field == 10) Float();
                    else if (field == 0 || field == 1 || field == 4 || field == 5 ||
                        field == 7 || field == 9 || field == 11 || field == 12 || field == 15) UInt();
                    else throw new InvalidDataException("Unknown RPG field before missions.");
                }
                else if (rpg.Default[field] && (field == 2 || field == 3) && Byte() != 5)
                    throw new InvalidDataException("Invalid RPG reset before missions.");
            }
        }

        /// <summary>Merge the twelve native mission journal slots.</summary>
        private void ReadMissionSlots(MissionState[] missions)
        {
            Flags slots = Mask(12);
            for (int slot = 0; slot < missions.Length; slot++)
            {
                if (slots.Default[slot])
                {
                    if (Byte() != 5) throw new InvalidDataException("Invalid mission slot reset.");
                    missions[slot] = null;
                    continue;
                }
                if (!slots.Present[slot]) continue;
                Flags entry = Mask(19);
                uint id = (uint)slot, count = 0, stage = 0;
                string name = "", summary = "";
                string[] reachedStages = Array.Empty<string>();
                bool visible = false;
                for (int field = 0; field < 19; field++)
                {
                    if (entry.Present[field])
                    {
                        switch (field)
                        {
                            case 0: id = UInt(); break;
                            case 1: name = Text(); break;
                            case 2: summary = Text(); break;
                            case 13: visible = Byte() != 0; break;
                            case 14: count = UInt(); break;
                            case 15: stage = UInt(); break;
                            case 16: reachedStages = ReadMissionStages(); break;
                            default: throw new InvalidDataException("Unknown mission AUX field.");
                        }
                    }
                    else if (entry.Default[field] && field == 16 && Byte() != 5)
                        throw new InvalidDataException("Invalid mission stages reset.");
                }
                if (id != slot || count == 0 || count > 20 || stage == 0 || stage >= count ||
                    name.Length > 63 || summary.Length > 127)
                    throw new InvalidDataException("Invalid mission journal entry.");
                missions[slot] = new MissionState(id, name, summary, count, stage,
                    reachedStages.Take(checked((int)stage)).ToArray(), visible);
            }
        }

        /// <summary>
        /// Returns:
        ///     string[]: Native descriptions for the reached stage slots.
        /// </summary>
        private string[] ReadMissionStages()
        {
            Flags stages = Mask(20);
            var reached = new string[20];
            for (int slot = 0; slot < 20; slot++)
            {
                if (stages.Present[slot])
                {
                    Flags entry = Mask(2);
                    if (entry.Present[0]) reached[slot] = Text();
                    if (entry.Present[1]) throw new InvalidDataException("Unknown mission stage field.");
                }
                else if (stages.Default[slot] && Byte() != 5)
                    throw new InvalidDataException("Invalid mission stage reset.");
            }
            return reached;
        }
    }
}
