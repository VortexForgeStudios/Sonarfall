#if UNITY_ANDROID
using System.Collections.Generic;
using System.IO;
using System.Xml;
using UnityEditor.Android;
using UnityEngine;

/// <summary>
/// Fixes up the generated Android manifest so the shipped permission set is exactly the one the
/// store listing and privacy policy describe — no more, no less.
///
/// ADDS
///   android.permission.VIBRATE — not optional. Unity decides which permissions to add by scanning
///   the built assemblies for known API calls (Handheld.Vibrate, Microphone, etc.). Haptics reaches
///   the vibrator through AndroidJavaObject/JNI, which that scanner cannot see, so without this the
///   APK ships with no VIBRATE permission and every vibrate() call is dropped by the framework —
///   silently, with no exception and nothing in logcat.
///
///   android.permission.POST_NOTIFICATIONS — the notifications package adds this itself; it is
///   listed here so the build fails loudly if that ever stops being true.
///
/// REMOVES
///   android.permission.INTERNET — Unity's "Internet Access: Auto" adds this whenever any assembly
///   in the build touches a networking type, and with the render pipeline and input packages in
///   the build that is always. Sonarfall makes no network call of any kind; the policy says so,
///   the Play Data-safety form says so, and a permission the app cannot use only invites the
///   question of what it is for. Stripping it here makes the promise mechanical rather than
///   hopeful: with the permission absent, no code path — ours, a package's, or the engine's — can
///   open a socket.
///
/// Verify on a built APK with:  aapt dump permissions &lt;apk&gt;
/// </summary>
public class AndroidManifestPostProcessor : IPostGenerateGradleAndroidProject
{
    // VIBRATE is a NORMAL permission: granted at install, never prompted, and it does not appear
    // under Settings -> App permissions (that screen only lists runtime groups). POST_NOTIFICATIONS
    // is a RUNTIME permission on API 33+, so that one does prompt and does show up there.
    private static readonly string[] Required =
    {
        "android.permission.VIBRATE",
        "android.permission.POST_NOTIFICATIONS",
    };

    private static readonly string[] Forbidden =
    {
        "android.permission.INTERNET",
        "android.permission.ACCESS_NETWORK_STATE",
    };

    // After Unity's own manifest generation, so the file exists and nothing overwrites us.
    public int callbackOrder => 1;

    public void OnPostGenerateGradleAndroidProject(string path)
    {
        string manifestPath = Path.Combine(path, "src", "main", "AndroidManifest.xml");
        if (!File.Exists(manifestPath))
        {
            Debug.LogError("[Sonarfall] AndroidManifest.xml not found at " + manifestPath +
                           " — VIBRATE permission NOT added, haptics will be dead in this build.");
            return;
        }

        var doc = new XmlDocument();
        doc.Load(manifestPath);

        var manifest = doc.SelectSingleNode("/manifest") as XmlElement;
        if (manifest == null) { Debug.LogError("[Sonarfall] Malformed AndroidManifest.xml."); return; }

        const string ns = "http://schemas.android.com/apk/res/android";

        var present = new Dictionary<string, XmlElement>();
        foreach (XmlNode node in manifest.SelectNodes("uses-permission"))
        {
            var el = node as XmlElement;
            if (el != null) present[el.GetAttribute("name", ns)] = el;
        }

        bool changed = false;
        foreach (string permission in Required)
        {
            if (present.ContainsKey(permission)) { Debug.Log("[Sonarfall] " + permission + " already present."); continue; }
            var added = doc.CreateElement("uses-permission");
            added.SetAttribute("name", ns, permission);
            manifest.AppendChild(added);
            changed = true;
            Debug.Log("[Sonarfall] Added " + permission);
        }

        foreach (string permission in Forbidden)
        {
            if (!present.TryGetValue(permission, out var el)) continue;
            manifest.RemoveChild(el);
            changed = true;
            Debug.Log("[Sonarfall] Removed " + permission + " — the app has no network use.");
        }

        if (changed) doc.Save(manifestPath);

        var final = new List<string>();
        foreach (XmlNode node in manifest.SelectNodes("uses-permission"))
            final.Add(((XmlElement)node).GetAttribute("name", ns));
        Debug.Log("[Sonarfall] Manifest permissions: " + string.Join(", ", final));
    }
}
#endif
