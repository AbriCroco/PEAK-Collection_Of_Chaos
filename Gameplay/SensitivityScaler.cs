using UnityEngine;

namespace Chaos.Gameplay
{
    // Minimal multiplier-only scaler; used by the CharacterInput patch below.
    public static class SensitivityScaler
    {
        public static float Multiplier { get; private set; } = 1f;

        private static int _beginCount;

        public static bool IsActive => _beginCount > 0;

        public static void Begin(float initialMultiplier)
        {
            _beginCount++;
            SetMultiplier(initialMultiplier);
        }

        public static void SetMultiplier(float m)
        {
            // Keep a small floor to avoid near-zero “frozen” look; cap at 1
            Multiplier = Mathf.Clamp(m, 0.05f, 1f);
        }

        public static void End()
        {
            if (_beginCount > 0) _beginCount--;
            if (_beginCount == 0) Multiplier = 1f;
        }
    }
}
