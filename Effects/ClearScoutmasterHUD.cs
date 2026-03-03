using System;
using Photon.Pun;
using Chaos.Utils;

namespace Chaos.Effects
{
    public static class ClearScoutmasterHUD
    {
        public static void BroadcastClearScoutmasterHUD()
        {
            if (!PhotonNetwork.InRoom)
            {
                ModLogger.Log("[ScoutmasterEffect] Cannot clear HUD: not in a room.");
                return;
            }

            var local = Character.localCharacter;
            if (local == null || local.photonView == null)
            {
                ModLogger.Log("[ScoutmasterEffect] Cannot clear HUD: local Character/PhotonView not available.");
                return;
            }

            try
            {
                string who = PhotonNetwork.LocalPlayer?.NickName ?? "Unknown";
                local.photonView.RPC("RPCA_Chaos_ClearScoutmasterHUD", RpcTarget.All);
                ModLogger.Log($"[ScoutmasterEffect] Clear HUD requested by '{who}', RPC broadcast to all.");
            }
            catch (Exception ex)
            {
                ModLogger.Log("[ScoutmasterEffect] Failed to broadcast ClearScoutmasterHUD RPC: " + ex);
            }
        }
    }
}
