using UnityEngine;

namespace Chaos.Gameplay
{
    // Simple controller for an extra ragdoll camera blend enforced during drunk effect.
    // Value range: 0..1 (0 = no extra ragdoll; 1 = full physics rotation).
    public static class DrunkRagdollCam
    {
        // Minimum ragdoll blend enforced by drunk effect (per frame, if enabled)
        public static float CurrentMinBlend { get; private set; } = 0f;

        // Whether the overlay is active
        public static bool Active { get; private set; } = false;

        // Begin with a max/minimum enforced ragdoll blend (0..1)
        public static void Begin(float minBlend)
        {
            Active = true;
            SetMinBlend(minBlend);
        }

        // Update the minimum enforced ragdoll blend (0..1)
        public static void SetMinBlend(float minBlend)
        {
            CurrentMinBlend = Mathf.Clamp01(minBlend);
        }

        // Stop enforcing extra ragdoll
        public static void End()
        {
            Active = false;
            CurrentMinBlend = 0f;
        }
    }
}
