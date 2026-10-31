using System;
using System.Linq;
using UnityEngine;

namespace FortyTwoAndBeyond.Client
{
    /// <summary>Exercise imported ship animations in a built player without a server.</summary>
    public static class OriginalAnimationRuntimeProbe
    {
        /// <returns>bool: Whether native PW shoulder animation moved in the built player.</returns>
        private static bool CheckPw()
        {
            var hull = new GameObject("Disposable PW animation probe");
            PwWarpArms.Attach(hull);
            PwWarpArms arms = hull.GetComponent<PwWarpArms>();
            Transform shoulder = arms?.GetComponentsInChildren<Transform>()
                .FirstOrDefault(part => part.name == "L_1");
            if (shoulder == null) return false;
            Quaternion resting = shoulder.localRotation;
            arms.Tick(true, 3f);
            float degrees = Quaternion.Angle(resting, shoulder.localRotation);
            Debug.Log("PW runtime shoulder moved " + degrees + " degrees.");
            return degrees >= 8f;
        }

        /// <returns>bool: Whether one JE FBX section changed a bone pose at warp.</returns>
        private static bool CheckJeSection(Transform section, JeWarpRig rig)
        {
            if (section == null) return false;
            Transform[] bones = section.GetComponentsInChildren<Transform>();
            Vector3[] positions = bones.Select(bone => bone.localPosition).ToArray();
            Quaternion[] rotations = bones.Select(bone => bone.localRotation).ToArray();
            rig.Tick(true, 3f);
            return bones.Where((bone, index) =>
                Vector3.Distance(positions[index], bone.localPosition) > .005f ||
                Quaternion.Angle(rotations[index], bone.localRotation) > 3f).Any();
        }

        /// <returns>bool: Whether both native JE sections animate in the built player.</returns>
        private static bool CheckJe()
        {
            var hull = new GameObject("Disposable JE animation probe");
            JeWarpRig.Attach(hull);
            JeWarpRig rig = hull.GetComponent<JeWarpRig>();
            if (rig == null) return false;
            Transform profession = hull.transform.Find("Original JE animated JEProfessionAnimated");
            Transform wings = hull.transform.Find("Original JE animated JEWingsAnimated");
            // Keep the profession's rest pose while testing the wing section separately.
            bool professionMoved = CheckJeSection(profession, rig);
            rig.Tick(false, 3f);
            bool wingsMoved = CheckJeSection(wings, rig);
            Debug.Log("JE runtime profession=" + professionMoved + " wings=" + wingsMoved);
            return professionMoved && wingsMoved;
        }

        /// <returns>bool: Whether both native Rally rigs render and animate in a built player.</returns>
        private static bool CheckRally()
        {
            var ship = new GameObject("Disposable native Rally probe");
            ship.transform.localScale = Vector3.one * .25f;
            var visual = new NativeRallyVisual(ship.transform, 676);
            if (!visual.Active) { Debug.LogError("Rally runtime visual inactive."); return false; }
            bool allMoved = true;
            foreach (string name in new[] { "RallyDamageAnimated", "RallyFiringAnimated" })
            {
                Transform root = ship.transform.Find("Original " + name);
                if (root == null) { Debug.LogError("Rally runtime root missing: " + name); return false; }
                MeshRenderer[] renderers = root.GetComponentsInChildren<MeshRenderer>(true);
                if (renderers.Length < 3 || renderers.Any(renderer =>
                    renderer.sharedMaterial == null ||
                    renderer.sharedMaterial.shader == null) ||
                    Resources.Load<Texture2D>("LegacyEffects/sk_rally07") == null)
                {
                    Debug.LogError("Rally runtime material or sprite missing: " + name);
                    return false;
                }
                Transform[] bones = root.GetComponentsInChildren<Transform>(true);
                Vector3[] before = bones.Select(bone => bone.localPosition).ToArray();
                Quaternion[] beforeRotation = bones.Select(bone => bone.localRotation).ToArray();
                Vector3[] beforeScale = bones.Select(bone => bone.localScale).ToArray();
                AnimationClip clip = Resources.LoadAll<AnimationClip>("LegacyEffects/" + name)
                    .FirstOrDefault(candidate => candidate.name == "Scene");
                if (clip == null) { Debug.LogError("Rally runtime clip missing: " + name); return false; }
                clip.SampleAnimation(root.gameObject, clip.length * .45f);
                bool moved = bones.Where((bone, index) =>
                    Vector3.Distance(before[index], bone.localPosition) > .005f ||
                    Quaternion.Angle(beforeRotation[index], bone.localRotation) > 1f ||
                    Vector3.Distance(beforeScale[index], bone.localScale) > .005f).Any();
                Debug.Log("Rally runtime " + name + " meshes=" + renderers.Length +
                    " native motion=" + moved);
                allMoved &= moved;
            }
            visual.Dispose();
            return allMoved;
        }

