using System;
using System.Linq;
using UnityEngine;
using Object = UnityEngine.Object;

namespace FortyTwoAndBeyond.Client
{
    /// <summary>Play the original PW shoulder animation from a rigged FBX asset.</summary>
    public sealed class PwWarpArms : MonoBehaviour
    {
        private const float Duration = 3f;
        private readonly Transform[] weaponSockets = new Transform[2];
        private GameObject animatedModel;
        private AnimationClip warpClip;
        private float progress;

        /// <summary>Attach the original rigged meshes and their imported animation clip.</summary>
        public static void Attach(GameObject hull)
        {
            if (hull == null) return;
            GameObject asset = Resources.Load<GameObject>("LegacyShips/PWArmsAnimated");
            AnimationClip clip = Resources.LoadAll<AnimationClip>("LegacyShips/PWArmsAnimated")
                .FirstOrDefault(candidate => candidate.name == "Scene");
            if (asset == null || clip == null)
            { Debug.LogError("PW animated FBX or warp clip is missing."); return; }
            GameObject model = Object.Instantiate(asset, hull.transform);
            model.name = "Original PW animated arms";
            model.transform.localPosition = Vector3.zero;
            Animator animator = model.GetComponent<Animator>();
            if (animator == null) animator = model.AddComponent<Animator>();
            animator.applyRootMotion = false;
            var controller = hull.AddComponent<PwWarpArms>();
            controller.animatedModel = model;
            controller.warpClip = clip;
            Transform left = FindBone(model, "~WEAP_02");
            Transform right = FindBone(model, "~WEAP_01");
            if (left == null || right == null)
            {
                Debug.LogError("PW FBX is missing its original weapon bones.");
                Object.Destroy(controller);
                Object.Destroy(model);
                return;
            }
            controller.AddWeaponSocket(0, left);
            controller.AddWeaponSocket(1, right);
            clip.SampleAnimation(model, 0);
        }

        /// <returns>Transform: The named bone inside the imported FBX, if present.</returns>
        private static Transform FindBone(GameObject model, string name)
        { return model.GetComponentsInChildren<Transform>(true)
            .FirstOrDefault(child => child.name == name); }

        /// <summary>Put a Unity-sized visual weapon on an animated original hardpoint.</summary>
        private void AddWeaponSocket(int side, Transform bone)
        {
            var socket = new GameObject("PW weapon socket " + side).transform;
            socket.SetParent(bone, false);
            socket.localPosition = Vector3.zero;
            Quaternion boneInHull = Quaternion.Inverse(transform.rotation) * bone.rotation;
            socket.localRotation = Quaternion.Inverse(boneInHull) * Quaternion.Euler(0, -90, 0);
            weaponSockets[side] = socket;
        }

        /// <summary>Keep mounted weapon housings at player-ship scale after normalization.</summary>
        public void ConfigureWeaponScale()
        {
            foreach (Transform socket in weaponSockets)
                if (socket != null)
                    socket.localScale = Vector3.one * .55f /
                        Mathf.Max(.001f, Mathf.Abs(socket.parent.lossyScale.x));
        }

        /// <returns>Transform: The animated left or right original weapon mount.</returns>
        public Transform WeaponSocket(int mount)
        { return weaponSockets[Math.Abs(mount) % weaponSockets.Length]; }

        /// <summary>Sample the imported shoulder clip from the server's warp phase.</summary>
        public void Tick(bool preparingOrTraveling, float delta)
        {
            float previous = progress;
            progress = Mathf.MoveTowards(progress, preparingOrTraveling ? 1f : 0f,
                Mathf.Max(0, delta) / Duration);
            if (Mathf.Approximately(previous, progress) || animatedModel == null || warpClip == null)
                return;
            warpClip.SampleAnimation(animatedModel, warpClip.length * progress);
        }
    }
}
