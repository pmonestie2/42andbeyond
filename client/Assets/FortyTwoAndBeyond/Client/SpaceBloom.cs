using UnityEngine;

namespace FortyTwoAndBeyond.Client
{
    /// <summary>Add low-resolution HDR glow to flight scenery using the game's existing renderer.</summary>
    [RequireComponent(typeof(Camera)), DisallowMultipleComponent]
    public sealed class SpaceBloom : MonoBehaviour
    {
        [Range(0, 1)] public float intensity = .3f;
        [Range(.5f, 4)] public float threshold = 1.15f;
        private Material material;

        /// <summary>Keep bright Sun and accretion emissions in HDR until compositing.</summary>
        private void OnEnable() { GetComponent<Camera>().allowHDR = true; }

        /// <summary>Extract bright pixels, blur at quarter resolution, and composite before overlay UI.</summary>
        private void OnRenderImage(RenderTexture source, RenderTexture destination)
        {
            if (material == null)
            {
                Shader shader = Resources.Load<Shader>("SpaceBloom");
                if (shader == null || !shader.isSupported) { Graphics.Blit(source, destination); return; }
                material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            }
            int width = Mathf.Max(1, source.width / 4), height = Mathf.Max(1, source.height / 4);
            RenderTexture a = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGBHalf);
            RenderTexture b = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGBHalf);
            a.filterMode = b.filterMode = FilterMode.Bilinear;
            try
            {
                material.SetFloat("_Threshold", threshold); material.SetFloat("_Intensity", intensity);
                Graphics.Blit(source, a, material, 0);
                material.SetVector("_BlurDirection", new Vector4(1f / width, 0, 0, 0));
                Graphics.Blit(a, b, material, 1);
                material.SetVector("_BlurDirection", new Vector4(0, 1f / height, 0, 0));
                Graphics.Blit(b, a, material, 1);
                material.SetTexture("_BloomTex", a); Graphics.Blit(source, destination, material, 2);
            }
            finally { RenderTexture.ReleaseTemporary(a); RenderTexture.ReleaseTemporary(b); }
        }

        /// <summary>Release only the post-effect material allocated by this component.</summary>
        private void OnDestroy() { if (material != null) Destroy(material); }
    }
}
