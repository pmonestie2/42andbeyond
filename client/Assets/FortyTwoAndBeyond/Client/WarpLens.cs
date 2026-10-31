using UnityEngine;

namespace FortyTwoAndBeyond.Client
{
    /// <summary>Bend the rendered world during server-owned warp without drawing foreground ribbons.</summary>
    [RequireComponent(typeof(Camera)), DisallowMultipleComponent]
    public sealed class WarpLens : MonoBehaviour
    {
        private Camera view;
        private Material material;
        private DepthTextureMode previousDepth;
        private bool requestedDepth;
        private float strength;
        private Vector3 heading;
        public float Strength => strength;

        /// <summary>Stay out of the camera pipeline until warp presentation is active.</summary>
        private void Awake() { view = GetComponent<Camera>(); enabled = false; }

        /// <summary>Request the depth buffer only while distortion needs depth-aware trail separation.</summary>
        private void OnEnable()
        {
            if (view == null) view = GetComponent<Camera>();
            previousDepth = view.depthTextureMode;
            requestedDepth = (previousDepth & DepthTextureMode.Depth) == 0;
            view.depthTextureMode |= DepthTextureMode.Depth;
        }

        /// <summary>Release this component's depth request while preserving pre-existing camera settings.</summary>
        private void OnDisable()
        {
            if (view != null && requestedDepth && view.depthTextureMode == (previousDepth | DepthTextureMode.Depth))
                view.depthTextureMode = previousDepth;
            requestedDepth = false;
        }

        /// <summary>Accept visual intensity and the authoritative rendered heading from the flight controller.</summary>
        public void SetState(float intensity, Vector3 worldHeading)
        {
            strength = Mathf.Clamp01(intensity); heading = worldHeading;
            enabled = strength > .001f && view != null && !view.orthographic;
        }

        /// Returns:
        ///     Vector3: Screen pixels aligned to the refracted world, retaining signed camera depth.
        public Vector3 ProjectWorld(Vector3 worldPosition)
        {
            Vector3 point = view.WorldToViewportPoint(worldPosition);
            if (enabled && material != null && point.z > 0 && point.x >= 0 && point.x <= 1 && point.y >= 0 && point.y <= 1)
            {
                Vector2 target = new Vector2(point.x, point.y), output = target, focus = Focus();
                for (int iteration = 0; iteration < 6; iteration++) output += target - SamplePosition(output, focus);
                point.x = output.x; point.y = output.y;
            }
            return new Vector3(point.x * view.pixelWidth, point.y * view.pixelHeight, point.z);
        }

        /// Returns:
        ///     Vector2: The current travel direction projected into the camera's image.
        private Vector2 Focus()
        {
            Vector3 point = view.WorldToViewportPoint(view.transform.position + heading.normalized * 1000);
            return point.z <= 0 ? new Vector2(.5f, .5f) :
                new Vector2(Mathf.Clamp(point.x, -.5f, 1.5f), Mathf.Clamp(point.y, -.5f, 1.5f));
        }

        /// Returns:
        ///     Vector2: The world-image sample used by the shader for this output pixel.
        private Vector2 SamplePosition(Vector2 output, Vector2 focus)
        {
            Vector2 delta = output - focus;
            Vector2 metric = new Vector2(delta.x * view.aspect, delta.y);
            float radius = metric.magnitude;
            float edge = Mathf.SmoothStep(0, 1, Mathf.InverseLerp(.08f, .72f, radius));
            float time = Time.timeSinceLevelLoad;
            float wave = Mathf.Sin(radius * 17 - time * 5.5f) * .0055f + Mathf.Sin(radius * 31 - time * 3.1f) * .0015f;
            Vector2 sample = focus + delta / (1 + strength * edge * (.08f + wave));
            sample += new Vector2(Mathf.Sin(metric.y * 9 - time * 2.2f), Mathf.Cos(metric.x * 8 - time * 1.7f)) *
                (strength * edge * .0018f);
            return new Vector2(Mathf.Clamp(sample.x, .001f, .999f), Mathf.Clamp(sample.y, .001f, .999f));
        }

        /// <summary>Deform all scene objects and stretch starlight without smearing across depth boundaries.</summary>
        private void OnRenderImage(RenderTexture source, RenderTexture destination)
        {
            if (strength <= .001f || view.orthographic)
            { Graphics.Blit(source, destination); return; }
            if (material == null)
            {
                Shader shader = Resources.Load<Shader>("WarpLens");
                if (shader == null || !shader.isSupported) { Graphics.Blit(source, destination); return; }
                material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            }
            Vector2 focus = Focus();
            material.SetVector("_Focus", new Vector4(focus.x, focus.y, 0, 0));
            material.SetFloat("_Strength", strength);
            material.SetFloat("_Aspect", view.aspect);
            Graphics.Blit(source, destination, material);
        }

        /// <summary>Release the camera material when the client scene is destroyed.</summary>
        private void OnDestroy() { if (material != null) Destroy(material); }
    }
}
