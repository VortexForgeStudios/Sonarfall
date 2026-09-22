#if UNITY_ANDROID
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

/// <summary>
/// Builds an unsigned-for-store APK into Builds/Verify and writes a plain-text report beside it:
/// result, size, the largest packed assets and the output files.
///
/// This exists because the two things that actually broke past releases — an INTERNET permission
/// nobody asked for and a 62 MB libil2cpp.so full of unused packages — are invisible in the editor
/// and only show up in the built artifact. Run this, then inspect the APK:
///
///   unzip -p Sonarfall-verify.apk AndroidManifest.xml | strings | grep permission
///   unzip -p Sonarfall-verify.apk assets/bin/Data/ScriptingAssemblies.json
///
/// It signs with the debug keystore so it works in a fresh editor session where the release
/// keystore password has not been entered. Never upload this APK; it is a probe, not a release.
/// </summary>
public static class VerificationBuild
{
    private const string Dir = "Builds/Verify";

    [MenuItem("Sonarfall/Build Verification APK")]
    public static void Build()
    {
        Directory.CreateDirectory(Dir);
        string reportPath = Path.Combine(Dir, "build-report.txt");
        string apk = Path.Combine(Dir, "Sonarfall-verify.apk");

        bool prevBundle = EditorUserBuildSettings.buildAppBundle;
        bool prevKeystore = PlayerSettings.Android.useCustomKeystore;
        try
        {
            File.WriteAllText(reportPath, "BUILD STARTED " + System.DateTime.Now + "\n");
            EditorUserBuildSettings.buildAppBundle = false;
            PlayerSettings.Android.useCustomKeystore = false;

            var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = EditorBuildSettings.scenes.Where(s => s.enabled).Select(s => s.path).ToArray(),
                locationPathName = apk,
                target = BuildTarget.Android,
                options = BuildOptions.None,
            });

            var sb = new System.Text.StringBuilder();
            sb.AppendFormat("RESULT {0}  size {1} bytes  time {2}\n", report.summary.result, report.summary.totalSize, report.summary.totalTime);
            sb.AppendFormat("errors {0} warnings {1}\n", report.summary.totalErrors, report.summary.totalWarnings);
            foreach (var step in report.steps)
                foreach (var m in step.messages)
                    if (m.type == LogType.Error || m.type == LogType.Exception || m.type == LogType.Assert)
                        sb.AppendFormat("ERR [{0}] {1}\n", step.name, m.content);

            sb.Append("TOP PACKED ASSETS:\n");
            foreach (var c in report.packedAssets.SelectMany(p => p.contents).OrderByDescending(c => c.packedSize).Take(25))
                sb.AppendFormat("  {0,10} {1} ({2})\n", c.packedSize, c.sourceAssetPath, c.type.Name);

            sb.Append("OUTPUT FILES:\n");
            foreach (var f in report.GetFiles().OrderByDescending(f => f.size).Take(12))
                sb.AppendFormat("  {0,10} {1} {2}\n", f.size, f.role, f.path);

            sb.Append("BUILD FINISHED " + System.DateTime.Now + "\n");
            File.WriteAllText(reportPath, sb.ToString());
        }
        catch (System.Exception e)
        {
            File.WriteAllText(reportPath, "BUILD EXCEPTION " + e + "\nBUILD FINISHED\n");
            throw;
        }
        finally
        {
            // Never leave the project in APK / debug-keystore mode: the store upload is an AAB
            // signed with the release key.
            EditorUserBuildSettings.buildAppBundle = prevBundle;
            PlayerSettings.Android.useCustomKeystore = prevKeystore;
            AssetDatabase.SaveAssets();
        }
    }
}
#endif
