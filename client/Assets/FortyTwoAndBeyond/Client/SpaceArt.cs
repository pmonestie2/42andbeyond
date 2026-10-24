using System;
using System.Collections.Generic;
using FortyTwoAndBeyond.Protocol;
using UnityEngine;
using Object = UnityEngine.Object;

namespace FortyTwoAndBeyond.Client
{
    /// <summary>Own reusable procedural models and materials for the local 3D client.</summary>
    public sealed class SpaceArt : IDisposable
    {
        private readonly List<Material> materials = new List<Material>();
        private readonly List<Mesh> meshes = new List<Mesh>();
        private readonly Mesh[] rocks = new Mesh[8];
        private readonly Mesh ring, shipHull;
        private readonly GameObject corvetteAsset, frigateAsset;
        private readonly Material corvetteSurface, frigateSurface, hostileFrigateSurface;
        private readonly Material hull, trim, dark, glass, glow, amber, hostile, crewSignal, mineral, rock, oreRock, field;
        public Material Sky { get; }
        public Material EffectLine { get; }

        /// <summary>Create shared assets once rather than allocating them per object or frame.</summary>
        public SpaceArt()
        {
            hull = Surface(new Color(.46f, .55f, .62f), .7f, .45f);
            trim = Surface(new Color(.08f, .15f, .2f), .8f, .5f);
            dark = Surface(new Color(.025f, .035f, .05f), .6f, .3f);
            hull.SetFloat("_Panels", 1); trim.SetFloat("_Panels", .65f); dark.SetFloat("_Panels", .5f);
            Texture2D plating = LoadDetail("HullPlating");
            foreach (Material surface in new[] { hull, trim, dark })
            { surface.SetTexture("_DetailTex", plating); surface.SetFloat("_TextureStrength", .8f); }
            glass = Surface(new Color(.015f, .12f, .18f), .8f, .85f);
            glow = Surface(new Color(.05f, .6f, .85f), .2f, .5f, new Color(.1f, 1.4f, 2.1f));
            amber = Surface(new Color(.8f, .35f, .06f), .4f, .3f, new Color(1.3f, .35f, .04f));
            hostile = Surface(new Color(.7f, .045f, .035f), .25f, .5f, new Color(1.9f, .08f, .03f));
            crewSignal = Surface(new Color(.08f, .7f, .36f), .2f, .45f, new Color(.12f, 1.5f, .6f));
            mineral = Surface(new Color(.04f, .62f, .72f), .35f, .65f, new Color(.08f, 1.15f, 1.4f));
            rock = Surface(new Color(.24f, .21f, .19f), .08f, .12f);
            rock.SetFloat("_Grain", 1);
            oreRock = Surface(new Color(.36f, .41f, .46f), .08f, .12f);
            oreRock.SetTexture("_DetailTex", LoadDetail("OreRock"));
            oreRock.SetFloat("_TextureStrength", .95f);
            field = ShaderMaterial("42 and Beyond/GateField");
            Sky = ShaderMaterial("42 and Beyond/SpaceSky");
            Shader effectShader = Resources.Load<Shader>("BeamAdditive");
            if (effectShader != null) { EffectLine = new Material(effectShader); materials.Add(EffectLine); }
            corvetteAsset = Resources.Load<GameObject>("ViktorShips/corvette03");
            frigateAsset = Resources.Load<GameObject>("ViktorShips/frigate03");
            corvetteSurface = ShipSurface("corvette03");
            frigateSurface = ShipSurface("frigate03");
            if (frigateSurface != null)
            {
                hostileFrigateSurface = new Material(frigateSurface);
                hostileFrigateSurface.SetColor("_Color", new Color(.94f, .65f, .6f));
                materials.Add(hostileFrigateSurface);
            }
            ring = Torus(1, .12f, 48, 8); meshes.Add(ring);
            shipHull = Hull(); meshes.Add(shipHull);
            for (int i = 0; i < rocks.Length; i++) { rocks[i] = Asteroid(i + 17); meshes.Add(rocks[i]); }
        }

