using UnityEngine;
using Object = UnityEngine.Object;

namespace FortyTwoAndBeyond.Client
{
    /// <summary>Bend and illuminate a shield at beam contact or confirmed damage.</summary>
    public sealed class ShieldHitVisual
    {
        public const float Duration = 1.1f;
        private static Mesh sharedShieldMesh;
        private readonly GameObject shell;
        private readonly Material shellMaterial;
        private readonly Material sparkMaterial;
        private readonly GameObject[] sparks = new GameObject[8];
        private readonly float radius;
        private readonly Vector3 direction;
        private readonly Vector3 tangentRight;
        private readonly Vector3 tangentUp;

        /// <returns>bool: Whether the shield membrane was created.</returns>
        public bool Active => shell != null;

        /// <summary>Prepare a dense deformable membrane and the impact's energy traces.</summary>
        public ShieldHitVisual(float radius, Vector3 incomingDirection)
        {
            this.radius = Mathf.Max(.45f, radius);
            direction = incomingDirection.sqrMagnitude > .001f ?
                incomingDirection.normalized : Vector3.forward;
            tangentRight = Vector3.Cross(direction,
                Mathf.Abs(Vector3.Dot(direction, Vector3.up)) > .9f ?
                Vector3.right : Vector3.up).normalized;
            tangentUp = Vector3.Cross(direction, tangentRight).normalized;
            Shader shellShader = Resources.Load<Shader>("ShieldImpact");
            Shader particleShader = Resources.Load<Shader>("LegacyEffect");
            Texture2D particleTexture = Resources.Load<Texture2D>("LegacyEffects/SH_PART");
            Texture2D flowTexture = Resources.Load<Texture2D>("ShieldFlow");
            if (shellShader == null || particleShader == null)
            {
                Debug.LogError("Shield impact shader is missing.");
                return;
            }
            shell = new GameObject("Confirmed shield impact envelope", typeof(MeshFilter),
                typeof(MeshRenderer));
            shell.name = "Confirmed shield impact envelope";
            shell.GetComponent<MeshFilter>().sharedMesh = ShieldMesh();
            shell.transform.localScale = Vector3.one * this.radius * 2f;
            shellMaterial = new Material(shellShader);
            shellMaterial.SetColor("_Tint", new Color(.3f, .85f, 1f, .9f));
            shellMaterial.SetVector("_HitDirection", direction);
            if (flowTexture != null)
            {
                flowTexture.wrapMode = TextureWrapMode.Repeat;
                shellMaterial.SetTexture("_FlowTex", flowTexture);
            }
            else Debug.LogError("Shield flowing-energy texture is missing.");
            shell.GetComponent<MeshRenderer>().sharedMaterial = shellMaterial;
            if (particleTexture == null) return;
            sparkMaterial = new Material(particleShader);
            sparkMaterial.SetTexture("_MainTex", particleTexture);
            sparkMaterial.SetColor("_Tint", new Color(.8f, .95f, 1f, .9f));
            for (int index = 0; index < sparks.Length; index++)
            {
                sparks[index] = GameObject.CreatePrimitive(PrimitiveType.Quad);
                sparks[index].name = "Original shield hit spark";
                Object.Destroy(sparks[index].GetComponent<Collider>());
                sparks[index].GetComponent<MeshRenderer>().sharedMaterial = sparkMaterial;
            }
        }

        /// <returns>Mesh: A smooth sphere with enough vertices for the impact dent.</returns>
        internal static Mesh ShieldMesh()
        {
            if (sharedShieldMesh != null) return sharedShieldMesh;
            const int segments = 64, rings = 32;
            var vertices = new Vector3[(segments + 1) * (rings + 1)];
            var normals = new Vector3[vertices.Length];
            var uvs = new Vector2[vertices.Length];
            var triangles = new int[segments * rings * 6];
            for (int row = 0; row <= rings; row++)
            {
                float polar = Mathf.PI * row / rings;
                for (int column = 0; column <= segments; column++)
                {
                    float azimuth = Mathf.PI * 2f * column / segments;
                    int index = row * (segments + 1) + column;
                    normals[index] = new Vector3(Mathf.Sin(polar) * Mathf.Cos(azimuth),
                        Mathf.Cos(polar), Mathf.Sin(polar) * Mathf.Sin(azimuth));
                    vertices[index] = normals[index] * .5f;
                    uvs[index] = new Vector2((float)column / segments, (float)row / rings);
                }
            }
            int next = 0;
            for (int row = 0; row < rings; row++)
                for (int column = 0; column < segments; column++)
                {
                    int top = row * (segments + 1) + column;
                    int bottom = top + segments + 1;
                    triangles[next++] = top; triangles[next++] = top + 1;
                    triangles[next++] = bottom;
                    triangles[next++] = top + 1; triangles[next++] = bottom + 1;
                    triangles[next++] = bottom;
                }
            sharedShieldMesh = new Mesh { name = "Deformable shield membrane" };
            sharedShieldMesh.vertices = vertices;
            sharedShieldMesh.normals = normals;
            sharedShieldMesh.uv = uvs;
            sharedShieldMesh.triangles = triangles;
            sharedShieldMesh.RecalculateBounds();
            return sharedShieldMesh;
        }

        /// <returns>float: Amount the membrane is pushed inward at this time.</returns>
        private static float Compression(float progress)
        {
            float rise = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(progress / .12f));
            float fall = 1f - Mathf.SmoothStep(0f, 1f,
                Mathf.Clamp01((progress - .22f) / .4f));
            float rebound = .12f * Mathf.Exp(-Mathf.Pow((progress - .7f) * 14f, 2f));
            return rise * fall - rebound;
        }

        /// <returns>Vector3: The hit point on the currently bent shield.</returns>
        public Vector3 ContactPoint(Vector3 center, float age)
        {
            float progress = Mathf.Clamp01(age / Duration);
            return center + direction * radius * (1f - .34f * Compression(progress));
        }

        /// <summary>Animate the dent, light-blue field, and absorbed particles.</summary>
        public void Tick(Vector3 center, float age)
        {
            if (!Active) return;
            float progress = Mathf.Clamp01(age / Duration);
            shell.transform.position = center;
            float wobble = .035f * Mathf.Sin(progress * 37f) *
                Mathf.Exp(-progress * 3.2f);
            shell.transform.localScale = Vector3.one * radius * 2f * (1f + wobble);
            shellMaterial.SetFloat("_Age", progress);
            if (sparkMaterial == null) return;
            sparkMaterial.SetColor("_Tint", new Color(.8f, .95f, 1f,
                .9f * (1f - progress)));
            for (int index = 0; index < sparks.Length; index++)
            {
                float angle = index * Mathf.PI * 2f / sparks.Length + index * .16f;
                Vector3 radial = tangentRight * Mathf.Cos(angle) +
                    tangentUp * Mathf.Sin(angle);
                sparks[index].transform.position = ContactPoint(center, age) +
                    radial * radius * progress * .25f -
                    direction * radius * progress * .65f;
                sparks[index].transform.rotation = Quaternion.LookRotation(direction);
                sparks[index].transform.localScale = Vector3.one * radius *
                    Mathf.Lerp(.12f, .025f, progress);
            }
        }

        /// <summary>Remove the temporary shield shell, particles, and materials.</summary>
        public void Dispose()
        {
            if (shell != null) Object.Destroy(shell);
            foreach (GameObject spark in sparks)
                if (spark != null) Object.Destroy(spark);
            if (shellMaterial != null) Object.Destroy(shellMaterial);
            if (sparkMaterial != null) Object.Destroy(sparkMaterial);
        }
    }
}
