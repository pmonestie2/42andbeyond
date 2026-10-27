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
        private readonly Mesh ring, shipHull, suitArmor, gateArc, gateLightRing;
        private readonly GameObject corvetteAsset, frigateAsset, destroyerAsset, cruiserAsset, carrierAsset;
        private readonly GameObject eveCaldariAsset, eveGallenteAsset;
        private readonly GameObject[] legacyClassAssets = new GameObject[9];
        private readonly GameObject[] legacyVeteranAssets = new GameObject[9];
        private readonly Dictionary<ushort, GameObject> legacyMobAssets =
            new Dictionary<ushort, GameObject>();
        private readonly Dictionary<ushort, string> enemyModelPaths = new Dictionary<ushort, string>();
        private readonly Dictionary<string, GameObject> enemyModels = new Dictionary<string, GameObject>();
        private readonly Dictionary<(Material, int), Material> legacySurfaces =
            new Dictionary<(Material, int), Material>();
        private readonly Dictionary<(Material, int), Material> legacyHostileSurfaces =
            new Dictionary<(Material, int), Material>();
        private readonly GameObject[] proteronAssets = new GameObject[5];
        private readonly Material[] proteronSurfaces = new Material[5];
        private readonly Material[] hostileProteronSurfaces = new Material[5];
        private readonly GameObject[] moonRockHigh = new GameObject[2], moonRockLow = new GameObject[2];
        private readonly GameObject[] packAsteroids = new GameObject[10];
        private readonly Material packRockSurface;
        private readonly GameObject blackHoleRing, blackHoleCore;
        private readonly Material blackHoleDiskSurface, blackHoleCloudSurface,
            blackHoleCoreSurface, blackHolePhotonSurface, blackHoleLensSurface;
        private readonly Material sunCoronaSurface, sunHazeSurface;
        private readonly Material[] moonStone = new Material[2], moonOre = new Material[2],
            moonMetal = new Material[2], moonHydrocarbon = new Material[2];
        private readonly Material corvetteSurface, frigateSurface, destroyerSurface, cruiserSurface, carrierSurface;
        private readonly Material hostileFrigateSurface, hostileDestroyerSurface, hostileCruiserSurface,
            hostileCarrierSurface;
        private readonly Material eveCaldariSurface, eveGallenteSurface, eveGallenteHostileSurface;
        private readonly Material hull, trim, dark, glass, glow, amber, hostile, crewSignal,
            mineral, metalSeam, hydrocarbon, gasVapor, rock, oreRock, field;
        private readonly Material gateArmor, gateInset, gateCircuit;
        private readonly Material stationDeck, stationWall, stationFrame, stationRecess, stationBlue, stationAmber;
        private readonly Material suitShell, suitJoint, suitVisor;
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
            stationDeck = StationSurface(new Color(.22f, .3f, .38f), .72f, .34f, .34f);
            stationWall = StationSurface(new Color(.32f, .42f, .51f), .6f, .42f, .24f);
            stationFrame = StationSurface(new Color(.12f, .19f, .26f), .83f, .55f, .45f);
            stationRecess = StationSurface(new Color(.045f, .075f, .11f), .38f, .24f, .37f);
            stationBlue = Surface(new Color(.025f, .43f, .55f), .42f, .5f,
                new Color(.01f, .15f, .24f));
            stationAmber = Surface(new Color(.61f, .28f, .065f), .47f, .48f,
                new Color(.22f, .085f, .012f));
            suitShell = Surface(new Color(.67f, .76f, .82f), .72f, .5f);
            suitJoint = Surface(new Color(.045f, .083f, .12f), .44f, .25f);
            suitVisor = Surface(new Color(.025f, .18f, .23f), .82f, .84f,
                new Color(.01f, .075f, .11f));
            suitShell.SetTexture("_DetailTex", plating);
            suitShell.SetFloat("_TextureStrength", .5f);
            gateArmor = Surface(new Color(.28f, .39f, .49f), .78f, .4f);
            gateInset = Surface(new Color(.035f, .075f, .12f), .61f, .34f);
            gateCircuit = Surface(new Color(.025f, .2f, .34f), .52f, .45f,
                new Color(.008f, .12f, .22f));
            foreach (Material surface in new[] { gateArmor, gateInset })
            { surface.SetTexture("_DetailTex", plating); surface.SetFloat("_TextureStrength", .62f); }
            glass = Surface(new Color(.015f, .12f, .18f), .8f, .85f);
            glow = Surface(new Color(.05f, .6f, .85f), .2f, .5f, new Color(.1f, 1.4f, 2.1f));
            amber = Surface(new Color(.8f, .35f, .06f), .4f, .3f, new Color(1.3f, .35f, .04f));
            hostile = Surface(new Color(.7f, .045f, .035f), .25f, .5f, new Color(1.9f, .08f, .03f));
            crewSignal = Surface(new Color(.08f, .7f, .36f), .2f, .45f, new Color(.12f, 1.5f, .6f));
            mineral = Surface(new Color(.07f, .51f, .63f), .35f, .65f,
                new Color(.04f, .55f, .72f));
            metalSeam = Surface(new Color(.64f, .32f, .12f), .66f, .51f,
                new Color(.22f, .065f, .018f));
            hydrocarbon = Surface(new Color(.15f, .095f, .14f), .18f, .86f,
                new Color(.09f, .025f, .075f));
            Shader cloudShader = Resources.Load<Shader>("ResourceCloud");
            if (cloudShader != null)
            {
                gasVapor = new Material(cloudShader);
                gasVapor.SetColor("_Tint", new Color(.19f, .64f, .88f));
                gasVapor.SetFloat("_Opacity", .34f);
                materials.Add(gasVapor);
            }
            Texture2D oreDetail = LoadDetail("OreRock");
            rock = Surface(new Color(.48f, .42f, .37f), .04f, .08f);
            rock.SetTexture("_DetailTex", oreDetail);
            rock.SetFloat("_TextureStrength", .7f);
            rock.SetFloat("_DetailGain", 1.4f);
            rock.SetFloat("_Grain", .3f);
            oreRock = Surface(new Color(.82f, .86f, .9f), .07f, .09f);
            oreRock.SetTexture("_DetailTex", oreDetail);
            oreRock.SetFloat("_TextureStrength", 1f);
            oreRock.SetFloat("_DetailGain", 1.35f);
            field = ShaderMaterial("42 and Beyond/GateField");
            sunCoronaSurface = ShaderMaterial("42 and Beyond/SunCorona");
            sunCoronaSurface.SetFloat("_Strength", 2.2f);
            sunHazeSurface = ShaderMaterial("42 and Beyond/SunCorona");
            sunHazeSurface.SetFloat("_Strength", .55f);
            Sky = ShaderMaterial("42 and Beyond/SpaceSky");
            Texture2D vividSky = Resources.Load<Texture2D>("NebulaPanorama");
            Texture2D proceduralSky = Resources.Load<Texture2D>("ProceduralSpace");
            Sky.SetTexture("_NebulaTex", vividSky != null ? vividSky :
                proceduralSky != null ? proceduralSky : LoadDetail("SpaceNebula"));
            Sky.SetFloat("_Exposure", vividSky != null ? .45f :
                proceduralSky != null ? 1.65f : .16f);
            Texture2D eveSky = Resources.Load<Texture2D>("EveShips/a13_cube");
            if (eveSky != null)
            {
                Sky.SetTexture("_AccentTex", eveSky);
                Sky.SetFloat("_AccentExposure", vividSky != null ? .55f : .34f);
                Sky.SetFloat("_AccentBlend", vividSky != null ? .1f : .42f);
            }
            Texture2D veil = Resources.Load<Texture2D>("NebulaVeil");
            if (veil != null)
            {
                Sky.SetTexture("_VeilTex", veil);
                Sky.SetFloat("_VeilStrength", .12f);
            }
            Shader effectShader = Resources.Load<Shader>("BeamAdditive");
            if (effectShader != null) { EffectLine = new Material(effectShader); materials.Add(EffectLine); }
            corvetteAsset = Resources.Load<GameObject>("ViktorShips/corvette03");
            frigateAsset = Resources.Load<GameObject>("ViktorShips/frigate03");
            destroyerAsset = Resources.Load<GameObject>("ViktorShips/destroyer03");
            cruiserAsset = Resources.Load<GameObject>("ViktorShips/cruiser03");
            carrierAsset = Resources.Load<GameObject>("ViktorShips/carrier03");
            corvetteSurface = ShipSurface("corvette03");
            frigateSurface = ShipSurface("frigate03");
            destroyerSurface = ShipSurface("destroyer03");
            cruiserSurface = ShipSurface("cruiser03");
            carrierSurface = ShipSurface("carrier03");
            hostileFrigateSurface = HostileSurface(frigateSurface);
            hostileDestroyerSurface = HostileSurface(destroyerSurface);
            hostileCruiserSurface = HostileSurface(cruiserSurface);
            hostileCarrierSurface = HostileSurface(carrierSurface);
            eveCaldariAsset = Resources.Load<GameObject>("EveShips/cc3");
            eveGallenteAsset = Resources.Load<GameObject>("EveShips/gc3");
            string[] legacyClasses = { "TVF01A", "TVT01A", "TVE01A", "JVF01A", "JVT01A",
                "JVE01A", "PVF01A", "PVT01A", "PVE01A" };
            for (int index = 0; index < legacyClasses.Length; index++)
                legacyClassAssets[index] = Resources.Load<GameObject>("LegacyShips/" +
                    legacyClasses[index]);
            string[] veteranClasses = { "TE", "TT", "TS", "JD", "JS", "JE",
                "PW", "PP", "PS" };
            for (int index = 0; index < veteranClasses.Length; index++)
                legacyVeteranAssets[index] = Resources.Load<GameObject>("LegacyShips/Original" +
                    veteranClasses[index]);
            legacyVeteranAssets[5] = Resources.Load<GameObject>("LegacyShips/OriginalJEBase") ??
                legacyVeteranAssets[5];
            foreach (var ship in new[] { (id: (ushort)1022, name: "npc01"),
                (id: (ushort)1023, name: "npc02"), (id: (ushort)433, name: "PVS01"),
                (id: (ushort)434, name: "PVS02"), (id: (ushort)317, name: "PPVF01A"),
                (id: (ushort)2, name: "PVS07"), (id: (ushort)18, name: "PVF01A"),
                (id: (ushort)21, name: "PVE01A"), (id: (ushort)10, name: "PVS12"),
                (id: (ushort)5, name: "PVS09"), (id: (ushort)11, name: "PVS13"),
                (id: (ushort)314, name: "PTVF01A"), (id: (ushort)1539, name: "MOB_03"),
                (id: (ushort)1538, name: "MOB_02"), (id: (ushort)15, name: "JVT01A"),
                (id: (ushort)24, name: "PVT01A"), (id: (ushort)9, name: "JVF01A"),
                (id: (ushort)1, name: "PVS06") })
            {
                GameObject asset = Resources.Load<GameObject>("LegacyShips/" + ship.name);
                if (asset != null) legacyMobAssets.Add(ship.id, asset);
            }
            TextAsset enemyCatalog = Resources.Load<TextAsset>("LegacyEnemies/Catalog");
            if (enemyCatalog != null)
                foreach (string row in enemyCatalog.text.Split('\n'))
                {
                    string[] fields = row.Trim().Split('\t');
                    if (fields.Length == 2 && ushort.TryParse(fields[0], out ushort id))
                        enemyModelPaths[id] = "LegacyEnemies/" + fields[1];
                }
            eveCaldariSurface = EveSurface("cc3", Color.white, new Color(.3f, .8f, 1f));
            eveGallenteSurface = EveSurface("gc3", Color.white, new Color(.3f, 1f, .55f));
            eveGallenteHostileSurface = EveSurface("gc3", new Color(1f, .72f, .7f),
                new Color(1f, .25f, .16f));
            string[] proteronNames = { "archimedes", "dalton", "hippocrates", "pythagoras", "watson" };
            for (int index = 0; index < proteronNames.Length; index++)
            {
                proteronAssets[index] = Resources.Load<GameObject>("ProteronShips/" + proteronNames[index]);
                proteronSurfaces[index] = ProteronSurface(proteronNames[index], new Color(.3f, .88f, 1f));
                hostileProteronSurfaces[index] = ProteronSurface(proteronNames[index],
                    new Color(1f, .34f, .23f));
            }
            for (int index = 0; index < moonRockHigh.Length; index++)
            {
                string name = index == 0 ? "moon_rock_01" : "moon_rock_03";
                moonRockHigh[index] = Resources.Load<GameObject>("MoonRocks/" + name + "_high");
                moonRockLow[index] = Resources.Load<GameObject>("MoonRocks/" + name + "_low");
                moonStone[index] = MoonSurface(name, new Color(.62f, .63f, .7f), Color.clear, 0f);
                moonOre[index] = MoonSurface(name, new Color(.45f, .65f, .77f),
                    new Color(.19f, .82f, 1f), .9f);
                moonMetal[index] = MoonSurface(name, new Color(.58f, .54f, .49f),
                    new Color(1f, .51f, .2f), .57f);
                moonHydrocarbon[index] = MoonSurface(name, new Color(.28f, .23f, .29f),
                    new Color(.92f, .31f, .38f), .72f);
            }
            for (int index = 0; index < packAsteroids.Length; index++)
                packAsteroids[index] = Resources.Load<GameObject>(
                    "AsteroidPack/Asteroid_no_" + (index + 1));
            Texture2D packColor = Resources.Load<Texture2D>("AsteroidPack/rock_color");
            if (packColor != null)
            {
                packRockSurface = ShaderMaterial("42 and Beyond/AsteroidPack");
                packRockSurface.SetTexture("_MainTex", packColor);
                packRockSurface.SetTexture("_NormalTex", Resources.Load<Texture2D>("AsteroidPack/rock_normal"));
                packRockSurface.SetTexture("_MetalTex", Resources.Load<Texture2D>("AsteroidPack/rock_metal"));
                packRockSurface.SetTexture("_RoughnessTex", Resources.Load<Texture2D>("AsteroidPack/rock_roughness"));
                packRockSurface.SetTexture("_AoTex", Resources.Load<Texture2D>("AsteroidPack/rock_ao"));
                packRockSurface.SetColor("_Tint", new Color(.78f, .84f, .92f));
            }
            blackHoleRing = Resources.Load<GameObject>("BlackHolePack/Blackhole_ring");
            blackHoleCore = Resources.Load<GameObject>("BlackHolePack/Blackhole_core");
            Texture2D ringColor = Resources.Load<Texture2D>("BlackHolePack/ring_color");
            if (blackHoleRing != null && blackHoleCore != null && ringColor != null)
            {
                blackHoleDiskSurface = ShaderMaterial("42 and Beyond/BlackHoleDisk");
                blackHoleDiskSurface.SetTexture("_MainTex", ringColor);
                blackHoleDiskSurface.SetTexture("_OpacityTex",
                    Resources.Load<Texture2D>("BlackHolePack/ring_opacity"));
                blackHoleDiskSurface.SetColor("_Tint", new Color(1.6f, .98f, .6f));
                blackHoleDiskSurface.SetFloat("_Opacity", .82f);
                Texture2D clouds = Resources.Load<Texture2D>("BlackHolePack/ring_clouds");
                if (clouds != null)
                {
                    blackHoleCloudSurface = ShaderMaterial("42 and Beyond/BlackHoleDisk");
                    blackHoleCloudSurface.SetTexture("_MainTex", clouds);
                    blackHoleCloudSurface.SetTexture("_OpacityTex",
                        Resources.Load<Texture2D>("BlackHolePack/ring_opacity"));
                    blackHoleCloudSurface.SetColor("_Tint", new Color(.65f, .78f, 1.12f));
                    blackHoleCloudSurface.SetFloat("_Opacity", .42f);
                }
                blackHoleCoreSurface = ShaderMaterial("42 and Beyond/BlackHoleCore");
                blackHolePhotonSurface = ShaderMaterial("42 and Beyond/BlackHolePhoton");
                blackHolePhotonSurface.SetColor("_Tint", new Color(1f, .76f, .5f, .6f));
                blackHolePhotonSurface.SetFloat("_Intensity", 1.1f);
                blackHoleLensSurface = ShaderMaterial("42 and Beyond/BlackHolePhoton");
                blackHoleLensSurface.SetColor("_Tint", new Color(.2f, .42f, .9f, .11f));
                blackHoleLensSurface.SetFloat("_Intensity", .4f);
            }
            ring = Torus(1, .12f, 48, 8); meshes.Add(ring);
            gateArc = GateArc(); meshes.Add(gateArc);
            gateLightRing = Torus(1, .045f, 64, 8); meshes.Add(gateLightRing);
            shipHull = Hull(); meshes.Add(shipHull);
            suitArmor = SuitArmor(); meshes.Add(suitArmor);
            for (int i = 0; i < rocks.Length; i++) { rocks[i] = Asteroid(i + 17); meshes.Add(rocks[i]); }
        }

        /// <returns>An owned lit material with optional emissive color.</returns>
        private Material Surface(Color color, float metal, float smoothness, Color emission = default)
        {
            Material material = ShaderMaterial("42 and Beyond/Surface");
            material.color = color; material.SetFloat("_Metallic", metal); material.SetFloat("_Glossiness", smoothness);
            material.SetColor("_EmissionColor", emission); return material;
        }

        /// <returns>Material: A world-aligned station metal with varied panel values.</returns>
        private Material StationSurface(Color color, float metal, float smoothness, float plateScale)
        {
            Shader shader = Resources.Load<Shader>("StationHull");
            if (shader == null) shader = Resources.Load<Shader>("Surface");
            if (shader == null) throw new InvalidOperationException("Missing station hull shader");
            var material = new Material(shader); materials.Add(material);
            material.SetColor("_Color", color);
            material.SetTexture("_DetailTex", LoadDetail("HullPlating"));
            material.SetFloat("_Metallic", metal);
            material.SetFloat("_Glossiness", smoothness);
            material.SetFloat("_PlateScale", plateScale);
            material.SetFloat("_PatternStrength", .68f);
            return material;
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

        /// <returns>Transform: A structural beam spanning two local-space points.</returns>
        private static Transform Beam(Transform parent, string name, Vector3 start, Vector3 end,
            float width, float depth, Material surface)
        {
            Vector3 direction = end - start;
            Transform part = Part(parent, PrimitiveType.Cube, (start + end) * .5f,
                new Vector3(width, direction.magnitude, depth), surface);
            part.name = name;
            part.localRotation = Quaternion.FromToRotation(Vector3.up, direction);
            return part;
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

        /// <returns>Material: A warmer tint of a textured hull, or null without its source.</returns>
        private Material HostileSurface(Material source)
        {
            if (source == null) return null;
            var surface = new Material(source);
            surface.SetColor("_Color", new Color(.94f, .65f, .6f));
            materials.Add(surface);
            return surface;
        }

        /// <returns>Material: A lit surface for an optional locally exported EVE hull.</returns>
        private Material EveSurface(string name, Color tint, Color glowColor)
        {
            Shader shader = Resources.Load<Shader>("EveShip");
            Texture2D albedo = Resources.Load<Texture2D>("EveShips/" + name + "_albedo");
            if (shader == null || albedo == null) return null;
            var surface = new Material(shader); materials.Add(surface);
            surface.SetTexture("_MainTex", albedo);
            surface.SetTexture("_GlowTex", Resources.Load<Texture2D>("EveShips/" + name + "_glow"));
            Texture2D normal = Resources.Load<Texture2D>("EveShips/" + name + "_normal");
            Texture2D roughness = Resources.Load<Texture2D>("EveShips/" + name + "_roughness");
            if (normal != null && roughness != null)
            {
                surface.SetTexture("_NormalTex", normal);
                surface.SetTexture("_RoughnessTex", roughness);
                surface.SetFloat("_HasDetail", 1);
            }
            surface.SetColor("_Tint", tint);
            surface.SetColor("_GlowColor", glowColor);
            surface.SetFloat("_Lift", name == "gc3" ? 1.85f : 1.35f);
            return surface;
        }

        /// <returns>Material: A faction-tinted PBR ship surface, or null without its packaged maps.</returns>
        private Material ProteronSurface(string assetName, Color accent)
        {
            Shader shader = Resources.Load<Shader>("StylizedShip");
            Texture2D diffuse = Resources.Load<Texture2D>("ProteronShips/" + assetName + "_diffuse");
            if (shader == null || diffuse == null) return null;
            var surface = new Material(shader); materials.Add(surface);
            surface.SetColor("_AccentColor", accent);
            surface.SetTexture("_MainTex", diffuse);
            surface.SetTexture("_BumpMap", Resources.Load<Texture2D>("ProteronShips/" + assetName + "_normal"));
            surface.SetTexture("_MetallicMap", Resources.Load<Texture2D>("ProteronShips/" + assetName + "_metallic"));
            surface.SetTexture("_RoughnessMap", Resources.Load<Texture2D>("ProteronShips/" + assetName + "_roughness"));
            surface.SetTexture("_EmissionMap", Resources.Load<Texture2D>("ProteronShips/" + assetName + "_emission"));
            return surface;
        }

        /// <returns>Material: A UV-mapped moon-rock surface tinted for one resource family.</returns>
        private Material MoonSurface(string assetName, Color tint, Color accent, float strength)
        {
            Shader shader = Resources.Load<Shader>("StylizedRock");
            Texture2D diffuse = Resources.Load<Texture2D>("MoonRocks/" + assetName + "_diff");
            if (shader == null || diffuse == null) return null;
            var surface = new Material(shader); materials.Add(surface);
            surface.SetColor("_Tint", tint);
            surface.SetColor("_AccentColor", accent);
            surface.SetFloat("_AccentStrength", strength);
            surface.SetTexture("_MainTex", diffuse);
            surface.SetTexture("_BumpMap", Resources.Load<Texture2D>("MoonRocks/" + assetName + "_normal"));
            surface.SetTexture("_ArmMap", Resources.Load<Texture2D>("MoonRocks/" + assetName + "_arm"));
            surface.SetTexture("_VeinTex", LoadDetail("OreRock"));
            return surface;
        }

        /// <summary>Switch imported near geometry to a cheap silhouette at small screen size.</summary>
        private static void SetLod(Transform root, Transform high, Transform low, float transition)
        {
            if (high == null || low == null) return;
            var group = root.gameObject.AddComponent<LODGroup>();
            group.SetLODs(new[] { new LOD(transition, high.GetComponentsInChildren<Renderer>()),
                new LOD(.003f, low.GetComponentsInChildren<Renderer>()) });
            group.RecalculateBounds();
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

        /// <returns>Transform: A centered original hull with its imported texture assignments.</returns>
        private Transform ImportedLegacy(Transform root, GameObject asset, bool hostile = false)
        {
            if (asset == null) return null;
            GameObject instance = Object.Instantiate(asset, root);
            instance.name = "Original Earth and Beyond hull " + asset.name;
            if (asset.name == "OriginalPW") PwWarpArms.Attach(instance);
            if (asset.name == "OriginalJEBase") JeWarpRig.Attach(instance);
            if (!TryLegacyBounds(instance, out Bounds bounds))
            { Object.Destroy(instance); return null; }
            bool modular = asset.name.StartsWith("Original", StringComparison.Ordinal);
            int modularRace = modular ? asset.name[8] == 'T' ? 0 :
                asset.name[8] == 'J' ? 1 : 2 : -1;
            float span = modular ? Mathf.Max(bounds.size.x, bounds.size.z) :
                Mathf.Max(bounds.size.z, bounds.size.x * .55f);
            float scale = 4.8f / Mathf.Max(1f, span);
            instance.transform.localScale = Vector3.one * scale;
            if (asset.name == "OriginalPW")
                instance.GetComponent<PwWarpArms>()?.ConfigureWeaponScale();
            bool playerHull = modular || Array.IndexOf(legacyClassAssets, asset) >= 0;
            instance.transform.localRotation = Quaternion.Euler(0, playerHull ? -90 : 90, 0);
            instance.transform.localPosition = -(instance.transform.localRotation *
                (bounds.center * scale));
            foreach (Collider collider in instance.GetComponentsInChildren<Collider>())
                Object.Destroy(collider);
            foreach (Renderer renderer in instance.GetComponentsInChildren<Renderer>())
            {
                Material[] assigned = renderer.sharedMaterials;
                for (int index = 0; index < assigned.Length; index++)
                    assigned[index] = LegacySurface(assigned[index], hostile, modularRace);
                renderer.sharedMaterials = assigned;
            }
            return instance.transform;
        }

        /// <returns>bool: Whether any mesh supplied complete model bounds in root coordinates.</returns>
        private static bool TryLegacyBounds(GameObject instance, out Bounds bounds)
        {
            bounds = new Bounds(Vector3.zero, Vector3.zero);
            bool found = false;
            foreach (MeshFilter filter in instance.GetComponentsInChildren<MeshFilter>())
            {
                if (filter.sharedMesh == null) continue;
                Bounds part = filter.sharedMesh.bounds;
                Matrix4x4 toRoot = instance.transform.worldToLocalMatrix *
                    filter.transform.localToWorldMatrix;
                for (int corner = 0; corner < 8; corner++)
                {
                    Vector3 point = new Vector3((corner & 1) == 0 ? part.min.x : part.max.x,
                        (corner & 2) == 0 ? part.min.y : part.max.y,
                        (corner & 4) == 0 ? part.min.z : part.max.z);
                    point = toRoot.MultiplyPoint3x4(point);
                    if (!found) { bounds = new Bounds(point, Vector3.zero); found = true; }
                    else bounds.Encapsulate(point);
                }
            }
            return found;
        }

        /// <returns>Material: A cached lit interpretation of an original W3D material.</returns>
        private Material LegacySurface(Material original, bool hostile, int modularRace)
        {
            if (original == null) return hull;
            Dictionary<(Material, int), Material> cache = hostile ?
                legacyHostileSurfaces : legacySurfaces;
            var key = (original, modularRace);
            if (cache.TryGetValue(key, out Material ready)) return ready;
            Shader shader = Resources.Load<Shader>("LegacyShip");
            if (shader == null) return original;
            var surface = new Material(shader); materials.Add(surface);
            string name = original.name.ToLowerInvariant();
            bool engine = name.Contains("engine") || name.Contains("jet") ||
                name.Contains("lite") || name.Contains("glow");
            bool window = name.Contains("window") || name.Contains("cockpit");
            bool textured = original.mainTexture != null;
            if (textured) surface.SetTexture("_MainTex", original.mainTexture);
            bool modular = modularRace >= 0;
            bool progen = modularRace == 2 || name.Contains("progen") || name.Contains("pvf") ||
                name.Contains("pve") || name.Contains("pvt");
            Color tint = textured ? hostile ? new Color(1f, .78f, .74f) :
                modular ? modularRace == 2 ? new Color(.47f, .39f, .34f) :
                modularRace == 1 ? new Color(.36f, .42f, .57f) :
                new Color(.39f, .48f, .55f) :
                progen ? new Color(.82f, .86f, .77f) : Color.white :
                engine ? new Color(.18f, .7f, 1f) : window ?
                new Color(.12f, .26f, .34f) : new Color(.75f, .79f, .82f);
            surface.SetColor("_Tint", tint);
            if (modular)
            {
                surface.SetColor("_Accent", modularRace == 2 ?
                    new Color(1f, .43f, .19f) : modularRace == 1 ?
                    new Color(.34f, .75f, 1f) : new Color(1f, .67f, .31f));
                surface.SetFloat("_PaintStrength", .8f);
            }
            surface.SetFloat("_Lift", textured ? hostile ? 1.55f :
                modular ? 1.4f : 1.28f : 1.3f);
            surface.SetFloat("_Desaturate", progen ? .48f : .12f);
            surface.SetFloat("_Glow", engine ? 1.15f : window ? .34f :
                modular ? 0f : .11f);
            if (!textured && !engine && !window)
            {
                surface.SetTexture("_DetailTex", LoadDetail("HullPlating"));
                surface.SetFloat("_UseDetail", 1f);
            }
            surface.SetFloat("_UseAlpha", name.Contains("flare") ||
                name.Contains("gradient") || name.Contains("scanner") ||
                name.Contains("decal") || name.Contains("sjh02") ||
                name.StartsWith("lf", StringComparison.Ordinal) ||
                name.Contains("glow") ? 1f : 0f);
            cache.Add(key, surface);
            return surface;
        }

        /// <returns>Transform: A detailed race hull, or null when its model is unavailable.</returns>
        private Transform ModernShip(Transform root, int classIndex)
        {
            int race = Mathf.Clamp(classIndex / 3, 0, 2);
            GameObject asset = race == 0 ? eveCaldariAsset : race == 1 ?
                eveGallenteAsset : proteronAssets[4];
            Material surface = race == 0 ? eveCaldariSurface : race == 1 ?
                eveGallenteSurface : proteronSurfaces[4];
            float scale = race == 0 ? .36f : race == 1 ? .69f : .31f;
            return Imported(root, asset, "Player hull visual " + asset?.name,
                Vector3.one * scale, surface);
        }

        /// <returns>int: The native class slot for a server-published player hull asset.</returns>
        public static int ClassIndex(ushort asset)
        { return asset >= 1600 && asset <= 1626 ? (asset - 1600) / 3 : 0; }

        /// <returns>uint: The visual upgrade stage encoded in a native player hull asset.</returns>
        public static uint HullStage(ushort asset)
        { return asset >= 1600 && asset <= 1626 ? (uint)((asset - 1600) % 3 * 3) : 0; }

        /// <returns>GameObject: The available original hull for this class and upgrade stage.</returns>
        private GameObject LegacyClassAsset(int classIndex, uint hullStage)
        {
            if (classIndex < 0 || classIndex >= legacyClassAssets.Length) return null;
            return hullStage >= 5 && legacyVeteranAssets[classIndex] != null ?
                legacyVeteranAssets[classIndex] : legacyClassAssets[classIndex];
        }

        /// <summary>Switch the local character's original hull after roster selection.</summary>
        public void SetShipClass(Transform ship, int classIndex, uint hullStage = 0)
        {
            if (ship == null || classIndex < 0 || classIndex >= legacyClassAssets.Length) return;
            GameObject asset = LegacyClassAsset(classIndex, hullStage);
            string expected = "Original Earth and Beyond hull " + asset?.name;
            foreach (Transform child in ship)
                if (child.name == expected) return;
            Transform replacement = ImportedLegacy(ship, asset) ??
                ModernShip(ship, classIndex);
            if (replacement == null) return;
            foreach (Transform child in ship)
                if (child != replacement && (child.name.StartsWith("Original Earth and Beyond hull ",
                    StringComparison.Ordinal) || child.name.StartsWith("Player hull visual ",
                    StringComparison.Ordinal))) Object.Destroy(child.gameObject);
        }

        /// <returns>A spacecraft with a solid hull, swept wings, cockpit and twin engines.</returns>
        public Transform Ship(Transform parent, int classIndex = 0, uint hullStage = 0)
        {
            Transform root = Root(parent, "Explorer spacecraft");
            Transform original = ImportedLegacy(root, LegacyClassAsset(classIndex, hullStage));
            if (original != null)
            {
                return root;
            }
            if (ModernShip(root, classIndex) != null) return root;
            if (Imported(root, eveCaldariAsset, "Locally exported Caldari hull", Vector3.one * .36f,
                    eveCaldariSurface) != null)
                return root;
            if (Imported(root, proteronAssets[4], "Proteron Watson explorer", Vector3.one * .31f,
                    proteronSurfaces[4]) != null)
                return root;
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

        /// <returns>A layered docking bay with a recessed berth and readable deck geometry.</returns>
        public Transform Hangar(Transform parent)
        {
            Transform root = Root(parent, "Station docking bay");
            Part(root, PrimitiveType.Cube, new Vector3(0, -.36f, 0), new Vector3(19, .5f, 25), stationRecess);
            Part(root, PrimitiveType.Cube, new Vector3(0, -.09f, 0), new Vector3(12, .08f, 23), stationDeck);
            Part(root, PrimitiveType.Cube, new Vector3(0, 6, 11.5f), new Vector3(19, 12, .9f), stationWall);
            Part(root, PrimitiveType.Cube, new Vector3(0, 4.4f, 11.01f),
                new Vector3(10, 7.2f, .12f), stationRecess);
            Part(root, PrimitiveType.Cube, new Vector3(0, 4.4f, 10.94f),
                new Vector3(7.4f, 5.6f, .08f), glass);
            Model(root, ring, stationFrame, new Vector3(0, 4.5f, 10.82f), Vector3.one * 2.25f);
            Model(root, ring, stationBlue, new Vector3(0, 4.5f, 10.74f), Vector3.one * 2.04f);
            for (int panel = -5; panel <= 5; panel++)
            {
                float z = panel * 2.05f;
                if (Mathf.Abs(panel) > 2)
                    Part(root, PrimitiveType.Cube, new Vector3(0, -.015f, z),
                        new Vector3(11.4f, .035f, .055f), stationFrame);
                for (int side = -1; side <= 1; side += 2)
                {
                    Part(root, PrimitiveType.Cube, new Vector3(side * 4.7f, -.02f, z),
                        new Vector3(.75f, .045f, 1.68f), stationWall);
                    Part(root, PrimitiveType.Cube, new Vector3(side * 5.45f, .005f, z),
                        new Vector3(.1f, .055f, 1.14f), stationBlue);
                }
            }
            for (int side = -1; side <= 1; side += 2)
            {
                Part(root, PrimitiveType.Cube, new Vector3(side * 9.2f, 5, 0),
                    new Vector3(.7f, 10, 25), stationWall);
                Part(root, PrimitiveType.Cube, new Vector3(side * 7.8f, 8.5f, 0),
                    new Vector3(3.2f, .4f, 25), stationFrame);
                for (int i = -2; i <= 2; i++)
                {
                    float z = i * 4.7f;
                    Beam(root, "Angled dock support", new Vector3(side * 8.85f, .2f, z),
                        new Vector3(side * 6.1f, 8.2f, z), .4f, .5f, stationFrame);
                    Part(root, PrimitiveType.Cube, new Vector3(side * 8.8f, 3.5f, z + 1.5f),
                        new Vector3(.08f, 3.9f, .18f), stationBlue);
                    Part(root, PrimitiveType.Cube, new Vector3(side * 8.72f, 4.8f, z - 1.4f),
                        new Vector3(.09f, 1.2f, .15f), stationAmber);
                }
                for (int bay = -1; bay <= 1; bay++)
                {
                    float z = bay * 7f;
                    Part(root, PrimitiveType.Cube, new Vector3(side * 6.25f, .04f, z),
                        new Vector3(.16f, .07f, 3.7f), stationFrame);
                    Part(root, PrimitiveType.Cube, new Vector3(side * 6.23f, .09f, z),
                        new Vector3(.045f, .035f, 3.45f), stationBlue);
                    Part(root, PrimitiveType.Cube, new Vector3(side * 8.3f, 6.9f, z),
                        new Vector3(.7f, .18f, 2.1f), stationDeck);
                    Part(root, PrimitiveType.Cube, new Vector3(side * 8.3f, 6.77f, z),
                        new Vector3(.52f, .035f, 1.8f), stationBlue);
                    StationLamp(root, new Vector3(side * 6.9f, 6.5f, z),
                        new Color(.46f, .78f, 1f), 1.1f, 11f);
                }
            }
            for (int z = -8; z <= 8; z += 4)
            {
                Beam(root, "Dock ceiling brace", new Vector3(-6.2f, 8.1f, z),
                    new Vector3(0, 10.25f, z), .32f, .38f, stationFrame);
                Beam(root, "Dock ceiling brace", new Vector3(6.2f, 8.1f, z),
                    new Vector3(0, 10.25f, z), .32f, .38f, stationFrame);
                Part(root, PrimitiveType.Cube, new Vector3(0, 10.16f, z),
                    new Vector3(2.4f, .08f, .23f), stationBlue);
            }
            Part(root, PrimitiveType.Cylinder, new Vector3(0, 0, 0),
                new Vector3(5.35f, .13f, 5.35f), stationFrame);
            Part(root, PrimitiveType.Cylinder, new Vector3(0, .09f, 0),
                new Vector3(4.45f, .04f, 4.45f), stationDeck);
            for (int i = 0; i < 12; i++)
            {
                float angle = i * Mathf.PI / 6;
                Part(root, PrimitiveType.Cube, new Vector3(Mathf.Cos(angle) * 4.3f, .2f, Mathf.Sin(angle) * 4.3f),
                    new Vector3(.65f, .08f, .18f), stationAmber);
            }
            return root;
        }

        /// <returns>A station concourse with a distinct floor, kiosks and luminous ceiling ribs.</returns>
        public Transform StationConcourse(Transform parent)
        {
            Transform root = Root(parent, "Station concourse");
            Part(root, PrimitiveType.Cube, new Vector3(0, -.32f, 0),
                new Vector3(20, .5f, 25), stationRecess);
            Part(root, PrimitiveType.Cube, new Vector3(0, -.055f, 0),
                new Vector3(14, .05f, 24), stationDeck);
            Part(root, PrimitiveType.Cube, new Vector3(0, 7, 11.5f),
                new Vector3(20, 14, .8f), stationWall);
            Part(root, PrimitiveType.Cube, new Vector3(0, 8.5f, 0),
                new Vector3(20, .5f, 25), stationFrame);
            Part(root, PrimitiveType.Cube, new Vector3(0, 3.5f, 11.02f),
                new Vector3(9, 4.5f, .1f), stationRecess);
            Part(root, PrimitiveType.Cube, new Vector3(0, 3.5f, 10.95f),
                new Vector3(7.8f, 3.5f, .08f), glass);
            for (int side = -1; side <= 1; side += 2)
            {
                Part(root, PrimitiveType.Cube, new Vector3(side * 9.5f, 4, 0),
                    new Vector3(.7f, 8, 25), stationWall);
                Part(root, PrimitiveType.Cube, new Vector3(side * 7.5f, .03f, 0),
                    new Vector3(.1f, .04f, 22), stationBlue);
                for (int stall = -1; stall <= 1; stall++)
                {
                    float z = stall * 6;
                    Part(root, PrimitiveType.Cube, new Vector3(side * 7.2f, 1.05f, z),
                        new Vector3(3.4f, 1.5f, 3.2f), stationFrame);
                    Part(root, PrimitiveType.Cube, new Vector3(side * 7.2f, 1.85f, z + 1.35f),
                        new Vector3(2.8f, .12f, .18f), stationWall);
                    Part(root, PrimitiveType.Cube, new Vector3(side * 7.2f, 2.7f, z + 1.35f),
                        new Vector3(2.5f, 1.2f, .08f), glass);
                    Part(root, PrimitiveType.Cube, new Vector3(side * 7.2f, 3.45f, z + 1.35f),
                        new Vector3(2.7f, .09f, .12f), stationBlue);
                    Beam(root, "Concourse kiosk strut", new Vector3(side * 8.95f, 1.8f, z + 1.3f),
                        new Vector3(side * 8.35f, 4.3f, z + 1.3f), .18f, .22f, stationWall);
                    Part(root, PrimitiveType.Cube, new Vector3(side * 9.1f, 4.5f, z + 3),
                        new Vector3(.16f, 8, .18f), stationFrame);
                    Part(root, PrimitiveType.Cube, new Vector3(side * 7.2f, 1.87f, z + 1.32f),
                        new Vector3(1.65f, .025f, .045f), stationAmber);
                }
                StationLamp(root, new Vector3(side * 5.9f, 6.6f, 1f),
                    new Color(.4f, .75f, 1f), 1.15f, 12f);
            }
            for (int rib = -2; rib <= 2; rib++)
            {
                float z = rib * 5;
                Beam(root, "Concourse ceiling arch", new Vector3(-9.15f, 5.9f, z),
                    new Vector3(0, 8.18f, z), .29f, .32f, stationWall);
                Beam(root, "Concourse ceiling arch", new Vector3(9.15f, 5.9f, z),
                    new Vector3(0, 8.18f, z), .29f, .32f, stationWall);
                Part(root, PrimitiveType.Cube, new Vector3(0, 8.06f, z),
                    new Vector3(2.6f, .07f, .2f), stationBlue);
                Part(root, PrimitiveType.Cube, new Vector3(0, .025f, z),
                    new Vector3(12, .035f, .09f), stationAmber);
            }
            for (int panel = -4; panel <= 4; panel++)
                for (int side = -1; side <= 1; side += 2)
                    Part(root, PrimitiveType.Cube, new Vector3(side * 5.5f, -.01f, panel * 2.8f),
                        new Vector3(2.4f, .02f, 2.4f), stationWall);
            Part(root, PrimitiveType.Cylinder, new Vector3(0, .02f, 2),
                new Vector3(2.4f, .06f, 2.4f), stationFrame);
            Transform medallion = Model(root, ring, stationBlue, new Vector3(0, .08f, 2),
                Vector3.one * 2.3f);
            medallion.localRotation = Quaternion.Euler(90, 0, 0);
            return root;
        }

        /// <summary>Light local station surfaces without adding realtime shadows.</summary>
        private static void StationLamp(Transform parent, Vector3 position, Color color,
            float intensity, float range)
        {
            var fixture = new GameObject("Station area lamp", typeof(Light));
            fixture.transform.SetParent(parent, false);
            fixture.transform.localPosition = position;
            Light light = fixture.GetComponent<Light>();
            light.type = LightType.Point; light.color = color;
            light.intensity = intensity; light.range = range;
            light.shadows = LightShadows.None;
        }

        /// <returns>Transform: A layered station suit, accented for a pilot or contact.</returns>
        public Transform StationAvatar(Transform parent, bool contact = false)
        {
            Transform root = Root(parent, contact ? "Station contact suit" : "Station pilot suit");
            Material accent = contact ? stationAmber : stationBlue;
            Model(root, suitArmor, suitJoint, new Vector3(0, .82f, 0),
                new Vector3(.43f, 1.02f, .32f));
            Model(root, suitArmor, suitShell, new Vector3(0, 1.06f, .18f),
                new Vector3(.38f, .66f, .2f));
            Part(root, PrimitiveType.Cube, new Vector3(0, 1.5f, .32f),
                new Vector3(.46f, .12f, .06f), suitShell);
            Part(root, PrimitiveType.Cube, new Vector3(0, 1.26f, .39f),
                new Vector3(.24f, .11f, .025f), accent);
            Model(root, suitArmor, stationFrame, new Vector3(0, 1.0f, -.35f),
                new Vector3(.29f, .6f, .19f));
            Part(root, PrimitiveType.Cube, new Vector3(0, 1.34f, -.52f),
                new Vector3(.42f, .5f, .12f), suitShell);
            Part(root, PrimitiveType.Cube, new Vector3(0, 1.38f, -.6f),
                new Vector3(.18f, .32f, .025f), accent);
            Part(root, PrimitiveType.Cylinder, new Vector3(0, 1.72f, 0),
                new Vector3(.4f, .1f, .39f), stationFrame);
            Part(root, PrimitiveType.Sphere, new Vector3(0, 2.03f, 0),
                new Vector3(.46f, .43f, .46f), suitShell);
            Part(root, PrimitiveType.Sphere, new Vector3(0, 2.04f, .22f),
                new Vector3(.36f, .25f, .29f), suitVisor);
            Part(root, PrimitiveType.Cube, new Vector3(0, 2.3f, .22f),
                new Vector3(.61f, .045f, .12f), stationFrame);
            Part(root, PrimitiveType.Cube, new Vector3(0, 1.85f, .37f),
                new Vector3(.48f, .035f, .08f), accent);
            Part(root, PrimitiveType.Cube, new Vector3(0, 2.07f, -.43f),
                new Vector3(.3f, .07f, .08f), accent);
            Part(root, PrimitiveType.Cube, new Vector3(0, .75f, 0),
                new Vector3(.53f, .15f, .44f), stationFrame);
            var arms = new Transform[2];
            var legs = new Transform[2];
            for (int side = -1; side <= 1; side += 2)
            {
                int index = side < 0 ? 0 : 1;
                Transform shoulder = Model(root, suitArmor, suitShell,
                    new Vector3(side * .48f, 1.48f, 0), new Vector3(.26f, .25f, .27f));
                shoulder.localRotation = Quaternion.Euler(0, 0, side * -15);
                Transform armPivot = Root(root, side < 0 ? "Left arm" : "Right arm");
                armPivot.localPosition = new Vector3(side * .53f, 1.45f, 0);
                arms[index] = armPivot;
                Transform arm = Model(armPivot, suitArmor, suitJoint,
                    new Vector3(0, -.64f, 0), new Vector3(.17f, .68f, .18f));
                arm.localRotation = Quaternion.Euler(0, 0, side * -7);
                Part(armPivot, PrimitiveType.Cube, new Vector3(side * .02f, -.4f, .17f),
                    new Vector3(.2f, .38f, .06f), suitShell);
                Part(armPivot, PrimitiveType.Cube, new Vector3(side * .02f, -.78f, .04f),
                    new Vector3(.24f, .2f, .26f), suitShell);
                Part(armPivot, PrimitiveType.Cube, new Vector3(side * .03f, -.93f, .07f),
                    new Vector3(.15f, .06f, .17f), accent);
                Transform legPivot = Root(root, side < 0 ? "Left leg" : "Right leg");
                legPivot.localPosition = new Vector3(side * .21f, .75f, 0);
                legs[index] = legPivot;
                Model(legPivot, suitArmor, suitJoint, new Vector3(0, -.67f, 0),
                    new Vector3(.2f, .69f, .21f));
                Part(legPivot, PrimitiveType.Cube, new Vector3(0, -.32f, .18f),
                    new Vector3(.25f, .33f, .08f), suitShell);
                Part(legPivot, PrimitiveType.Cube, new Vector3(0, -.62f, .2f),
                    new Vector3(.28f, .2f, .39f), stationFrame);
                Part(root, PrimitiveType.Cube, new Vector3(side * .35f, 1.48f, -.26f),
                    new Vector3(.07f, .28f, .04f), accent);
                Part(root, PrimitiveType.Cube, new Vector3(side * .34f, 2.04f, 0),
                    new Vector3(.12f, .22f, .12f), stationFrame);
            }
            root.gameObject.AddComponent<StationSuitMotion>().Configure(
                arms[0], arms[1], legs[0], legs[1], contact);
            return root;
        }

        /// <returns>A server-mapped original enemy model, with optional art fallbacks.</returns>
        public Transform Mob(Transform parent, ushort nativeAsset, bool isHostile)
        {
            Transform root = Root(parent, "Server mob");
            if (enemyModelPaths.TryGetValue(nativeAsset, out string path))
            {
                if (!enemyModels.TryGetValue(path, out GameObject model))
                {
                    model = Resources.Load<GameObject>(path);
                    enemyModels[path] = model;
                }
                if (model != null && ImportedLegacy(root, model, isHostile) != null) return root;
            }
            if (legacyMobAssets.TryGetValue(nativeAsset, out GameObject original) &&
                ImportedLegacy(root, original, isHostile) != null)
                return root;
            GameObject asset = frigateAsset;
            Material surface = isHostile ? hostileFrigateSurface : frigateSurface;
            float size = 1.5f;
            int proteron = 1;
            float proteronSize = .85f;
            string modelName = "Viktor Hahn frigate";
            if (nativeAsset == 1022 || nativeAsset == 1023)
            {
                Transform imported = Imported(root, eveGallenteAsset,
                    "Locally exported Gallente hull", Vector3.one * .69f,
                    isHostile ? eveGallenteHostileSurface : eveGallenteSurface);
                if (imported != null)
                {
                    Transform distant = Imported(root, destroyerAsset, "Distant destroyer silhouette",
                        Vector3.one * .55f, isHostile ? hostileDestroyerSurface : destroyerSurface);
                    SetLod(root, imported, distant, .08f);
                    return root;
                }
                asset = destroyerAsset; surface = isHostile ? hostileDestroyerSurface : destroyerSurface;
                size = .55f; modelName = "Viktor Hahn destroyer";
                proteron = 3; proteronSize = .28f;
            }
            else if (nativeAsset == 384)
            {
                asset = cruiserAsset; surface = isHostile ? hostileCruiserSurface : cruiserSurface;
                size = .38f; modelName = "Viktor Hahn cruiser";
                proteron = 0; proteronSize = .36f;
            }
            else if (nativeAsset >= 1030 && nativeAsset <= 1049)
            {
                asset = carrierAsset; surface = isHostile ? hostileCarrierSurface : carrierSurface;
                size = .45f; modelName = "Viktor Hahn carrier";
                proteron = 2; proteronSize = .6f;
            }
            Material detailedSurface = isHostile ? hostileProteronSurfaces[proteron] :
                proteronSurfaces[proteron];
            Transform detailed = Imported(root, proteronAssets[proteron],
                "Proteron " + proteronAssets[proteron]?.name, Vector3.one * proteronSize,
                detailedSurface);
            if (detailed != null)
            {
                Transform silhouette = Imported(root, asset, modelName + " distant LOD", Vector3.one * size,
                    surface);
                SetLod(root, detailed, silhouette, .08f);
                return root;
            }
            if (Imported(root, asset, modelName, Vector3.one * size, surface) != null)
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
            else if (item.Kind == 38) { ResourceRock(root, item); root.localScale *= .72f; }
            else if (item.Asset == 412 || item.Asset == 377) { Station(root); root.localScale *= .03f; }
            else if (item.Asset == 127) { Beacon(root); root.localScale *= .2f; }
            else
            {
                float radius = item.Navigable ? .4f : item.Asset == 491 ? 3.8f : item.Asset == 490 ? 2.7f : 1.6f;
                if (!PackRock(root, item.Id, radius * 1.75f) &&
                    !MoonRock(root, item.Id, moonStone,
                        new Vector3(radius * .68f, radius * .55f, radius * .78f)))
                {
                    int variant = (int)(item.Id % (uint)rocks.Length);
                    Model(root, rocks[variant], rock, Vector3.zero,
                        new Vector3(radius, radius * .8f, radius * 1.2f));
                }
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

        private enum ResourceFamily { Metallic, Crystalline, Hydrocarbon, Gas, Hulk }

        /// <returns>ResourceFamily: The native resource family conveyed by its public object name.</returns>
        private static ResourceFamily ResourceFamilyOf(MapObject item)
        {
            if (item.Name.StartsWith("Crystalline", StringComparison.OrdinalIgnoreCase))
                return ResourceFamily.Crystalline;
            if (item.Name.StartsWith("Hydrocarbon", StringComparison.OrdinalIgnoreCase))
                return ResourceFamily.Hydrocarbon;
            if (item.Name.StartsWith("Gas Cloud", StringComparison.OrdinalIgnoreCase))
                return ResourceFamily.Gas;
            if (item.Name.StartsWith("Hulk", StringComparison.OrdinalIgnoreCase))
                return ResourceFamily.Hulk;
            return ResourceFamily.Metallic;
        }

        /// <summary>Give each server resource family a readable material and silhouette.</summary>
        private void ResourceRock(Transform root, MapObject item)
        {
            uint id = item.Id;
            ResourceFamily family = ResourceFamilyOf(item);
            if (family == ResourceFamily.Gas)
            {
                if (gasVapor != null)
                    for (int layer = 0; layer < 2; layer++)
                    {
                        Transform cloud = Part(root, PrimitiveType.Sphere,
                            new Vector3(layer == 0 ? -.28f : .34f, layer == 0 ? .06f : -.08f, 0),
                            layer == 0 ? new Vector3(2.35f, 1.2f, 1.5f) :
                                new Vector3(1.65f, 1.4f, 2.05f), gasVapor);
                        cloud.localRotation = Quaternion.Euler(22 + layer * 39, id % 91, layer * 57);
                    }
                else MoonRock(root, id, moonOre, new Vector3(1.2f, .8f, 1.1f));
                for (int spark = 0; spark < 3; spark++)
                {
                    float angle = (spark * 120 + id % 53) * Mathf.Deg2Rad;
                    Part(root, PrimitiveType.Sphere,
                        new Vector3(Mathf.Cos(angle) * .8f, Mathf.Sin(angle) * .42f,
                            Mathf.Sin(angle * 1.4f) * .72f), Vector3.one * .1f, mineral);
                }
                return;
            }
            if (family == ResourceFamily.Hulk)
            {
                for (int piece = 0; piece < 4; piece++)
                {
                    float angle = (piece * 89 + id % 37) * Mathf.Deg2Rad;
                    Transform debris = Part(root, PrimitiveType.Cube,
                        new Vector3(Mathf.Cos(angle) * .66f, Mathf.Sin(angle) * .35f,
                            Mathf.Sin(angle * 1.5f) * .62f),
                        new Vector3(1.1f - piece * .13f, .14f, .65f),
                        piece == 0 ? metalSeam : stationFrame);
                    debris.localRotation = Quaternion.Euler(piece * 31, piece * 67, piece * 23);
                }
                Part(root, PrimitiveType.Sphere, Vector3.zero, Vector3.one * .28f, amber);
                return;
            }
            Material[] surface = family == ResourceFamily.Crystalline ? moonOre :
                family == ResourceFamily.Hydrocarbon ? moonHydrocarbon : moonMetal;
            Vector3 scale = family == ResourceFamily.Hydrocarbon ?
                new Vector3(1.45f, .85f, 1.35f) : new Vector3(1.35f, 1.05f, 1.3f);
            bool packed = family == ResourceFamily.Metallic && PackRock(root, id, 2.7f);
            if (!packed && !MoonRock(root, id, surface, scale))
                Model(root, rocks[(int)(id % (uint)rocks.Length)], surface[0] ?? oreRock,
                    Vector3.zero, new Vector3(2.1f, 1.5f, 1.9f));
            int features = family == ResourceFamily.Crystalline ? 5 : 3;
            Material accent = family == ResourceFamily.Crystalline ? mineral :
                family == ResourceFamily.Hydrocarbon ? hydrocarbon : metalSeam;
            for (int i = 0; i < features; i++)
            {
                float angle = (i * 360f / features + id % 31) * Mathf.Deg2Rad;
                Vector3 position = new Vector3(Mathf.Cos(angle) * 1.2f,
                    Mathf.Sin(angle * 1.7f) * .55f, Mathf.Sin(angle) * 1.1f);
                Transform seam = Part(root, family == ResourceFamily.Hydrocarbon ?
                    PrimitiveType.Sphere : PrimitiveType.Cube, position,
                    family == ResourceFamily.Crystalline ? new Vector3(.17f, .12f, .62f) :
                    family == ResourceFamily.Hydrocarbon ? new Vector3(.48f, .08f, .31f) :
                        new Vector3(.3f, .11f, .52f), accent);
                seam.localRotation = Quaternion.Euler(20 + i * 13, i * 73, 25 + i * 19);
                if (family == ResourceFamily.Crystalline && i < 2)
                {
                    Transform crystal = Part(root, PrimitiveType.Capsule, position * 1.16f,
                        new Vector3(.14f, .31f + i * .025f, .14f), mineral);
                    crystal.localRotation = Quaternion.Euler(i * 31, i * 67, 24 + i * 9);
                }
            }
        }

        /// <returns>bool: Whether a textured moon-rock model was available and placed.</returns>
        private bool MoonRock(Transform root, uint id, Material[] surfaces, Vector3 scale)
        {
            int variant = (int)(id % (uint)moonRockHigh.Length);
            Material material = surfaces[variant];
            Transform detailed = Imported(root, moonRockHigh[variant], "Faceted moon rock",
                scale, material);
            if (detailed == null) return false;
            Transform silhouette = Imported(root, moonRockLow[variant], "Moon rock distant LOD",
                scale, material);
            SetLod(root, detailed, silhouette, .07f);
            return true;
        }

        /// <returns>bool: Whether a locally exported, UV-textured asteroid was placed.</returns>
        private bool PackRock(Transform root, uint id, float diameter)
        {
            GameObject asset = packAsteroids[(int)(id % (uint)packAsteroids.Length)];
            if (asset == null || packRockSurface == null) return false;
            MeshFilter filter = asset.GetComponentInChildren<MeshFilter>();
            if (filter == null || filter.sharedMesh == null) return false;
            Vector3 size = filter.sharedMesh.bounds.size;
            float scale = diameter / Mathf.Max(size.x, size.y, size.z, .01f);
            return Imported(root, asset, "Metallic asteroid pack hull", Vector3.one * scale,
                packRockSurface) != null;
        }

        /// <summary>Assemble a segmented jump gate with armored arcs and a recessed energy field.</summary>
        private void Gate(Transform root)
        {
            Model(root, gateArc, gateArmor, Vector3.zero, Vector3.one);
            Model(root, ring, gateInset, new Vector3(0, 0, .18f), Vector3.one * 4.13f);
            Model(root, gateLightRing, gateCircuit, new Vector3(0, 0, -.23f),
                Vector3.one * 4.02f);
            for (int i = 0; i < 8; i++)
            {
                float a = i * Mathf.PI / 4;
                Transform pylon = Root(root, "Gate radial actuator");
                pylon.localRotation = Quaternion.Euler(0, 0, i * 45);
                Part(pylon, PrimitiveType.Cube, new Vector3(5.05f, 0, 0),
                    new Vector3(1.05f, .85f, 1.65f), gateInset);
                Part(pylon, PrimitiveType.Cube, new Vector3(5.05f, 0, -.88f),
                    new Vector3(.7f, .34f, .14f), gateArmor);
                Part(pylon, PrimitiveType.Cube, new Vector3(4.54f, 0, -.99f),
                    new Vector3(.27f, .2f, .035f), gateCircuit);
                Part(pylon, PrimitiveType.Cube, new Vector3(5.72f, 0, .22f),
                    new Vector3(.14f, .42f, 1.3f), gateArmor);
            }
            Part(root, PrimitiveType.Quad, new Vector3(0, 0, .07f),
                new Vector3(7.75f, 7.75f, 1), field);
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
            BlackHole(root);
            GameObject volcanicMesh = Resources.Load<GameObject>("PlanetPack/planet");
            Texture2D volcanicMap = Resources.Load<Texture2D>("PlanetPack/planet_diffuse");
            if (volcanicMesh != null && volcanicMap != null)
            {
                Material surface = ShaderMaterial("42 and Beyond/ProceduralPlanet");
                surface.SetTexture("_MainTex", volcanicMap);
                surface.SetTexture("_NormalTex", Resources.Load<Texture2D>("PlanetPack/planet_normal"));
                surface.SetTexture("_RoughnessTex", Resources.Load<Texture2D>("PlanetPack/planet_roughness"));
                surface.SetTexture("_AoTex", Resources.Load<Texture2D>("PlanetPack/planet_ao"));
                Vector3 location = new Vector3(6500, -2000, 16000);
                Transform world = Imported(root, volcanicMesh, "Procedural volcanic world",
                    Vector3.one * (4400f / 1.711f), surface);
                world.localPosition = location;
                world.localRotation = Quaternion.Euler(-14, 28, -18);
                Material atmosphere = ShaderMaterial("42 and Beyond/PlanetAtmosphere");
                Part(root, PrimitiveType.Sphere, location, Vector3.one * 4570, atmosphere);
                return root;
            }
            Material planetMaterial = ShaderMaterial("42 and Beyond/Planet");
            Texture2D saturn = Resources.Load<Texture2D>("SaturnNASA");
            if (saturn != null)
            {
                planetMaterial.SetTexture("_MainTex", saturn);
                planetMaterial.SetFloat("_UseTexture", 1f);
            }
            Transform planet = Part(root, PrimitiveType.Sphere, new Vector3(6500, -2000, 16000),
                Vector3.one * 4400, planetMaterial);
            planet.localRotation = Quaternion.Euler(0, 0, -18);
            Material ringMaterial = ShaderMaterial("42 and Beyond/PlanetRings");
            Mesh belt = Annulus(1.25f, 2.15f, 160); meshes.Add(belt);
            Transform rings = Model(planet, belt, ringMaterial, Vector3.zero, Vector3.one * .5f);
            rings.localRotation = Quaternion.Euler(65, 0, 0);
            return root;
        }

        /// <returns>Transform: A bright camera-facing star and its distant corona.</returns>
        public Transform Sun(Transform environment)
        {
            Transform star = Root(environment, "System sun");
            Part(star, PrimitiveType.Quad, Vector3.zero, new Vector3(3500, 3500, 1),
                sunCoronaSurface).name = "Stellar photosphere and corona";
            Part(star, PrimitiveType.Quad, new Vector3(0, 0, 35),
                new Vector3(7600, 7600, 1), sunHazeSurface).name = "Diffuse stellar glow";
            return star;
        }

        /// <summary>Match the visible star's tint to its sector's directional light.</summary>
        public void TintSun(Color color)
        {
            sunCoronaSurface.SetColor("_Tint", color);
            sunHazeSurface.SetColor("_Tint", color);
        }

        /// <summary>Place the locally exported accretion disk around an opaque dark core.</summary>
        private void BlackHole(Transform root)
        {
            if (blackHoleDiskSurface == null || blackHoleCoreSurface == null) return;
            Transform body = Root(root, "Distant accretion disk");
            body.localPosition = new Vector3(0, -7500, 17000);
            body.localRotation = Quaternion.Euler(7, -14, 24);
            Imported(body, blackHoleCore, "Black hole event horizon", Vector3.one * 3000,
                blackHoleCoreSurface);
            Transform disk = Imported(body, blackHoleRing, "Black hole accretion disk",
                Vector3.one * 1350, blackHoleDiskSurface);
            if (disk != null) disk.localRotation = Quaternion.Euler(66, -12, 8);
            Transform clouds = Imported(body, blackHoleRing, "Black hole outer plasma",
                Vector3.one * 1430, blackHoleCloudSurface);
            if (clouds != null) clouds.localRotation = Quaternion.Euler(61, 19, -28);
            Transform photon = Model(body, gateLightRing, blackHolePhotonSurface, Vector3.zero,
                Vector3.one * 1450);
            photon.localRotation = Quaternion.Euler(68, -9, 5);
            Model(body, gateLightRing, blackHoleLensSurface, Vector3.zero,
                Vector3.one * 2250);
        }

        /// <returns>Mesh: A tapered octagonal armor shell shared by the station pilot's suit.</returns>
        private static Mesh SuitArmor()
        {
            float[] levels = { 0f, .16f, .78f, 1f };
            float[] widths = { .64f, .85f, 1f, .73f };
            float[] depths = { .72f, .9f, 1f, .72f };
            Vector2[] outline =
            {
                new Vector2(-.78f, -1), new Vector2(.78f, -1),
                new Vector2(1, -.72f), new Vector2(1, .72f),
                new Vector2(.78f, 1), new Vector2(-.78f, 1),
                new Vector2(-1, .72f), new Vector2(-1, -.72f)
            };
            var vertices = new Vector3[levels.Length * outline.Length];
            var triangles = new List<int>();
            for (int ringIndex = 0; ringIndex < levels.Length; ringIndex++)
                for (int corner = 0; corner < outline.Length; corner++)
                    vertices[ringIndex * outline.Length + corner] = new Vector3(
                        outline[corner].x * widths[ringIndex], levels[ringIndex],
                        outline[corner].y * depths[ringIndex]);
            for (int ringIndex = 0; ringIndex < levels.Length - 1; ringIndex++)
                for (int corner = 0; corner < outline.Length; corner++)
                {
                    int a = ringIndex * outline.Length + corner;
                    int b = ringIndex * outline.Length + (corner + 1) % outline.Length;
                    int c = a + outline.Length, d = b + outline.Length;
                    triangles.AddRange(new[] { a, c, b, b, c, d });
                }
            int top = (levels.Length - 1) * outline.Length;
            for (int corner = 1; corner < outline.Length - 1; corner++)
            {
                triangles.AddRange(new[] { 0, corner, corner + 1 });
                triangles.AddRange(new[] { top, top + corner + 1, top + corner });
            }
            var mesh = new Mesh { name = "Pilot suit armor", vertices = vertices,
                triangles = triangles.ToArray() };
            mesh.RecalculateNormals(); mesh.RecalculateBounds();
            return mesh;
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

        /// <returns>Mesh: Eight separated, flat-shaded armor sectors around the jump aperture.</returns>
        private static Mesh GateArc()
        {
            var vertices = new List<Vector3>();
            var triangles = new List<int>();
            const int sectors = 8, steps = 10;
            for (int sector = 0; sector < sectors; sector++)
            {
                float start = (sector + .09f) * Mathf.PI * 2 / sectors;
                float end = (sector + .91f) * Mathf.PI * 2 / sectors;
                for (int step = 0; step < steps; step++)
                {
                    float a = Mathf.Lerp(start, end, (float)step / steps);
                    float b = Mathf.Lerp(start, end, (float)(step + 1) / steps);
                    Vector3 ia = new Vector3(Mathf.Cos(a) * 4.18f, Mathf.Sin(a) * 4.18f, -.53f);
                    Vector3 oa = new Vector3(Mathf.Cos(a) * 5.42f, Mathf.Sin(a) * 5.42f, -.53f);
                    Vector3 ib = new Vector3(Mathf.Cos(b) * 4.18f, Mathf.Sin(b) * 4.18f, -.53f);
                    Vector3 ob = new Vector3(Mathf.Cos(b) * 5.42f, Mathf.Sin(b) * 5.42f, -.53f);
                    Vector3 iaRear = ia + Vector3.forward * 1.06f;
                    Vector3 oaRear = oa + Vector3.forward * 1.06f;
                    Vector3 ibRear = ib + Vector3.forward * 1.06f;
                    Vector3 obRear = ob + Vector3.forward * 1.06f;
                    GateQuad(vertices, triangles, ia, ib, ob, oa);
                    GateQuad(vertices, triangles, iaRear, oaRear, obRear, ibRear);
                    GateQuad(vertices, triangles, oa, ob, obRear, oaRear);
                    GateQuad(vertices, triangles, iaRear, ibRear, ib, ia);
                    if (step == 0) GateQuad(vertices, triangles, ia, oa, oaRear, iaRear);
                    if (step == steps - 1)
                        GateQuad(vertices, triangles, ibRear, obRear, ob, ib);
                }
            }
            var mesh = new Mesh { name = "Segmented jump gate", vertices = vertices.ToArray(),
                triangles = triangles.ToArray() };
            mesh.RecalculateNormals(); mesh.RecalculateBounds();
            return mesh;
        }

        /// <summary>Add one flat-shaded quadrilateral to a generated gate shell.</summary>
        private static void GateQuad(List<Vector3> vertices, List<int> triangles,
            Vector3 a, Vector3 b, Vector3 c, Vector3 d)
        {
            int first = vertices.Count;
            vertices.AddRange(new[] { a, b, c, d });
            triangles.AddRange(new[] { first, first + 1, first + 2,
                first, first + 2, first + 3 });
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
