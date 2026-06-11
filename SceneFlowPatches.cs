using System.Collections;
using HarmonyLib;
using UnityEngine;

namespace RedAllianceSpeedrun
{
    // Fixes the black-screen-on-restart bug.
    //
    // The public 6-arg SceneManagerScript.LoadLevel destroys the NetworkManager, the player
    // and the player canvas, draws the loading screen, waits two frames and only THEN starts
    // the private LoadLevel coroutine — whose first line is:
    //
    //     if (LoadingLevel) { Debug.LogError("Tried Loading Level while already loading..."); yield break; }
    //
    // If any second load starts inside that two-frame window (level-end trigger racing a TAB
    // restart, double scripted load, etc.), the loser passes the public part — destroying the
    // player and fading the screen — and then aborts in the private guard. Result: permanent
    // black/loading screen, game must be killed.
    //
    // This prefix rejects the duplicate call BEFORE anything is destroyed. The pending flag
    // covers the window between the public call and LoadingLevel becoming true; it's cleared
    // by Plugin.OnSceneLoaded (and by a timeout as a safety net against a load that never
    // finished).
    [HarmonyPatch(typeof(SceneManagerScript), nameof(SceneManagerScript.LoadLevel),
        new System.Type[] { typeof(string), typeof(float), typeof(bool), typeof(Vector3), typeof(Vector3), typeof(LevelLoadingType) })]
    internal static class LoadLevelDuplicateGuardPatch
    {
        internal static bool Pending;
        internal static float PendingSince;
        private const float PendingTimeout = 30f; // covers additionalDelay loads + slow scene loads

        [HarmonyPrefix]
        private static bool Prefix(string nextLevelString, ref IEnumerator __result)
        {
            bool pendingActive = Pending && (Time.unscaledTime - PendingSince) < PendingTimeout;
            if (SceneManagerScript.LoadingLevel || pendingActive)
            {
                Plugin.Logger.LogWarning(
                    $"[loadguard] LoadLevel('{nextLevelString}') rejected: another load is in progress " +
                    $"(LoadingLevel={SceneManagerScript.LoadingLevel}, pending={pendingActive}). " +
                    "Without this guard the duplicate would destroy the player and hang on a black screen.");
                __result = EmptyRoutine();
                return false; // skip original — nothing gets destroyed
            }
            Pending = true;
            PendingSince = Time.unscaledTime;
            return true;
        }

        internal static void Clear()
        {
            Pending = false;
        }

        private static IEnumerator EmptyRoutine()
        {
            yield break;
        }
    }

    // prison_1 is a ~38s cutscene level; speedruns load prison_2 directly. Redirect every
    // load of prison_1 to prison_2 so timing stays simple. Three layers (6-arg LoadLevel,
    // LoadLevelRPC, raw SceneManager.LoadSceneAsync) so no code path slips through.
    // Gated by Plugin.SkipPrison1Redirect (Restart.SkipPrison1Level in the config).
    [HarmonyPatch(typeof(SceneManagerScript), nameof(SceneManagerScript.LoadLevel),
        new System.Type[] { typeof(string), typeof(float), typeof(bool), typeof(Vector3), typeof(Vector3), typeof(LevelLoadingType) })]
    internal static class SkipPrison1Patch
    {
        [HarmonyPrefix]
        private static void Prefix(ref string nextLevelString)
        {
            if (!Plugin.SkipPrison1Redirect) return;
            if (nextLevelString == "prison_1")
            {
                Plugin.Logger.LogInfo("[prison1skip] LoadLevel redirect: prison_1 → prison_2");
                nextLevelString = "prison_2";
            }
        }
    }

    [HarmonyPatch(typeof(SceneManagerScript), nameof(SceneManagerScript.LoadLevelRPC))]
    internal static class SkipPrison1RpcPatch
    {
        [HarmonyPrefix]
        private static void Prefix(ref string level)
        {
            if (!Plugin.SkipPrison1Redirect) return;
            if (level == "prison_1")
            {
                Plugin.Logger.LogInfo("[prison1skip] LoadLevelRPC redirect: prison_1 → prison_2");
                level = "prison_2";
            }
        }
    }

    [HarmonyPatch(typeof(UnityEngine.SceneManagement.SceneManager),
        nameof(UnityEngine.SceneManagement.SceneManager.LoadSceneAsync),
        new System.Type[] { typeof(string) })]
    internal static class SkipPrison1AsyncPatch
    {
        [HarmonyPrefix]
        private static void Prefix(ref string sceneName)
        {
            if (!Plugin.SkipPrison1Redirect) return;
            if (sceneName == "prison_1")
            {
                Plugin.Logger.LogInfo("[prison1skip] LoadSceneAsync redirect: prison_1 → prison_2");
                sceneName = "prison_2";
            }
        }
    }

    // Marks the next scene load as "caused by loading a save", so the autosplitter can
    // suppress the split: jumping levels via a save is not run progress. GameSaveScript.Loading
    // covers most of the load window too, but the flag survives ordering differences between
    // the Loading property and the sceneLoaded callback.
    [HarmonyPatch(typeof(GameSaveScript), nameof(GameSaveScript.LoadGameRemote))]
    internal static class SaveLoadDetectPatch
    {
        internal static bool Pending;
        internal static float PendingSince;
        private const float PendingTimeout = 60f;

        [HarmonyPrefix]
        private static void Prefix()
        {
            Pending = true;
            PendingSince = Time.unscaledTime;
            Plugin.Logger.LogInfo("[splitguard] save load started — next scene change will not split.");
        }

        internal static bool IsActive()
        {
            return Pending && (Time.unscaledTime - PendingSince) < PendingTimeout;
        }

        internal static void Clear()
        {
            Pending = false;
        }
    }
}
