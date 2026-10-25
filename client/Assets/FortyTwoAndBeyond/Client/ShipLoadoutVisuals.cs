using System;
using System.Collections.Generic;
using FortyTwoAndBeyond.Protocol;
using UnityEngine;
using Object = UnityEngine.Object;

namespace FortyTwoAndBeyond.Client
{
    /// <summary>Show installed ship systems as small textured modules on the player craft.</summary>
    public sealed class ShipLoadoutVisuals : IDisposable
    {
        private readonly Transform ship;
        private readonly List<GameObject> modules = new List<GameObject>();
        private readonly Mesh moduleShell;
        private readonly Material housing, beam, projectile, missile, core;
        private uint[] templates = Array.Empty<uint>();
        private uint[] types = Array.Empty<uint>();

        /// <summary>Keep all module geometry under the locally rendered ship.</summary>
        public ShipLoadoutVisuals(Transform ship)
        {
            this.ship = ship;
            moduleShell = ModuleMesh();
            Shader shader = Resources.Load<Shader>("Surface");
            if (shader == null) return;
            Texture2D plating = Resources.Load<Texture2D>("HullPlating");
            housing = CreateMaterial(shader, plating, new Color(.14f, .2f, .25f), Color.black);
            beam = CreateMaterial(shader, plating, new Color(.17f, .47f, .55f), new Color(.09f, .95f, 1.3f));
            projectile = CreateMaterial(shader, plating, new Color(.55f, .32f, .2f), new Color(.7f, .22f, .08f));
            missile = CreateMaterial(shader, plating, new Color(.43f, .39f, .22f), new Color(.35f, .25f, .04f));
            core = CreateMaterial(shader, plating, new Color(.2f, .4f, .52f), new Color(.06f, .53f, .8f));
        }

        /// <returns>Material: A textured metal surface with a restrained device glow.</returns>
        private static Material CreateMaterial(Shader shader, Texture2D plating, Color color, Color emission)
        {
            var result = new Material(shader);
            result.SetColor("_Color", color);
            result.SetFloat("_Metallic", .65f);
            result.SetFloat("_Glossiness", .55f);
            result.SetColor("_EmissionColor", emission);
            if (plating != null)
            {
                result.SetTexture("_DetailTex", plating);
                result.SetFloat("_TextureStrength", .65f);
            }
            return result;
        }

        /// <summary>Rebuild visible modules only when the server-published mounted slots change.</summary>
        public void Apply(Snapshot snapshot)
        {
            ItemView[] mounted = snapshot.Mounted;
            if (templates.Length == mounted.Length)
            {
                bool same = true;
                for (int slot = 0; slot < mounted.Length; slot++)
                    if (templates[slot] != (mounted[slot]?.Template ?? 0) ||
                        types[slot] != (mounted[slot]?.Type ?? 0)) { same = false; break; }
                if (same) return;
            }
            Clear();
            templates = new uint[mounted.Length];
            types = new uint[mounted.Length];
            for (int slot = 0; slot < mounted.Length; slot++)
            {
                ItemView item = mounted[slot];
                if (item == null) continue;
                templates[slot] = item.Template; types[slot] = item.Type;
                if (housing == null) continue;
                if (slot >= 3 && slot < 3 + snapshot.WeaponMounts) Weapon(slot - 3, item.Type);
                else if (slot >= 9 && slot < 9 + snapshot.DeviceMounts && item.Type == 11)
                    Device(slot - 9);
                else if (slot == 0 && item.Type == 2) Shield();
                else if (slot == 1 && item.Type == 7) Reactor();
                else if (slot == 2 && item.Type == 6) Engine();
            }
        }

        /// <returns>Transform: A collider-free textured primitive attached to the ship.</returns>
        private Transform Part(string name, PrimitiveType shape, Vector3 position, Vector3 scale,
            Material surface, Quaternion rotation = default)
        {
            GameObject model = GameObject.CreatePrimitive(shape);
            model.name = name;
            model.transform.SetParent(ship, false);
            model.transform.localPosition = position;
            model.transform.localScale = scale;
            model.transform.localRotation = rotation == default ? Quaternion.identity : rotation;
            model.GetComponent<Renderer>().sharedMaterial = surface;
            Object.Destroy(model.GetComponent<Collider>());
            modules.Add(model);
            return model.transform;
        }

        /// <returns>Mesh: A faceted eight-corner hard-surface housing shared by installed modules.</returns>
        private static Mesh ModuleMesh()
        {
            var mesh = new Mesh { name = "Chamfered system housing" };
            mesh.vertices = new[]
            {
                new Vector3(-.5f, -.5f, -.5f), new Vector3(.5f, -.5f, -.5f),
                new Vector3(.5f, -.5f, .5f), new Vector3(-.5f, -.5f, .5f),
                new Vector3(-.35f, .5f, -.43f), new Vector3(.35f, .5f, -.43f),
                new Vector3(.35f, .5f, .42f), new Vector3(-.35f, .5f, .42f)
            };
            mesh.triangles = new[]
            {
                0, 2, 1, 0, 3, 2, 4, 5, 6, 4, 6, 7,
                0, 1, 5, 0, 5, 4, 1, 2, 6, 1, 6, 5,
                2, 3, 7, 2, 7, 6, 3, 0, 4, 3, 4, 7
            };
            mesh.RecalculateNormals(); mesh.RecalculateBounds();
            return mesh;
        }

