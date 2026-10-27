using System;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace FortyTwoAndBeyond.Editor
{
    /// <summary>Build the standalone Windows x64 client from the shared Unity scene.</summary>
    public static class BuildWindowsClient
    {
        /// <summary>Write a Windows x64 player and fail batch mode on a build error.</summary>
        [MenuItem("42 and Beyond/Build Windows x64 client")]
        public static void BuildWindows()
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
                locationPathName = "Builds/Windows/42 and Beyond.exe",
                target = BuildTarget.StandaloneWindows64,
                options = BuildOptions.None,
            });
            if (report.summary.result != BuildResult.Succeeded)
                throw new InvalidOperationException("42 and Beyond Windows x64 build failed: " +
                    report.summary.result);
            Debug.Log("42 and Beyond Windows x64 build succeeded: " + report.summary.outputPath);
        }
    }
}
