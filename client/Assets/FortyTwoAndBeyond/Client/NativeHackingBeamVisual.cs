using UnityEngine;
using Object = UnityEngine.Object;

namespace FortyTwoAndBeyond.Client
{
    /// <summary>Render the original animated Hacking line texture on a target.</summary>
    public sealed class NativeHackingBeamVisual
    {
        private readonly GameObject root;
        private readonly LineRenderer line;
        private readonly Material material;
        private readonly float startedAt;

        /// <returns>bool: Whether the archived sprite was bound to a line renderer.</returns>
        public bool Active => line != null;

        /// <summary>Attach the original eight-tile native Hacking delivery sprite.</summary>
        public NativeHackingBeamVisual(Transform parent)
        {
            Shader shader = Resources.Load<Shader>("LegacyEffect");
            if (shader == null) shader = Shader.Find("42 and Beyond/LegacyEffect");
            Texture2D sprite = Resources.Load<Texture2D>("LegacyEffects/sk_hack02");
            if (parent == null || shader == null || sprite == null)
            {
                Debug.LogError("Original Hacking beam shader or texture is missing.");
                return;
            }
            root = new GameObject("Original Hacking beam", typeof(LineRenderer));
            root.transform.SetParent(parent, false);
            line = root.GetComponent<LineRenderer>();
            line.positionCount = 2;
            line.useWorldSpace = true;
            line.alignment = LineAlignment.View;
            line.textureMode = LineTextureMode.Tile;
            line.widthMultiplier = .16f;
            line.numCapVertices = 0;
            line.enabled = false;
            material = new Material(shader);
            material.SetTexture("_MainTex", sprite);
            material.SetTextureScale("_MainTex", new Vector2(8f, 1f));
            material.SetFloat("_UseSourceColor", 1f);
            material.SetColor("_Tint", new Color(.45f, 1f, .72f, .8f));
            line.sharedMaterial = material;
            startedAt = Time.unscaledTime;
        }

        /// <summary>Join the pilot to the target while the server effect exists.</summary>
        public void Tick(Vector3 source, Vector3 target)
        {
            if (line == null) return;
            line.enabled = true;
            line.SetPosition(0, source);
            line.SetPosition(1, target);
            material.SetTextureOffset("_MainTex", new Vector2(0,
                -1.5f * (Time.unscaledTime - startedAt)));
        }

        /// <summary>Hide the beam while target position has not arrived.</summary>
        public void Hide()
        {
            if (line != null) line.enabled = false;
        }

        /// <summary>Release the native beam and scene-owned material.</summary>
        public void Dispose()
        {
            if (root != null) Object.Destroy(root);
            if (material != null) Object.Destroy(material);
        }
    }
}
