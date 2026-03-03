using HarmonyLib;
using Chaos.Manager;

namespace Chaos.Patches
{
    [HarmonyPatch(typeof(ItemDatabase), "OnLoaded")]
    static class ItemDatabasePatch
    {
        static void Postfix()
        {
            ModItemIDs.Initialize();
        }
    }
}

