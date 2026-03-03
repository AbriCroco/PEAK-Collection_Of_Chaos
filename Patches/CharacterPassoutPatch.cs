using HarmonyLib;
using Chaos.Networking;
using Chaos.Utils;

namespace Chaos.Patches
{
    [HarmonyPatch(typeof(Character), "RPCA_PassOut")]
    public static class Character_RPCA_PassOut_Patch
    {
        static bool Prefix(Character __instance)
        {
            try
            {
                if (__instance == null || __instance.photonView == null) return true;
                int vid = __instance.photonView.ViewID;

                if (!ForcedPassoutManager.Contains(vid))
                {
                    return true;
                }

                // If we were allowed once, consume the allow and let this passout run.
                if (ForcedPassoutManager.ConsumeAllowOnce(vid)) return true;

                return false;
            }
            catch (System.Exception ex)
            {
                ModLogger.Log("[PassoutPatch] Prefix exception: " + ex);
                return true;
            }
        }
    }
}
