using System.Collections.Generic;
using UnityEngine;
using Object = UnityEngine.Object;

namespace FortyTwoAndBeyond.Client
{
    /// <summary>Render original Fold Space emitter sprites at an affected ship.</summary>
    public sealed class NativeFoldVisual
    {
        private readonly GameObject root;
        private readonly List<Material> materials = new List<Material>();

        /// <returns>bool: Whether the archived Fold Space texture was attached.</returns>
        public bool Active => root != null;

        /// <summary>Translate three archived W3D emitter rates, lives, and sizes.</summary>
        public NativeFoldVisual(Transform parent, ushort descriptor)
        {
            Shader shader = Resources.Load<Shader>("LegacyEffect");
            if (shader == null) shader = Shader.Find("42 and Beyond/LegacyEffect");
            Texture2D atlas = Resources.Load<Texture2D>("LegacyEffects/sk_fold01");
            if (parent == null || shader == null || atlas == null)
            {
                Debug.LogError("Original Fold Space emitter texture is missing.");
                return;
            }
            root = new GameObject("Original Fold Space emitters");
            root.transform.SetParent(parent, false);
            bool materializing = descriptor == 267;
            AddEmitter(shader, atlas, materializing ? 2 : 0,
                1f, 100f, .181f, .85f);
            AddEmitter(shader, atlas, materializing ? 3 : 2,
                1.5f, 50f, .268f, 1.25f);
            AddEmitter(shader, atlas, materializing ? 1 : 3,
                .5f, 100f, .09f, .5f);
        }

        /// <summary>Use one native W3D emitter record for a Unity particle layer.</summary>
        private void AddEmitter(Shader shader, Texture2D atlas, int cell, float lifetime,
            float rate, float size, float radius)
        {
            var layer = new GameObject("Original Fold Space layer", typeof(ParticleSystem));
            layer.transform.SetParent(root.transform, false);
            ParticleSystem particles = layer.GetComponent<ParticleSystem>();
            var main = particles.main;
            main.playOnAwake = false;
            main.loop = true;
            main.simulationSpace = ParticleSystemSimulationSpace.Local;
            main.startLifetime = lifetime;
            main.startSpeed = .45f;
            main.startSize = size;
            main.maxParticles = 180;
            var emission = particles.emission;
            emission.rateOverTime = rate;
            var shape = particles.shape;
            shape.shapeType = ParticleSystemShapeType.Sphere;
            shape.radius = radius;
            var renderer = layer.GetComponent<ParticleSystemRenderer>();
            renderer.renderMode = ParticleSystemRenderMode.Billboard;
            var material = new Material(shader);
            material.SetTexture("_MainTex", atlas);
            material.SetTextureScale("_MainTex", new Vector2(.5f, .5f));
            material.SetTextureOffset("_MainTex",
                new Vector2((cell % 2) * .5f, (cell / 2) * .5f));
            material.SetFloat("_UseSourceColor", 1f);
            material.SetColor("_Tint", new Color(.8f, .86f, 1f, .8f));
            renderer.sharedMaterial = material;
            materials.Add(material);
            particles.Play();
        }

        /// <summary>Follow the authoritative Fold Space source or recipient.</summary>
        public void Tick(Vector3 position)
        {
            if (root != null) root.transform.position = position;
        }

        /// <summary>Hide the emitters while the recipient position is unknown.</summary>
        public void Hide()
        {
            if (root != null) root.SetActive(false);
        }

        /// <summary>Restore the emitters when the recipient position returns.</summary>
        public void Show()
        {
            if (root != null) root.SetActive(true);
        }

        /// <summary>Release all Fold Space emitters and their materials.</summary>
        public void Dispose()
        {
            if (root != null) Object.Destroy(root);
            foreach (Material material in materials)
                if (material != null) Object.Destroy(material);
            materials.Clear();
        }
    }
}
