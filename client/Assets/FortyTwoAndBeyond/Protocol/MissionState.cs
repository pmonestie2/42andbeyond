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
        public readonly bool FullyVisible;

        /// <summary>Keep one mission journal row immutable across worker snapshots.</summary>
        public MissionState(uint slot, string name, string summary, uint stageCount, uint stage,
            string[] reachedStages, bool fullyVisible)
        {
            Slot = slot; Name = name; Summary = summary; StageCount = stageCount;
            Stage = stage;
            string[] copiedStages = (string[])reachedStages.Clone();
            for (int index = 0; index < copiedStages.Length; index++)
                if (copiedStages[index] == null) copiedStages[index] = "";
            ReachedStages = Array.AsReadOnly(copiedStages);
            Objective = copiedStages.Length == 0 ? "" : copiedStages[copiedStages.Length - 1];
            FullyVisible = fullyVisible;
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
            reader.SkipPlayerPrefix(global, 9);
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
                        field == 7 || field == 9 || field == 11 || field == 12 ||
                        field == 13 || field == 14 || field == 15) UInt();
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
                MissionState prior = missions[slot];
                uint id = (uint)slot, count = prior?.StageCount ?? 0, stage = prior?.Stage ?? 0;
                string name = prior?.Name ?? "", summary = prior?.Summary ?? "";
                string[] reachedStages = prior?.ReachedStages.ToArray() ?? Array.Empty<string>();
                bool fullyVisible = prior?.FullyVisible ?? false;
                for (int field = 0; field < 19; field++)
                {
                    if (entry.Present[field])
                    {
                        switch (field)
                        {
                            case 0: id = UInt(); break;
                            case 1: name = Text(); break;
                            case 2: summary = Text(); break;
                            case 3: case 4: case 5: Text(); break;
                            case 6: case 9: case 10: case 11: case 12: case 18: Byte(); break;
                            case 7: case 8: case 17: UInt(); break;
                            case 13: fullyVisible = Byte() != 0; break;
                            case 14: count = UInt(); break;
                            case 15: stage = UInt(); break;
                            case 16: reachedStages = ReadMissionStages(reachedStages); break;
                        }
                    }
                    else if (entry.Default[field])
                    {
                        switch (field)
                        {
                            case 0: id = (uint)slot; break;
                            case 1: name = ""; break;
                            case 2: summary = ""; break;
                            case 13: fullyVisible = false; break;
                            case 14: count = 0; break;
                            case 15: stage = 0; break;
                            case 16:
                                if (Byte() != 5) throw new InvalidDataException("Invalid mission stages reset.");
                                reachedStages = Array.Empty<string>();
                                break;
                        }
                    }
                }
                if (count > 20 || stage >= count && count > 0)
                    throw new InvalidDataException("Invalid mission journal entry: slot " + slot +
                        ", id " + id + ", stage " + stage + "/" + count + ".");
                if (count == 0 || stage == 0 || name.Length == 0)
                { missions[slot] = null; continue; }
                if (id != slot)
                    throw new InvalidDataException("Mission journal slot " + slot +
                        " contains identity " + id + ".");
                missions[slot] = new MissionState(id, name, summary, count, stage,
                    reachedStages.Take(checked((int)stage)).ToArray(), fullyVisible);
            }
        }

        /// <summary>
        /// Returns:
        ///     string[]: Native descriptions for the reached stage slots.
        /// </summary>
        private string[] ReadMissionStages(string[] previous)
        {
            Flags stages = Mask(20);
            var reached = new string[20];
            Array.Copy(previous, reached, Math.Min(previous.Length, reached.Length));
            for (int slot = 0; slot < 20; slot++)
            {
                if (stages.Present[slot])
                {
                    Flags entry = Mask(2);
                    if (entry.Present[0]) reached[slot] = Text();
                    else if (entry.Default[0]) reached[slot] = null;
                    if (entry.Present[1]) Byte();
                }
                else if (stages.Default[slot])
                { if (Byte() != 5) throw new InvalidDataException("Invalid mission stage reset."); reached[slot] = null; }
            }
            return reached;
        }
    }
}
