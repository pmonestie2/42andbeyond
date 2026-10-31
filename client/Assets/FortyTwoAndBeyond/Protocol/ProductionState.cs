using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace FortyTwoAndBeyond.Protocol
{
    /// <summary>One formula advertised by the station's native workbench.</summary>
    public sealed class ProductionFormula
    {
        public readonly uint Template, Level, IconId, Type;
        public readonly string Name;

        /// <summary>Keep the displayed formula and its server template together.</summary>
        public ProductionFormula(uint template, uint level, string name, uint iconId = 0, uint type = 0)
        { Template = template; Level = level; Name = name; IconId = iconId; Type = type; }
    }

    /// <summary>Worker-owned native workbench selection, quote, and formula list.</summary>
    public sealed class ProductionState
    {
        public uint LabId, Mode, Validity, Category, Additional, Filter;
        public uint PageIndex, TotalFormulas, PageSize = 500;
        public ulong PageRevision;
        public ulong Cost, BaseCost;
        public float Chance;
        public string Failure = "";
        public ItemState[] Target = new ItemState[1], Components = new ItemState[6];
        public ProductionFormula[] Formulas = new ProductionFormula[500];
        public bool Open => Mode != 0;
    }

    /// <summary>Immutable workbench data for station UI on Unity's main thread.</summary>
    public sealed class ProductionView
    {
        public readonly uint LabId, Mode, Validity, Category, Additional, Filter;
        public readonly uint PageIndex, TotalFormulas, PageSize;
        public readonly ulong PageRevision;
        public readonly ulong Cost, BaseCost;
        public readonly float Chance;
        public readonly string Failure;
        public readonly ItemView Target;
        public readonly ItemView[] Components;
        public readonly ProductionFormula[] Formulas;
        public bool Open => Mode != 0;
        public bool Ready => Validity == 13;

        /// <summary>Copy the current worker state and resolve preview item names.</summary>
        public ProductionView(ProductionState state, IReadOnlyDictionary<uint, ItemTemplateInfo> catalog)
        {
            LabId = state.LabId; Mode = state.Mode; Validity = state.Validity;
            Category = state.Category; Additional = state.Additional; Filter = state.Filter;
            Cost = state.Cost; BaseCost = state.BaseCost; Chance = state.Chance;
            Failure = state.Failure;
            if (state.Target[0]?.Present == true)
                Target = new ItemView(0, state.Target[0], catalog);
            Components = new ItemView[state.Components.Length];
            for (int slot = 0; slot < Components.Length; slot++)
                if (state.Components[slot]?.Present == true)
                    Components[slot] = new ItemView(slot, state.Components[slot], catalog);
            Formulas = state.Formulas.Where(formula => formula != null).Select(formula =>
                catalog.TryGetValue(formula.Template, out ItemTemplateInfo item) ?
                    new ProductionFormula(formula.Template, formula.Level, formula.Name, item.IconId, item.Type) :
                    formula).ToArray();
            PageIndex = state.PageIndex; PageSize = state.PageSize; PageRevision = state.PageRevision;
            TotalFormulas = state.TotalFormulas > 0 ? state.TotalFormulas : (uint)Formulas.Length;
        }
    }

    internal sealed partial class AuxState
    {
        /// <summary>Apply a complete workbench view or a native formula-list diff.</summary>
        public static void ReadProduction(byte[] body, ProductionState state)
        {
            var reader = new AuxState(body);
            Flags fields = reader.Mask(21);
            for (int field = 0; field < 21; field++)
            {
                if (fields.Default[field])
                {
                    if (field == 5 || field == 6 || field == 7 || field == 10 || field == 11 || field == 12)
                    {
                        if (reader.Byte() != 5) throw new InvalidDataException("Invalid production AUX reset.");
                        if (field == 11) state.Formulas = new ProductionFormula[500];
                    }
                    continue;
                }
                if (!fields.Present[field]) continue;
                switch (field)
                {
                    case 0: reader.Text(); break;
                    case 1: state.Mode = reader.UInt(); break;
                    case 2: state.Validity = reader.UInt(); break;
                    case 3: state.Failure = reader.Text(); break;
                    case 4: reader.UInt(); break;
                    case 5: reader.ReadSlots(state.Target, 10); break;
                    case 6: reader.ReadSlots(state.Components, 10); break;
                    case 7: reader.SkipSlots(1, 10); break;
                    case 8: state.Cost = reader.UInt() | ((ulong)reader.UInt() << 32); break;
                    case 9: state.BaseCost = reader.UInt() | ((ulong)reader.UInt() << 32); break;
                    case 10: reader.SkipProductionCategories(); break;
                    case 11: reader.ReadProductionFormulas(state); break;
                    case 13: state.Category = reader.UInt(); break;
                    case 14: state.Chance = reader.Float(); break;
                    case 15: case 16: case 17: case 18: reader.Float(); break;
                    case 19: state.Additional = reader.UInt(); break;
                    case 20: state.Filter = reader.UInt(); break;
                    default: throw new InvalidDataException("Unknown production AUX field.");
                }
            }
            if (state.Mode > 4 || state.Filter > 0x1ff || state.Chance < 0 || state.Chance > 1)
                throw new InvalidDataException("Invalid production window state.");
        }

        /// <summary>Consume native primary, category, and subcategory records.</summary>
        private void SkipProductionCategories()
        {
            Flags primary = Mask(2);
            for (int root = 0; root < 2; root++)
            {
                if (!primary.Present[root])
                { if (primary.Default[root] && Byte() != 5) throw new InvalidDataException("Invalid production category reset."); continue; }
                Flags group = Mask(2);
                if (group.Present[0]) Text();
                if (!group.Present[1]) continue;
                Flags categories = Mask(5);
                for (int category = 0; category < 5; category++)
                {
                    if (!categories.Present[category])
                    { if (categories.Default[category] && Byte() != 5) throw new InvalidDataException("Invalid production category slot."); continue; }
                    Flags entry = Mask(3);
                    if (entry.Present[0]) Text();
                    if (entry.Present[1])
                    {
                        Flags subs = Mask(5);
                        for (int sub = 0; sub < 5; sub++)
                        {
                            if (!subs.Present[sub])
                            { if (subs.Default[sub] && Byte() != 5) throw new InvalidDataException("Invalid production subcategory slot."); continue; }
                            Flags detail = Mask(3);
                            if (detail.Present[0]) Text();
                            if (detail.Present[1]) UInt();
                            if (detail.Present[2]) Byte();
                        }
                    }
                    if (entry.Present[2]) UInt();
                }
            }
        }

        /// <summary>Merge changed formula slots and clear removed rows.</summary>
        private void ReadProductionFormulas(ProductionState state)
        {
            Flags slots = Mask(500);
            for (int slot = 0; slot < state.Formulas.Length; slot++)
            {
                if (slots.Default[slot])
                { if (Byte() != 5) throw new InvalidDataException("Invalid production formula reset."); state.Formulas[slot] = null; continue; }
                if (!slots.Present[slot]) continue;
                Flags entry = Mask(3);
                if (!entry.Present[0] && !entry.Present[1] && !entry.Present[2])
                { state.Formulas[slot] = null; continue; }
                string name = state.Formulas[slot]?.Name ?? "";
                uint template = state.Formulas[slot]?.Template ?? 0;
                uint level = state.Formulas[slot]?.Level ?? 0;
                if (entry.Present[0]) name = Text();
                if (entry.Present[1]) template = UInt();
                if (entry.Present[2]) level = UInt();
                state.Formulas[slot] = template == 0 ? null : new ProductionFormula(template, level, name);
            }
        }
    }
}
