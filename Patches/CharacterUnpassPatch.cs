using HarmonyLib;
using Chaos.Networking;
using Chaos.Utils;

namespace Chaos.Patches
{
    [HarmonyPatch(typeof(Character), "RPCA_UnPassOut")]
    public static class Character_RPCA_UnPassOut_Patch
    {
        // Prevent the game's unpass RPC from running while we have a forced passout for this view.
        static bool Prefix(Character __instance)
        {
            try
            {
                if (__instance == null || __instance.photonView == null) return true;
                int vid = __instance.photonView.ViewID;
                if (ForcedPassoutManager.Contains(vid))
                {
                    return false;
                }
            }
            catch (System.Exception ex)
            {
                ModLogger.Log("[CharacterUnpassPatch] Prefix exception: " + ex);
            }
            return true;
        }
    }
}