        /// <returns>An owned lit material with optional emissive color.</returns>
        private Material Surface(Color color, float metal, float smoothness, Color emission = default)
        {
            Material material = ShaderMaterial("42 and Beyond/Surface");
            material.color = color; material.SetFloat("_Metallic", metal); material.SetFloat("_Glossiness", smoothness);
            material.SetColor("_EmissionColor", emission); return material;
        }

        /// <returns>An owned material referencing a shader included in Resources.</returns>
        private Material ShaderMaterial(string name)
        {
            Shader shader = Shader.Find(name);
            if (shader == null) throw new InvalidOperationException("Missing shader: " + name);
            var material = new Material(shader); materials.Add(material); return material;
        }

        /// <returns>Texture2D: A Unity-owned bitmap detail texture included with the client.</returns>
        private static Texture2D LoadDetail(string name)
        {
            Texture2D texture = Resources.Load<Texture2D>(name);
            if (texture == null) throw new InvalidOperationException("Missing surface texture: " + name);
            texture.wrapMode = TextureWrapMode.Repeat;
            return texture;
        }

        /// <returns>A parented model root in local coordinates.</returns>
        private static Transform Root(Transform parent, string name)
        { var obj = new GameObject(name); obj.transform.SetParent(parent, false); return obj.transform; }

        /// <returns>A solid primitive without local collision authority.</returns>
        private static Transform Part(Transform parent, PrimitiveType shape, Vector3 position, Vector3 scale, Material material)
        {
            GameObject obj = GameObject.CreatePrimitive(shape); obj.transform.SetParent(parent, false);
            obj.transform.localPosition = position; obj.transform.localScale = scale;
            obj.GetComponent<Renderer>().sharedMaterial = material;
            Object.Destroy(obj.GetComponent<Collider>()); return obj.transform;
        }

        /// <returns>A mesh instance sharing the factory's owned geometry.</returns>
        private static Transform Model(Transform parent, Mesh mesh, Material material, Vector3 position, Vector3 scale)
        {
            var obj = new GameObject(mesh.name, typeof(MeshFilter), typeof(MeshRenderer));
            obj.transform.SetParent(parent, false); obj.transform.localPosition = position; obj.transform.localScale = scale;
            obj.GetComponent<MeshFilter>().sharedMesh = mesh; obj.GetComponent<MeshRenderer>().sharedMaterial = material;
            return obj.transform;
        }

        /// <returns>Material: A textured material, or null when no compatible shader is available.</returns>
        private Material ShipSurface(string assetName)
        {
            Texture2D diffuse = Resources.Load<Texture2D>("ViktorShips/" + assetName + "_diffuse");
            if (diffuse == null) return null;
            Shader shader = Resources.Load<Shader>("TexturedShip");
            bool textured = shader != null;
            if (shader == null) shader = Resources.Load<Shader>("Surface");
            if (shader == null) return null;
            var surface = new Material(shader); materials.Add(surface);
            surface.SetColor("_Color", new Color(.72f, .75f, .8f));
            if (!textured)
            {
                surface.SetTexture("_DetailTex", diffuse);
                surface.SetFloat("_TextureStrength", .8f);
                return surface;
            }
            surface.SetTexture("_MainTex", diffuse);
            Texture2D normal = Resources.Load<Texture2D>("ViktorShips/" + assetName + "_normal");
            Texture2D specular = Resources.Load<Texture2D>("ViktorShips/" + assetName + "_specular");
            Texture2D emission = Resources.Load<Texture2D>("ViktorShips/" + assetName + "_emission");
            if (normal != null) surface.SetTexture("_BumpMap", normal);
            if (specular != null) surface.SetTexture("_SpecGlossMap", specular);
            if (emission != null) surface.SetTexture("_EmissionMap", emission);
            return surface;
        }

