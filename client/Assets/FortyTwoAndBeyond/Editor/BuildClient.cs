using System;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace FortyTwoAndBeyond.Editor
{
    public static class BuildClient
    {
        /// <summary>Build a native Mac client and fail the batch process if compilation fails.</summary>
        [MenuItem("42 and Beyond/Build macOS client")]
        public static void BuildMac()
        {
            PlayerSettings.companyName = "42 and Beyond";
            PlayerSettings.productName = "42 and Beyond";
            PlayerSettings.defaultScreenWidth = 1440;
            PlayerSettings.defaultScreenHeight = 900;
            PlayerSettings.fullScreenMode = FullScreenMode.Windowed;
            PlayerSettings.resizableWindow = true;
            PlayerSettings.runInBackground = true;
            BuildReport report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = new[] { "Assets/FortyTwoAndBeyond/Space.unity" },
                locationPathName = "Builds/42 and Beyond.app",
                target = BuildTarget.StandaloneOSX,
                options = BuildOptions.None,
            });
            if (report.summary.result != BuildResult.Succeeded)
                throw new InvalidOperationException("42 and Beyond macOS build failed: " + report.summary.result);
            Debug.Log("42 and Beyond macOS build succeeded: " + report.summary.outputPath);
        }
    }
}
