using System.Collections.Generic;
using System.Linq;

namespace FortyTwoAndBeyond.Protocol
{
    public enum ContactSelectionChange { None, LocalAcknowledged, ServerSelected }

    /// <summary>Reconcile local contact clicks with authoritative ship target updates.</summary>
    public sealed class ContactSelection
    {
        private uint serverTarget;
        private uint pendingTarget;
        private double pendingAt;
        private bool initialized;

        public uint SelectedId { get; private set; }

        /// <summary>Show a clicked contact while its native target request is pending.</summary>
        public void SelectLocal(uint id, double now)
        {
            SelectedId = pendingTarget = id;
            pendingAt = now;
        }

        /// <summary>Clear a contact chosen locally without changing the server target.</summary>
        public void ClearLocal()
        {
            SelectedId = pendingTarget = 0;
        }

        /// <summary>Discard selection history when a new sector or character loads.</summary>
        public void Reset()
        {
            serverTarget = pendingTarget = SelectedId = 0;
            pendingAt = 0;
            initialized = false;
        }

        /// Returns:
        ///     ContactSelectionChange: Whether the update acknowledged a click or selected a contact remotely.
        public ContactSelectionChange Observe(uint target, IReadOnlyCollection<uint> visibleContacts, double now)
        {
            bool visible = target != 0 && visibleContacts.Contains(target);
            if (!initialized)
            {
                initialized = true;
                serverTarget = target;
                if (pendingTarget == 0) SelectedId = visible ? target : 0;
                return ContactSelectionChange.None;
            }
            if (target != serverTarget)
            {
                serverTarget = target;
                bool acknowledged = pendingTarget == target;
                pendingTarget = 0;
                SelectedId = visible ? target : 0;
                return acknowledged ? ContactSelectionChange.LocalAcknowledged :
                    ContactSelectionChange.ServerSelected;
            }
            if (pendingTarget != 0 && now - pendingAt >= 3)
            {
                pendingTarget = 0;
                SelectedId = visible ? target : 0;
            }
            else if (SelectedId != 0 && !visibleContacts.Contains(SelectedId))
                ClearLocal();
            return ContactSelectionChange.None;
        }
    }
}
