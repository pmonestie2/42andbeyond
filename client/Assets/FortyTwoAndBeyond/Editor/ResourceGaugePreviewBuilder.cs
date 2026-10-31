using System;
using System.IO;
using System.Linq;
using FortyTwoAndBeyond.Client;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace FortyTwoAndBeyond.Editor
{
    /// <summary>Build and capture a new isolated Unity scene for reviewing the reference-inspired gauges.</summary>
    [InitializeOnLoad]
    public static class ResourceGaugePreviewBuilder
    {
        private const string Pending="42Beyond.ResourceGaugePreview";
        private static UiViewportScope viewport;
        private static double after;
        private static int stage,frame;
        private static string Output=>Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop),"gauges");

        /// <summary>Resume the explicitly requested capture after the preview's Play-mode reload.</summary>
        static ResourceGaugePreviewBuilder(){EditorApplication.update+=Tick;}

        /// <summary>Create only the new preview scene and import its transparent metal housing correctly.</summary>
        [MenuItem("42 and Beyond/Previews/Create resource gauges")]
        public static void Create()
        {
            if(!Application.isBatchMode)throw new InvalidOperationException("Use batch mode to create this isolated preview without replacing an open scene.");
            string texture="Assets/FortyTwoAndBeyond/Resources/UI/ResourceDials/ArmoredBezel.png";
            var importer=(TextureImporter)AssetImporter.GetAtPath(texture);
            importer.textureType=TextureImporterType.Default;importer.alphaIsTransparency=true;
            importer.mipmapEnabled=true;importer.maxTextureSize=2048;
            importer.textureCompression=TextureImporterCompression.Uncompressed;importer.SaveAndReimport();
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);
            var camera=new GameObject("Preview camera",typeof(Camera)).GetComponent<Camera>();
            camera.clearFlags=CameraClearFlags.SolidColor;camera.backgroundColor=new Color(.016f,.025f,.037f);camera.cullingMask=0;
            new GameObject("Resource gauge design preview",typeof(ResourceGaugePreview));
            Directory.CreateDirectory("Assets/FortyTwoAndBeyond/Previews");
            EditorSceneManager.SaveScene(EditorSceneManager.GetActiveScene(),"Assets/FortyTwoAndBeyond/Previews/ResourceGauges.unity");
            Directory.CreateDirectory(Output);SessionState.SetBool(Pending,true);EditorApplication.EnterPlaymode();
        }

        /// <summary>Save actual rendered animation states and verify shader support without starting a game session.</summary>
        private static void Tick()
        {
            if(!SessionState.GetBool(Pending,false)||!EditorApplication.isPlaying)return;
            try
            {
                var preview=UnityEngine.Object.FindFirstObjectByType<ResourceGaugePreview>();
                if(preview==null)return;
                if(stage==0)
                {
                    viewport=new UiViewportScope();viewport.Set(1920,1480);preview.previewSeconds=1;
                    stage=1;after=EditorApplication.timeSinceStartup+1;return;
                }
                if(EditorApplication.timeSinceStartup<after)return;
                Shader shader=Resources.Load<Shader>("UI/ResourceDials/Instrument");
                if(!shader.isSupported||ShaderUtil.GetShaderMessages(shader).Any(message=>message.severity==UnityEditor.Rendering.ShaderCompilerMessageSeverity.Error))
                    throw new InvalidOperationException("Resource dial GPU shader did not compile.");
                if(stage==1)
                {
                    ScreenCapture.CaptureScreenshot(Path.Combine(Output,"unity-gauges-preview.png"));
                    stage=11;after=EditorApplication.timeSinceStartup+.3;return;
                }
                if(stage==11)
                {
                    preview.previewSeconds=3.5f;stage=2;after=EditorApplication.timeSinceStartup+.5;return;
                }
                if(stage==2)
                {
                    ScreenCapture.CaptureScreenshot(Path.Combine(Output,"unity-gauges-animation.png"));
                    stage=3;after=EditorApplication.timeSinceStartup+.5;return;
                }
                if(stage==3)
                {
                    Directory.CreateDirectory("Builds/verification/resource-gauge-frames");
                    frame=0;preview.previewSeconds=1;stage=20;after=EditorApplication.timeSinceStartup+.1;return;
                }
                if(stage==20)
                {
                    ScreenCapture.CaptureScreenshot("Builds/verification/resource-gauge-frames/"+frame.ToString("D3")+".png");
                    stage=21;after=EditorApplication.timeSinceStartup+.1;return;
                }
                if(stage==21 && ++frame<36)
                {preview.previewSeconds=1+frame/12f;stage=20;after=EditorApplication.timeSinceStartup+.1;return;}
                File.WriteAllText(Path.Combine(Output,"README.txt"),"Actual Unity-rendered resource gauge preview.\nOpen client/Assets/FortyTwoAndBeyond/Previews/ResourceGauges.unity and press Play.\nSliders adjust percentages; toggle animates discharge/recovery.\nMetal housing generated with built-in image_gen from your gauges.png reference.\nUnity draws animated cores, live labels and proportional segments.\nThe installed game HUD has not been replaced.\n");
                Complete(0,"PASS: real Unity resource dials, transparent housing, compiled animated shader; preview saved to "+Output);
            }
            catch(Exception error){Complete(1,error.ToString());}
        }

        /// <summary>Remove owned Editor presets and finish the isolated preview process.</summary>
        private static void Complete(int code,string message)
        {
            SessionState.SetBool(Pending,false);viewport?.Dispose();viewport=null;
            if(code==0)Debug.Log(message);else Debug.LogError(message);
            EditorApplication.Exit(code);
        }
    }
}
