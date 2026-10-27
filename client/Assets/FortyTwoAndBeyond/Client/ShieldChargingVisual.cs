using UnityEngine;
using Object = UnityEngine.Object;

namespace FortyTwoAndBeyond.Client
{
    /// <summary>A persistent TT shield field that absorbs impacts on its own membrane.</summary>
    public sealed class ShieldChargingVisual
    {
        private readonly GameObject shell;
        private readonly Material material;
        private float radius;
        private float impactAt = -10f;
        private Vector3 impactDirection = Vector3.forward;

        /// <returns>bool: Whether the charging membrane has a packaged shader.</returns>
        public bool Active => shell != null;

        /// <summary>Create a light blue field using the shield impact sphere.</summary>
        public ShieldChargingVisual(float radius)
        {
            Shader shader = Resources.Load<Shader>("ShieldCharging");
            if (shader == null)
            {
                Debug.LogError("Shield Charging shader is missing.");
                return;
            }
            material = new Material(shader);
            Texture2D flow = Resources.Load<Texture2D>("ShieldFlow");
            if (flow != null)
            {
                flow.wrapMode = TextureWrapMode.Repeat;
                material.SetTexture("_FlowTex", flow);
            }
            material.SetColor("_Tint", new Color(.28f, .78f, 1f, 1f));
            shell = new GameObject("Shield Charging field", typeof(MeshFilter),
                typeof(MeshRenderer));
            shell.GetComponent<MeshFilter>().sharedMesh = ShieldHitVisual.ShieldMesh();
            shell.GetComponent<MeshRenderer>().sharedMaterial = material;
            SetRadius(radius);
        }

        /// <summary>Keep the field outside the ship's current visible hull.</summary>
        public void SetRadius(float value)
        {
            radius = Mathf.Max(.45f, value) * 1.045f;
            if (shell != null) shell.transform.localScale = Vector3.one * (radius * 2f);
        }

        /// <summary>Bend and brighten the persistent field at an incoming contact.</summary>
        public void Impact(Vector3 incomingDirection)
        {
            if (!Active || Time.unscaledTime - impactAt < .2f) return;
            impactDirection = incomingDirection.sqrMagnitude > .001f ?
                incomingDirection.normalized : Vector3.forward;
            impactAt = Time.unscaledTime;
        }

        /// <summary>Animate the persistent field and its contact response.</summary>
        public void Tick(Vector3 center)
        {
            if (!Active) return;
            shell.transform.position = center;
            material.SetVector("_HitDirection", impactDirection);
            material.SetFloat("_HitAge", Mathf.Clamp01((Time.unscaledTime - impactAt) /
                ShieldHitVisual.Duration));
        }

        /// <summary>Release the shell and its owned material when the server buff ends.</summary>
        public void Dispose()
        {
            if (shell != null) Object.Destroy(shell);
            if (material != null) Object.Destroy(material);
        }
    }
}