        /// <returns>bool: Whether the original Combat Trance sprite drives an emitter.</returns>
        private static bool CheckTrance()
        {
            var ship = new GameObject("Disposable Combat Trance probe");
            var visual = new NativeTranceVisual(ship.transform, 669);
            if (!visual.Active) return false;
            ParticleSystem emitter = ship.GetComponentInChildren<ParticleSystem>();
            ParticleSystemRenderer renderer = ship.GetComponentInChildren<ParticleSystemRenderer>();
            Transform rail = ship.transform.Find("Original Combat Trance power rail");
            MeshRenderer railRenderer = rail?.GetComponentInChildren<MeshRenderer>();
            bool valid = emitter != null && emitter.isPlaying && renderer != null &&
                renderer.sharedMaterial != null &&
                railRenderer != null && railRenderer.sharedMaterial != null &&
                railRenderer.sharedMaterial.shader.name == "42 and Beyond/CombatTranceRing" &&
                Resources.Load<Texture2D>("LegacyEffects/sk_comtra02") != null &&
                Mathf.Abs(emitter.main.startLifetime.constant - .25f) < .001f &&
                Mathf.Abs(emitter.emission.rateOverTime.constant - 30.303f) < .001f;
            visual.Tick();
            Debug.Log("Combat Trance original emitter active=" + valid);
            visual.Dispose();
            return valid;
        }

        /// <returns>bool: Whether the original Environment Shield sphere imports in the player.</returns>
        private static bool CheckEnvironmentShield()
        {
            var ship = new GameObject("Disposable Environment Shield probe");
            var visual = new NativeSkillShieldVisual(ship.transform, 216);
            MeshRenderer[] renderers = ship.GetComponentsInChildren<MeshRenderer>(true);
            bool valid = visual.Active && renderers.Length > 0 &&
                Resources.Load<Texture2D>("LegacyEffects/shld_env01") != null &&
                Resources.Load<Texture2D>("LegacyEffects/JVE0108") != null;
            visual.Tick();
            visual.Dispose();
            Debug.Log("Environment Shield original sphere active=" + valid);
            return valid;
        }

        /// <returns>bool: Whether the original Psionic Shield meshes import in the player.</returns>
        private static bool CheckPsionicShield()
        {
            var ship = new GameObject("Disposable Psionic Shield probe");
            var visual = new NativeSkillShieldVisual(ship.transform, 214);
            MeshRenderer[] renderers = ship.GetComponentsInChildren<MeshRenderer>(true);
            bool valid = visual.Active && renderers.Length >= 2 &&
                Resources.Load<Texture2D>("LegacyEffects/sk_psysh05") != null &&
                Resources.Load<Texture2D>("LegacyEffects/shld_reg02") != null;
            visual.Tick();
            visual.Dispose();
            Debug.Log("Psionic Shield original meshes active=" + valid);
            return valid;
        }

        /// <returns>bool: Whether the original Hacking sprite traces a target line.</returns>
        private static bool CheckHackingBeam()
        {
            var parent = new GameObject("Disposable Hacking beam probe");
            var visual = new NativeHackingBeamVisual(parent.transform);
            visual.Tick(Vector3.zero, new Vector3(0, 0, 4));
            LineRenderer line = parent.GetComponentInChildren<LineRenderer>();
            bool valid = visual.Active && line != null && line.enabled &&
                Vector3.Distance(line.GetPosition(1), new Vector3(0, 0, 4)) < .001f &&
                Resources.Load<Texture2D>("LegacyEffects/sk_hack02") != null;
            visual.Dispose();
            Debug.Log("Hacking original line active=" + valid);
            return valid;
        }

        /// <returns>bool: Whether the original Menace skull renders on a mob target.</returns>
        private static bool CheckMenace()
        {
            var parent = new GameObject("Disposable Menace probe");
            var visual = new NativeMenaceVisual(parent.transform, 198);
            visual.Tick(new Vector3(0, 0, 4), null);
            MeshRenderer[] meshes = parent.GetComponentsInChildren<MeshRenderer>(true);
            bool valid = visual.Active && meshes.Length >= 2 &&
                Resources.Load<Texture2D>("LegacyEffects/sk_menace01") != null;
            visual.Dispose();
            Debug.Log("Menace original hologram active=" + valid);
            return valid;
        }

        /// <returns>bool: Whether the original Befriend sprite emits on a target.</returns>
        private static bool CheckBefriend()
        {
            var parent = new GameObject("Disposable Befriend probe");
            var visual = new NativeBefriendVisual(parent.transform);
            visual.Tick(new Vector3(0, 0, 4));
            ParticleSystem emitter = parent.GetComponentInChildren<ParticleSystem>();
            bool valid = visual.Active && emitter != null && emitter.isPlaying &&
                Mathf.Abs(emitter.emission.rateOverTime.constant - 40f) < .001f &&
                Resources.Load<Texture2D>("LegacyEffects/FANTA01") != null;
            visual.Dispose();
            Debug.Log("Befriend original emitter active=" + valid);
            return valid;
        }

