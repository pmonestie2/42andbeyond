using UnityEngine;
using Object = UnityEngine.Object;

namespace FortyTwoAndBeyond.Client
{
    /// <summary>Draw three shaded red energy strands converging on an active Turbo ship.</summary>
    public sealed class TurboEnergyVisual
    {
        private readonly GameObject root;
        private readonly LineRenderer[] strands = new LineRenderer[3];
        private readonly Material material;
        private readonly Light coreLight;
        private readonly float startedAt;

        /// <summary>Create a ship-local effect from an authoritative active device.</summary>
        public TurboEnergyVisual(Transform ship)
        {
            if (ship == null) return;
            Shader shader = Resources.Load<Shader>("LegacyEffect");
            if (shader == null) shader = Shader.Find("Sprites/Default");
            if (shader == null) return;
            root = new GameObject("Turbo Weapons red energy");
            root.transform.SetParent(ship, false);
            material = new Material(shader);
            material.SetTexture("_MainTex", Texture2D.whiteTexture);
            material.SetFloat("_UseSourceColor", 1f);
            material.SetColor("_Tint", Color.white);
            for (int index = 0; index < strands.Length; index++)
            {
                var strand = new GameObject("Turbo strand " + index, typeof(LineRenderer));
                strand.transform.SetParent(root.transform, false);
                LineRenderer line = strand.GetComponent<LineRenderer>();
                line.useWorldSpace = false;
                line.alignment = LineAlignment.View;
                line.positionCount = 25;
                line.widthMultiplier = .04f;
                line.widthCurve = new AnimationCurve(new Keyframe(0, 0),
                    new Keyframe(.18f, .52f), new Keyframe(.62f, 1f),
                    new Keyframe(1, .06f));
                line.numCapVertices = 3;
                line.sharedMaterial = material;
                strands[index] = line;
            }
            var core = new GameObject("Turbo heart light", typeof(Light));
            core.transform.SetParent(root.transform, false);
            core.transform.localPosition = new Vector3(0, .35f, .08f);
            coreLight = core.GetComponent<Light>();
            coreLight.type = LightType.Point;
            coreLight.color = new Color(1f, .13f, .055f);
            coreLight.range = 2.8f;
            coreLight.shadows = LightShadows.None;
            startedAt = Time.unscaledTime;
        }

        /// <summary>Pulse the three strands and their hull light while Turbo remains active.</summary>
        public void Tick()
        {
            if (root == null) return;
            float time = Time.unscaledTime - startedAt;
            float hum = .72f + .28f * Mathf.Sin(time * 9f);
            for (int index = 0; index < strands.Length; index++)
            {
                LineRenderer line = strands[index];
                var gradient = new Gradient();
                gradient.SetKeys(new[] {
                    new GradientColorKey(new Color(.25f, .01f, .01f), 0),
                    new GradientColorKey(new Color(.86f, .055f, .025f), .45f),
                    new GradientColorKey(new Color(1f, .35f, .09f), .78f),
                    new GradientColorKey(new Color(.5f, .035f, .02f), 1)
                }, new[] {
                    new GradientAlphaKey(0, 0),
                    new GradientAlphaKey(.62f * hum, .25f),
                    new GradientAlphaKey(.85f * hum, .72f),
                    new GradientAlphaKey(0, 1)
                });
                line.colorGradient = gradient;
                float lane = index - 1f;
                for (int point = 0; point < 25; point++)
                {
                    float t = point / 24f;
                    float approach = t * t * (3f - 2f * t);
                    float x = lane * .75f * (1f - approach);
                    float y = .24f + .20f * Mathf.Sin(t * Mathf.PI) +
                        .025f * Mathf.Sin(t * 30f - time * 13f + index * 1.7f);
                    float z = Mathf.Lerp(-1.55f, .08f, t);
                    line.SetPosition(point, new Vector3(x, y, z));
                }
            }
            if (coreLight != null) coreLight.intensity = .5f + .75f * hum;
        }

        /// <summary>Release ship-local geometry and its material when Turbo ends.</summary>
        public void Dispose()
        {
            if (root != null) Object.Destroy(root);
            if (material != null) Object.Destroy(material);
        }
    }

    /// <summary>Draw heated energy from an active Shield Inversion cast to its target.</summary>
    public sealed class ShieldBurnEnergyVisual
    {
        private readonly bool nova;
        private readonly GameObject root;
        private readonly LineRenderer[] strands = new LineRenderer[3];
        private readonly LineRenderer contact;
        private readonly Material material;
        private readonly Light impactLight;
        private readonly float startedAt;

