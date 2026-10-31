using UnityEngine;
using Object = UnityEngine.Object;

namespace FortyTwoAndBeyond.Client
{
    /// <summary>A turbulent plasma aperture and recessed spiral throat, matching the approved Blender design.</summary>
    public sealed class NativeWormholeVisual
    {
        private readonly GameObject root;
        private readonly Mesh rim, throat;
        private readonly Material rimMaterial, throatMaterial;
        public bool Active => root != null;

        /// <summary>Create reusable portal geometry without legacy cast sprites.</summary>
        public NativeWormholeVisual(Transform parent)
        {
            Shader shader = Resources.Load<Shader>("WormholePlasma");
            if (shader == null) { Debug.LogError("Wormhole plasma shader missing."); return; }
            root = new GameObject("Wormhole plasma aperture");
            root.transform.SetParent(parent, false);
            root.SetActive(false);
            rimMaterial = new Material(shader); throatMaterial = new Material(shader);
            throatMaterial.SetFloat("_Mode", 1);
            rim = BuildSurface(false); throat = BuildSurface(true);
            AddSurface("Satin turbulent plasma rim", rim, rimMaterial);
            AddSurface("Recessed four-start spiral throat", throat, throatMaterial);
        }

        /// <summary>Attach a portal surface to its owned aperture.</summary>
        private void AddSurface(string name, Mesh mesh, Material material)
        {
            var part = new GameObject(name, typeof(MeshFilter), typeof(MeshRenderer));
            part.transform.SetParent(root.transform, false);
            part.GetComponent<MeshFilter>().sharedMesh = mesh;
            part.GetComponent<MeshRenderer>().sharedMaterial = material;
        }

        /// <summary>Generate an irregular toroidal rim or tapered seven-meter throat.</summary>
        /// <returns>Mesh: Owned UV-mapped portal surface.</returns>
        private static Mesh BuildSurface(bool tunnel)
        {
            const int around = 256, across = 64;
            var vertices = new Vector3[(around + 1) * (across + 1)];
            var uv = new Vector2[vertices.Length];
            var triangles = new int[around * across * 6];
            for (int a = 0; a <= around; a++)
                for (int b = 0; b <= across; b++)
                {
                    float u = a / (float)around, v = b / (float)across;
                    float angle = u * Mathf.PI * 2, cross = v * Mathf.PI * 2;
                    float thickness = .16f + .065f * Mathf.Pow(Mathf.Sin(angle * 7 + .6f), 2);
                    float radius = tunnel ? Mathf.Lerp(3.3f, .18f, v) :
                        3.3f + thickness * Mathf.Cos(cross) + .04f * Mathf.Sin(angle * 11);
                    int index = a * (across + 1) + b;
                    vertices[index] = new Vector3(Mathf.Cos(angle) * radius,
                        Mathf.Sin(angle) * radius, tunnel ? v * 7 : thickness * Mathf.Sin(cross));
                    uv[index] = new Vector2(u, v);
                    if (a == around || b == across) continue;
                    int offset = (a * across + b) * 6, next = index + across + 1;
                    triangles[offset] = index; triangles[offset + 1] = next; triangles[offset + 2] = index + 1;
                    triangles[offset + 3] = index + 1; triangles[offset + 4] = next; triangles[offset + 5] = next + 1;
                }
            var mesh = new Mesh { name = tunnel ? "Wormhole tapered throat" : "Wormhole irregular torus",
                vertices = vertices, uv = uv, triangles = triangles };
            mesh.RecalculateNormals(); mesh.RecalculateBounds();
            Bounds bounds = mesh.bounds; bounds.Expand(.2f); mesh.bounds = bounds; return mesh;
        }

        /// <summary>Keep a stand-alone cast preview ahead of the pilot.</summary>
        public void Tick(Vector3 ship, Quaternion heading)
        { SetPose(ship + heading * Vector3.forward * 5, heading, 1); }

        /// <summary>Place and open a departure or arrival mouth in render space.</summary>
        public void SetPose(Vector3 position, Quaternion heading, float opening)
        {
            if (root == null) return;
            root.SetActive(true);
            root.transform.SetPositionAndRotation(position, heading);
            root.transform.localScale = Vector3.one * Mathf.Max(.001f, opening);
            rimMaterial.SetFloat("_Open", opening); throatMaterial.SetFloat("_Open", opening);
        }

        /// <summary>Hide a mouth while the chart or loading view owns the camera.</summary>
        public void SetVisible(bool visible) { if (root != null) root.SetActive(visible); }

        /// <summary>Release all meshes, materials and scene objects owned by this mouth.</summary>
        public void Dispose()
        {
            if (root != null) Object.Destroy(root);
            if (rim != null) Object.Destroy(rim); if (throat != null) Object.Destroy(throat);
            if (rimMaterial != null) Object.Destroy(rimMaterial);
            if (throatMaterial != null) Object.Destroy(throatMaterial);
        }
    }
}
