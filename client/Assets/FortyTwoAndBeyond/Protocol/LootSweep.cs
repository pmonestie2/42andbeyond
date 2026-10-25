using System;
using System.Diagnostics;
using System.Linq;

namespace FortyTwoAndBeyond.Protocol
{
    /// <summary>Sequence one wreck-slot tractor at a time from authoritative replies.</summary>
    public sealed class LootSweep
    {
        private int pendingSlot = -1;
        private uint pendingTemplate, pendingQuantity;
        private long requestedAt, tractorSeenAt;
        private bool sawTractor;
        public uint CorpseId { get; private set; }
        public int CollectedStacks { get; private set; }
        public string Status { get; private set; } = "";
        public bool Active => CorpseId != 0;

        /// <summary>
        /// Returns:
        ///     int?: The first advertised wreck slot to request, or null when collection cannot start.
        /// </summary>
        public int? Begin(uint corpseId, uint openCorpseId, uint selectedTargetId,
            CorpseLoot contents, LootActivity tractor, long now)
        {
            Cancel("");
            if (corpseId == 0 || openCorpseId != corpseId || selectedTargetId != corpseId ||
                contents?.Id != corpseId || tractor != null) return null;
            CorpseId = corpseId;
            return RequestNext(contents, now);
        }

        /// <summary>
        /// Returns:
        ///     int?: A new slot only after the previous cast ended and wreck stock changed.
        /// </summary>
        public int? Observe(uint openCorpseId, uint selectedTargetId, CorpseLoot contents,
            LootActivity tractor, bool traveling, long now)
        {
            if (!Active) return null;
            if (openCorpseId != CorpseId || selectedTargetId != CorpseId || traveling ||
                contents?.Id != CorpseId || tractor != null && tractor.CorpseId != CorpseId)
            { Cancel("Collect All stopped: wreck selection changed."); return null; }
            if (pendingSlot < 0) return RequestNext(contents, now);
            if (tractor != null)
            {
                sawTractor = true; tractorSeenAt = now;
                Status = $"TRACTORING STACK {CollectedStacks + 1}";
                return null;
            }
            ItemView remaining = pendingSlot < contents.Items.Length ? contents.Items[pendingSlot] : null;
            if (sawTractor && (remaining == null || remaining.Template != pendingTemplate ||
                remaining.Quantity < pendingQuantity))
            {
                CollectedStacks++;
                pendingSlot = -1;
                return RequestNext(contents, now);
            }
            long reference = sawTractor ? tractorSeenAt : requestedAt;
            if (now - reference > 4 * Stopwatch.Frequency)
                Cancel("Collect All stopped: server did not confirm the next pickup.");
            return null;
        }

        /// <summary>Stop requesting new stacks; an already accepted tractor remains server-owned.</summary>
        public void Cancel(string reason)
        {
            CorpseId = 0; pendingSlot = -1; pendingTemplate = pendingQuantity = 0;
            requestedAt = tractorSeenAt = 0; sawTractor = false;
            CollectedStacks = 0; Status = reason;
        }

        /// <summary>
        /// Returns:
        ///     int?: The next native wreck slot, or null when the server lists no more stock.
        /// </summary>
        private int? RequestNext(CorpseLoot contents, long now)
        {
            ItemView next = contents.Items.FirstOrDefault(item => item != null);
            if (next == null) { Finish(); return null; }
            pendingSlot = next.Slot;
            pendingTemplate = next.Template;
            pendingQuantity = next.Quantity;
            requestedAt = now; tractorSeenAt = 0; sawTractor = false;
            Status = $"REQUESTING STACK {CollectedStacks + 1}";
            return pendingSlot;
        }

        /// <summary>Record completion without erasing the number of confirmed collected stacks.</summary>
        private void Finish()
        {
            int completed = CollectedStacks;
            Cancel("");
            Status = $"COLLECTED {completed} STACK{(completed == 1 ? "" : "S")}";
        }
    }
}