        /// <returns>Transform: A textured ship model instance, or null when its optional asset is absent.</returns>
        private static Transform Imported(Transform parent, GameObject asset, string name, Vector3 scale,
            Material surface)
        {
            if (asset == null || surface == null) return null;
            GameObject instance = Object.Instantiate(asset, parent);
            instance.name = name; instance.transform.localPosition = Vector3.zero;
            instance.transform.localRotation = Quaternion.identity; instance.transform.localScale = scale;
            foreach (Collider collider in instance.GetComponentsInChildren<Collider>()) Object.Destroy(collider);
            foreach (Renderer renderer in instance.GetComponentsInChildren<Renderer>())
                renderer.sharedMaterial = surface;
            return instance.transform;
        }

        /// <returns>A spacecraft with a solid hull, swept wings, cockpit and twin engines.</returns>
        public Transform Ship(Transform parent)
        {
            Transform root = Root(parent, "Explorer spacecraft");
            if (Imported(root, corvetteAsset, "Viktor Hahn corvette", Vector3.one * 1.6f, corvetteSurface) != null)
            {
                return root;
            }
            Model(root, shipHull, hull, Vector3.zero, new Vector3(.55f, .55f, .75f));
            Model(root, shipHull, glass, new Vector3(0, .15f, .15f), new Vector3(.22f, .34f, .32f));
            Part(root, PrimitiveType.Cube, new Vector3(0, -.08f, -.45f), new Vector3(.22f, .12f, 1.3f), trim);
            Part(root, PrimitiveType.Cube, new Vector3(0, .3f, -.5f), new Vector3(.08f, .14f, .62f), glow);
            Transform nose = Model(root, shipHull, trim, new Vector3(0, -.03f, 1.02f),
                new Vector3(.26f, .16f, .5f));
            nose.localRotation = Quaternion.Euler(0, 180, 0);
            for (int side = -1; side <= 1; side += 2)
            {
                Transform wing = Model(root, shipHull, trim, new Vector3(side * .65f, -.03f, -.3f), new Vector3(.5f, .1f, .48f));
                wing.localRotation = Quaternion.Euler(0, side * -28, side * 8);
                Part(root, PrimitiveType.Cube, new Vector3(side * .42f, .08f, -.35f), new Vector3(.25f, .26f, 1.1f), hull);
                Part(root, PrimitiveType.Cube, new Vector3(side * .85f, -.02f, -.2f),
                    new Vector3(.54f, .07f, .1f), glow);
                Part(root, PrimitiveType.Cube, new Vector3(side * 1.03f, .12f, -.65f),
                    new Vector3(.08f, .3f, .48f), hull);
                Transform engine = Part(root, PrimitiveType.Cylinder, new Vector3(side * .42f, .08f, -.95f), new Vector3(.24f, .22f, .24f), dark);
                engine.localRotation = Quaternion.Euler(90, 0, 0);
                Transform flame = Part(root, PrimitiveType.Sphere, new Vector3(side * .42f, .08f, -1.2f), new Vector3(.14f, .14f, .45f), glow);
                flame.name = "Engine glow";
                Part(root, PrimitiveType.Cube, new Vector3(side * .7f, .02f, -.6f), new Vector3(.04f, .04f, .3f), glow);
            }
            return root;
        }

        /// <returns>A compact procedural docking bay surrounding the parked ship.</returns>
        public Transform Hangar(Transform parent)
        {
            Transform root = Root(parent, "Station docking bay");
            Part(root, PrimitiveType.Cube, new Vector3(0, -.25f, 0), new Vector3(18, .4f, 24), dark);
            Part(root, PrimitiveType.Cube, new Vector3(0, 6, 11.5f), new Vector3(18, 12, .8f), trim);
            for (int side = -1; side <= 1; side += 2)
            {
                Part(root, PrimitiveType.Cube, new Vector3(side * 8.5f, 5, 0), new Vector3(.7f, 10, 24), trim);
                Part(root, PrimitiveType.Cube, new Vector3(side * 5.4f, .05f, 0), new Vector3(.08f, .04f, 18), glow);
                for (int i = -2; i <= 2; i++)
                {
                    Part(root, PrimitiveType.Cube, new Vector3(side * 8.05f, 4.5f, i * 4),
                        new Vector3(.12f, 7, .18f), hull);
                    Part(root, PrimitiveType.Cube, new Vector3(side * 7.9f, 7.5f, i * 4),
                        new Vector3(.2f, .14f, 2.2f), glow);
                }
            }
            Part(root, PrimitiveType.Cylinder, new Vector3(0, 0, 0), new Vector3(5, .12f, 5), hull);
            Part(root, PrimitiveType.Cylinder, new Vector3(0, .1f, 0), new Vector3(3.7f, .04f, 3.7f), trim);
            for (int i = 0; i < 12; i++)
            {
                float angle = i * Mathf.PI / 6;
                Part(root, PrimitiveType.Cube, new Vector3(Mathf.Cos(angle) * 4.3f, .2f, Mathf.Sin(angle) * 4.3f),
                    new Vector3(.65f, .08f, .18f), amber);
            }
            return root;
        }

