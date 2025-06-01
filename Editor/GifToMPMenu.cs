using System;
using System.IO;
using System.Net;
using UnityEditor;
using UnityEngine;

// Place this script in an "Editor" folder (e.g. Assets/Editor/GifToMPMenu.cs)
// so Unity knows it belongs to the Editor rather than runtime builds.

public static class GifToMPMenu
{
    private const string ReactToUltEventsKey = "GifToMP_ReactsToUltEvents";
    private const string EnableLoggingKey = "GifToMP_EnableLogging";
    private const string DefaultFrameDelayKey = "GifToMP_DefaultFrameDelay";
    private const string DefaultLoopForeverKey = "GifToMP_DefaultLoopForever";

    private const string CurrentVersion = "1.0.1";
    private const string LatestReleaseApiUrl = "https://api.github.com/repos/the-bl-heretic/gif-to-mp/releases/latest";

    // — GifToMP / Settings
    [MenuItem("GifToMP/Settings")]
    private static void OpenSettingsWindow()
    {
        GifToMPSettingsWindow.ShowWindow();
    }

    // Validate shows checkmark next to Settings if ReactToUltEvents is true
    [MenuItem("GifToMP/Settings", validate = true)]
    private static bool ToggleSettingsValidate()
    {
        bool reacts = EditorPrefs.GetBool(ReactToUltEventsKey, false);
        Menu.SetChecked("GifToMP/Settings", reacts);
        return true;
    }

    // — GifToMP / Check For Updates
    [MenuItem("GifToMP/Check For Updates")]
    private static void CheckForUpdates()
    {
        try
        {
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(LatestReleaseApiUrl);
            request.UserAgent = "GifToMP-UnityEditor";

            using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
            using (StreamReader reader = new StreamReader(response.GetResponseStream()))
            {
                string json = reader.ReadToEnd();
                ReleaseInfo info = JsonUtility.FromJson<ReleaseInfo>(json);

                if (string.IsNullOrEmpty(info.tag_name))
                {
                    EditorUtility.DisplayDialog(
                        "GifToMP – Check For Updates",
                        "Could not parse version information from GitHub response.",
                        "OK"
                    );
                    return;
                }

                string latestVersion = info.tag_name.StartsWith("v", StringComparison.OrdinalIgnoreCase)
                    ? info.tag_name.Substring(1)
                    : info.tag_name;

                int comparison = CompareVersions(latestVersion, CurrentVersion);
                if (comparison > 0)
                {
                    bool openRepo = EditorUtility.DisplayDialog(
                        "GifToMP – Update Available",
                        $"A new version of GifToMP is available:\n\n" +
                        $"Latest: {latestVersion}\n" +
                        $"Installed: {CurrentVersion}\n\n" +
                        $"Open GitHub to download?",
                        "Open GitHub",
                        "Later"
                    );
                    if (openRepo)
                        UnityEngine.Application.OpenURL("https://github.com/the-bl-heretic/gif-to-mp/releases/latest");
                }
                else if (comparison < 0)
                {
                    EditorUtility.DisplayDialog(
                        "GifToMP – Version Check",
                        $"You are running a newer version ({CurrentVersion}) than GitHub’s latest release ({latestVersion}).",
                        "OK"
                    );
                }
                else
                {
                    EditorUtility.DisplayDialog(
                        "GifToMP – Up To Date",
                        $"You are already on the latest version ({CurrentVersion}).",
                        "OK"
                    );
                }
            }
        }
        catch (WebException webEx)
        {
            string message = webEx.Message;
            if (webEx.Response is HttpWebResponse httpResponse)
                message = $"HTTP {(int)httpResponse.StatusCode} – {httpResponse.StatusDescription}";

            EditorUtility.DisplayDialog(
                "GifToMP – Check Failed",
                $"Could not check for updates:\n\n{message}",
                "OK"
            );
        }
        catch (Exception ex)
        {
            EditorUtility.DisplayDialog(
                "GifToMP – Error",
                $"Unexpected error:\n\n{ex.Message}",
                "OK"
            );
        }
    }

    // — GifToMP / Documentation
    [MenuItem("GifToMP/Documentation")]
    private static void OpenDocumentation()
    {
        UnityEngine.Application.OpenURL("https://github.com/the-bl-heretic/gif-to-mp#readme");
    }

    // — GifToMP / About GifToMP
    [MenuItem("GifToMP/About GifToMP")]
    private static void ShowAbout()
    {
        EditorUtility.DisplayDialog(
            "About GifToMP",
            $"GifToMP Version: {CurrentVersion}\n\n" +
            "GifToMP is a Unity tool for playing GIFs frame-by-frame\n\n" +
            "GitHub: https://github.com/the-bl-heretic/gif-to-mp",
            "Cool :)"
        );
    }

