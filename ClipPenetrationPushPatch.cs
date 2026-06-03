using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace RedAllianceSpeedrun
{
    // Correct emulation of Unity 2017 CharacterController penetration response.
    //
    // OLD Unity 2017: when InstantUncrouch made the capsule grow inside a wall, the engine's
    // built-in resolution iterated pushes along the velocity vector until the capsule was
    // OUT of the wall. Single-pass push by penetration depth was not enough — for thick
    // walls the engine looped. This is why even "shallow" clip setups in OLD worked: each
    // loop iteration revealed more depth and kept pushing. Right-tap before clip = micro
    // sideways velocity = deeper initial overlap = faster convergence = 100% reliable.
    //
    // NEW Unity 2019: rewrote resolution to push along SHORTEST exit (collision normal),
    // not along velocity. Same setup pushes player back out of the wall.
    //
    // We can't patch native CC internals. Emulation: after the snap, iterate ComputePenetration
    // + push-along-velocity until clear, hit iteration cap, or stop making progress (velocity
    // parallel to wall surface = no exit along velocity = bail).
    //
    // Selectivity (matches OLD geometry-dependence):
    //   - Capsule not in wall after snap → count=0, no-op.
    //   - Velocity into wall → each iter reduces depth → cleared → clip succeeds.
    //   - Velocity parallel to wall → depth doesn't decrease → progress-check bails → no
    //     forced shove along wall surface.
    //   - Floor under player → direction.y high → floor filter skips → no horizontal scoot
    //     off ledge.
    internal static class ClipPenetrationPushPatch
    {
        private static FieldInfo _velocityField;
        private static FieldInfo _charControllerField;
        private static FieldInfo _capsuleColliderField;
        private static readonly Collider[] _overlapBuf = new Collider[16];
        private static bool _failed;

        public static void Push(FPSWalker walker)
        {
            if (!Plugin.ClipPenetrationPush) return;
            if (_failed) return;

            try
            {
                if (!EnsureFields()) return;

                var cc = _charControllerField.GetValue(walker) as CharacterController;
                if ((object)cc == null) return;
                var capsule = _capsuleColliderField.GetValue(walker) as CapsuleCollider;

                Vector3 velocity = (Vector3)_velocityField.GetValue(walker);
                Vector3 horiz = new Vector3(velocity.x, 0f, velocity.z);
                float speed = horiz.magnitude;
                if (speed < Plugin.ClipPenetrationPushMinSpeed)
                {
                    if (Plugin.LogClipEvents)
                        Plugin.Logger.LogInfo($"[clip-push] skip: speed {speed:F2} < {Plugin.ClipPenetrationPushMinSpeed:F2}");
                    return;
                }
                Vector3 pushDir = horiz / speed;

                Transform t = walker.transform;
                float halfHeight = Mathf.Max(cc.height * 0.5f - cc.radius, 0f);

                // Force PhysX to see the new capsule dimensions before the first overlap.
                Physics.SyncTransforms();

                float totalPush = 0f;
                float prevDepth = float.MaxValue;
                int iterations = 0;
                int maxIter = Mathf.Max(1, Plugin.ClipPenetrationIterations);
                float maxTotalPush = Plugin.ClipPenetrationMaxPush;
                float floorDot = Plugin.ClipPenetrationFloorDot;
                float margin = Plugin.ClipPenetrationPushMargin;

                while (iterations < maxIter)
                {
                    iterations++;

                    // Recompute capsule endpoints (transform may have moved last iter).
                    Vector3 centerWorld = t.TransformPoint(cc.center);
                    Vector3 up = t.up;
                    Vector3 p0 = centerWorld + up * halfHeight;
                    Vector3 p1 = centerWorld - up * halfHeight;

                    int count = Physics.OverlapCapsuleNonAlloc(p0, p1, cc.radius, _overlapBuf,
                        Plugin.ClipPenetrationLayerMask, QueryTriggerInteraction.Ignore);

                    if (count == 0) break;

                    float maxDepth = 0f;
                    for (int i = 0; i < count; i++)
                    {
                        var other = _overlapBuf[i];
                        if ((object)other == null) continue;
                        if (other == (Collider)cc) continue;
                        if ((object)capsule != null && other == (Collider)capsule) continue;
                        if ((object)other.attachedRigidbody != null && !other.attachedRigidbody.isKinematic) continue;

                        bool overlap = Physics.ComputePenetration(
                            cc, t.position, t.rotation,
                            other, other.transform.position, other.transform.rotation,
                            out Vector3 dir, out float distance);

                        if (!overlap) continue;
                        if (dir.y > floorDot) continue; // floor / step — skip

                        if (distance > maxDepth) maxDepth = distance;
                    }

                    if (maxDepth <= 0f) break; // brushing only / all floors

                    // Deadzone: if depth is already tiny, we're at the wall surface. Pushing
                    // further past margin would overshoot — leave the rest to native CC.Move's
                    // shortest-exit resolution (which is fine at this depth) so the player
                    // ends ON the wall surface, not META through. Critical at low speed where
                    // even 0.07m of margin-overshoot can land player in void → no isGrounded
                    // → no jump.
                    if (maxDepth < Plugin.ClipPenetrationDepthDeadzone)
                    {
                        if (Plugin.LogClipEvents)
                            Plugin.Logger.LogInfo($"[clip-push] stop iter={iterations}: depth {maxDepth:F3} < deadzone {Plugin.ClipPenetrationDepthDeadzone:F3}");
                        break;
                    }

                    // Progress check: if depth isn't shrinking, velocity is parallel to wall
                    // surface — pushing more along velocity won't help, bail.
                    if (maxDepth >= prevDepth - 0.001f)
                    {
                        if (Plugin.LogClipEvents)
                            Plugin.Logger.LogInfo($"[clip-push] bail iter={iterations}: no progress (depth {maxDepth:F3} >= prev {prevDepth:F3})");
                        break;
                    }
                    prevDepth = maxDepth;

                    float step = maxDepth + margin;
                    // Per-iter step cap — prevents one fat depth reading from teleporting
                    // the player clean through a thin wall into the next room. Iterations
                    // still proceed, but each is bounded so total motion is controlled.
                    if (step > Plugin.ClipPenetrationStepCap) step = Plugin.ClipPenetrationStepCap;
                    if (totalPush + step > maxTotalPush)
                    {
                        step = maxTotalPush - totalPush;
                        if (step <= 0f) break;
                    }

                    t.position += pushDir * step;
                    totalPush += step;
                    Physics.SyncTransforms();

                    if (Plugin.LogClipEvents)
                        Plugin.Logger.LogInfo($"[clip-push] iter={iterations}: depth={maxDepth:F3} step={step:F3} total={totalPush:F3}");
                }

                if (Plugin.LogClipEvents && totalPush > 0f)
                    Plugin.Logger.LogInfo($"[clip-push] done: total={totalPush:F3}m in {iterations} iters (speed={speed:F2})");
                else if (Plugin.LogClipEvents && iterations >= 1 && totalPush == 0f)
                    Plugin.Logger.LogInfo($"[clip-push] no-op: no penetration / all floors / not aligned");
            }
            catch (Exception e)
            {
                _failed = true;
                Plugin.Logger.LogError("[clip-push] ClipPenetrationPushPatch failed: " + e);
            }
        }

        private static bool EnsureFields()
        {
            const BindingFlags B = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
            if ((object)_velocityField == null) _velocityField = typeof(FPSWalker).GetField("velocity", B);
            if ((object)_charControllerField == null) _charControllerField = typeof(FPSWalker).GetField("charController", B);
            if ((object)_capsuleColliderField == null) _capsuleColliderField = typeof(FPSWalker).GetField("capsuleCollider", B);

            if ((object)_velocityField == null || (object)_charControllerField == null)
            {
                _failed = true;
                Plugin.Logger.LogError("[clip-push] could not resolve velocity/charController fields; disabled.");
                return false;
            }
            return true;
        }
    }
}
