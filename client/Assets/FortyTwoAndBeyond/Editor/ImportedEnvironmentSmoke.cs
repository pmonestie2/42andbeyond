using System;
using System.IO;
using System.Linq;
using FortyTwoAndBeyond.Client;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Object = UnityEngine.Object;

namespace FortyTwoAndBeyond.Editor
{
    /// <summary>Render the imported game scenery in an owned Play-mode scene without any server or account.</summary>
    [InitializeOnLoad]
    public static class ImportedEnvironmentSmoke
    {
        private const string Pending = "42andBeyond.ImportedEnvironmentSmoke";
        private static SpaceArt art;
        private static DynamicSpacePreview preview;
        private static SpaceEnvironmentBackground environment;
        private static GameObject owner;
        private static Vector3 moonPosition;
        private static Quaternion earthRotation;
        private static double started;
        private static int stage;

        /// <summary>Resume only the explicitly requested batch check after the Play-mode reload.</summary>
        static ImportedEnvironmentSmoke() { EditorApplication.update += Tick; }

        /// <summary>Create a disposable scene; the game and user clients are never launched or contacted.</summary>
        public static void Run()
        {
            if (!Application.isBatchMode) throw new InvalidOperationException("Run this isolated check with -batchmode.");
            Directory.CreateDirectory("Builds/verification");
            var catalog=(TextureImporter)AssetImporter.GetAtPath("Assets/FortyTwoAndBeyond/Resources/SpaceEnvironment/Textures/NASADeepStars2020.exr");
            catalog.textureType=TextureImporterType.Default;catalog.textureShape=TextureImporterShape.Texture2D;
            catalog.sRGBTexture=false;catalog.mipmapEnabled=true;catalog.maxTextureSize=4096;
            catalog.wrapModeU=TextureWrapMode.Repeat;catalog.wrapModeV=TextureWrapMode.Clamp;
            catalog.filterMode=FilterMode.Trilinear;catalog.textureCompression=TextureImporterCompression.CompressedHQ;
            catalog.SaveAndReimport();
            foreach(string file in new[]{"4k_mercury","4k_mars","4k_venus_surface","4k_makemake_fictional","4k_eris_fictional","2k_uranus","2k_neptune"})
            {
                var map=(TextureImporter)AssetImporter.GetAtPath("Assets/FortyTwoAndBeyond/Resources/SpaceEnvironment/Textures/"+file+".jpg");
                map.textureType=TextureImporterType.Default;map.sRGBTexture=true;map.mipmapEnabled=true;map.maxTextureSize=4096;
                map.wrapModeU=TextureWrapMode.Repeat;map.wrapModeV=TextureWrapMode.Clamp;map.textureCompression=TextureImporterCompression.CompressedHQ;map.SaveAndReimport();
            }
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            SessionState.SetBool(Pending, true); EditorApplication.EnterPlaymode();
        }

        /// <summary>Verify assets, real orbit/spin motion, shader compilation and rendered animation.</summary>
        private static void Tick()
        {
            if (!SessionState.GetBool(Pending, false) || !EditorApplication.isPlaying) return;
            try
            {
                if (stage == 0)
                {
                    art = new SpaceArt(); owner = new GameObject("Owned scenery test");
                    environment = art.Environment(owner.transform).GetComponent<SpaceEnvironmentBackground>();
                    if (owner.GetComponentsInChildren<Collider>().Length != 0 || owner.GetComponentsInChildren<Camera>().Length != 0)
                        throw new InvalidOperationException("Decorative scenery contains gameplay colliders or a camera.");
                    foreach (string path in new[] { "Planets/Earth/Clouds", "Planets/Earth/Moon", "Planets/Jupiter", "Planets/Saturn/Rings", "BlackHole/AccretionDisk", "AsteroidField" })
                        if (environment.transform.Find(path) == null) throw new InvalidOperationException("Missing imported background: " + path);
                    moonPosition = environment.transform.Find("Planets/Earth/Moon").position;
                    earthRotation = environment.transform.Find("Planets/Earth/Surface").rotation;
                    RenderSettings.skybox = art.Sky;
                    preview = new DynamicSpacePreview(art); preview.Capture(true);
                    started = EditorApplication.timeSinceStartup; stage = 1; return;
                }
                if (EditorApplication.timeSinceStartup - started < 3) return;
                preview.Capture(false); preview.ValidateOpacity(); preview.ValidateSectors();
                if (Vector3.Distance(moonPosition, environment.transform.Find("Planets/Earth/Moon").position) < 1 ||
                    Quaternion.Angle(earthRotation, environment.transform.Find("Planets/Earth/Surface").rotation) < .1f)
                    throw new InvalidOperationException("The actual background orbit or spin did not advance.");
                foreach (Shader shader in Resources.LoadAll<Shader>("SpaceEnvironment/Shaders").Append(Resources.Load<Shader>("SpaceBloom")).Append(Resources.Load<Shader>("WormholePlasma")))
                {
                    if (shader == null || !shader.isSupported) throw new InvalidOperationException("Unsupported imported shader.");
                    var errors = ShaderUtil.GetShaderMessages(shader).Where(message => message.severity == UnityEditor.Rendering.ShaderCompilerMessageSeverity.Error).ToArray();
                    if (errors.Length != 0) throw new InvalidOperationException(shader.name + ": " + string.Join("; ", errors.Select(error => error.message)));
                }
                Complete(0, "PASS: imported planets, clouds, Moon, Saturn rings, original black-hole prefab and animated disk; GPU shader compilation, visible shader animation, actual orbit/spin, HDR/Bloom, filtered high-resolution wormhole animation, opaque Earth/Saturn/Sun interior regression; no background cameras or colliders. No server or player state used.");
            }
            catch (Exception error) { Complete(1, "FAIL: " + error); }
        }

        /// <summary>Release the owned scene objects and finish the isolated Unity process.</summary>
        private static void Complete(int code, string report)
        {
            SessionState.SetBool(Pending, false);
            File.WriteAllText("Builds/verification/environment-smoke.txt", report + "\n");
            Debug.Log(report); preview?.Dispose(); art?.Dispose(); if (owner != null) Object.Destroy(owner);
            EditorApplication.Exit(code);
        }
    }
}
