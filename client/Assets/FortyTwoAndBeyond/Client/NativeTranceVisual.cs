using UnityEngine;
using Object = UnityEngine.Object;

namespace FortyTwoAndBeyond.Client
{
    /// <summary>Show the saved Blender power rail with the original Trance emitter sprite.</summary>
    public sealed class NativeTranceVisual
    {
        private readonly GameObject root;
        private readonly GameObject ring;
        private readonly Material material, ringMaterial;
        private readonly Vector3 ringRestScale;

        /// <returns>bool: Whether the power rail and original emitter were attached.</returns>
        public bool Active => root != null && ring != null;

        /// <summary>Fit the saved rail to the hull and add the original native sprite emitter.</summary>
        public NativeTranceVisual(Transform ship, ushort descriptor)
        {
            Shader shader = Resources.Load<Shader>("BeamAdditive");
            if (shader == null) shader = Shader.Find("42 and Beyond/BeamAdditive");
            Shader railShader = Resources.Load<Shader>("CombatTranceRing");
            GameObject railAsset = Resources.Load<GameObject>("LegacyEffects/CombatTranceRing");
            Texture2D sprite = Resources.Load<Texture2D>("LegacyEffects/sk_comtra02");
            if (ship == null || shader == null || railShader == null || railAsset == null ||
                sprite == null)
            {
                Debug.LogError("Combat Trance rail, shader, or original sprite is missing.");
                return;
            }
            RallyHullFrame hull = RallyHullFrame.Measure(ship);
            float shipScale = Mathf.Max(.001f, Mathf.Abs(ship.lossyScale.x));
            float radius = Mathf.Max(hull.HalfWidth * .78f,
                hull.HalfLength * .55f) + .08f;
            Vector3 center = ship.position + ship.right * hull.CenterX +
                ship.forward * hull.CenterZ + ship.up * (hull.BottomY - .22f);

            ring = Object.Instantiate(railAsset, ship);
            ring.name = "Original Combat Trance power rail";
            ring.transform.localPosition = Vector3.zero;
            ring.transform.localRotation = Quaternion.identity;
            ring.transform.localScale = Vector3.one / shipScale;
            MeshRenderer[] rails = ring.GetComponentsInChildren<MeshRenderer>(true);
            if (rails.Length == 0)
            {
                Debug.LogError("Combat Trance Blender rail has no mesh renderer.");
                Object.Destroy(ring);
                return;
            }
            // FBX can retain Blender's XY ring plane. Align its thin axis with the ship's up axis.
            MeshFilter orientationMesh = rails[0].GetComponent<MeshFilter>();
            if (orientationMesh != null && orientationMesh.sharedMesh != null)
            {
                Vector3 size = orientationMesh.sharedMesh.bounds.size;
                Vector3 normal = size.x < size.y && size.x < size.z ? Vector3.right :
                    size.y < size.z ? Vector3.up : Vector3.forward;
                Vector3 localNormal = ring.transform.InverseTransformDirection(
                    orientationMesh.transform.TransformDirection(normal));
                ring.transform.localRotation = Quaternion.FromToRotation(localNormal, Vector3.up);
            }
            Bounds bounds = rails[0].bounds;
            for (int index = 1; index < rails.Length; index++)
                bounds.Encapsulate(rails[index].bounds);
            float diameter = Mathf.Max(bounds.size.x, bounds.size.z);
            ring.transform.localScale *= radius * 2f / Mathf.Max(.01f, diameter);
            bounds = rails[0].bounds;
            for (int index = 1; index < rails.Length; index++)
                bounds.Encapsulate(rails[index].bounds);
            ring.transform.position += center - bounds.center;
            ringRestScale = ring.transform.localScale;
            ringMaterial = new Material(railShader);
            ringMaterial.SetColor("_Tint", Color.Lerp(
                new Color(1f, .62f, .42f, .9f), TierColor(descriptor), .38f));
            MeshFilter railMesh = rails[0].GetComponent<MeshFilter>();
            if (railMesh != null && railMesh.sharedMesh != null)
                ringMaterial.SetFloat("_RingPlane",
                    railMesh.sharedMesh.bounds.size.z > railMesh.sharedMesh.bounds.size.y ? 1f : 0f);
            foreach (MeshRenderer rail in rails) rail.sharedMaterial = ringMaterial;

            root = new GameObject("Original Combat Trance emitter", typeof(ParticleSystem));
            root.transform.SetParent(ship, false);
            root.transform.position = center;
            root.transform.localScale = Vector3.one / shipScale;

            ParticleSystem particles = root.GetComponent<ParticleSystem>();
            var main = particles.main;
            main.loop = true;
            main.playOnAwake = false;
            main.simulationSpace = ParticleSystemSimulationSpace.Local;
            main.startLifetime = .25f;
            main.startSpeed = 1.5f;
            main.startSize = .1f;
            main.maxParticles = 64;
            main.startColor = TierColor(descriptor);
            var emission = particles.emission;
            emission.rateOverTime = 30.303f;
            var shape = particles.shape;
            shape.shapeType = ParticleSystemShapeType.Circle;
            shape.radius = radius * .97f;
            shape.radiusThickness = .08f;
            shape.rotation = new Vector3(90f, 0, 0);
            var velocity = particles.velocityOverLifetime;
            velocity.enabled = true;
            velocity.space = ParticleSystemSimulationSpace.Local;
            velocity.radial = .28f;
            var renderer = root.GetComponent<ParticleSystemRenderer>();
            renderer.renderMode = ParticleSystemRenderMode.Billboard;
            renderer.alignment = ParticleSystemRenderSpace.View;
            material = new Material(shader);
            material.SetTexture("_MainTex", sprite);
            renderer.sharedMaterial = material;
            particles.Play();
        }

        /// <summary>Give the saved rail its restrained Blender breathing motion.</summary>
        public void Tick()
        {
            if (ring != null)
                ring.transform.localScale = ringRestScale *
                    (1f + .018f * Mathf.Sin(Time.unscaledTime * 3.14f));
        }

        /// <returns>Color: The tier tint applied to the original emitter sprite.</returns>
        private static Color TierColor(ushort descriptor)
        {
            switch (descriptor)
            {
                case 669: return new Color(1f, .47f, .23f, .8f);
                case 670: return new Color(.35f, .8f, 1f, .8f);
                case 671: return new Color(.42f, 1f, .65f, .8f);
                case 672: return new Color(1f, .79f, .3f, .8f);
                default: return new Color(.79f, .55f, 1f, .8f);
            }
        }

        /// <summary>Release scene-owned particles and their material.</summary>
        public void Dispose()
        {
            if (root != null) Object.Destroy(root);
            if (ring != null) Object.Destroy(ring);
            if (material != null) Object.Destroy(material);
            if (ringMaterial != null) Object.Destroy(ringMaterial);
        }
    }
}
