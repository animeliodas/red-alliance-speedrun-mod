using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace RedAllianceSpeedrun
{
    // WaterPhysicsScript.FloatObjects uses `floatingObjects.ElementAt(i)` inside a for loop.
    // ElementAt is a LINQ method that on Dictionary iterates from the start each call,
    // allocating an enumerator wrapper and giving O(n²) total work.
    // This replacement uses foreach (struct enumerator, alloc-free) and defers removals.
    [HarmonyPatch(typeof(WaterPhysicsScript), "FloatObjects")]
    internal static class WaterPhysicsFloatObjectsPatch
    {
        // Reused scratch list — avoids per-call allocation of removal list.
        private static readonly List<Transform> _removeBuf = new List<Transform>(16);

        [HarmonyPrefix]
        private static bool ReplaceFloatObjects(WaterPhysicsScript __instance)
        {
            var dict = __instance.floatingObjects;
            if (dict == null || dict.Count == 0) return false;

            _removeBuf.Clear();
            Vector3 myPos = __instance.transform.position;
            float waterLevelOffset = __instance.waterLevelOffset;
            float floatUpwardsSpeed = __instance.floatUpwardsSpeed;

            foreach (var kvp in dict)
            {
                var key = kvp.Key;
                if (key == null || !key.gameObject.activeSelf)
                {
                    _removeBuf.Add(key);
                    continue;
                }

                var fo = kvp.Value;
                var floatScript = fo.floatScript;
                var rb = fo.rigidbody;
                if (floatScript == null || rb == null)
                {
                    _removeBuf.Add(key);
                    continue;
                }

                Vector3 bounceOffset = key.TransformDirection(floatScript.bounceCenterOffset);
                Vector3 position = key.position + bounceOffset;
                float angle = Vector3.Angle(bounceOffset, Vector3.up);
                if (angle > 15f)
                {
                    key.rotation = Quaternion.Slerp(key.rotation,
                        Quaternion.Euler(bounceOffset), 0.03f);
                }
                float depth = Mathf.Clamp(myPos.y + waterLevelOffset - key.position.y,
                    0.2f, float.PositiveInfinity);
                Vector3 antiGravity = -Physics.gravity * rb.mass;
                Vector3 buoyancy = Vector3.ClampMagnitude(
                    antiGravity * 10f,
                    antiGravity.magnitude + floatUpwardsSpeed) * depth;
                float massBoost = rb.mass > 0.4f ? 1f : 1.4f;
                rb.AddForceAtPosition(buoyancy * massBoost, position);
                rb.velocity = Vector3.ClampMagnitude(rb.velocity, antiGravity.magnitude * depth * 0.5f);
            }

            for (int i = 0; i < _removeBuf.Count; i++)
            {
                dict.Remove(_removeBuf[i]);
            }
            _removeBuf.Clear();

            return false; // skip original
        }
    }
}
