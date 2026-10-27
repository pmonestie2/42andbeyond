using UnityEngine;
using Object = UnityEngine.Object;

namespace FortyTwoAndBeyond.Client
{
    /// <summary>Display the original numbered Self Destruct countdown atlas.</summary>
    public sealed class NativeSelfDestructVisual
    {
        private readonly GameObject quad;
        private readonly Material material;
        private readonly float startedAt;

        /// <returns>bool: Whether the original countdown atlas was attached.</returns>
        public bool Active => quad != null;

        /// <summary>Bind the native 10-frame, five-column countdown sprite.</summary>
        public NativeSelfDestructVisual(Transform parent)
        {
            Shader shader = Resources.Load<Shader>("LegacyEffect");
            if (shader == null) shader = Shader.Find("42 and Beyond/LegacyEffect");
            Texture2D atlas = Resources.Load<Texture2D>("LegacyEffects/sk_sdest01");
            if (parent == null || shader == null || atlas == null)
            {
                Debug.LogError("Original Self Destruct countdown atlas is missing.");
                return;
            }
            quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
            quad.name = "Original Self Destruct countdown";
            quad.transform.SetParent(parent, false);
            Collider collider = quad.GetComponent<Collider>();
            if (collider != null) Object.Destroy(collider);
            quad.transform.localScale = Vector3.one * 1.4f;
            material = new Material(shader);
            material.SetTexture("_MainTex", atlas);
            material.SetTextureScale("_MainTex", new Vector2(.2f, .5f));
            material.SetFloat("_UseSourceColor", 1f);
            material.SetColor("_Tint", new Color(1f, .35f, .2f, .9f));
            quad.GetComponent<MeshRenderer>().sharedMaterial = material;
            startedAt = Time.unscaledTime;
        }

        /// <summary>Show the original 3, 2, and 1 atlas cells for one second each.</summary>
        public void Tick(Vector3 ship, Vector3 forward, Camera camera)
        {
            if (quad == null) return;
            int second = Mathf.FloorToInt(Time.unscaledTime - startedAt);
            quad.SetActive(second < 3);
            if (second >= 3) return;
            int frame = 7 + Mathf.Clamp(second, 0, 2);
            material.SetTextureOffset("_MainTex", new Vector2((frame % 5) * .2f, 0));
            quad.transform.position = ship + forward * 2f + Vector3.up * 1.2f;
            if (camera != null)
                quad.transform.rotation = Quaternion.LookRotation(
                    quad.transform.position - camera.transform.position, Vector3.up);
        }

        /// <summary>Release the numbered sprite and scene-owned material.</summary>
        public void Dispose()
        {
            if (quad != null) Object.Destroy(quad);
            if (material != null) Object.Destroy(material);
        }
    }
}
