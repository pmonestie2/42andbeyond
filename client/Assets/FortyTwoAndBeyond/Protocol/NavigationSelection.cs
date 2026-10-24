using System;
using System.Collections.Generic;
using System.Linq;

namespace FortyTwoAndBeyond.Protocol
{
    /// <summary>Keep explicit free flight separate from automatic destination selection.</summary>
    public sealed class NavigationSelection
    {
        public uint TargetId { get; private set; }
        public bool FreeFlight { get; private set; }

        /// <summary>Choose a visible navigable destination.</summary>
        public void ChooseTarget(uint id)
        {
            if (id == 0) throw new ArgumentOutOfRangeException(nameof(id));
            TargetId = id;
            FreeFlight = false;
        }

        /// <summary>Leave the route empty for an ahead-of-ship warp.</summary>
        public void ChooseFreeFlight()
        {
            TargetId = 0;
            FreeFlight = true;
        }

        /// <summary>Restore automatic nearest-target selection in a new sector.</summary>
        public void Reset()
        {
            TargetId = 0;
            FreeFlight = false;
        }

        /// Returns:
        ///     uint: The valid selected destination, or zero for explicit free flight or an empty sector.
        public uint Resolve(IReadOnlyCollection<uint> visibleIds, uint nearestId)
        {
            if (TargetId != 0 && !visibleIds.Contains(TargetId)) TargetId = 0;
            if (TargetId == 0 && !FreeFlight && visibleIds.Contains(nearestId)) TargetId = nearestId;
            return TargetId;
        }
    }
}