        /// <returns>A station concourse with a distinct floor, kiosks and luminous ceiling ribs.</returns>
        public Transform StationConcourse(Transform parent)
        {
            Transform root = Root(parent, "Station concourse");
            Part(root, PrimitiveType.Cube, new Vector3(0, -.25f, 0), new Vector3(20, .4f, 25), dark);
            Part(root, PrimitiveType.Cube, new Vector3(0, 7, 11.5f), new Vector3(20, 14, .8f), trim);
            Part(root, PrimitiveType.Cube, new Vector3(0, 8.5f, 0), new Vector3(20, .5f, 25), hull);
            for (int side = -1; side <= 1; side += 2)
            {
                Part(root, PrimitiveType.Cube, new Vector3(side * 9.5f, 4, 0), new Vector3(.7f, 8, 25), trim);
                Part(root, PrimitiveType.Cube, new Vector3(side * 7.5f, .03f, 0), new Vector3(.1f, .04f, 22), glow);
                for (int stall = -1; stall <= 1; stall++)
                {
                    float z = stall * 6;
                    Part(root, PrimitiveType.Cube, new Vector3(side * 7.2f, 1.1f, z), new Vector3(3.4f, 1.4f, 3.2f), hull);
                    Part(root, PrimitiveType.Cube, new Vector3(side * 7.2f, 2.7f, z + 1.35f), new Vector3(2.5f, 1.2f, .08f), glass);
                    Part(root, PrimitiveType.Cube, new Vector3(side * 7.2f, 3.45f, z + 1.35f), new Vector3(2.7f, .08f, .1f), glow);
                    Part(root, PrimitiveType.Cube, new Vector3(side * 9.1f, 4.5f, z + 3), new Vector3(.16f, 8, .18f), hull);
                }
            }
            for (int rib = -2; rib <= 2; rib++)
            {
                Part(root, PrimitiveType.Cube, new Vector3(0, 7.9f, rib * 5), new Vector3(18, .16f, .18f), glow);
                Part(root, PrimitiveType.Cube, new Vector3(0, .025f, rib * 5), new Vector3(14, .035f, .1f), amber);
            }
            Part(root, PrimitiveType.Cylinder, new Vector3(0, .02f, 2), new Vector3(2.4f, .06f, 2.4f), hull);
            Transform medallion = Model(root, ring, glow, new Vector3(0, .08f, 2), Vector3.one * 2.3f);
            medallion.localRotation = Quaternion.Euler(90, 0, 0);
            return root;
        }

        /// <returns>A compact station pilot silhouette built from shared materials.</returns>
        public Transform StationAvatar(Transform parent)
        {
            Transform root = Root(parent, "Station pilot");
            Part(root, PrimitiveType.Capsule, new Vector3(0, 1.1f, 0), new Vector3(.55f, .8f, .42f), hull);
            Part(root, PrimitiveType.Sphere, new Vector3(0, 2.1f, 0), new Vector3(.48f, .48f, .48f), glass);
            Part(root, PrimitiveType.Cube, new Vector3(0, 1.38f, .26f), new Vector3(.32f, .14f, .05f), glow);
            for (int side = -1; side <= 1; side += 2)
            {
                Part(root, PrimitiveType.Capsule, new Vector3(side * .48f, 1.25f, 0), new Vector3(.22f, .6f, .26f), trim);
                Part(root, PrimitiveType.Capsule, new Vector3(side * .2f, .38f, 0), new Vector3(.25f, .45f, .3f), dark);
            }
            return root;
        }

