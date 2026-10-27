using System;
using System.Linq;
using UnityEngine;
using Object = UnityEngine.Object;

namespace FortyTwoAndBeyond.Client
{
    /// <summary>Play the original JE profession and wing FBX animations together.</summary>
    public sealed class JeWarpRig : MonoBehaviour
    {
        private const float Duration = 3f;
        private static readonly string[] AssetNames = {
            "JEProfessionAnimated", "JEWingsAnimated" };
        private readonly GameObject[] models = new GameObject[2];
        private readonly AnimationClip[] clips = new AnimationClip[2];
        private float progress;

        /// <summary>Attach both native JE rigs to the shared static hull and engine.</summary>
        public static void Attach(GameObject hull)
        {
            if (hull == null) return;
            var assets = new GameObject[AssetNames.Length];
            var animations = new AnimationClip[AssetNames.Length];
            for (int index = 0; index < AssetNames.Length; index++)
            {
                string path = "LegacyShips/" + AssetNames[index];
                assets[index] = Resources.Load<GameObject>(path);
                animations[index] = Resources.LoadAll<AnimationClip>(path)
                    .FirstOrDefault(candidate => candidate.name == "Scene");
                if (assets[index] != null && animations[index] != null) continue;
                Debug.LogError("JE animated FBX or warp clip is missing: " + path);
                return;
            }
            var controller = hull.AddComponent<JeWarpRig>();
            for (int index = 0; index < assets.Length; index++)
            {
                GameObject model = Object.Instantiate(assets[index], hull.transform);
                model.name = "Original JE animated " + AssetNames[index];
                model.transform.localPosition = Vector3.zero;
                Animator animator = model.GetComponent<Animator>();
                if (animator == null) animator = model.AddComponent<Animator>();
                animator.applyRootMotion = false;
                controller.models[index] = model;
                controller.clips[index] = animations[index];
                animations[index].SampleAnimation(model, 0);
            }
        }

        /// <summary>Sample both imported clips from the authoritative warp phase.</summary>
        public void Tick(bool preparingOrTraveling, float delta)
        {
            float previous = progress;
            progress = Mathf.MoveTowards(progress, preparingOrTraveling ? 1f : 0f,
                Mathf.Max(0, delta) / Duration);
            if (Mathf.Approximately(previous, progress)) return;
            for (int index = 0; index < models.Length; index++)
                if (models[index] != null && clips[index] != null)
                    clips[index].SampleAnimation(models[index], clips[index].length * progress);
        }
    }
}