        /// <returns>bool: Whether the original recharge flash follows a ship target.</returns>
        private static bool CheckRecharge()
        {
            var parent = new GameObject("Disposable recharge probe");
            var visual = new NativeSkillShieldVisual(parent.transform, 136);
            visual.TickAt(new Vector3(0, 0, 4));
            MeshRenderer[] meshes = parent.GetComponentsInChildren<MeshRenderer>(true);
            bool valid = visual.Active && meshes.Length >= 2 &&
                Resources.Load<Texture2D>("LegacyEffects/shldtype07") != null;
            visual.Dispose();
            Debug.Log("Recharge original flash active=" + valid);
            return valid;
        }

        /// <returns>bool: Whether original Shield Inversion lightning loads and aims.</returns>
        private static bool CheckInversion()
        {
            var parent = new GameObject("Disposable Shield Inversion probe");
            var visual = new NativeInversionVisual(parent.transform);
            visual.Tick(Vector3.zero, new Vector3(0, 0, 4));
            MeshRenderer[] meshes = parent.GetComponentsInChildren<MeshRenderer>(true);
            bool valid = visual.Active && meshes.Length > 0 &&
                Resources.Load<Texture2D>("LegacyEffects/shld_inv06") != null;
            visual.Dispose();
            Debug.Log("Shield Inversion original lightning active=" + valid +
                " meshes=" + meshes.Length + " texture=" +
                (Resources.Load<Texture2D>("LegacyEffects/shld_inv06") != null));
            return valid;
        }

        /// <returns>bool: Whether the original Self Destruct atlas shows its 3 cell.</returns>
        private static bool CheckSelfDestruct()
        {
            var parent = new GameObject("Disposable Self Destruct probe");
            var visual = new NativeSelfDestructVisual(parent.transform);
            visual.Tick(Vector3.zero, Vector3.forward, null);
            MeshRenderer sprite = parent.GetComponentInChildren<MeshRenderer>();
            bool valid = visual.Active && sprite != null && sprite.enabled &&
                Resources.Load<Texture2D>("LegacyEffects/sk_sdest01") != null;
            visual.Dispose();
            Debug.Log("Self Destruct original countdown active=" + valid);
            return valid;
        }

        /// <returns>bool: Whether original Fold Space emitters start on a ship.</returns>
        private static bool CheckFold()
        {
            var parent = new GameObject("Disposable Fold Space probe");
            var visual = new NativeFoldVisual(parent.transform, 202);
            visual.Tick(new Vector3(0, 0, 4));
            ParticleSystem[] emitters = parent.GetComponentsInChildren<ParticleSystem>();
            bool valid = visual.Active && emitters.Length == 3 &&
                emitters.All(emitter => emitter.isPlaying) &&
                Resources.Load<Texture2D>("LegacyEffects/sk_fold01") != null;
            visual.Dispose();
            Debug.Log("Fold Space original emitters active=" + valid);
            return valid;
        }

        /// <returns>bool: Whether both procedural wormhole surfaces use supported shaders.</returns>
        private static bool CheckWormhole()
        {
            var parent = new GameObject("Disposable wormhole probe");
            var visual = new NativeWormholeVisual(parent.transform);
            visual.Tick(Vector3.zero, Quaternion.identity);
            MeshRenderer[] surfaces = parent.GetComponentsInChildren<MeshRenderer>();
            bool valid = visual.Active && surfaces.Length == 2 &&
                surfaces.All(surface => surface.sharedMaterial.shader.isSupported);
            visual.Dispose();
            Debug.Log("Create Wormhole plasma surfaces active=" + valid);
            return valid;
        }

        /// <summary>Run only when the isolated built-player animation flag is present.</summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Run()
        {
            if (Array.IndexOf(Environment.GetCommandLineArgs(),
                "--42andbeyond-trance-probe") >= 0)
            {
                bool tranceReady = CheckTrance();
                Debug.Log(tranceReady ? "COMBAT_TRANCE_PROBE_PASS" : "COMBAT_TRANCE_PROBE_FAIL");
                Application.Quit(tranceReady ? 0 : 1);
                return;
            }
            if (Array.IndexOf(Environment.GetCommandLineArgs(),
                "--42andbeyond-animation-probe") < 0) return;
            bool passed = CheckPw() && CheckJe() && CheckRally() && CheckTrance() &&
                CheckEnvironmentShield() && CheckPsionicShield() && CheckHackingBeam() &&
                CheckMenace() && CheckBefriend() && CheckRecharge() && CheckInversion() &&
                CheckSelfDestruct() && CheckFold() && CheckWormhole();
            if (passed) Debug.Log("ORIGINAL_ANIMATION_PROBE_PASS");
            else Debug.LogError("ORIGINAL_ANIMATION_PROBE_FAIL");
            Application.Quit(passed ? 0 : 1);
        }
    }
}
