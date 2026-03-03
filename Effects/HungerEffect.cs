using System.Collections.Generic;
using UnityEngine;
using Photon.Pun;
using Chaos.Utils;
using Chaos.Networking;

namespace Chaos.Effects
{
    public class HungerEffect : IEffect
    {
        public byte Id => EffectIds.Hunger;
        public string IntroMessage => "@triggerer@ shared his hunger... how nice?!";
        public int CountdownSeconds => 3;

        public void Apply(string? triggererName = null)
        {
            if (string.IsNullOrEmpty(triggererName))
            {
                ModLogger.Log("[HungerEffect] No triggerer provided.");
                return;
            }

            if (PhotonNetwork.LocalPlayer == null || PhotonNetwork.LocalPlayer.NickName != triggererName)
            {
                // Not the player who pressed the key: ignore
                return;
            }

            var triggerer = Character.localCharacter;
            if (triggerer == null)
            {
                ModLogger.Log("[HungerEffect] Triggerer character not found on local client.");
                return;
            }

            var affs = triggerer.refs.afflictions;
            if (affs == null)
            {
                ModLogger.Log("[HungerEffect] Triggerer afflictions missing.");
                return;
            }

            float triggererHunger = affs.GetCurrentStatus(CharacterAfflictions.STATUSTYPE.Hunger);
            if (triggererHunger <= 0f)
            {
                return;
            }

            try
            {
                affs.SetStatus(CharacterAfflictions.STATUSTYPE.Hunger, 0f);
            }
            catch (System.Exception ex)
            {
                ModLogger.Log("[HungerEffect] Failed to clear triggerer hunger locally: " + ex);
            }

            var allPlayers = PlayerHandler.GetAllPlayerCharacters();
            var targets = new List<Character>();
            foreach (var p in allPlayers)
            {
                if (p == null) continue;
                if (p == triggerer) continue;
                if (p.isBot) continue;
                if (p.data.dead) continue;
                if (p.data.fullyPassedOut) continue;
                targets.Add(p);
            }
            if (targets.Count == 0)
            {
                ModLogger.Log("[HungerEffect] No other players to receive hunger.");
                return;
            }


            float remaining = triggererHunger;
            for (int i = 0; i < targets.Count; i++)
            {
                var p = targets[i];
                float give;
                if (i < targets.Count - 1)
                {
                    give = UnityEngine.Random.Range(0f, remaining);
                }
                else
                {
                    give = remaining;
                }

                try
                {
                    p.photonView.RPC("RPCA_Chaos_AddStatus", p.photonView.Owner, (int)CharacterAfflictions.STATUSTYPE.Hunger, give);
                }
                catch (System.Exception ex)
                {
                    ModLogger.Log($"[HungerEffect] Failed to RPC add hunger to {p?.name ?? "<null>"}: {ex}");
                }

                remaining = Mathf.Max(0f, remaining - give);
                if (remaining <= 0f) break;
            }
        }
    }
}
