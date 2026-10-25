using System;

namespace FortyTwoAndBeyond.Protocol
{
    /// <summary>A requested slot and quantity for a server-validated stack transfer.</summary>
    public readonly struct InventoryTransferPlan
    {
        public readonly int Slot;
        public readonly uint Quantity;
        public readonly bool Merge;

        /// <summary>Keep one candidate transfer request immutable.</summary>
        public InventoryTransferPlan(int slot, uint quantity, bool merge)
        { Slot = slot; Quantity = quantity; Merge = merge; }

        /// <summary>
        /// Returns:
        ///     InventoryTransferPlan?: A full merge, empty slot, or bounded partial merge.
        /// </summary>
        public static InventoryTransferPlan? Choose(ItemState source, ItemState[] destination,
            uint usableSlots, ItemTemplateInfo template)
        {
            if (source?.Present != true || source.Quantity == 0 || destination == null) return null;
            int limit = (int)Math.Min(usableSlots, (uint)destination.Length);
            int emptySlot = -1, partialSlot = -1;
            uint partialRoom = 0;
            for (int slot = 0; slot < limit; slot++)
            {
                ItemState candidate = destination[slot];
                if (candidate?.Present != true)
                {
                    if (emptySlot < 0) emptySlot = slot;
                    continue;
                }
                if (template == null || template.MaxStack <= candidate.Quantity ||
                    source.Template != candidate.Template || source.Quality != candidate.Quality ||
                    source.Structure != candidate.Structure ||
                    !string.Equals(source.BuilderName, candidate.BuilderName, StringComparison.Ordinal))
                    continue;
                uint room = template.MaxStack - candidate.Quantity;
                if (room >= source.Quantity)
                    return new InventoryTransferPlan(slot, source.Quantity, true);
                if (room > partialRoom) { partialRoom = room; partialSlot = slot; }
            }
            if (emptySlot >= 0) return new InventoryTransferPlan(emptySlot, source.Quantity, false);
            if (partialSlot >= 0)
                return new InventoryTransferPlan(partialSlot, partialRoom, true);
            return null;
        }
    }
}
