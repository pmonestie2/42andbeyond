using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Object = UnityEngine.Object;

namespace FortyTwoAndBeyond.Client
{
    /// <summary>Display original animated Environment, Psionic, or Recharge Shield rigs.</summary>
    public sealed class NativeSkillShieldVisual
    {
        private readonly GameObject model;
        private readonly AnimationClip clip;
        private readonly List<Material> materials = new List<Material>();
        private readonly float startedAt;

        /// <returns>bool: Whether the original animated sphere was attached.</returns>
        public bool Active => model != null;

        /// <summary>Attach one original shield rig and its archived texture stages.</summary>
        public NativeSkillShieldVisual(Transform ship, ushort descriptor)
        {
            bool psionic = descriptor == 214;
            bool recharge = descriptor == 136;
            string name = psionic ? "PsionicShieldAnimated" :
                recharge ? "ShieldRechargeAnimated" : "EnvironmentShieldLoopAnimated";
            GameObject asset = Resources.Load<GameObject>("LegacyEffects/" + name);
            clip = Resources.LoadAll<AnimationClip>("LegacyEffects/" + name)
                .FirstOrDefault(candidate => candidate.name == "Scene");
            Shader shader = Resources.Load<Shader>("LegacyEffect");
            if (shader == null) shader = Shader.Find("42 and Beyond/LegacyEffect");
            Texture2D surface = Resources.Load<Texture2D>("LegacyEffects/" +
                (psionic || recharge ? "shld_reg02" : "shld_env01"));
            Texture2D tunnel = Resources.Load<Texture2D>("LegacyEffects/" +
                (psionic ? "GRAD" : recharge ? "shld_reg04" : "JVE0108"));
            Texture2D psionicShell = psionic ?
                Resources.Load<Texture2D>("LegacyEffects/sk_psysh05") : null;
            Texture2D psionicDetail = psionic ?
                Resources.Load<Texture2D>("LegacyEffects/sk_psysh06") : null;
            Texture2D rechargeShell = recharge ?
                Resources.Load<Texture2D>("LegacyEffects/shldtype07") : null;
            if (ship == null || asset == null || clip == null || shader == null ||
                surface == null || tunnel == null ||
                psionic && (psionicShell == null || psionicDetail == null) ||
                recharge && rechargeShell == null)
            {
                Debug.LogError("Original " + name + " rig or texture is missing.");
                return;
            }
            model = Object.Instantiate(asset, ship);
            model.name = "Original " + name;
            float shipScale = Mathf.Max(.001f, Mathf.Abs(ship.lossyScale.x));
            model.transform.localPosition = Vector3.zero;
            model.transform.localScale = Vector3.one / shipScale;
            Animator animator = model.GetComponent<Animator>();
            if (animator == null) animator = model.AddComponent<Animator>();
            animator.applyRootMotion = false;
            clip.SampleAnimation(model, 0);
            foreach (MeshRenderer renderer in model.GetComponentsInChildren<MeshRenderer>(true))
            {
                Material material = new Material(shader);
                bool shell = psionic && renderer.name.StartsWith("HEDRA");
                bool rechargeRing = recharge && renderer.name.StartsWith("GEOSPHERE");
                material.SetTexture("_MainTex", shell ? psionicShell :
                    rechargeRing ? rechargeShell : surface);
                material.SetTexture("_DetailTex", shell ? psionicDetail : tunnel);
                material.SetFloat("_UseSourceColor", 1f);
                material.SetColor("_Tint", psionic ?
                    new Color(.5f, 1f, .76f, .72f) : recharge ?
                    new Color(.75f, .93f, 1f, .82f) : new Color(.6f, .9f, 1f, .7f));
                renderer.sharedMaterial = material;
                materials.Add(material);
            }
            Bounds bounds = new Bounds(ship.position, Vector3.zero);
            foreach (MeshRenderer renderer in model.GetComponentsInChildren<MeshRenderer>(true))
                bounds.Encapsulate(renderer.bounds);
            if (bounds.size.magnitude > .01f)
                model.transform.localScale *= 3.4f /
                    Mathf.Max(bounds.size.x, bounds.size.y, bounds.size.z);
            startedAt = Time.unscaledTime;
        }

        /// <summary>Loop the original W3D animation while the buff is active.</summary>
        public void Tick()
        {
            if (model != null && clip != null)
                clip.SampleAnimation(model,
                    Mathf.Repeat(Time.unscaledTime - startedAt, clip.length));
        }

        /// <summary>Animate an original shield at a server-owned target position.</summary>
        public void TickAt(Vector3 target)
        {
            Tick();
            if (model != null) model.transform.position = target;
        }

        /// <summary>Release the imported sphere and scene-owned materials.</summary>
        public void Dispose()
        {
            if (model != null) Object.Destroy(model);
            foreach (Material material in materials)
                if (material != null) Object.Destroy(material);
            materials.Clear();
        }
    }
}
