using UnityEngine;

namespace FortyTwoAndBeyond.Client
{
    /// <summary>Visible ship extents in world units along its own flight axes.</summary>
    public readonly struct RallyHullFrame
    {
        public readonly float CenterX, CenterY, CenterZ;
        public readonly float HalfWidth, HalfLength, BottomY, NoseZ;

        /// <summary>Store a measured visible hull frame.</summary>
        private RallyHullFrame(Vector3 minimum, Vector3 maximum)
        {
            CenterX = (minimum.x + maximum.x) * .5f;
            CenterY = (minimum.y + maximum.y) * .5f;
            CenterZ = (minimum.z + maximum.z) * .5f;
            HalfWidth = Mathf.Clamp((maximum.x - minimum.x) * .5f, .8f, 4f);
            HalfLength = Mathf.Clamp((maximum.z - minimum.z) * .5f, .8f, 6f);
            BottomY = minimum.y;
            NoseZ = maximum.z;
        }

        /// <returns>bool: Whether a mesh belongs to a temporary skill effect.</returns>
        private static bool IsEffect(Transform part, Transform ship)
        {
            for (Transform current = part; current != null && current != ship;
                current = current.parent)
                if (current.name.StartsWith("Original Rally") ||
                    current.name.StartsWith("Original Combat Trance") ||
                    current.name.StartsWith("Original Environment Shield") ||
                    current.name.StartsWith("Original Psionic Shield") ||
                    current.name.StartsWith("Rally ")) return true;
            return false;
        }

        /// <summary>Accumulate a mesh's eight corners in ship-oriented world units.</summary>
        private static void Include(Bounds bounds, Transform part, Vector3 origin,
            Quaternion inverse, ref Vector3 minimum, ref Vector3 maximum)
        {
            for (int x = -1; x <= 1; x += 2)
                for (int y = -1; y <= 1; y += 2)
                    for (int z = -1; z <= 1; z += 2)
                    {
                        Vector3 local = bounds.center + Vector3.Scale(bounds.extents,
                            new Vector3(x, y, z));
                        Vector3 relative = inverse * (part.TransformPoint(local) - origin);
                        minimum = Vector3.Min(minimum, relative);
                        maximum = Vector3.Max(maximum, relative);
                    }
        }

        /// <returns>RallyHullFrame: Visible hull center, floor, nose, and width in world units.</returns>
        public static RallyHullFrame Measure(Transform ship)
        {
            if (ship == null) return new RallyHullFrame(
                new Vector3(-1f, -.8f, -1.5f), new Vector3(1f, .8f, 1.5f));
            Vector3 minimum = new Vector3(float.PositiveInfinity, float.PositiveInfinity,
                float.PositiveInfinity);
            Vector3 maximum = new Vector3(float.NegativeInfinity, float.NegativeInfinity,
                float.NegativeInfinity);
            Quaternion inverse = Quaternion.Inverse(ship.rotation);
            bool found = false;
            foreach (MeshFilter filter in ship.GetComponentsInChildren<MeshFilter>(true))
            {
                MeshRenderer renderer = filter.GetComponent<MeshRenderer>();
                if (filter.sharedMesh == null || renderer == null || !renderer.enabled ||
                    !filter.gameObject.activeInHierarchy || IsEffect(filter.transform, ship)) continue;
                Include(filter.sharedMesh.bounds, filter.transform, ship.position, inverse,
                    ref minimum, ref maximum);
                found = true;
            }
            foreach (SkinnedMeshRenderer renderer in ship.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (renderer.sharedMesh == null || !renderer.enabled ||
                    !renderer.gameObject.activeInHierarchy || IsEffect(renderer.transform, ship))
                    continue;
                Include(renderer.localBounds, renderer.transform, ship.position, inverse,
                    ref minimum, ref maximum);
                found = true;
            }
            if (!found) return new RallyHullFrame(
                new Vector3(-1f, -.8f, -1.5f), new Vector3(1f, .8f, 1.5f));
            return new RallyHullFrame(minimum, maximum);
        }
    }
}
