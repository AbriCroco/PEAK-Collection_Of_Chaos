using HarmonyLib;
using Photon.Pun;
using UnityEngine;

namespace Chaos.Patches
{
    [HarmonyPatch(typeof(Campfire), "Light_Rpc")]
    public static class CampfirePatch
    {
        static void Postfix(Campfire __instance)
        {
            // Only the host/master applies the cleanse
            if (!PhotonNetwork.IsMasterClient)
                return;

            foreach (var player in PlayerHandler.GetAllPlayerCharacters())
            {
                if (player == null || player.data.dead)
                    continue;

                float dist = Vector3.Distance(__instance.transform.position, player.Center);
                if (dist > __instance.moraleBoostRadius)
                    continue;

                var aff = player.refs.afflictions;
                var pv = player.photonView;
                if (aff == null || pv == null)
                    continue;

                // Copy their current statuses
                float[] newStatuses = (float[])aff.currentStatuses.Clone();

                // Reset everything to 0, except Weight
                for (int i = 0; i < newStatuses.Length; i++)
                {
                    var type = (CharacterAfflictions.STATUSTYPE)i;
                    if (type == CharacterAfflictions.STATUSTYPE.Weight ||
                        type == CharacterAfflictions.STATUSTYPE.Hunger ||
                        type == CharacterAfflictions.STATUSTYPE.Curse ||
                        type == CharacterAfflictions.STATUSTYPE.Injury)
                        continue;
                    newStatuses[i] = 0f;
                }

                // Lower hunger instead of resetting
                float currentHunger = newStatuses[(int)CharacterAfflictions.STATUSTYPE.Hunger];
                if (currentHunger > 0.2f)
                    newStatuses[(int)CharacterAfflictions.STATUSTYPE.Hunger] = Mathf.Clamp(0f, currentHunger - 0.2f, 2f);

                // Apply new statuses on the player's client
                pv.RPC("ApplyStatusesFromFloatArrayRPC", pv.Owner, newStatuses);
            }
        }
    }
}
