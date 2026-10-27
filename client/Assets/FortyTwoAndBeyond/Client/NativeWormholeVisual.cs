using System.Collections.Generic;
using UnityEngine;
using Object = UnityEngine.Object;

namespace FortyTwoAndBeyond.Client
{
    /// <summary>Render the original Create Wormhole cast sprites near the pilot.</summary>
    public sealed class NativeWormholeVisual
    {
        private readonly GameObject root;
        private readonly List<Material> materials = new List<Material>();
        private readonly LineRenderer ring;
        private readonly float startedAt;

        /// <returns>bool: Whether both original cast sprites were attached.</returns>
        public bool Active => root != null;

        /// <summary>Translate the native p3, p4, and p5 W3D emitters.</summary>
        public NativeWormholeVisual(Transform parent)
        {
            Shader effectShader = Resources.Load<Shader>("LegacyEffect");
            if (effectShader == null) effectShader = Shader.Find("42 and Beyond/LegacyEffect");
            Shader lineShader = Shader.Find("Sprites/Default");
            Texture2D portal = Resources.Load<Texture2D>("LegacyEffects/tsg0206");
            Texture2D flare = Resources.Load<Texture2D>("LegacyEffects/aline");
            if (parent == null || effectShader == null || lineShader == null ||
                portal == null || flare == null)
            {
                Debug.LogError("Original Create Wormhole cast sprites are missing.");
                return;
            }
            root = new GameObject("Original Create Wormhole visual");
            root.transform.SetParent(parent, false);
            AddEmitter(effectShader, portal, "Original portal p3", 3f, 1.5f, .677f);
            AddEmitter(effectShader, portal, "Original portal p4", 3f, 1.5f, .676f);
            AddEmitter(effectShader, flare, "Original portal p5", 1.5f, 5f, .474f);
            var circle = new GameObject("Wormhole mouth", typeof(LineRenderer));
            circle.transform.SetParent(root.transform, false);
            ring = circle.GetComponent<LineRenderer>();
            ring.useWorldSpace = false;
            ring.loop = true;
            ring.positionCount = 64;
            ring.widthMultiplier = .08f;
            ring.startColor = ring.endColor = new Color(.35f, .78f, 1f, .58f);
            var ringMaterial = new Material(lineShader);
            ring.sharedMaterial = ringMaterial;
            materials.Add(ringMaterial);
            startedAt = Time.unscaledTime;
        }

        /// <summary>Create one native sprite layer with its archived emitter timing.</summary>
        private void AddEmitter(Shader shader, Texture2D texture, string name,
            float lifetime, float rate, float size)
        {
            var layer = new GameObject(name, typeof(ParticleSystem));
            layer.transform.SetParent(root.transform, false);
            ParticleSystem particles = layer.GetComponent<ParticleSystem>();
            var main = particles.main;
            main.playOnAwake = false;
            main.loop = true;
            main.simulationSpace = ParticleSystemSimulationSpace.Local;
            main.startLifetime = lifetime;
            main.startSpeed = .35f;
            main.startSize = size;
            main.maxParticles = 24;
            var emission = particles.emission;
            emission.rateOverTime = rate;
            var shape = particles.shape;
            shape.shapeType = ParticleSystemShapeType.Circle;
            shape.radius = 1.4f;
            shape.radiusThickness = .2f;
            var renderer = layer.GetComponent<ParticleSystemRenderer>();
            renderer.renderMode = ParticleSystemRenderMode.Billboard;
            var material = new Material(shader);
            material.SetTexture("_MainTex", texture);
            material.SetFloat("_UseSourceColor", 1f);
            material.SetColor("_Tint", new Color(.55f, .8f, 1f, .8f));
            renderer.sharedMaterial = material;
            materials.Add(material);
            particles.Play();
        }

        /// <summary>Keep the portal mouth ahead of the ship during the cast.</summary>
        public void Tick(Vector3 ship, Quaternion heading)
        {
            if (root == null) return;
            root.transform.position = ship + heading * Vector3.forward * 2.6f;
            root.transform.rotation = heading;
            float age = Time.unscaledTime - startedAt;
            float radius = 1.4f + .15f * Mathf.Sin(age * 2.5f);
            for (int index = 0; index < ring.positionCount; index++)
            {
                float angle = index * Mathf.PI * 2f / ring.positionCount;
                ring.SetPosition(index, new Vector3(Mathf.Cos(angle) * radius,
                    Mathf.Sin(angle) * radius, 0));
            }
        }

        /// <summary>Release the portal and all scene-owned materials.</summary>
        public void Dispose()
        {
            if (root != null) Object.Destroy(root);
            foreach (Material material in materials)
                if (material != null) Object.Destroy(material);
            materials.Clear();
        }
    }
}
