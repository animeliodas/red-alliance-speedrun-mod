using HarmonyLib;
using UnityEngine;
using UnityStandardAssets.CinematicEffects;

namespace RedAllianceSpeedrun
{
    // Fixes the per-frame LUT rebuild in TonemappingColorGrading (CinematicEffects).
    //
    // OnRenderImage gates LUT generation with `if (m_LutTex == null || m_Dirty) UpdateLut()`.
    // In fastMode, UpdateLut -> Create1DLut only fills m_LutCurveTex1D and never assigns
    // m_LutTex, so the null check stays true and the LUT is regenerated EVERY FRAME:
    // a Color[] allocation + SetPixels + texture Apply per frame (~15KB/frame managed churn
    // plus a GPU upload), which drives the Boehm GC to 10+ collections per second.
    //
    // After UpdateLut runs in fastMode we install a 1x1 marker Texture3D into m_LutTex purely
    // to satisfy the null check; the fastMode render path only ever samples m_LutCurveTex1D.
    // If the effect later switches to the 3D path (fastMode off + dirty), the prefix removes
    // the marker so Create3DLut builds the real 32^3 texture.
    [HarmonyPatch(typeof(TonemappingColorGrading), "UpdateLut")]
    internal static class TonemappingLutFixPatch
    {
        internal static bool Enabled = true;
        internal static int MarkersInstalled;

        private static readonly AccessTools.FieldRef<TonemappingColorGrading, Texture3D> LutTexRef =
            AccessTools.FieldRefAccess<TonemappingColorGrading, Texture3D>("m_LutTex");

        [HarmonyPrefix]
        public static void Prefix(TonemappingColorGrading __instance)
        {
            if (!Enabled || __instance.fastMode) return;
            // Switching to the 3D path: drop our marker so Create3DLut allocates a real LUT.
            var tex = LutTexRef(__instance);
            if (tex != null && tex.width == 1)
            {
                Object.DestroyImmediate(tex);
                LutTexRef(__instance) = null;
            }
        }

        [HarmonyPostfix]
        public static void Postfix(TonemappingColorGrading __instance)
        {
            if (!Enabled || !__instance.fastMode) return;
            if (LutTexRef(__instance) != null) return;
            var marker = new Texture3D(1, 1, 1, TextureFormat.RGB24, mipmap: false)
            {
                hideFlags = HideFlags.DontSave
            };
            LutTexRef(__instance) = marker;
            MarkersInstalled++;
            Plugin.Logger.LogDebug($"[lutfix] marker installed (total {MarkersInstalled}); per-frame LUT rebuild stopped.");
        }
    }
}
