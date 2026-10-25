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
        private readonly Material housing, beam, projectile, missile, core;
        private uint[] templates = Array.Empty<uint>();
        private uint[] types = Array.Empty<uint>();

        /// <summary>Keep all module geometry under the locally rendered ship.</summary>
        public ShipLoadoutVisuals(Transform ship)
        {
            this.ship = ship;
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

        /// <summary>Attach a distinct beam, projectile, or missile housing to a native hardpoint.</summary>
        private void Weapon(int mount, uint type)
        {
            float x = (mount - 2.5f) * .33f;
            float z = .9f - (mount / 2) * .2f;
            Material accent = type == 14 ? beam : type == 15 ? projectile : missile;
            Part("Installed weapon " + (mount + 1) + " housing", PrimitiveType.Cube,
                new Vector3(x, -.1f, z), new Vector3(.28f, .16f, .72f), housing);
            if (type == 15)
            {
                for (int side = -1; side <= 1; side += 2)
                    Part("Projectile barrel", PrimitiveType.Cylinder,
                        new Vector3(x + side * .085f, -.04f, z + .45f),
                        new Vector3(.045f, .38f, .045f), accent, Quaternion.Euler(90, 0, 0));
            }
            else if (type == 16)
            {
                Part("Missile cassette", PrimitiveType.Cube, new Vector3(x, .02f, z + .18f),
                    new Vector3(.26f, .19f, .44f), accent);
                Part("Missile seeker", PrimitiveType.Sphere, new Vector3(x, .02f, z + .48f),
                    new Vector3(.1f, .1f, .12f), core);
            }
            else
            {
                Part("Beam focusing lens", PrimitiveType.Sphere, new Vector3(x, -.03f, z + .4f),
                    new Vector3(.13f, .13f, .17f), accent);
            }
        }

        /// <summary>Expose the installed shield's small port and starboard emitters.</summary>
        private void Shield()
        {
            for (int side = -1; side <= 1; side += 2)
                Part("Shield emitter", PrimitiveType.Cube, new Vector3(side * 1.15f, .2f, -.2f),
                    new Vector3(.16f, .1f, .53f), core);
        }

        /// <summary>Expose the installed reactor's pair of warm rear coils.</summary>
        private void Reactor()
        {
            for (int side = -1; side <= 1; side += 2)
                Part("Reactor coil", PrimitiveType.Cylinder, new Vector3(side * .38f, .18f, -1.25f),
                    new Vector3(.18f, .16f, .18f), projectile);
        }

        /// <summary>Expose the installed engine's armored aft nozzles.</summary>
        private void Engine()
        {
            for (int side = -1; side <= 1; side += 2)
                Part("Drive nozzle", PrimitiveType.Cylinder, new Vector3(side * .59f, -.08f, -1.37f),
                    new Vector3(.14f, .22f, .14f), beam, Quaternion.Euler(90, 0, 0));
        }

        /// <summary>Attach one compact top-deck module for an installed active device.</summary>
        private void Device(int mount)
        {
            float x = (mount - 2.5f) * .25f;
            Part("Active device socket " + (mount + 1), PrimitiveType.Cube,
                new Vector3(x, .34f, -.45f), new Vector3(.17f, .1f, .26f), housing);
            Part("Active device indicator", PrimitiveType.Cube,
                new Vector3(x, .4f, -.45f), new Vector3(.07f, .025f, .15f), core);
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
        }
    }
}
