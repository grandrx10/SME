using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace SuddenMageEncounter.EditorTools
{
    /// <summary>
    /// Windows player build for <c>Tools/release.ps1</c> (runs in batch mode on a copy of the project):
    /// <c>-executeMethod SuddenMageEncounter.EditorTools.ReleaseBuild.Build -smeVersion 3 -smeOut C:\path</c>.
    /// </summary>
    public static class ReleaseBuild
    {
        public const string ExeName = "SuddenMageEncounter.exe";

        public static void Build()
        {
            string version = Arg("-smeVersion") ?? "dev";
            string outDir = Arg("-smeOut") ?? Path.GetFullPath("Builds/Release");

            File.WriteAllText("Assets/Resources/GameVersion.txt", version);
            AssetDatabase.ImportAsset("Assets/Resources/GameVersion.txt", ImportAssetOptions.ForceSynchronousImport);
            PlayerSettings.bundleVersion = version;

            if (Directory.Exists(outDir)) Directory.Delete(outDir, true);
            var options = new BuildPlayerOptions
            {
                scenes = EditorBuildSettings.scenes.Where(s => s.enabled).Select(s => s.path).ToArray(),
                locationPathName = Path.Combine(outDir, ExeName),
                target = BuildTarget.StandaloneWindows64,
                options = BuildOptions.None,
            };
            BuildReport report = BuildPipeline.BuildPlayer(options);
            Debug.Log("SME release build: " + report.summary.result + ", " + report.summary.totalSize / (1024 * 1024) + " MB");
            if (Application.isBatchMode) EditorApplication.Exit(report.summary.result == BuildResult.Succeeded ? 0 : 1);
        }

        static string Arg(string name)
        {
            var args = Environment.GetCommandLineArgs();
            int i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }
    }
}
