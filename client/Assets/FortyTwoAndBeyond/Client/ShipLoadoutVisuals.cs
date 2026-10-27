using System;
using System.Collections.Generic;
using FortyTwoAndBeyond.Protocol;
using UnityEngine;
using Object = UnityEngine.Object;

namespace FortyTwoAndBeyond.Client
{
    /// <summary>Keep invisible firing points attached to installed weapon slots.</summary>
    public sealed class ShipLoadoutVisuals : IDisposable
    {
        private readonly Transform ship;
        private readonly List<GameObject> firingPoints = new List<GameObject>();
        private readonly List<Transform> muzzles = new List<Transform>();
        private uint[] templates = Array.Empty<uint>();
        private PwWarpArms currentRig;

        /// <summary>Track firing points beneath the rendered ship.</summary>
        public ShipLoadoutVisuals(Transform ship)
        { this.ship = ship; }

        /// <summary>Refresh firing points when the server changes installed weapons.</summary>
        public void Apply(Snapshot snapshot)
        {
            ItemView[] mounted = snapshot.Mounted;
            PwWarpArms availableRig = ship.GetComponentInChildren<PwWarpArms>();
            if (templates.Length == mounted.Length && currentRig == availableRig)
            {
                bool same = true;
                for (int slot = 0; slot < mounted.Length; slot++)
                    if (templates[slot] != (mounted[slot]?.Template ?? 0))
                    { same = false; break; }
                if (same) return;
            }
            Clear();
            currentRig = availableRig;
            templates = new uint[mounted.Length];
            for (int slot = 0; slot < mounted.Length; slot++)
            {
                ItemView item = mounted[slot];
                if (item == null) continue;
                templates[slot] = item.Template;
                if (slot >= 3 && slot < 3 + snapshot.WeaponMounts)
                    AddFiringPoint(slot - 3);
            }
        }

        /// <summary>Attach an invisible muzzle to the ship or its animated original arm.</summary>
        private void AddFiringPoint(int mount)
        {
            Transform parent = currentRig != null ? currentRig.WeaponSocket(mount) : ship;
            Vector3 position = currentRig != null ?
                new Vector3((mount / 2 - 1) * .23f, 0, .47f) :
                new Vector3((mount - 2.5f) * .37f, .54f, 1.24f - (mount / 2) * .24f);
            var point = new GameObject("Installed weapon " + (mount + 1) + " firing point");
            point.transform.SetParent(parent, false);
            point.transform.localPosition = position;
            muzzles.Add(point.transform);
            firingPoints.Add(point);
        }

        /// <returns>bool: Whether an installed weapon supplied an invisible firing point.</returns>
        public bool TryMuzzle(long sequence, out Vector3 point)
        {
            if (muzzles.Count > 0)
            {
                Transform muzzle = muzzles[(int)((ulong)sequence % (ulong)muzzles.Count)];
                if (muzzle != null) { point = muzzle.position; return true; }
            }
            point = default;
            return false;
        }

        /// <summary>Remove firing points for the previous server loadout.</summary>
        private void Clear()
        {
            foreach (GameObject point in firingPoints)
                if (point != null) Object.Destroy(point);
            firingPoints.Clear();
            muzzles.Clear();
        }

        /// <summary>Forget installed weapons when the world or account changes.</summary>
        public void Reset()
        {
            Clear();
            templates = Array.Empty<uint>();
            currentRig = null;
        }

        /// <summary>Release scene-owned firing points.</summary>
        public void Dispose()
        { Reset(); }
    }
}
