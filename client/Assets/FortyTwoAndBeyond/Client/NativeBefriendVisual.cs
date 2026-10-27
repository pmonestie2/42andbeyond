using UnityEngine;
using Object = UnityEngine.Object;

namespace FortyTwoAndBeyond.Client
{
    /// <summary>Recreate the original Befriend emitter around its affected mob.</summary>
    public sealed class NativeBefriendVisual
    {
        private readonly GameObject root;
        private readonly Material material;

        /// <returns>bool: Whether the original emitter sprite was attached.</returns>
        public bool Active => root != null;

        /// <summary>Use the W3D emitter's archived one-second life and 40 Hz emission.</summary>
        public NativeBefriendVisual(Transform parent)
        {
            Shader shader = Resources.Load<Shader>("LegacyEffect");
            if (shader == null) shader = Shader.Find("42 and Beyond/LegacyEffect");
            Texture2D sprite = Resources.Load<Texture2D>("LegacyEffects/FANTA01");
            if (parent == null || shader == null || sprite == null)
            {
                Debug.LogError("Original Befriend emitter sprite is missing.");
                return;
            }
            root = new GameObject("Original Befriend sparks", typeof(ParticleSystem));
            root.transform.SetParent(parent, false);
            ParticleSystem particles = root.GetComponent<ParticleSystem>();
            var main = particles.main;
            main.loop = true;
            main.playOnAwake = false;
            main.simulationSpace = ParticleSystemSimulationSpace.Local;
            main.startLifetime = 1f;
            main.startSpeed = .4f;
            main.startSize = .115f;
            main.startColor = new Color(1f, .88f, .52f, .78f);
            main.maxParticles = 64;
            var emission = particles.emission;
            emission.rateOverTime = 40f;
            var shape = particles.shape;
            shape.shapeType = ParticleSystemShapeType.Sphere;
            shape.radius = .85f;
            var renderer = root.GetComponent<ParticleSystemRenderer>();
            renderer.renderMode = ParticleSystemRenderMode.Billboard;
            material = new Material(shader);
            material.SetTexture("_MainTex", sprite);
            material.SetFloat("_UseSourceColor", 1f);
            material.SetColor("_Tint", new Color(1f, .86f, .48f, .75f));
            renderer.sharedMaterial = material;
            particles.Play();
        }

        /// <summary>Place the original sparks on the authoritative target.</summary>
        public void Tick(Vector3 target)
        {
            if (root != null) root.transform.position = target;
        }

        /// <summary>Hide the emitter while target position has not arrived.</summary>
        public void Hide()
        {
            if (root != null) root.SetActive(false);
        }

        /// <summary>Restore emission when the target position returns.</summary>
        public void Show()
        {
            if (root != null) root.SetActive(true);
        }

        /// <summary>Release native sparks and their scene-owned material.</summary>
        public void Dispose()
        {
            if (root != null) Object.Destroy(root);
            if (material != null) Object.Destroy(material);
        }
    }
}
