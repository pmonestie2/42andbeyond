using UnityEngine;

namespace FortyTwoAndBeyond.Client
{
    /// <summary>Animate the complete gate assembly after the native server approves gating.</summary>
    public sealed class GateTransitVisual : MonoBehaviour
    {
        private Transform assembly, rotor, collar;
        private Renderer[] channels;
        private MaterialPropertyBlock properties;

        /// <summary>Allocate native rendering state on Unity's main thread.</summary>
        private void Awake() { properties = new MaterialPropertyBlock(); }
        private float age = -1;
        private bool active;
        public float Contraction => assembly == null ? 1 : assembly.localScale.x;

        /// <summary>Bind the bearing, armored rotor and recessed induction collar.</summary>
        public void Configure(Transform wholeAssembly, Transform armoredRotor, Transform inductionCollar,
            Renderer[] lightChannels)
        { assembly = wholeAssembly; rotor = armoredRotor; collar = inductionCollar; channels = lightChannels; }

        /// <summary>Synchronize activation to the immutable native gate-state snapshot.</summary>
        public void Observe(bool approved, float elapsed)
        { active = approved; if (approved) age = Mathf.Max(0, elapsed); }

        /// <summary>Rotate the armor, compress the whole gate and sequence its recessed circuits.</summary>
        private void Update()
        {
            if (assembly == null) return;
            float dt = Mathf.Min(Time.unscaledDeltaTime, .1f);
            if (active) age += dt;
            else age = Mathf.MoveTowards(age, -1, dt * 8);
            float pressure = active ? Mathf.SmoothStep(0, 1, Mathf.Clamp01((age - 2) / 5)) : 0;
            float desired = 1 - .24f * pressure;
            assembly.localScale = Vector3.Lerp(assembly.localScale, Vector3.one * desired, 1 - Mathf.Exp(-4 * dt));
            float speed = active ? Mathf.Lerp(3, 34, pressure) : 0;
            rotor.Rotate(0, 0, speed * dt, Space.Self);
            collar.Rotate(0, 0, -speed * 1.6f * dt, Space.Self);
            collar.localPosition = new Vector3(0, 0, -.23f + .7f * pressure);
            for (int i = 0; i < channels.Length; i++)
            {
                Renderer channel = channels[i];
                channel.GetPropertyBlock(properties);
                float sequence = active ? .7f + .3f * Mathf.Sin(age * 4 - i * .8f) : 0;
                properties.SetColor("_EmissionColor", new Color(.06f, .6f, 1) * (1 + sequence * pressure * 4));
                channel.SetPropertyBlock(properties);
            }
        }
    }
}
