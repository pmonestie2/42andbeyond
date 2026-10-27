using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Object = UnityEngine.Object;

namespace FortyTwoAndBeyond.Client
{
    /// <summary>Show the original holographic Menace skull on an affected mob.</summary>
    public sealed class NativeMenaceVisual
    {
        private readonly GameObject model;
        private readonly AnimationClip clip;
        private readonly List<Material> materials = new List<Material>();
        private readonly float startedAt;

        /// <returns>bool: Whether the original hologram geometry was loaded.</returns>
        public bool Active => model != null;

        /// <summary>Load the native skull and its two archived texture stages.</summary>
        public NativeMenaceVisual(Transform parent, ushort descriptor)
        {
            string path = "LegacyEffects/MenaceAnimated";
            GameObject asset = Resources.Load<GameObject>(path);
            clip = Resources.LoadAll<AnimationClip>(path)
                .FirstOrDefault(candidate => candidate.name == "Scene");
            Shader shader = Resources.Load<Shader>("LegacyEffect");
            if (shader == null) shader = Shader.Find("42 and Beyond/LegacyEffect");
            Texture2D skull = Resources.Load<Texture2D>("LegacyEffects/sk_menace01");
            Texture2D glow = Resources.Load<Texture2D>("LegacyEffects/sk_menace02");
            if (parent == null || asset == null || shader == null || skull == null || glow == null)
            {
                Debug.LogError("Original Menace hologram or texture is missing.");
                return;
            }
            model = Object.Instantiate(asset, parent);
            model.name = "Original Menace hologram";
            Animator animator = model.GetComponent<Animator>();
            if (animator == null) animator = model.AddComponent<Animator>();
            animator.applyRootMotion = false;
            if (clip != null) clip.SampleAnimation(model, 0);
            foreach (MeshRenderer renderer in model.GetComponentsInChildren<MeshRenderer>(true))
            {
                var material = new Material(shader);
                material.SetTexture("_MainTex", skull);
                material.SetTexture("_DetailTex", glow);
                material.SetFloat("_UseSourceColor", 1f);
                material.SetColor("_Tint", descriptor == 199 ?
                    new Color(1f, .35f, .43f, .8f) : new Color(1f, .72f, .63f, .72f));
                renderer.sharedMaterial = material;
                materials.Add(material);
            }
            Bounds bounds = new Bounds(model.transform.position, Vector3.zero);
            foreach (MeshRenderer renderer in model.GetComponentsInChildren<MeshRenderer>(true))
                bounds.Encapsulate(renderer.bounds);
            if (bounds.size.magnitude > .01f)
                model.transform.localScale *= 1.35f /
                    Mathf.Max(bounds.size.x, bounds.size.y, bounds.size.z);
            startedAt = Time.unscaledTime;
        }

        /// <summary>Keep the hologram above its authoritative target.</summary>
        public void Tick(Vector3 target, Camera camera)
        {
            if (model == null) return;
            float age = Time.unscaledTime - startedAt;
            if (clip != null && clip.length > .01f)
                clip.SampleAnimation(model, Mathf.Repeat(age, clip.length));
            model.transform.position = target + Vector3.up * (1.2f + .12f * Mathf.Sin(age * 3f));
            if (camera != null)
                model.transform.rotation = Quaternion.LookRotation(
                    model.transform.position - camera.transform.position, Vector3.up);
        }

        /// <summary>Hide the skull while its target position is unknown.</summary>
        public void Hide()
        {
            if (model != null) model.SetActive(false);
        }

        /// <summary>Restore the skull when the target position returns.</summary>
        public void Show()
        {
            if (model != null) model.SetActive(true);
        }

        /// <summary>Release the imported hologram and scene-owned materials.</summary>
        public void Dispose()
        {
            if (model != null) Object.Destroy(model);
            foreach (Material material in materials)
                if (material != null) Object.Destroy(material);
            materials.Clear();
        }
    }
}
