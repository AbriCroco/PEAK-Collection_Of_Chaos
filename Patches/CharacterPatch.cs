using HarmonyLib;

namespace Chaos.Patches
{
    // Ensure every Character has the ChaosCharacterRPCs component so we can target RPCs to it.
    [HarmonyPatch(typeof(Character), "Awake")]
    public static class Character_Awake_Patch
    {
        static void Postfix(Character __instance)
        {
            if (__instance == null) return;
            if (__instance.GetComponent<CharacterRPCs>() == null)
            {
                __instance.gameObject.AddComponent<CharacterRPCs>();
            }
        }
    }
}
