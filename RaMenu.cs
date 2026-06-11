using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace RedAllianceSpeedrun
{
    // In-game IMGUI menu for run-mode selection, level launching, and config editing.
    // Toggle with the hotkey defined in Hotkeys.ToggleMenuKey (default F10).
    //
    // Ported from the v1.4 plugin. v1.3 differences: no Speedrun% mode (the mode patches
    // are v1.4-only and deliberately not ported), and SceneManagerScript.LoadLevel is an
    // IEnumerator here so launches go through StartCoroutine.
    //
    // Built as an OnGUI-driven window. Editable ConfigEntries keep their own text-input
    // buffer until Apply is pressed (or Enter on field) — so partial edits don't break
    // values mid-typing.
    internal static class RaMenu
    {
        private static bool _visible;
        private static Rect _windowRect = new Rect(80, 80, 540, 600);
        private static Vector2 _scroll = Vector2.zero;
        private static readonly Dictionary<ConfigDefinition, string> _editBuf =
            new Dictionary<ConfigDefinition, string>();
        private static string _customLevelInput = "";
        private static string _filter = "";
        private static string _levelFilter = "";
        private static Vector2 _levelScroll;
        private static GUIStyle _hdrStyle, _btnStyle, _lblStyle, _fldStyle;
        private static bool _stylesInitialized;

        // Populated lazily from SceneManager build settings.
        private static string[] _allLevels;

        public static bool Visible => _visible;

        public static void Toggle() => _visible = !_visible;

        public static void Draw(int windowId)
        {
            EnsureStyles();
            // No GUI.Window/GUILayout.Window: their deferred window callbacks proved
            // unreliable on this Unity 2017.4 build (GUI.Window rendered an empty frame,
            // GUILayout.Window nothing at all). A plain BeginArea with a box background
            // draws in the immediate OnGUI pass and just works. Fixed position, no drag.
            // Height tracks the screen so the config section gets all remaining space.
            _windowRect = new Rect(80, 40, 560, Mathf.Max(400, Screen.height - 80));
            GUI.Box(_windowRect, GUIContent.none);
            GUILayout.BeginArea(_windowRect);
            GUILayout.BeginVertical(GUI.skin.box, GUILayout.ExpandHeight(true),
                GUILayout.Height(_windowRect.height));
            GUILayout.Label("Red Alliance Speedrun — Menu", _hdrStyle);
            DrawWindow(windowId);
            GUILayout.EndVertical();
            GUILayout.EndArea();
        }

        private static void EnsureStyles()
        {
            if (_stylesInitialized) return;
            _hdrStyle = new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold, fontSize = 14 };
            _btnStyle = new GUIStyle(GUI.skin.button) { fontSize = 12 };
            _lblStyle = new GUIStyle(GUI.skin.label) { fontSize = 12 };
            _fldStyle = new GUIStyle(GUI.skin.textField) { fontSize = 12 };
            _stylesInitialized = true;
        }

        private static void DrawWindow(int id)
        {
            GUILayout.BeginVertical();

            DrawModeSection();
            GUILayout.Space(6);
            DrawLevelLauncherSection();
            GUILayout.Space(6);
            DrawConfigSection();

            GUILayout.EndVertical();
        }

        private static void DrawModeSection()
        {
            GUILayout.Label("Mode", _hdrStyle);
            GUILayout.BeginHorizontal();

            bool isPractice = Plugin.PracticeMode;
            bool isNormal = !isPractice;

            if (GUILayout.Toggle(isNormal, "Normal", _btnStyle) && !isNormal)
                SetPracticeMode(false);
            if (GUILayout.Toggle(isPractice, "Practice", _btnStyle) && !isPractice)
                SetPracticeMode(true);

            GUILayout.EndHorizontal();
            GUILayout.Label(
                isPractice ? "Practice: cheats allowed (desu), normal levels" :
                             "Normal: cheats forced off, dump on run end",
                _lblStyle);
        }

        private static void SetPracticeMode(bool practice)
        {
            SetConfigBool("Restart", "PracticeMode", practice);
            Plugin.Instance?.SyncStatics();
            Plugin.ConfigRef?.Save();
        }

        private static void SetConfigBool(string section, string key, bool value)
        {
            if (Plugin.ConfigRef == null) return;
            foreach (var def in Plugin.ConfigRef.Keys)
            {
                if (def.Section == section && def.Key == key)
                {
                    Plugin.ConfigRef[def].BoxedValue = value;
                    return;
                }
            }
        }

        private static void EnsureLevelList()
        {
            if (_allLevels != null) return;
            var list = new List<string>();
            int n = SceneManager.sceneCountInBuildSettings;
            for (int i = 0; i < n; i++)
            {
                try
                {
                    string path = UnityEngine.SceneManagement.SceneUtility.GetScenePathByBuildIndex(i);
                    if (string.IsNullOrEmpty(path)) continue;
                    string name = System.IO.Path.GetFileNameWithoutExtension(path);
                    if (!string.IsNullOrEmpty(name)) list.Add(name);
                }
                catch { /* skip */ }
            }
            _allLevels = list.ToArray();
            Plugin.Logger.LogInfo($"[menu] loaded {_allLevels.Length} scene names from build settings");
        }

        private static void DrawLevelLauncherSection()
        {
            GUILayout.Label("Quick Level Launch", _hdrStyle);
            EnsureLevelList();

            GUILayout.BeginHorizontal();
            GUILayout.Label("Filter:", _lblStyle, GUILayout.Width(60));
            _levelFilter = GUILayout.TextField(_levelFilter, _fldStyle);
            GUILayout.EndHorizontal();

            _levelScroll = GUILayout.BeginScrollView(_levelScroll, GUILayout.MinHeight(120), GUILayout.MaxHeight(180));
            string lf = _levelFilter?.ToLowerInvariant();
            int colCount = 0;
            GUILayout.BeginHorizontal();
            foreach (var name in _allLevels)
            {
                if (!string.IsNullOrEmpty(lf) && !name.ToLowerInvariant().Contains(lf)) continue;
                if (colCount > 0 && colCount % 3 == 0)
                {
                    GUILayout.EndHorizontal();
                    GUILayout.BeginHorizontal();
                }
                if (GUILayout.Button(name, _btnStyle, GUILayout.MinWidth(140)))
                    LaunchLevel(name);
                colCount++;
            }
            GUILayout.EndHorizontal();
            GUILayout.EndScrollView();

            GUILayout.BeginHorizontal();
            GUILayout.Label("Custom:", _lblStyle, GUILayout.Width(60));
            _customLevelInput = GUILayout.TextField(_customLevelInput, _fldStyle);
            if (GUILayout.Button("Load", _btnStyle, GUILayout.Width(60)) && !string.IsNullOrEmpty(_customLevelInput))
                LaunchLevel(_customLevelInput);
            GUILayout.EndHorizontal();
        }

        private static void LaunchLevel(string sceneName)
        {
            try
            {
                var smgr = SceneManagerScript.Instance;
                if ((object)smgr == null)
                {
                    Plugin.Logger.LogWarning("[menu] SceneManagerScript.Instance null");
                    return;
                }
                if (SceneManagerScript.LoadingLevel)
                {
                    Plugin.Logger.LogInfo("[menu] launch ignored: already loading");
                    return;
                }
                var loadType = (sceneName == "main_menu" || sceneName == "credits")
                    ? LevelLoadingType.Reset
                    : LevelLoadingType.Transfer;
                try { NetworkManager.OnStartedNewGame(); }
                catch { /* may not be required for arbitrary loads */ }
                Plugin.Instance?.PrepareTimersForLaunch(sceneName);
                smgr.StartCoroutine(smgr.LoadLevel(sceneName, 0f, false, Vector3.zero, Vector3.zero, loadType));
                Plugin.Logger.LogInfo($"[menu] launching '{sceneName}'");
                _visible = false;
            }
            catch (Exception e)
            {
                Plugin.Logger.LogError("[menu] launch failed: " + e);
            }
        }

        private static void DrawConfigSection()
        {
            GUILayout.Label("Config", _hdrStyle);

            GUILayout.BeginHorizontal();
            GUILayout.Label("Filter:", _lblStyle, GUILayout.Width(60));
            _filter = GUILayout.TextField(_filter, _fldStyle);
            if (GUILayout.Button("Save .cfg", _btnStyle, GUILayout.Width(80)))
                Plugin.ConfigRef?.Save();
            GUILayout.EndHorizontal();

            // ExpandHeight: take all space left below the Mode and Level sections, so long
            // config lists scroll inside the menu instead of being clipped by its bottom edge.
            _scroll = GUILayout.BeginScrollView(_scroll, GUILayout.ExpandHeight(true));
            if (Plugin.ConfigRef != null)
            {
                string section = null;
                foreach (var def in SortedKeys())
                {
                    if (!MatchesFilter(def)) continue;
                    if (def.Section != section)
                    {
                        section = def.Section;
                        GUILayout.Space(4);
                        GUILayout.Label("[" + section + "]", _hdrStyle);
                    }
                    DrawEntryRow(def, Plugin.ConfigRef[def]);
                }
            }
            GUILayout.EndScrollView();
        }

        private static IEnumerable<ConfigDefinition> SortedKeys()
        {
            var list = new List<ConfigDefinition>();
            foreach (var def in Plugin.ConfigRef.Keys) list.Add(def);
            list.Sort((a, b) =>
            {
                int s = string.Compare(a.Section, b.Section, StringComparison.Ordinal);
                return s != 0 ? s : string.Compare(a.Key, b.Key, StringComparison.Ordinal);
            });
            return list;
        }

        private static bool MatchesFilter(ConfigDefinition def)
        {
            if (string.IsNullOrEmpty(_filter)) return true;
            string lf = _filter.ToLowerInvariant();
            return def.Section.ToLowerInvariant().Contains(lf) ||
                   def.Key.ToLowerInvariant().Contains(lf);
        }

        private static void DrawEntryRow(ConfigDefinition def, ConfigEntryBase entry)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(def.Key, _lblStyle, GUILayout.Width(220));

            if (entry.SettingType == typeof(bool))
            {
                bool cur = (bool)entry.BoxedValue;
                bool next = GUILayout.Toggle(cur, cur ? "true" : "false", _btnStyle);
                if (next != cur)
                {
                    entry.BoxedValue = next;
                    Plugin.Instance?.SyncStatics();
                }
            }
            else
            {
                if (!_editBuf.TryGetValue(def, out string buf))
                {
                    buf = entry.BoxedValue?.ToString() ?? "";
                    _editBuf[def] = buf;
                }
                string newBuf = GUILayout.TextField(buf, _fldStyle, GUILayout.MinWidth(140));
                if (newBuf != buf) _editBuf[def] = newBuf;
                if (GUILayout.Button("Apply", _btnStyle, GUILayout.Width(60)))
                {
                    try
                    {
                        object parsed = TomlTypeConverter.ConvertToValue(_editBuf[def], entry.SettingType);
                        entry.BoxedValue = parsed;
                        _editBuf[def] = entry.BoxedValue.ToString();
                        Plugin.Instance?.SyncStatics();
                    }
                    catch (Exception e)
                    {
                        Plugin.Logger.LogWarning($"[menu] parse '{def.Key}' failed: {e.Message}");
                    }
                }
                if (GUILayout.Button("↺", _btnStyle, GUILayout.Width(28)))
                {
                    _editBuf[def] = entry.BoxedValue?.ToString() ?? "";
                }
            }

            GUILayout.EndHorizontal();
        }
    }
}
