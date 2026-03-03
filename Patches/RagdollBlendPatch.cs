using System.Reflection;
using HarmonyLib;
using UnityEngine;
using Chaos.Gameplay;

namespace Chaos.Patches
{
    // Increases the effective ragdoll camera blend during drunk effect without changing game physics.
    // It:
    // - Reads MainCameraMovement.private fields: ragdollCam (float) and physicsRot (Quaternion)
    // - After CharacterCam finishes, it blends the camera a bit more towards physicsRot
    //   to achieve at least DrunkRagdollCam.CurrentMinBlend.
    [HarmonyPatch(typeof(MainCameraMovement), "CharacterCam")]
    public static class RagdollBlendPatch
    {
        static readonly FieldInfo F_ragdollCam = AccessTools.Field(typeof(MainCameraMovement), "ragdollCam");
        static readonly FieldInfo F_physicsRot = AccessTools.Field(typeof(MainCameraMovement), "physicsRot");

        [HarmonyPostfix]
        static void Postfix(MainCameraMovement __instance)
        {
            if (!DrunkRagdollCam.Active) return;
            if (DrunkRagdollCam.CurrentMinBlend <= 0f) return;

            // Access private state
            if (F_ragdollCam == null || F_physicsRot == null) return;

            float currentBlend = (float)F_ragdollCam.GetValue(__instance);
            float desiredMin = Mathf.Clamp01(DrunkRagdollCam.CurrentMinBlend);

            if (desiredMin <= currentBlend) return; // game already blends at least this much

            // Pull the final camera rotation a bit closer to physicsRot to reach desiredMin
            var physicsRot = (Quaternion)F_physicsRot.GetValue(__instance);

            // Incremental lerp from the already-set rotation towards physics target
            // Amount: the missing blend (desiredMin - currentBlend)
            var tr = __instance.transform;
            tr.rotation = Quaternion.Lerp(tr.rotation, physicsRot, desiredMin - currentBlend);

            // Optional: bump the stored ragdollCam up for the next frame smoothing
            F_ragdollCam.SetValue(__instance, desiredMin);
        }
    }
}
