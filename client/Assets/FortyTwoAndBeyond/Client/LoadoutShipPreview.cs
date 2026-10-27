using UnityEngine;
using System.Collections.Generic;
using Object = UnityEngine.Object;

namespace FortyTwoAndBeyond.Client
{
    /// <summary>Render the player's current ship model into the fitting display.</summary>
    internal sealed class LoadoutShipPreview
    {
        private readonly GameObject cameraObject;
        private readonly Camera camera;
        private readonly RenderTexture texture;
        private readonly Light keyLight, rimLight;
        private float renderedAt = -1f;
        public Texture Texture => texture;

        /// <summary>Create an isolated camera that sees only the player's model.</summary>
        public LoadoutShipPreview()
        {
            texture = new RenderTexture(768, 768, 24, RenderTextureFormat.ARGB32)
            { name = "Loadout ship preview", antiAliasing = 4 };
            texture.Create();
            cameraObject = new GameObject("Loadout preview camera", typeof(Camera));
            camera = cameraObject.GetComponent<Camera>();
            camera.enabled = false;
            camera.targetTexture = texture;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(14f / 255f, 19f / 255f, 25f / 255f, 1f);
            camera.cullingMask = 1 << 30;
            camera.orthographic = true;
            camera.nearClipPlane = .01f;
            camera.farClipPlane = 10000f;
            keyLight = new GameObject("Fitting warm key", typeof(Light)).GetComponent<Light>();
            keyLight.transform.SetParent(cameraObject.transform, false);
            keyLight.type = LightType.Directional;
            keyLight.color = new Color(1f, .82f, .64f);
            keyLight.intensity = 1.25f;
            keyLight.cullingMask = 1 << 30;
            rimLight = new GameObject("Fitting blue rim", typeof(Light)).GetComponent<Light>();
            rimLight.transform.SetParent(cameraObject.transform, false);
            rimLight.type = LightType.Directional;
            rimLight.color = new Color(.37f, .73f, 1f);
            rimLight.intensity = .8f;
            rimLight.cullingMask = 1 << 30;
        }

        /// <returns>Texture: The current ship model rendered on a dark fitting field.</returns>
        public Texture Render(Transform ship)
        {
            if (ship == null || !ship.gameObject.activeInHierarchy) return texture;
            if (renderedAt >= 0 && Time.unscaledTime - renderedAt < .2f) return texture;
            Renderer[] renderers = ship.GetComponentsInChildren<Renderer>();
            var layers = new Dictionary<GameObject, int>();
            bool found = false;
            Bounds bounds = default;
            foreach (Renderer renderer in renderers)
            {
                if (!(renderer is MeshRenderer) && !(renderer is SkinnedMeshRenderer) ||
                    !renderer.enabled) continue;
                layers[renderer.gameObject] = renderer.gameObject.layer;
                renderer.gameObject.layer = 30;
                if (!found) { bounds = renderer.bounds; found = true; }
                else bounds.Encapsulate(renderer.bounds);
            }
            if (!found) return texture;
            float radius = Mathf.Max(.6f, bounds.extents.magnitude);
            Vector3 direction = new Vector3(-1.3f, .8f, -1f).normalized;
            camera.transform.position = bounds.center + direction * radius * 4f;
            camera.transform.LookAt(bounds.center);
            keyLight.transform.rotation = camera.transform.rotation * Quaternion.Euler(14, -32, 0);
            rimLight.transform.rotation = camera.transform.rotation * Quaternion.Euler(-24, 145, 0);
            camera.orthographicSize = radius * .80f;
            try
            {
                camera.Render();
                renderedAt = Time.unscaledTime;
            }
            finally
            {
                foreach (KeyValuePair<GameObject, int> entry in layers)
                    if (entry.Key != null) entry.Key.layer = entry.Value;
            }
            return texture;
        }

        /// <summary>Release the fitting camera and its render target.</summary>
        public void Dispose()
        {
            if (camera != null) camera.targetTexture = null;
            if (cameraObject != null) Object.Destroy(cameraObject);
            if (texture != null) { texture.Release(); Object.Destroy(texture); }
        }
    }
}