        /// <returns>A distinct procedural NPC craft using shared rendering assets.</returns>
        public Transform Mob(Transform parent, bool isHostile)
        {
            Transform root = Root(parent, "Server mob");
            Material surface = isHostile ? hostileFrigateSurface ?? frigateSurface : frigateSurface;
            if (Imported(root, frigateAsset, "Viktor Hahn frigate", Vector3.one * 1.5f, surface) != null)
            {
                return root;
            }
            Model(root, shipHull, isHostile ? hostile : dark, Vector3.zero, new Vector3(.8f, .5f, 1.2f));
            Part(root, PrimitiveType.Sphere, new Vector3(0, .2f, .4f), new Vector3(.65f, .4f, .65f),
                isHostile ? hostile : amber);
            for (int side = -1; side <= 1; side += 2)
            {
                Transform wing = Model(root, shipHull, rock, new Vector3(side * .75f, 0, 0), new Vector3(.4f, .5f, .9f));
                wing.localRotation = Quaternion.Euler(0, side * 35, side * 25);
                Part(root, PrimitiveType.Sphere, new Vector3(side * .6f, 0, -.8f), new Vector3(.25f, .25f, .6f), amber);
            }
            return root;
        }

        /// <returns>Transform: A compact, affiliation-colored scanner marker beneath a visible ship.</returns>
        public Transform ContactMarker(Transform ship, bool isPlayer, bool isHostile, bool isCrew,
            bool selected)
        {
            Transform marker = Root(ship, selected ? "Selected target outline" : isCrew ? "Crew scanner outline" :
                isPlayer ? "Player scanner outline" : "Mob scanner outline");
            marker.localScale = Vector3.one / Mathf.Max(.01f, ship.localScale.x);
            Material signal = selected ? amber : isCrew ? crewSignal : isPlayer ? glow :
                isHostile ? hostile : amber;
            Model(marker, ring, signal, Vector3.zero, Vector3.one * (selected ? .37f : isCrew ? .29f : .22f));
            for (int side = -1; side <= 1; side += 2)
                Part(marker, PrimitiveType.Cube, new Vector3(side * (selected ? .45f : .29f), 0, 0),
                    new Vector3(selected ? .13f : .08f, .02f, .02f), signal);
            if (isCrew)
                Part(marker, PrimitiveType.Cube, new Vector3(0, .36f, 0),
                    new Vector3(.18f, .045f, .025f), signal);
            if (selected)
                Part(marker, PrimitiveType.Cube, new Vector3(0, -.45f, 0),
                    new Vector3(.18f, .04f, .025f), signal);
            return marker;
        }

        /// <returns>A solid replacement model for a server object, or null for invisible boundary markers.</returns>
        public Transform WorldObject(Transform parent, MapObject item)
        {
            if (item.Asset == 198 || item.Asset == 217 || item.Asset == 0) return null;
            Transform root = Root(parent, item.Name);
            if (item.Kind == 11) Gate(root);
            else if (item.Kind == 12) Station(root);
            else if (item.Kind == 25) { Wreck(root); root.localScale *= .015f; }
            else if (item.Kind == 38) { ResourceRock(root, item.Id); root.localScale *= .72f; }
            else if (item.Asset == 412 || item.Asset == 377) { Station(root); root.localScale *= .03f; }
            else if (item.Asset == 127) { Beacon(root); root.localScale *= .2f; }
            else
            {
                float radius = item.Navigable ? .4f : item.Asset == 491 ? 3.8f : item.Asset == 490 ? 2.7f : 1.6f;
                int variant = (int)(item.Id % (uint)rocks.Length);
                Model(root, rocks[variant], rock, Vector3.zero, new Vector3(radius, radius * .8f, radius * 1.2f));
            }
            root.localScale *= Mathf.Clamp(item.Scale, .05f, 100);
            return root;
        }

