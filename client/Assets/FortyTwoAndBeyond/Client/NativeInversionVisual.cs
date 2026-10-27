using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Object = UnityEngine.Object;

namespace FortyTwoAndBeyond.Client
{
    /// <summary>Animate the original Shield Inversion lightning at its target.</summary>
    public sealed class NativeInversionVisual
    {
        private readonly GameObject root;
        private readonly GameObject model;
        private readonly AnimationClip clip;
        private readonly List<Material> materials = new List<Material>();
        private readonly float startedAt;

        /// <returns>bool: Whether the original W3D lightning rig was attached.</returns>
        public bool Active => model != null;

        /// <summary>Load the native sustained Shield Inversion phase and texture stages.</summary>
        public NativeInversionVisual(Transform parent)
        {
            string path = "LegacyEffects/ShieldInversionAnimated";
            GameObject asset = Resources.Load<GameObject>(path);
            clip = Resources.LoadAll<AnimationClip>(path)
                .FirstOrDefault(candidate => candidate.name == "Scene");
            Shader shader = Resources.Load<Shader>("LegacyEffect");
            if (shader == null) shader = Shader.Find("42 and Beyond/LegacyEffect");
            Texture2D lightning = Resources.Load<Texture2D>("LegacyEffects/shld_inv06");
            Texture2D noise = Resources.Load<Texture2D>("LegacyEffects/shld_inv01");
            if (parent == null || asset == null || clip == null || shader == null ||
                lightning == null || noise == null)
            {
                Debug.LogError("Original Shield Inversion rig or texture is missing.");
                return;
            }
            root = new GameObject("Shield Inversion target impact");
            root.transform.SetParent(parent, false);
            model = Object.Instantiate(asset, root.transform);
            model.name = "Original Shield Inversion lightning";
            Animator animator = model.GetComponent<Animator>();
            if (animator == null) animator = model.AddComponent<Animator>();
            animator.applyRootMotion = false;
            clip.SampleAnimation(model, 0);
            foreach (MeshRenderer renderer in model.GetComponentsInChildren<MeshRenderer>(true))
            {
                var material = new Material(shader);
                material.SetTexture("_MainTex", lightning);
                material.SetTexture("_DetailTex", noise);
                material.SetFloat("_UseSourceColor", 1f);
                material.SetColor("_Tint", new Color(.56f, .78f, 1f, .48f));
                renderer.sharedMaterial = material;
                materials.Add(material);
            }
            Bounds bounds = new Bounds(model.transform.position, Vector3.zero);
            foreach (MeshRenderer renderer in model.GetComponentsInChildren<MeshRenderer>(true))
                bounds.Encapsulate(renderer.bounds);
            if (bounds.size.magnitude > .01f)
                root.transform.localScale *= .45f /
                    Mathf.Max(bounds.size.x, bounds.size.y, bounds.size.z);
            startedAt = Time.unscaledTime;
        }

        /// <summary>Place the native lightning at the confirmed impact target.</summary>
        public void Tick(Vector3 source, Vector3 target)
        {
            if (model == null || clip == null) return;
            clip.SampleAnimation(model, Mathf.Repeat(Time.unscaledTime - startedAt, clip.length));
            root.transform.position = target;
            Vector3 direction = target - source;
            if (direction.sqrMagnitude > .01f)
                root.transform.rotation = Quaternion.LookRotation(direction, Vector3.up);
        }

        /// <summary>Release the W3D rig and its scene-owned materials.</summary>
        public void Dispose()
        {
            if (root != null) Object.Destroy(root);
            foreach (Material material in materials)
                if (material != null) Object.Destroy(material);
            materials.Clear();
        }
    }
}
