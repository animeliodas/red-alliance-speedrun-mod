using HarmonyLib;
using UnityEngine;

namespace RedAllianceSpeedrun
{
    // SpeedrunMode-specific Harmony patches. All gated by Plugin.SpeedrunMode at runtime
    // so they're no-ops in normal play.
    //
    // 1) SceneManagerScript.LoadLevel — rewrites "prison_1" → "prison_2". prison_1 in Vortex
    //    is a 38s cutscene; skip straight to playable level.
    // 2) PauseMenuScript.Pause — forces save menu button interactable=true. Original code
    //    disables it when VortexRun=true (which SpeedrunMode also enables).

    // Public LoadLevel has two overloads (6-arg + 1-arg IEnumerator). Explicit type list
    // pins us to the 6-arg version called by triggers/scripts.
    [HarmonyPatch(typeof(SceneManagerScript), nameof(SceneManagerScript.LoadLevel),
        new System.Type[] { typeof(string), typeof(float), typeof(bool), typeof(Vector3), typeof(Vector3), typeof(LevelLoadingType) })]
    internal static class SpeedrunSkipPrison1Patch
    {
        [HarmonyPrefix]
        private static void Prefix(ref string nextLevelString)
        {
            if (!Plugin.SpeedrunMode) return;
            if (nextLevelString == "prison_1")
            {
                Plugin.Logger.LogInfo("[speedrun] LoadLevel redirect: prison_1 → prison_2");
                nextLevelString = "prison_2";
            }
        }
    }

    // Backstop: also patch LoadLevelRPC (network path) and SceneManager.LoadSceneAsync
    // (lowest layer) so any code path that bypasses the 6-arg LoadLevel still gets caught.
    [HarmonyPatch(typeof(SceneManagerScript), nameof(SceneManagerScript.LoadLevelRPC))]
    internal static class SpeedrunSkipPrison1RpcPatch
    {
        [HarmonyPrefix]
        private static void Prefix(ref string level)
        {
            if (!Plugin.SpeedrunMode) return;
            if (level == "prison_1")
            {
                Plugin.Logger.LogInfo("[speedrun] LoadLevelRPC redirect: prison_1 → prison_2");
                level = "prison_2";
            }
        }
    }

    [HarmonyPatch(typeof(UnityEngine.SceneManagement.SceneManager), nameof(UnityEngine.SceneManagement.SceneManager.LoadSceneAsync),
        new System.Type[] { typeof(string) })]
    internal static class SpeedrunSkipPrison1AsyncPatch
    {
        [HarmonyPrefix]
        private static void Prefix(ref string sceneName)
        {
            if (!Plugin.SpeedrunMode) return;
            if (sceneName == "prison_1")
            {
                Plugin.Logger.LogInfo("[speedrun] LoadSceneAsync redirect: prison_1 → prison_2");
                sceneName = "prison_2";
            }
        }
    }

    // Block all trigger-based saves in SpeedrunMode. AutoSaveScript triggers (both start-of-
    // level and mid-level) call GameSaveScript.SaveRemote with a script-configurable
    // autoSave bool — some zones pass false, slipping past a SaveRemote-only patch. Skipping
    // AutoSaveScript.Update entirely catches all of them regardless of that field.
    //
    // F5 / pause menu / console save commands DON'T go through AutoSaveScript so they still
    // work (subject to GameSaveScript's own canSave gate, which our VortexLegacyMode=false
    // setup keeps green).
    [HarmonyPatch(typeof(AutoSaveScript), "Update")]
    internal static class SpeedrunBlockAutosaveScriptPatch
    {
        [HarmonyPrefix]
        private static bool Prefix()
        {
            if (!Plugin.SpeedrunMode) return true;
            return false; // skip entire Update — no save call, no cleanup, no retrigger
        }
    }

    // Backstop: also block any SaveRemote(autoSave=true) calls that don't originate from
    // AutoSaveScript (some future scripted save trigger could route here directly).
    [HarmonyPatch(typeof(GameSaveScript), nameof(GameSaveScript.SaveRemote))]
    internal static class SpeedrunBlockAutosaveRemotePatch
    {
        [HarmonyPrefix]
        private static bool Prefix(bool autoSave)
        {
            if (!Plugin.SpeedrunMode) return true;
            if (!autoSave) return true; // manual save passes
            Plugin.Logger.LogInfo("[speedrun] SaveRemote(autoSave=true) blocked");
            return false;
        }
    }

    // VortexRun hardcodes a 3x AI damage multiplier in CalculateDamageMultiplier,
    // overriding gameDifficulty entirely. SpeedrunMode wants Easy behavior — patch
    // returns 1f (same as Normal/Easy fallback in the original switch).
    [HarmonyPatch(typeof(GlobalAIScript), nameof(GlobalAIScript.CalculateDamageMultiplier))]
    internal static class SpeedrunDamageMultiplierPatch
    {
        [HarmonyPrefix]
        private static bool Prefix(ref float __result)
        {
            if (!Plugin.SpeedrunMode) return true;
            __result = 1f;
            return false; // skip original
        }
    }

    [HarmonyPatch(typeof(PauseMenuScript), "Pause")]
    internal static class SpeedrunSaveButtonEnablePatch
    {
        private static readonly System.Reflection.FieldInfo _saveBtnField =
            typeof(PauseMenuScript).GetField("saveMenuButton",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

        [HarmonyPostfix]
        private static void Postfix(PauseMenuScript __instance)
        {
            if (!Plugin.SpeedrunMode) return;
            try
            {
                if ((object)_saveBtnField != null)
                {
                    var btn = _saveBtnField.GetValue(__instance) as UnityEngine.UI.Button;
                    if ((object)btn != null) btn.interactable = true;
                }
            }
            catch (System.Exception e)
            {
                Plugin.Logger.LogError("[speedrun] save-button enable failed: " + e);
            }
        }
    }
}