        /// <summary>Show a fallen ship as identifiable debris with a warm reactor glow.</summary>
        private void Wreck(Transform root)
        {
            Transform imported = Imported(root, frigateAsset, "Wrecked frigate", Vector3.one * .85f, frigateSurface);
            if (imported != null)
            {
                imported.localRotation = Quaternion.Euler(24, 31, -28);
                Part(root, PrimitiveType.Sphere, new Vector3(0, 0, -.8f), Vector3.one * .28f, amber);
                return;
            }
            Transform hullPiece = Model(root, shipHull, dark, Vector3.zero, new Vector3(.9f, .3f, 1.4f));
            hullPiece.localRotation = Quaternion.Euler(25, 18, -35);
            Transform wing = Model(root, shipHull, rock, new Vector3(-.8f, -.18f, .45f), new Vector3(.55f, .15f, .8f));
            wing.localRotation = Quaternion.Euler(-35, -26, 22);
            Part(root, PrimitiveType.Sphere, new Vector3(.3f, 0, -.2f), new Vector3(.3f, .25f, .3f), amber);
        }

        /// <summary>Expose bright mineral seams and facets on mineable asteroids.</summary>
        private void ResourceRock(Transform root, uint id)
        {
            Model(root, rocks[(int)(id % (uint)rocks.Length)], oreRock, Vector3.zero,
                new Vector3(2.1f, 1.5f, 1.9f));
            for (int i = 0; i < 5; i++)
            {
                float angle = (i * 72 + id % 31) * Mathf.Deg2Rad;
                Vector3 position = new Vector3(Mathf.Cos(angle) * 1.2f, Mathf.Sin(angle * 1.7f) * .55f, Mathf.Sin(angle) * 1.1f);
                Transform shard = Part(root, PrimitiveType.Cube, position, new Vector3(.2f, .14f, .85f), mineral);
                shard.localRotation = Quaternion.Euler(20 + i * 13, i * 73, 25 + i * 19);
                if (i < 2)
                {
                    Transform crystal = Part(root, PrimitiveType.Capsule, position * 1.16f,
                        new Vector3(.17f, .42f + i * .035f, .17f), mineral);
                    crystal.localRotation = Quaternion.Euler(i * 31, i * 67, 24 + i * 9);
                }
            }
            for (int fragment = 0; fragment < 3; fragment++)
            {
                float angle = (fragment * 117 + id % 47) * Mathf.Deg2Rad;
                Vector3 offset = new Vector3(Mathf.Cos(angle) * 1.9f,
                    Mathf.Sin(angle * 1.4f) * .7f, Mathf.Sin(angle) * 1.7f);
                Transform chip = Model(root, rocks[(int)((id + (uint)fragment + 3) % (uint)rocks.Length)],
                    oreRock, offset, Vector3.one * (.35f + fragment * .08f));
                chip.localRotation = Quaternion.Euler(fragment * 26, fragment * 71, fragment * 39);
            }
        }

        /// <summary>Assemble a traversable circular gate with a recessed energy field.</summary>
        private void Gate(Transform root)
        {
            Model(root, ring, hull, Vector3.zero, Vector3.one * 5);
            Model(root, ring, glow, new Vector3(0, 0, -.1f), Vector3.one * 4.35f);
            for (int i = 0; i < 8; i++)
            {
                float a = i * Mathf.PI / 4;
                Transform pylon = Part(root, PrimitiveType.Cube, new Vector3(Mathf.Cos(a) * 5, Mathf.Sin(a) * 5, 0), new Vector3(1.2f, 1.5f, 2.3f), trim);
                pylon.localRotation = Quaternion.Euler(0, 0, i * 45);
                Part(pylon, PrimitiveType.Cube, new Vector3(0, 0, -.51f), new Vector3(.35f, .5f, .04f), amber);
            }
            Part(root, PrimitiveType.Quad, Vector3.zero, new Vector3(8.3f, 8.3f, 1), field);
        }

