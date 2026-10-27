using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Object = UnityEngine.Object;

namespace FortyTwoAndBeyond.Client
{
    /// <summary>Play the original Rally W3D meshes and loop clips on a ship.</summary>
    public sealed class NativeRallyVisual
    {
        private readonly GameObject[] models = new GameObject[2];
        private readonly AnimationClip[] clips = new AnimationClip[2];
        private readonly List<Material> materials = new List<Material>();
        private readonly float startedAt;

        /// <returns>bool: Whether at least one original Rally rig was imported and attached.</returns>
        public bool Active { get; private set; }

        /// <summary>Attach the native Rally wheel beneath the ship.</summary>
        public NativeRallyVisual(Transform ship, ushort descriptor)
            : this(ship, descriptor, RallyHullFrame.Measure(ship)) { }

        /// <summary>Size and place the original Rally wheel against visible hull geometry.</summary>
        public NativeRallyVisual(Transform ship, ushort descriptor, RallyHullFrame hull)
        {
            startedAt = Time.unscaledTime;
            Shader shader = Resources.Load<Shader>("LegacyEffect");
            if (shader == null) shader = Shader.Find("42 and Beyond/LegacyEffect");
            if (ship == null || shader == null)
            { Debug.LogError("Original Rally ship or shader is missing."); return; }
            Attach(1, "RallyFiringAnimated", ship, shader, hull);
        }

        /// <summary>Instantiate and texture one original W3D effect rig.</summary>
        private void Attach(int slot, string name, Transform ship, Shader shader,
            RallyHullFrame hull)
        {
            string path = "LegacyEffects/" + name;
            GameObject asset = Resources.Load<GameObject>(path);
            AnimationClip clip = Resources.LoadAll<AnimationClip>(path)
                .FirstOrDefault(candidate => candidate.name == "Scene");
            if (asset == null || clip == null)
            { Debug.LogError("Original Rally model or clip is missing: " + path); return; }
            GameObject model = Object.Instantiate(asset, ship);
            model.name = "Original " + name;
            float shipScale = Mathf.Max(.001f, Mathf.Abs(ship.lossyScale.x));
            model.transform.localPosition = Vector3.zero;
            model.transform.localRotation = Quaternion.Euler(0, 90, 0) *
                model.transform.localRotation;
            model.transform.localScale = Vector3.one * (1.4f / shipScale);
            Animator animator = model.GetComponent<Animator>();
            if (animator == null) animator = model.AddComponent<Animator>();
            animator.applyRootMotion = false;
            clip.SampleAnimation(model, 0);
            MeshRenderer[] renderers = model.GetComponentsInChildren<MeshRenderer>(true);
            foreach (MeshRenderer renderer in renderers)
            {
                var textures = Textures(slot, renderer.name);
                Texture2D first = Resources.Load<Texture2D>("LegacyEffects/" + textures.Item1);
                Texture2D second = Resources.Load<Texture2D>("LegacyEffects/" + textures.Item2);
                if (first == null || second == null)
                    Debug.LogError("Original Rally texture missing for " + renderer.name);
                Material material = new Material(shader);
                material.SetTexture("_MainTex", first);
                material.SetTexture("_DetailTex", second);
                bool wheel = renderer.name.StartsWith("WHEEL");
                bool grid = renderer.name.StartsWith("GRAPH");
                material.SetColor("_Tint", wheel ?
                    new Color(1f, .68f, .24f, .55f) : grid ?
                    new Color(.7f, 2f, 2f, .8f) :
                    new Color(1.5f, 1.5f, 1.5f, .85f));
                material.SetFloat("_UseSourceColor", wheel ? 0f : 1f);
                renderer.sharedMaterial = material;
                materials.Add(material);
            }
            if (renderers.Length > 0)
            {
                MeshRenderer[] visible = renderers.Where(renderer =>
                    slot != 1 || renderer.name.StartsWith("WHEEL")).ToArray();
                Bounds bounds = visible[0].bounds;
                for (int index = 1; index < visible.Length; index++)
                    bounds.Encapsulate(visible[index].bounds);
                float width = slot == 0 ? hull.HalfWidth * 1.6f : hull.HalfWidth * 3.5f;
                model.transform.localScale *= width / Mathf.Max(.01f, bounds.size.x);
                bounds = visible[0].bounds;
                for (int index = 1; index < visible.Length; index++)
                    bounds.Encapsulate(visible[index].bounds);
                Vector3 center = ship.position + ship.right * hull.CenterX +
                    ship.forward * hull.CenterZ +
                    ship.up * (hull.BottomY - .35f);
                model.transform.position += center - bounds.center;
            }
            models[slot] = model;
            clips[slot] = clip;
            Active = true;
        }

        /// <returns>System.Tuple&lt;string, string&gt;: Original first and second texture stages.</returns>
        private static System.Tuple<string, string> Textures(int slot, string mesh)
        {
            if (slot == 0)
            {
                if (mesh.StartsWith("TARGET")) return System.Tuple.Create("sk_rally11", "sk_rally12");
                if (mesh.StartsWith("GRAPH")) return System.Tuple.Create("sk_rally07", "sk_rally13");
                return System.Tuple.Create("sk_rally09", "sk_rally10");
            }
            if (mesh.StartsWith("WHEEL")) return System.Tuple.Create("sk_rally21", "sk_rally19");
            if (mesh.StartsWith("TARGET")) return System.Tuple.Create("sk_rally18", "sk_rally19");
            if (mesh.StartsWith("GRAPH")) return System.Tuple.Create("sk_rally16", "sk_rally20");
            return System.Tuple.Create("sk_rally17", "sk_rally10");
        }

        /// <summary>Sample the original looping wheel while the server buff is active.</summary>
        public void Tick()
        {
            if (!Active) return;
            float age = Time.unscaledTime - startedAt;
            for (int index = 0; index < models.Length; index++)
                if (models[index] != null && clips[index] != null)
                    clips[index].SampleAnimation(models[index], Mathf.Repeat(age, clips[index].length));
        }

        /// <summary>Release imported effect instances and scene-owned materials.</summary>
        public void Dispose()
        {
            foreach (GameObject model in models)
                if (model != null) Object.Destroy(model);
            foreach (Material material in materials)
                if (material != null) Object.Destroy(material);
            materials.Clear();
            Active = false;
        }
    }
}