    private static int CompareVersions(string a, string b)
    {
        string[] pa = a.Split('.');
        string[] pb = b.Split('.');
        int len = Mathf.Max(pa.Length, pb.Length);
        for (int i = 0; i < len; i++)
        {
            int va = (i < pa.Length && int.TryParse(pa[i], out int ava)) ? ava : 0;
            int vb = (i < pb.Length && int.TryParse(pb[i], out int avb)) ? avb : 0;
            if (va != vb)
                return va.CompareTo(vb);
        }
        return 0;
    }

    [Serializable]
    private class ReleaseInfo
    {
        public string tag_name;
    }
}


public class GifToMPSettingsWindow : EditorWindow
{
    private const string ReactToUltEventsKey = "GifToMP_ReactsToUltEvents";
    private const string EnableLoggingKey = "GifToMP_EnableLogging";
    private const string DefaultFrameDelayKey = "GifToMP_DefaultFrameDelay";
    private const string DefaultLoopForeverKey = "GifToMP_DefaultLoopForever";

    private bool _reactsToUltEvents;
    private bool _enableLogging;
    private float _defaultFrameDelay;
    private bool _defaultLoopForever;

    private const float kPadding = 8f;
    private const float kLineHeight = 18f;

    public static void ShowWindow()
    {
        GifToMPSettingsWindow window = GetWindow<GifToMPSettingsWindow>(false, "GifToMP Settings", true);
        window.minSize = new Vector2(320, 200);
        window.LoadPrefs();
        window.Show();
    }

    private void OnEnable() => LoadPrefs();

    private void LoadPrefs()
    {
        _reactsToUltEvents = EditorPrefs.GetBool(ReactToUltEventsKey, false);
        _enableLogging = EditorPrefs.GetBool(EnableLoggingKey, false);
        _defaultFrameDelay = EditorPrefs.GetFloat(DefaultFrameDelayKey, 0.02f);
        _defaultLoopForever = EditorPrefs.GetBool(DefaultLoopForeverKey, false);
    }

    private void SavePrefs()
    {
        EditorPrefs.SetBool(ReactToUltEventsKey, _reactsToUltEvents);
        EditorPrefs.SetBool(EnableLoggingKey, _enableLogging);
        EditorPrefs.SetFloat(DefaultFrameDelayKey, _defaultFrameDelay);
        EditorPrefs.SetBool(DefaultLoopForeverKey, _defaultLoopForever);
    }

    private void OnGUI()
    {
        GUILayout.Space(kPadding);
        EditorGUILayout.LabelField("GifToMP General Settings", EditorStyles.boldLabel);
        GUILayout.Space(kPadding);

        EditorGUI.BeginChangeCheck();
        _reactsToUltEvents = EditorGUILayout.ToggleLeft(
            new GUIContent(
                "React to UltEvents",
                "If enabled, GifPlayerTrigger will automatically subscribe to UltEvents in your scene."
            ),
            _reactsToUltEvents
        );
        if (EditorGUI.EndChangeCheck()) SavePrefs();

        GUILayout.Space(kPadding);

        EditorGUI.BeginChangeCheck();
        _enableLogging = EditorGUILayout.ToggleLeft(
            new GUIContent(
                "Enable Logging",
                "If enabled, GifPlayerTrigger will print debug logs about frame decoding and errors."
            ),
            _enableLogging
        );
        if (EditorGUI.EndChangeCheck()) SavePrefs();

        GUILayout.Space(kPadding);

        EditorGUI.BeginChangeCheck();
        _defaultFrameDelay = EditorGUILayout.FloatField(
            new GUIContent(
                "Default Frame Delay (s)",
                "The default minimum frame delay in seconds when playing GIFs."
            ),
            _defaultFrameDelay
        );
        if (_defaultFrameDelay < 0f) _defaultFrameDelay = 0f;
        if (EditorGUI.EndChangeCheck()) SavePrefs();

        GUILayout.Space(kPadding);

        EditorGUI.BeginChangeCheck();
        _defaultLoopForever = EditorGUILayout.ToggleLeft(
            new GUIContent(
                "Default Loop Forever",
                "If enabled, new GifPlayerTrigger instances will loop indefinitely by default."
            ),
            _defaultLoopForever
        );
        if (EditorGUI.EndChangeCheck()) SavePrefs();

        GUILayout.Space(kPadding);
        EditorGUILayout.HelpBox(
            "Settings:\n" +
            "- React to UltEvents: Auto-subscribe GifPlayerTrigger to UltEvents.\n" +
            "- Enable Logging: Log decoding steps to Console.\n" +
            $"- Default Frame Delay: {_defaultFrameDelay:0.000} seconds.\n" +
            $"- Default Loop Forever: {(_defaultLoopForever ? "Yes" : "No")}.",
            MessageType.Info
        );
    }
}
