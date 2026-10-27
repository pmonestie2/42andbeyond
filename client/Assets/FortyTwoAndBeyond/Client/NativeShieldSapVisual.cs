using UnityEngine;
using Object = UnityEngine.Object;

namespace FortyTwoAndBeyond.Client
{
    /// <summary>Render Shield Sap's confirmed siphon beam or shield drain orb.</summary>
    public sealed class NativeShieldSapVisual
    {
        private readonly GameObject root;
        private readonly LineRenderer[] strands = new LineRenderer[3];
        private readonly Material material;
        private readonly Light contactLight;
        private readonly ushort descriptor;
        private readonly bool personalAward;
        private readonly float startedAt, duration;

        /// Returns:
        ///     bool: Whether this shield sphere can render without the caster ship in view.
        public bool OrbOnly => descriptor != 139;

        /// <summary>Build the three animated energy strands from one native shot descriptor.</summary>
        public NativeShieldSapVisual(Transform parent, ushort descriptor, float duration,
            bool personalAward)
        {
            this.descriptor = descriptor;
            this.personalAward = personalAward;
            this.duration = Mathf.Clamp(duration, .1f, 2f);
            startedAt = Time.unscaledTime;
            Shader shader = Resources.Load<Shader>("LegacyEffect");
            if (shader == null) shader = Shader.Find("Sprites/Default");
            if (parent == null || shader == null) return;
            root = new GameObject(descriptor == 139 ? "Shield Sap siphon" :
                personalAward ? "Shield Sap group award" : "Shield Sap target orb");
            root.transform.SetParent(parent, false);
            material = new Material(shader);
            material.SetTexture("_MainTex", Texture2D.whiteTexture);
            material.SetFloat("_UseSourceColor", 1f);
            material.SetColor("_Tint", Color.white);
            for (int index = 0; index < strands.Length; index++)
            {
                var strand = new GameObject("Energy strand " + index, typeof(LineRenderer));
                strand.transform.SetParent(root.transform, false);
                LineRenderer line = strand.GetComponent<LineRenderer>();
                line.useWorldSpace = true;
                line.alignment = LineAlignment.View;
                line.numCapVertices = 4;
                line.positionCount = descriptor == 139 ? 32 : 33;
                line.widthMultiplier = descriptor == 139 ? .035f + index * .006f :
                    personalAward ? .025f : .055f;
                line.widthCurve = new AnimationCurve(new Keyframe(0, .08f),
                    new Keyframe(.22f, .75f), new Keyframe(.58f, 1f),
                    new Keyframe(1, .05f));
                line.sharedMaterial = material;
                strands[index] = line;
            }
            var contact = new GameObject("Shield Sap contact light", typeof(Light));
            contact.transform.SetParent(root.transform, false);
            contactLight = contact.GetComponent<Light>();
            contactLight.type = LightType.Point;
            contactLight.color = new Color(.1f, .68f, 1f);
                contactLight.range = 4f;
            contactLight.shadows = LightShadows.None;
        }

        /// Returns:
        ///     bool: Whether the one-second native effect is still visible.
        public bool Tick(Vector3 source, Vector3 target)
        {
            if (root == null) return false;
            float age = Time.unscaledTime - startedAt;
            if (age >= duration) return false;
            float phase = age / duration;
            float fade = Mathf.Pow(1f - Mathf.Clamp01(phase), .45f);
            if (contactLight != null)
            {
                contactLight.transform.position = target;
                contactLight.intensity = descriptor == 139 ? fade * 1.8f : fade * 1.5f;
            }
            for (int strand = 0; strand < strands.Length; strand++)
            {
                LineRenderer line = strands[strand];
                var gradient = new Gradient();
                gradient.SetKeys(new[] {
                    new GradientColorKey(new Color(.035f, .34f, .52f), 0),
                    new GradientColorKey(new Color(.20f, .74f, .94f), .45f),
                    new GradientColorKey(new Color(.65f, .91f, 1f), .72f),
                    new GradientColorKey(new Color(.08f, .45f, .7f), 1)
                }, new[] {
                    new GradientAlphaKey(0, 0),
                    new GradientAlphaKey(.9f * fade, .19f),
                    new GradientAlphaKey(.95f * fade, .65f),
                    new GradientAlphaKey(.04f * fade, 1)
                });
                line.colorGradient = gradient;
                if (descriptor == 139)
                {
                    Vector3 axis = (source - target).normalized;
                    Vector3 side = Vector3.Cross(axis, Vector3.up);
                    if (side.sqrMagnitude < .01f) side = Vector3.Cross(axis, Vector3.forward);
                    side.Normalize();
                    Vector3 up = Vector3.Cross(axis, side).normalized;
                    for (int point = 0; point < 32; point++)
                    {
                        float t = point / 31f;
                        float envelope = Mathf.Sin(t * Mathf.PI);
                        float wave = (Mathf.Sin(t * 17f - age * 19f + strand * 2.09f) * .055f +
                            Mathf.Sin(t * 43f + age * 31f + strand * 3.1f) * .023f) * envelope;
                        line.SetPosition(point, Vector3.Lerp(target, source, t) +
                            side * wave + up * Mathf.Cos(t * 23f + age * 12f + strand) * wave);
                    }
                }
                else
                {
                    float radius = (personalAward ? .55f + strand * .06f :
                        2.5f + strand * .25f) * (1f + .20f * phase);
                    for (int point = 0; point <= 32; point++)
                    {
                        float angle = point * Mathf.PI * 2f / 32f + age * (strand + 1);
                        Vector3 offset = strand == 0 ?
                            new Vector3(Mathf.Cos(angle), Mathf.Sin(angle), 0) :
                            strand == 1 ? new Vector3(Mathf.Cos(angle), 0, Mathf.Sin(angle)) :
                            new Vector3(0, Mathf.Cos(angle), Mathf.Sin(angle));
                        line.SetPosition(point, target + offset * radius);
                    }
                }
            }
            return true;
        }

        /// <summary>Release the short-lived scene objects and material.</summary>
        public void Dispose()
        {
            if (root != null) Object.Destroy(root);
            if (material != null) Object.Destroy(material);
        }
    }
}
