using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace FortyTwoAndBeyond.Client
{
    /// <summary>Animate the imported demo scenery without cameras, colliders or gameplay state.</summary>
    public sealed partial class SpaceEnvironmentBackground : MonoBehaviour
    {
        public static readonly Vector3 EarthPosition = new Vector3(-7500, 1500, 24000);
        public static readonly Vector3 JupiterPosition = new Vector3(6000, 4200, 28000);
        public static readonly Vector3 SaturnPosition = new Vector3(11500, -2600, 25500);
        public static readonly Vector3 BlackHolePosition = new Vector3(-800, -5700, 23500);
        public static readonly Vector3 SunPosition = new Vector3(13200, 1700, 22000);
        private readonly Dictionary<string, Material> materials = new Dictionary<string, Material>();
        private readonly List<Spin> spins = new List<Spin>();
        private readonly List<Material> litMaterials = new List<Material>();
        private Transform earth, moon, jupiter, saturn, rings, hole;
        private Material accretion;
        [Range(.1f, 4f)] public float motionSpeed = 1f;
        private Material photosphere, corona, saturnRings;
        private Vector3 previousSun;
        private Color currentSunColor=Color.white;
        private float elapsed;
        public Transform Sun { get; private set; }

        private sealed class Spin
        {
            public readonly Transform Body;
            public readonly Quaternion Tilt;
            public readonly float Speed;

            /// <summary>Keep an authored tilt and an independent axial rotation rate.</summary>
            public Spin(Transform body, float speed)
            { Body = body; Tilt = body.localRotation; Speed = speed; }
        }

        /// Returns:
        ///     SpaceEnvironmentBackground: An owned decorative environment using imported baked assets.
        public static SpaceEnvironmentBackground Create(Transform parent)
        {
            var root = new GameObject("Distant sky scenery");
            root.transform.SetParent(parent, false);
            var background = root.AddComponent<SpaceEnvironmentBackground>();
            background.Build();
            return background;
        }

        /// <summary>Assemble the demo's planets, Sun, original black-hole prefab and shared asteroid meshes.</summary>
        private void Build()
        {
            Mesh sphere = Load<Mesh>("Generated/PlanetSphere");
            Child(transform, "Starfield — imported procedural sky");
            Sun = Child(transform, "System sun"); Sun.localPosition = SunPosition;
            photosphere = StellarMaterial("Imported photosphere", "StellarSurface");
            corona = StellarMaterial("Imported corona", "StellarCorona");
            MeshObject(Sun, "EmissiveSurface", sphere, photosphere, 1050, Vector3.zero, 1.6f);
            MeshObject(Sun, "SolarGlow", sphere, corona, 2400, Vector3.zero);
            Transform planets = showcasePlanets = Child(transform, "Planets");
            earth = Body(planets, "Earth", EarthPosition);
            MeshObject(earth, "Surface", sphere, Material("EarthSurface"), 2400, new Vector3(0, 0, -23.4f), 2.2f);
            MeshObject(earth, "Clouds", sphere, Material("EarthClouds"), 2426, new Vector3(0, 0, -23.4f), 3.6f);
            MeshObject(earth, "Atmosphere", sphere, Material("EarthAtmosphere"), 2488, Vector3.zero);
            moon = Body(earth, "Moon", Vector3.right * 4400);
            MeshObject(moon, "Surface", sphere, Material("MoonSurface"), 640, Vector3.zero, 2.0f);
            jupiter = Body(planets, "Jupiter", JupiterPosition);
            MeshObject(jupiter, "Surface", sphere, Material("JupiterSurface"), 3000, new Vector3(0, 0, 3), 4f);
            saturn = Body(planets, "Saturn", SaturnPosition);
            MeshObject(saturn, "Surface", sphere, Material("SaturnSurface"), 2200, new Vector3(18, 0, -26.7f), 3f);
            MeshObject(saturn, "Atmosphere", sphere, Material("SaturnAtmosphere"), 2244, Vector3.zero);
            saturnRings = Material("SaturnRings");
            saturnRings.SetFloat("_PlanetRadius", 2200); saturnRings.SetVector("_PlanetCenter", saturn.position);
            rings = MeshObject(saturn, "Rings", Load<Mesh>("Generated/SaturnAnnulus"), saturnRings,
                2200f / 6f, new Vector3(18, 0, -26.7f));
            hole = Body(transform, "BlackHole", BlackHolePosition);
            GameObject prefab = Load<GameObject>("BlackHole/Prefabs/BlackHole");
            GameObject disk = Instantiate(prefab, hole, false); disk.name = "AccretionDisk";
            disk.transform.localScale = Vector3.one * 10000;
            disk.transform.localRotation = Quaternion.Euler(12, 0, 18);
            accretion = Material("CinematicAccretionDisk");
            accretion.SetFloat("_StepCount", 160); accretion.SetFloat("_Supersample", 1);
            accretion.EnableKeyword("_SUPERSAMPLE_4X"); accretion.SetFloat("_StarfieldIntensity", 1.25f);
            accretion.SetFloat("_DiskRotationSpeed", 1.6f); ConfigureSky(accretion);
            disk.GetComponent<Renderer>().sharedMaterial = accretion;
            Transform field = debrisField = Child(transform, "AsteroidField");
            var random = new System.Random(42042);
            Material stone = Material("CeresAsteroid");
            for (int index = 0; index < 56; index++)
            {
                float angle = (float)random.NextDouble() * Mathf.PI * 2;
                float radius = 9500 + (float)random.NextDouble() * 4000;
                Transform asteroid = MeshObject(field, "Distant asteroid " + index,
                    Load<Mesh>("Generated/Asteroid" + (index % 12)), stone,
                    20 + (float)random.NextDouble() * 45,
                    new Vector3(index * 27, index * 61, index * 13), 4f);
                asteroid.localPosition = new Vector3(Mathf.Cos(angle) * radius,
                    -4000 + Mathf.Sin(angle) * 1000, 28500 + Mathf.Sin(angle) * 2600);
            }
            Material("JupiterSurface").SetFloat("_BodyFlow", 1f);
            Material("SaturnSurface").SetFloat("_BodyFlow", .65f);
            photosphere.SetFloat("_FlowSpeed", .18f);
            TintSun(new Color(1f, .88f, .72f));
        }

        /// Returns:
        ///     T: A required imported asset, retained by the game build through Resources.
        /// Raises:
        ///     InvalidOperationException: A required demo asset was not imported.
        private static T Load<T>(string path) where T : UnityEngine.Object
        {
            T asset = Resources.Load<T>("SpaceEnvironment/" + path);
            if (asset == null) throw new InvalidOperationException("Missing space environment asset: " + path);
            return asset;
        }

        /// Returns:
        ///     Material: A cached per-environment copy that never mutates imported source assets.
        private Material Material(string name)
        {
            if (materials.TryGetValue(name, out Material material)) return material;
            material = new Material(Load<Material>("Generated/" + name)) { name = "Scenery " + name };
            materials.Add(name, material);
            if (material.HasProperty("_SunPosition")) litMaterials.Add(material);
            if (material.HasProperty("_Ambient")) material.SetFloat("_Ambient", .24f);
            return material;
        }

        /// Returns:
        ///     Material: An owned Unity port of the imported procedural stellar asset.
        private Material StellarMaterial(string name, string shader)
        {
            var material=new Material(Load<Shader>("Shaders/"+shader)){name=name};
            materials.Add(name,material);return material;
        }

        /// Returns:
        ///     Transform: An identity child used to group independently animated scenery.
        private static Transform Child(Transform parent, string name)
        {
            var child = new GameObject(name).transform; child.SetParent(parent, false); child.gameObject.layer=parent.gameObject.layer; return child;
        }

        /// Returns:
        ///     Transform: A locally positioned grouping transform without gameplay components.
        private static Transform Body(Transform parent, string name, Vector3 position)
        { Transform body = Child(parent, name); body.localPosition = position; return body; }

        /// Returns:
        ///     Transform: A shared textured mesh with no collider, shadows or targeting marker.
        private Transform MeshObject(Transform parent, string name, Mesh mesh, Material material,
            float scale, Vector3 tilt, float rotationSpeed = 0)
        {
            Transform part = Child(parent, name); part.localScale = Vector3.one * scale;
            part.localRotation = Quaternion.Euler(tilt);
            part.gameObject.AddComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = part.gameObject.AddComponent<MeshRenderer>(); renderer.sharedMaterial = material;
            renderer.shadowCastingMode = ShadowCastingMode.Off; renderer.receiveShadows = false;
            renderer.lightProbeUsage = LightProbeUsage.Off; renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
            if (rotationSpeed != 0) spins.Add(new Spin(part, rotationSpeed));
            return part;
        }

        /// <summary>Use identical star-map and nebula inputs for the skybox and the black-hole lens.</summary>
        public static void ConfigureSky(Material material)
        {
            material.SetTexture("_DeepStars",Load<Texture2D>("Textures/NASADeepStars2020"));
            material.SetTexture("_NebulaTex",Resources.Load<Texture2D>("NebulaPanorama"));
            material.SetFloat("_DeepStarExposure",2.6f);
        }

        /// <summary>Match planet lighting and the textured star to the sector's actual visible Sun.</summary>
        public void TintSun(Color color)
        {
            currentSunColor=color;
            photosphere.SetColor("_Tint", Color.Lerp(Color.white, color, .18f));
            corona.SetColor("_Tint", Color.Lerp(Color.white, color, .15f));
            previousSun = Sun.position;
            foreach (Material material in litMaterials)
            {
                material.SetVector("_SunPosition", previousSun);
                if (material.HasProperty("_LightColor")) material.SetColor("_LightColor", color);
            }
        }

        /// <summary>Advance only inexpensive transforms; the Sun and accretion turbulence run on the GPU.</summary>
        private void Update()
        {
            elapsed += Time.unscaledDeltaTime * motionSpeed;
            if(sectorProfile!=null)UpdateSectorBodies(elapsed);
            else
            {
            earth.localPosition = EarthPosition + new Vector3(Mathf.Sin(elapsed*.045f)*260,Mathf.Sin(elapsed*.067f)*180,0);
            jupiter.localPosition = JupiterPosition + new Vector3(Mathf.Sin(elapsed*.035f)*360,Mathf.Sin(elapsed*.052f)*260,0);
            saturn.localPosition = SaturnPosition + new Vector3(Mathf.Sin(elapsed*.032f)*280,Mathf.Sin(elapsed*.047f)*200,0);
            rings.localRotation = Quaternion.Euler(18+Mathf.Sin(elapsed*.12f)*3,elapsed*.6f,-26.7f+Mathf.Sin(elapsed*.09f)*3);
            saturnRings.SetVector("_PlanetCenter",saturn.position);
            hole.localRotation = Quaternion.Euler(Mathf.Sin(elapsed*.1f)*2,0,Mathf.Sin(elapsed*.08f)*2);
            }
            accretion.SetFloat("_DiskBrightness",6.5f*(1+Mathf.Sin(elapsed*.7f)*.18f+Mathf.Sin(elapsed*1.8f)*.1f));
            foreach (Spin spin in spins)
                spin.Body.localRotation = spin.Tilt * Quaternion.AngleAxis(elapsed * spin.Speed, Vector3.up);
            float angle = elapsed * 3.5f * Mathf.Deg2Rad;
            if(sectorProfile==null)moon.localPosition = new Vector3(Mathf.Cos(angle), Mathf.Sin(angle) * .09f, Mathf.Sin(angle)) * 4400;
            if ((Sun.position - previousSun).sqrMagnitude > .01f)
            {
                previousSun = Sun.position;
                foreach (Material material in litMaterials) material.SetVector("_SunPosition", previousSun);
            }
        }

        /// <summary>Release the cloned materials when the scenery owner is removed.</summary>
        private void OnDestroy()
        { foreach (Material material in materials.Values) Destroy(material); materials.Clear(); }
    }
}
