using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace FortyTwoAndBeyond.Client
{
    /// <summary>Fade only hull surfaces into the approved faint ghost, retaining original materials.</summary>
    public sealed class ShipCloakVisual : IDisposable
    {
        private sealed class Surface
        {
            public Renderer Renderer;
            public Material[] Original, Ghost;
            public ShadowCastingMode Shadows;
        }

        private readonly Transform hull;
        private readonly List<Surface> surfaces = new List<Surface>();
        private readonly List<Material> materials = new List<Material>();
        private readonly MaterialPropertyBlock properties = new MaterialPropertyBlock();
        private float amount;
        public float Amount => amount;
        public const float GhostOpacity = .18f;

        /// <summary>Bind to an existing ship without changing its opaque appearance.</summary>
        public ShipCloakVisual(Transform hull) { this.hull = hull; }

        /// <summary>Follow authoritative cloak state; the post-cloak damage buff does not hide the hull.</summary>
        public void Tick(bool cloaked, float deltaTime)
        {
            float next = Mathf.MoveTowards(amount, cloaked ? 1 : 0, Mathf.Max(0, deltaTime) / .85f);
            if (next == 0 && amount == 0) return;
            if (surfaces.Count == 0 && !Capture()) return;
            amount = next;
            foreach (Surface surface in surfaces)
            {
                if (surface.Renderer == null) continue;
                surface.Renderer.GetPropertyBlock(properties);
                properties.SetFloat("_CloakAmount", amount);
                surface.Renderer.SetPropertyBlock(properties);
            }
            if (amount == 0) ResetHull();
        }

        /// Returns:
        ///     bool: Whether the ship has supported hull surfaces and the packaged cloak shader.
        private bool Capture()
        {
            Shader shader = Resources.Load<Shader>("ShipCloak");
            if (shader == null || hull == null) return false;
            foreach (Renderer renderer in hull.GetComponentsInChildren<Renderer>(true))
            {
                if (!(renderer is MeshRenderer) && !(renderer is SkinnedMeshRenderer)) continue;
                Material[] original = renderer.sharedMaterials;
                var ghost = (Material[])original.Clone();
                bool changed = false;
                for (int index = 0; index < original.Length; index++)
                {
                    Material source = original[index];
                    if (source == null || source.shader == null) continue;
                    string name = source.shader.name;
                    if (name != "42 and Beyond/LegacyShip" && name != "42 and Beyond/TexturedShip" &&
                        name != "42 and Beyond/StylizedShip" && name != "42 and Beyond/EveShip" &&
                        name != "42 and Beyond/Surface") continue;
                    var material = new Material(shader) { name = source.name + " cloaked" };
                    if (source.HasProperty("_MainTex"))
                    {
                        material.SetTexture("_MainTex", source.GetTexture("_MainTex"));
                        material.SetTextureScale("_MainTex", source.GetTextureScale("_MainTex"));
                        material.SetTextureOffset("_MainTex", source.GetTextureOffset("_MainTex"));
                    }
                    material.SetColor("_Color", source.HasProperty("_Tint") ? source.GetColor("_Tint") :
                        source.HasProperty("_Color") ? source.GetColor("_Color") : Color.white);
                    material.SetFloat("_GhostOpacity", GhostOpacity);
                    material.SetFloat("_Legacy", name == "42 and Beyond/LegacyShip" ? 1 : 0);
                    material.SetFloat("_UseAlpha", source.HasProperty("_UseAlpha") ? source.GetFloat("_UseAlpha") : 0);
                    material.SetFloat("_Lift", source.HasProperty("_Lift") ? source.GetFloat("_Lift") : 1);
                    ghost[index] = material; materials.Add(material); changed = true;
                }
                if (!changed) continue;
                surfaces.Add(new Surface { Renderer = renderer, Original = original, Ghost = ghost,
                    Shadows = renderer.shadowCastingMode });
                renderer.sharedMaterials = ghost;
                renderer.shadowCastingMode = ShadowCastingMode.Off;
            }
            return surfaces.Count > 0;
        }

        /// <summary>Restore original surfaces before hull replacement, disconnect, or destruction.</summary>
        public void ResetHull()
        {
            foreach (Surface surface in surfaces)
            {
                if (surface.Renderer == null) continue;
                surface.Renderer.sharedMaterials = surface.Original;
                surface.Renderer.shadowCastingMode = surface.Shadows;
                surface.Renderer.GetPropertyBlock(properties);
                properties.SetFloat("_CloakAmount", 0);
                surface.Renderer.SetPropertyBlock(properties);
            }
            foreach (Material material in materials) UnityEngine.Object.Destroy(material);
            surfaces.Clear(); materials.Clear(); amount = 0;
        }

        /// <summary>Release per-ship ghost surfaces without touching shared art materials.</summary>
        public void Dispose() { ResetHull(); }
    }
}
