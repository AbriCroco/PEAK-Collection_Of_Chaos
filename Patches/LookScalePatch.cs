using HarmonyLib;
using UnityEngine;
using Chaos.Gameplay;

namespace Chaos.Patches
{
    // Post-process CharacterInput.Sample to scale lookInput for the local player.
    [HarmonyPatch(typeof(CharacterInput))]
    public static class LookScalePatch
    {
        [HarmonyPatch(nameof(CharacterInput.Sample))]
        [HarmonyPostfix]
        private static void Postfix(CharacterInput __instance, bool playerMovementActive)
        {
            // Only when input sampling is active
            if (!playerMovementActive) return;

            // Only for the local player's CharacterInput
            var ch = __instance.GetComponent<Character>();
            if (ch != null && !ch.IsLocal) return;

            float m = Mathf.Clamp(SensitivityScaler.Multiplier, 0.05f, 1f);
            if (Mathf.Approximately(m, 1f)) return;

            // Scale raw look delta; affects both mouse and gamepad uniformly
            __instance.lookInput *= m;
        }
    }
}
