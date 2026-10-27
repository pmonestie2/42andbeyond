namespace FortyTwoAndBeyond.Protocol
{
    /// <summary>One requested server-owned move between cargo, mounts, and magazines.</summary>
    public readonly struct InventoryMove
    {
        public readonly uint Source, Destination, Quantity;
        public readonly int SourceSlot, DestinationSlot;

        /// <summary>Keep one transfer request immutable until the worker sends it.</summary>
        public InventoryMove(uint source, int sourceSlot, uint destination,
            int destinationSlot, uint quantity)
        {
            Source = source; SourceSlot = sourceSlot;
            Destination = destination; DestinationSlot = destinationSlot;
            Quantity = quantity;
        }
    }
}
