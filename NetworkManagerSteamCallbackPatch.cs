using System;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;

namespace RedAllianceSpeedrun
{
    // Postfix on NetworkManager.OnEnable: a new NetworkManager just registered two new Steam
    // Callback<T> objects. The previous NetworkManager's callbacks are now orphaned but rely
    // on Mono finalizers to release their pinned GCHandles. On a busy/fragmented heap this
    // can lag, leaving pinned regions that fragment the heap further. We explicitly dispose
    // the cached previous-instance callbacks here, then cache the new ones for next time.
    [HarmonyPatch(typeof(NetworkManager), "OnEnable")]
    internal static class NetworkManagerSteamCallbackPatch
    {
        private static FieldInfo _overlayField;
        private static FieldInfo _playerCountField;

        [HarmonyPostfix]
        private static void Postfix(NetworkManager __instance)
        {
            try
            {
                // Dispose whatever we cached from the previous NetworkManager
                DisposeIfNotNull(ref Plugin.s_prevOverlayCallback, "m_GameOverlayActivated");
                DisposeIfNotNull(ref Plugin.s_prevPlayerCountCallback, "m_NumberOfCurrentPlayers");

                // Cache the new instance's callbacks so we hold them alive and can dispose
                // next time around.
                if ((object)_overlayField == null)
                {
                    _overlayField = typeof(NetworkManager).GetField("m_GameOverlayActivated",
                        BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                }
                if ((object)_playerCountField == null)
                {
                    _playerCountField = typeof(NetworkManager).GetField("m_NumberOfCurrentPlayers",
                        BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                }

                if ((object)_overlayField != null)
                {
                    Plugin.s_prevOverlayCallback = _overlayField.GetValue(__instance) as IDisposable;
                }
                if ((object)_playerCountField != null)
                {
                    Plugin.s_prevPlayerCountCallback = _playerCountField.GetValue(__instance) as IDisposable;
                }
            }
            catch (Exception e)
            {
                Plugin.Logger.LogError("[steamfix] Postfix failed: " + e);
            }
        }

        private static void DisposeIfNotNull(ref IDisposable d, string fieldName)
        {
            if (d == null) return;
            try
            {
                d.Dispose();
                Plugin.s_steamCallbackDisposeCount++;
                Plugin.Logger.LogInfo($"[steamfix] Disposed previous {fieldName} (total disposed: {Plugin.s_steamCallbackDisposeCount}).");
            }
            catch (Exception e)
            {
                Plugin.Logger.LogWarning($"[steamfix] Failed to dispose {fieldName}: {e.Message}");
            }
            finally
            {
                d = null;
            }
        }
    }
}