        /// <returns>Transform: A collider-free faceted module attached to the rendered ship.</returns>
        private Transform Housing(string name, Vector3 position, Vector3 scale, Material surface)
        {
            var model = new GameObject(name, typeof(MeshFilter), typeof(MeshRenderer));
            model.transform.SetParent(ship, false);
            model.transform.localPosition = position;
            model.transform.localScale = scale;
            model.GetComponent<MeshFilter>().sharedMesh = moduleShell;
            model.GetComponent<MeshRenderer>().sharedMaterial = surface;
            modules.Add(model);
            return model.transform;
        }

        /// <summary>Attach a distinct beam, projectile, or missile housing to a native hardpoint.</summary>
        private void Weapon(int mount, uint type)
        {
            float x = (mount - 2.5f) * .37f;
            float z = .82f - (mount / 2) * .24f;
            Material accent = type == 14 ? beam : type == 15 ? projectile : missile;
            Housing("Installed weapon " + (mount + 1) + " armored housing",
                new Vector3(x, .54f, z), new Vector3(.32f, .19f, .78f), housing);
            Part("Weapon thermal spine", PrimitiveType.Cube, new Vector3(x, .66f, z - .06f),
                new Vector3(.065f, .025f, .38f), accent);
            if (type == 15)
            {
                for (int side = -1; side <= 1; side += 2)
                    Part("Projectile barrel", PrimitiveType.Cylinder,
                        new Vector3(x + side * .09f, .53f, z + .46f),
                        new Vector3(.055f, .42f, .055f), accent, Quaternion.Euler(90, 0, 0));
            }
            else if (type == 16)
            {
                Housing("Missile cassette", new Vector3(x, .64f, z + .13f),
                    new Vector3(.25f, .13f, .42f), accent);
                Part("Missile seeker", PrimitiveType.Sphere, new Vector3(x, .61f, z + .48f),
                    new Vector3(.1f, .1f, .12f), core);
            }
            else
            {
                Part("Beam focusing lens", PrimitiveType.Sphere, new Vector3(x, .55f, z + .43f),
                    new Vector3(.16f, .13f, .18f), accent);
                Part("Beam aperture rim", PrimitiveType.Cylinder, new Vector3(x, .55f, z + .52f),
                    new Vector3(.15f, .035f, .15f), core, Quaternion.Euler(90, 0, 0));
            }
        }

        /// <summary>Expose the installed shield's small port and starboard emitters.</summary>
        private void Shield()
        {
            for (int side = -1; side <= 1; side += 2)
            {
                Housing("Shield emitter", new Vector3(side * 1.05f, .17f, -.2f),
                    new Vector3(.24f, .13f, .62f), housing);
                Part("Shield field strip", PrimitiveType.Cube,
                    new Vector3(side * 1.05f, .25f, -.2f),
                    new Vector3(.08f, .035f, .47f), core);
            }
        }

        /// <summary>Expose the installed reactor's pair of warm rear coils.</summary>
        private void Reactor()
        {
            for (int side = -1; side <= 1; side += 2)
            {
                Housing("Reactor armor", new Vector3(side * .46f, .51f, -1.4f),
                    new Vector3(.36f, .24f, .58f), housing);
                Part("Reactor coil", PrimitiveType.Cylinder, new Vector3(side * .38f, .18f, -1.25f),
                    new Vector3(.18f, .16f, .18f), projectile);
            }
        }

        /// <summary>Expose the installed engine's armored aft nozzles.</summary>
        private void Engine()
        {
            for (int side = -1; side <= 1; side += 2)
            {
                Housing("Drive nacelle", new Vector3(side * .61f, .3f, -1.76f),
                    new Vector3(.38f, .27f, .9f), housing);
                Part("Drive nozzle", PrimitiveType.Cylinder, new Vector3(side * .59f, -.08f, -1.37f),
                    new Vector3(.14f, .22f, .14f), beam, Quaternion.Euler(90, 0, 0));
            }
        }

        /// <summary>Attach one compact top-deck module for an installed active device.</summary>
        private void Device(int mount)
        {
            float x = (mount - 2.5f) * .25f;
            Housing("Active device socket " + (mount + 1),
                new Vector3(x, .77f, -.52f), new Vector3(.19f, .13f, .33f), housing);
            Part("Active device indicator", PrimitiveType.Cube,
                new Vector3(x, .85f, -.52f), new Vector3(.08f, .025f, .2f), core);
        }

        /// <summary>Remove the prior server loadout's visual modules.</summary>
        private void Clear()
        {
            foreach (GameObject module in modules) if (module != null) Object.Destroy(module);
            modules.Clear();
        }

        /// <summary>Forget installed modules when the world or account is cleared.</summary>
        public void Reset()
        {
            Clear();
            templates = Array.Empty<uint>();
            types = Array.Empty<uint>();
        }

        /// <summary>Release module geometry and owned surface materials.</summary>
        public void Dispose()
        {
            Reset();
            foreach (Material material in new[] { housing, beam, projectile, missile, core })
                if (material != null) Object.Destroy(material);
            if (moduleShell != null) Object.Destroy(moduleShell);
        }
    }
}
