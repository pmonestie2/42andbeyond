using System.Linq;
using UnityEditor;
using UnityEngine;

namespace FortyTwoAndBeyond.Editor
{
    /// <summary>Inspect converted native effect rigs inside the actual Unity importer.</summary>
    public static class LegacyEffectImportAudit
    {
        /// <summary>Measure native Rally mesh movement across its imported loop.</summary>
        public static void AuditRallyMotion()
        {
            foreach (string name in new[] { "RallyDamageAnimated", "RallyFiringAnimated" })
            {
                string path = "Assets/FortyTwoAndBeyond/Resources/LegacyEffects/" + name + ".fbx";
                GameObject asset = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                AnimationClip clip = AssetDatabase.LoadAllAssetsAtPath(path)
                    .OfType<AnimationClip>().FirstOrDefault(item => item.name == "Scene");
                if (asset == null || clip == null)
                    throw new System.InvalidOperationException("Missing Rally audit input: " + path);
                GameObject instance = UnityEngine.Object.Instantiate(asset);
                foreach (float fraction in new[] { 0f, .2f, .5f, .8f, .99f })
                {
                    clip.SampleAnimation(instance, clip.length * fraction);
                    MeshRenderer[] meshes = instance.GetComponentsInChildren<MeshRenderer>(true)
                        .Where(mesh => name == "RallyDamageAnimated" || mesh.name.StartsWith("WHEEL"))
                        .ToArray();
                    Bounds bounds = meshes[0].bounds;
                    foreach (MeshRenderer mesh in meshes.Skip(1)) bounds.Encapsulate(mesh.bounds);
                    Debug.Log("RALLY_BOUNDS " + name + " t=" + fraction + " center=" +
                        bounds.center.ToString("F3") + " size=" + bounds.size.ToString("F3"));
                }
                UnityEngine.Object.DestroyImmediate(instance);
            }
            Debug.Log("RALLY_MOTION_AUDIT_PASS");
        }

        /// <summary>Check both Rally rigs and record their imported meshes and clips.</summary>
        public static void VerifyRally()
        {
            foreach (string name in new[] { "RallyDamageAnimated", "RallyFiringAnimated" })
            {
                string path = "Assets/FortyTwoAndBeyond/Resources/LegacyEffects/" + name + ".fbx";
                GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                AnimationClip clip = AssetDatabase.LoadAllAssetsAtPath(path)
                    .OfType<AnimationClip>().FirstOrDefault(item => !item.name.StartsWith("__preview__"));
                if (prefab == null || clip == null || clip.length <= .1f ||
                    prefab.GetComponentsInChildren<MeshFilter>(true).Length < 3)
                    throw new System.InvalidOperationException("Native Rally FBX import failed: " + path);
                foreach (MeshRenderer mesh in prefab.GetComponentsInChildren<MeshRenderer>(true))
                    Debug.Log("RALLY_MESH " + name + " " + mesh.name + " textures=" +
                        string.Join(",", mesh.sharedMaterials.Select(item =>
                            item != null && item.mainTexture != null ? item.mainTexture.name : "none")));
                Debug.Log("RALLY_CLIP " + name + " " + clip.name + " duration=" + clip.length);
            }
            Debug.Log("RALLY_IMPORT_PASS");
        }
    }
}
