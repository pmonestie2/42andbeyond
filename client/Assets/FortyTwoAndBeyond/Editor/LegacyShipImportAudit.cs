using System.Linq;
using System.IO;
using UnityEditor;
using UnityEngine;
using FortyTwoAndBeyond.Client;

namespace FortyTwoAndBeyond.Editor
{
    /// <summary>Inspect converted W3D hull imports without entering a live session.</summary>
    public static class LegacyShipImportAudit
    {
        /// <summary>Check every converted model and the standard PW animation after import.</summary>
        public static void VerifyConvertedAssets()
        {
            const string folder = "Assets/FortyTwoAndBeyond/Resources/LegacyShips";
            string[] objects = Directory.GetFiles(folder, "*.obj");
            int missing = 0;
            foreach (string path in objects)
            {
                GameObject model = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (model != null && model.GetComponentsInChildren<MeshFilter>().Length > 0)
                    continue;
                Debug.LogError("Converted model failed to import: " + path);
                missing++;
            }
            foreach (var spec in new[] { (name: "PWArmsAnimated", meshes: 8),
                (name: "JEProfessionAnimated", meshes: 10),
                (name: "JEWingsAnimated", meshes: 8) })
            {
                string fbx = folder + "/" + spec.name + ".fbx";
                GameObject asset = AssetDatabase.LoadAssetAtPath<GameObject>(fbx);
                AnimationClip clip = AssetDatabase.LoadAllAssetsAtPath(fbx)
                    .OfType<AnimationClip>().FirstOrDefault(item => item.name == "Scene");
                Debug.Log("Imported " + spec.name + " meshes=" +
                    (asset != null ? asset.GetComponentsInChildren<MeshFilter>().Length : 0) +
                    " clip=" + (clip != null ? clip.length.ToString() : "missing"));
                if (asset != null && asset.GetComponentsInChildren<MeshFilter>().Length ==
                    spec.meshes && clip != null && clip.length >= .9f) continue;
                Debug.LogError("Rigged ship FBX or its warp clip failed to import: " + fbx);
                missing++;
            }
            if (objects.Length != 70)
            { Debug.LogError("Expected 70 converted OBJ models; found " + objects.Length); missing++; }
            Debug.Log("LEGACY IMPORT " + (missing == 0 ? "PASS" : "FAIL") +
                ": " + objects.Length + " OBJ models and three animated FBXs.");
            EditorApplication.Exit(missing == 0 ? 0 : 1);
        }

        /// <summary>Print mesh and material bindings for representative original ships.</summary>
        public static void Run()
        {
            foreach (string name in new[] { "OriginalPW", "TVF01A", "PVF01A", "JVF01A", "npc01", "PVS02" })
            {
                GameObject asset = Resources.Load<GameObject>("LegacyShips/" + name);
                if (asset == null) { Debug.LogError("Missing legacy ship " + name); continue; }
                Renderer[] renderers = asset.GetComponentsInChildren<Renderer>();
                Debug.Log("LEGACY " + name + " meshes=" + renderers.Length + " materials=" +
                    string.Join(" | ", renderers.SelectMany(renderer => renderer.sharedMaterials)
                        .Where(surface => surface != null).Select(surface => surface.name + ":" +
                            (surface.mainTexture != null ? surface.mainTexture.name : "none"))
                        .Distinct().Take(12)));
                if (name == "OriginalPW")
                    Debug.Log("ORIGINAL PW OBJECTS " + string.Join(" | ",
                        asset.GetComponentsInChildren<MeshFilter>().Select(filter =>
                            filter.name + ":" + filter.sharedMesh?.name)));
            }
            GameObject animated = Resources.Load<GameObject>("LegacyShips/PWArmsAnimated");
            AnimationClip[] clips = Resources.LoadAll<AnimationClip>("LegacyShips/PWArmsAnimated");
            Debug.Log("PW FBX model=" + (animated != null ? animated.name : "missing") +
                " meshes=" + (animated != null ? animated.GetComponentsInChildren<MeshFilter>().Length : 0) +
                " clips=" + string.Join(" | ", clips.Select(clip => clip.name + ":" + clip.length)));
            if (animated != null)
            {
                GameObject instance = Object.Instantiate(animated);
                Renderer[] parts = instance.GetComponentsInChildren<Renderer>();
                Bounds bounds = parts[0].bounds;
                foreach (Renderer part in parts.Skip(1)) bounds.Encapsulate(part.bounds);
                Debug.Log("PW FBX bounds=" + bounds + " root rotation=" +
                    instance.transform.localRotation.eulerAngles + " children=" + string.Join(" | ",
                    instance.GetComponentsInChildren<Transform>().Take(14).Select(part =>
                        part.name + ":" + part.localPosition + ":" + part.localScale)));
                Transform shoulder = instance.GetComponentsInChildren<Transform>()
                    .FirstOrDefault(part => part.name == "L_1");
                Quaternion resting = shoulder.localRotation;
                clips.First(clip => clip.name == "Scene").SampleAnimation(instance, .95f);
                Debug.Log("PW FBX sampled shoulder delta=" +
                    Quaternion.Angle(resting, shoulder.localRotation));
                Debug.Log("PW FBX materials=" + string.Join(" | ", parts.SelectMany(part =>
                    part.sharedMaterials).Where(material => material != null).Select(material =>
                    material.name + ":" + (material.mainTexture != null ?
                    material.mainTexture.name : "none")).Distinct()));
                Object.DestroyImmediate(instance);
            }
            GameObject originalPw = Resources.Load<GameObject>("LegacyShips/OriginalPW");
            if (originalPw != null)
            {
                GameObject instance = Object.Instantiate(originalPw);
                Renderer[] parts = instance.GetComponentsInChildren<Renderer>();
                Bounds bounds = parts[0].bounds;
                foreach (Renderer part in parts.Skip(1)) bounds.Encapsulate(part.bounds);
                Debug.Log("PW OBJ bounds=" + bounds);
                PwWarpArms.Attach(instance);
                instance.transform.localScale = Vector3.one * .03f;
                PwWarpArms controller = instance.GetComponent<PwWarpArms>();
                controller.ConfigureWeaponScale();
                Debug.Log("PW FBX socket=" + controller.WeaponSocket(0).lossyScale +
                    " parent=" + controller.WeaponSocket(0).parent.lossyScale +
                    " local=" + controller.WeaponSocket(0).localScale);
                Object.DestroyImmediate(instance);
            }
            EditorApplication.Exit(0);
        }
    }
}