        /// <summary>Create an effect that remains owned by its server-published buff.</summary>
        public ShieldBurnEnergyVisual(Transform parent, bool nova = false)
        {
            this.nova = nova;
            if (parent == null) return;
            Shader shader = Resources.Load<Shader>("LegacyEffect");
            if (shader == null) shader = Shader.Find("Sprites/Default");
            if (shader == null) return;
            root = new GameObject(nova ? "Shield Nova target stream" :
                "Shield Burn heated energy");
            root.transform.SetParent(parent, false);
            material = new Material(shader);
            material.SetTexture("_MainTex", Texture2D.whiteTexture);
            material.SetFloat("_UseSourceColor", 1f);
            material.SetColor("_Tint", Color.white);
            for (int index = 0; index < strands.Length; index++)
            {
                var strand = new GameObject("Burn filament " + index, typeof(LineRenderer));
                strand.transform.SetParent(root.transform, false);
                LineRenderer line = strand.GetComponent<LineRenderer>();
                line.useWorldSpace = true;
                line.alignment = LineAlignment.View;
                line.positionCount = 29;
                line.widthMultiplier = nova ? index == 1 ? .22f : .13f :
                    index == 1 ? .035f : .02f;
                line.widthCurve = new AnimationCurve(new Keyframe(0, .04f),
                    new Keyframe(.2f, .7f), new Keyframe(.75f, 1f),
                    new Keyframe(1, .12f));
                line.sharedMaterial = material;
                strands[index] = line;
            }
            var impact = new GameObject("Burn shield contact", typeof(LineRenderer));
            impact.transform.SetParent(root.transform, false);
            contact = impact.GetComponent<LineRenderer>();
            contact.useWorldSpace = true;
            contact.alignment = LineAlignment.View;
            contact.positionCount = 33;
            contact.widthMultiplier = nova ? .1f : .03f;
            contact.sharedMaterial = material;
            var lightObject = new GameObject("Burn impact light", typeof(Light));
            lightObject.transform.SetParent(root.transform, false);
            impactLight = lightObject.GetComponent<Light>();
            impactLight.type = LightType.Point;
            impactLight.color = nova ? new Color(.28f, .67f, 1f) :
                new Color(1f, .29f, .045f);
            impactLight.range = nova ? 4f : 2.4f;
            impactLight.shadows = LightShadows.None;
            startedAt = Time.unscaledTime;
        }

        /// <summary>Follow the selected target while the authoritative inversion buff exists.</summary>
        public void Tick(Vector3 source, Vector3 target)
        {
            if (root == null) return;
            float time = Time.unscaledTime - startedAt;
            Vector3 direction = (target - source).normalized;
            if (direction.sqrMagnitude < .01f) direction = Vector3.forward;
            Vector3 side = Vector3.Cross(direction, Vector3.up);
            if (side.sqrMagnitude < .01f) side = Vector3.Cross(direction, Vector3.forward);
            side.Normalize();
            Vector3 up = Vector3.Cross(direction, side).normalized;
            Vector3 begin = source + direction * .7f;
            Vector3 hit = target - direction * .95f;
            float pulse = .75f + .25f * Mathf.Sin(time * 11f);
            for (int index = 0; index < strands.Length; index++)
            {
                LineRenderer line = strands[index];
                var gradient = new Gradient();
                gradient.SetKeys(nova ? new[] {
                    new GradientColorKey(new Color(.045f, .18f, .42f), 0),
                    new GradientColorKey(new Color(.22f, .6f, 1f), .48f),
                    new GradientColorKey(new Color(.82f, .95f, 1f), 1)
                } : new[] {
                    new GradientColorKey(new Color(.28f, .035f, .005f), 0),
                    new GradientColorKey(new Color(.85f, .21f, .035f), .48f),
                    new GradientColorKey(new Color(1f, .64f, .19f), 1)
                }, new[] {
                    new GradientAlphaKey(.05f, 0),
                    new GradientAlphaKey((nova ? .85f : .65f) * pulse, .25f),
                    new GradientAlphaKey((nova ? 1f : .9f) * pulse, .87f),
                    new GradientAlphaKey(.12f, 1)
                });
                line.colorGradient = gradient;
                for (int point = 0; point < 29; point++)
                {
                    float t = point / 28f;
                    float envelope = Mathf.Sin(t * Mathf.PI);
                    float noise = Mathf.Sin(t * 20f - time * 17f + index * 2.3f) * .05f +
                        Mathf.Sin(t * 48f + time * 29f + index * 1.4f) * .018f;
                    line.SetPosition(point, Vector3.Lerp(begin, hit, t) +
                        side * ((index - 1f) * .11f * envelope + noise * envelope) +
                        up * (.17f * envelope + noise * .6f));
                }
            }
            float radius = (nova ? .44f : .24f) + .05f * Mathf.Sin(time * 14f);
            for (int point = 0; point < 33; point++)
            {
                float angle = point * Mathf.PI * 2f / 32f;
                contact.SetPosition(point, hit +
                    (side * Mathf.Cos(angle) + up * Mathf.Sin(angle)) * radius);
            }
            contact.startColor = contact.endColor = nova ?
                new Color(.55f, .85f, 1f, .72f * pulse) :
                new Color(1f, .42f, .09f, .46f * pulse);
            impactLight.transform.position = hit;
            impactLight.intensity = (nova ? 2f : 1.2f) * pulse;
        }

        /// <summary>Release the heated contact and beam when the buff ends.</summary>
        public void Dispose()
        {
            if (root != null) Object.Destroy(root);
            if (material != null) Object.Destroy(material);
        }
    }
}