        /// <summary>Assemble a station with a central core, habitation ring and docking arms.</summary>
        private void Station(Transform root)
        {
            Part(root, PrimitiveType.Cylinder, Vector3.zero, new Vector3(3, 5, 3), hull);
            Transform habitat = Model(root, ring, hull, Vector3.zero, Vector3.one * 8);
            habitat.localRotation = Quaternion.Euler(90, 0, 0);
            Transform lamps = Model(root, ring, glow, new Vector3(0, .1f, 0), Vector3.one * 8.1f);
            lamps.localRotation = Quaternion.Euler(90, 0, 0);
            for (int i = 0; i < 6; i++)
            {
                float angle = i * 60;
                Transform arm = Root(root, "Docking arm"); arm.localRotation = Quaternion.Euler(0, angle, 0);
                Part(arm, PrimitiveType.Cube, new Vector3(0, 0, 5), new Vector3(.6f, .6f, 10), trim);
                Part(arm, PrimitiveType.Cube, new Vector3(0, .3f, 9), new Vector3(2, 2.2f, 3.5f), hull);
                Part(arm, PrimitiveType.Cube, new Vector3(0, .6f, 10.8f), new Vector3(1.4f, .6f, .05f), glow);
                Part(arm, PrimitiveType.Cube, new Vector3(0, 1.5f, 9), new Vector3(.12f, .4f, 1.5f), amber);
            }
            Part(root, PrimitiveType.Sphere, new Vector3(0, 5, 0), new Vector3(2.7f, 1.5f, 2.7f), glass);
            Part(root, PrimitiveType.Cylinder, new Vector3(0, 7, 0), new Vector3(.12f, 1.5f, .12f), glow);
        }

        /// <summary>Assemble a small navigation transmitter with an emissive marker.</summary>
        private void Beacon(Transform root)
        {
            Part(root, PrimitiveType.Cylinder, Vector3.zero, new Vector3(.35f, 1.5f, .35f), trim);
            Model(root, ring, glow, Vector3.zero, Vector3.one * .9f);
            Part(root, PrimitiveType.Sphere, new Vector3(0, 1.6f, 0), Vector3.one * .35f, amber);
        }

        /// <returns>A non-interactive distant planet used as a depth reference in the sky.</returns>
        public Transform Environment(Transform parent)
        {
            Transform root = Root(parent, "Distant sky scenery");
            Material planetMaterial = ShaderMaterial("42 and Beyond/Planet");
            Texture2D saturn = Resources.Load<Texture2D>("SaturnNASA");
            if (saturn != null)
            {
                planetMaterial.SetTexture("_MainTex", saturn);
                planetMaterial.SetFloat("_UseTexture", 1f);
            }
            Transform planet = Part(root, PrimitiveType.Sphere, new Vector3(6500, -2000, 16000),
                Vector3.one * 6800, planetMaterial);
            planet.localRotation = Quaternion.Euler(0, 0, -18);
            Material ringMaterial = ShaderMaterial("42 and Beyond/PlanetRings");
            Mesh belt = Annulus(1.25f, 2.15f, 160); meshes.Add(belt);
            Transform rings = Model(planet, belt, ringMaterial, Vector3.zero, Vector3.one * .5f);
            rings.localRotation = Quaternion.Euler(65, 0, 0);
            return root;
        }

        /// <returns>A closed swept spacecraft hull with recalculated normals.</returns>
        private static Mesh Hull()
        {
            Vector3[] vertices = { new Vector3(0, 0, 2), new Vector3(-.7f, 0, -1), new Vector3(.7f, 0, -1), new Vector3(0, .45f, -.5f), new Vector3(0, -.2f, -.5f) };
            int[] triangles = { 0, 3, 1, 0, 2, 3, 1, 3, 2, 0, 1, 4, 0, 4, 2, 1, 2, 4 };
            var mesh = new Mesh { name = "Swept hull", vertices = vertices, triangles = triangles }; mesh.RecalculateNormals(); mesh.RecalculateBounds(); return mesh;
        }

        /// <returns>A deterministic irregular sphere suitable for a shared asteroid variant.</returns>
        private static Mesh Asteroid(int seed)
        {
            const int rows = 12, columns = 20;
            var vertices = new Vector3[(rows + 1) * (columns + 1)]; var triangles = new List<int>();
            for (int y = 0; y <= rows; y++) for (int x = 0; x <= columns; x++)
                {
                    float latitude = y * Mathf.PI / rows, longitude = x * Mathf.PI * 2 / columns;
                    Vector3 direction = new Vector3(Mathf.Sin(latitude) * Mathf.Cos(longitude), Mathf.Cos(latitude), Mathf.Sin(latitude) * Mathf.Sin(longitude));
                    float large = Mathf.PerlinNoise(direction.x * 2.3f + seed, direction.z * 2.3f + seed);
                    float detail = Mathf.PerlinNoise(direction.y * 7.1f + seed * .17f,
                        direction.x * 6.7f + direction.z * 2.2f + seed * .29f);
                    float radius = .65f + .34f * large + .13f * detail;
                    vertices[y * (columns + 1) + x] = direction * radius;
                    if (y < rows && x < columns) { int a = y * (columns + 1) + x, b = a + columns + 1; triangles.AddRange(new[] { a, a + 1, b, a + 1, b + 1, b }); }
                }
            var facets = new Vector3[triangles.Count];
            var facetIndices = new int[triangles.Count];
            for (int index = 0; index < triangles.Count; index++)
            { facets[index] = vertices[triangles[index]]; facetIndices[index] = index; }
            var mesh = new Mesh { name = "Asteroid " + seed, vertices = facets, triangles = facetIndices };
            mesh.RecalculateNormals(); mesh.RecalculateBounds(); return mesh;
        }

        /// <returns>A torus oriented around local Z.</returns>
        private static Mesh Torus(float radius, float tube, int segments, int sides)
        {
            var vertices = new Vector3[(segments + 1) * (sides + 1)]; var triangles = new List<int>();
            for (int i = 0; i <= segments; i++) for (int j = 0; j <= sides; j++)
                {
                    float a = i * Mathf.PI * 2 / segments, b = j * Mathf.PI * 2 / sides;
                    vertices[i * (sides + 1) + j] = new Vector3((radius + tube * Mathf.Cos(b)) * Mathf.Cos(a), (radius + tube * Mathf.Cos(b)) * Mathf.Sin(a), tube * Mathf.Sin(b));
                    if (i < segments && j < sides) { int p = i * (sides + 1) + j, q = p + sides + 1; triangles.AddRange(new[] { p, q, p + 1, p + 1, q, q + 1 }); }
                }
            var mesh = new Mesh { name = "Structural ring", vertices = vertices, triangles = triangles.ToArray() }; mesh.RecalculateNormals(); mesh.RecalculateBounds(); return mesh;
        }

        /// <returns>A double-sided flat ring for distant planetary scenery.</returns>
        private static Mesh Annulus(float inner, float outer, int segments)
        {
            var vertices = new Vector3[(segments + 1) * 2]; var triangles = new List<int>();
            for (int i = 0; i <= segments; i++)
            {
                float angle = i * Mathf.PI * 2 / segments; Vector3 radial = new Vector3(Mathf.Cos(angle), Mathf.Sin(angle), 0);
                vertices[i * 2] = radial * inner; vertices[i * 2 + 1] = radial * outer;
                if (i < segments) { int p = i * 2; triangles.AddRange(new[] { p, p + 2, p + 1, p + 1, p + 2, p + 3, p, p + 1, p + 2, p + 1, p + 3, p + 2 }); }
            }
            var mesh = new Mesh { name = "Planetary rings", vertices = vertices, triangles = triangles.ToArray() }; mesh.RecalculateNormals(); mesh.RecalculateBounds(); return mesh;
        }

        /// <summary>Release only resources created by this factory.</summary>
        public void Dispose()
        { foreach (Material material in materials) Object.Destroy(material); foreach (Mesh mesh in meshes) Object.Destroy(mesh); }
    }
}
