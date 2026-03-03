using System.Linq;
using Chaos.Utils;
using Chaos.Networking;
using Photon.Pun;
using System;

namespace Chaos.Effects
{
    public class CleanseEffect : IEffect
    {
        public byte Id => EffectIds.Cleanse;

        public int CountdownSeconds => 5;

        const float FailChance = 0.05f;

        public string IntroMessage
        {
            get
            {
                if (EffectIntroHandler.TryGetLatest(Id, out var text, out _))
                    return text;
                return "";
            }
        }

        public string PreparePreview(string triggererName, float ttlSeconds = 20f)
        {
            if (PhotonNetwork.LocalPlayer == null || PhotonNetwork.LocalPlayer.NickName != triggererName)
                return IntroMessage;

            float luck = UnityEngine.Random.value;
            string text = (luck < FailChance)
                ? "@triggerer@ just failed a ritual... miserably"
                : "An occult ritual just occured...";

            try
            {
                var ch = Character.localCharacter;
                var pv = ch?.photonView;
                if (pv != null)
                {
                    pv.RPC("RPCA_Chaos_IntroMessage", RpcTarget.All, Id, triggererName, text, luck, ttlSeconds);
                }
            }
            catch (Exception ex)
            {
                ModLogger.Log("[CleanseEffect] Failed to broadcast intro: " + ex);
            }
            return text;
        }

        public void Apply(string triggererName, int victimId)
        {
            if (string.IsNullOrEmpty(triggererName)) return;
            if (PhotonNetwork.LocalPlayer == null || PhotonNetwork.LocalPlayer.NickName != triggererName) return;

            var triggerer = Character.localCharacter;
            if (triggerer == null)
            {
                ModLogger.Log("[CleanseEffect] Triggerer character not found on local client.");
                return;
            }

            var affs = triggerer.refs.afflictions;
            if (affs == null)
            {
                ModLogger.Log("[CleanseEffect] Triggerer afflictions missing.");
                return;
            }

            if (!EffectIntroHandler.Consume(Id, triggererName, out _, out float luck))
            {
                ModLogger.Log("[CleanseEffect] No prepared intro value found; aborting to avoid desync.");
                return;
            }

            if (luck < FailChance)
            {
                affs.AddStatus(CharacterAfflictions.STATUSTYPE.Injury, 0.7f, fromRPC: false);
                return;
            }

            float removedSum = 0f;
            var enumCount = System.Enum.GetNames(typeof(CharacterAfflictions.STATUSTYPE)).Length;
            for (int i = 0; i < enumCount; i++)
            {
                var type = (CharacterAfflictions.STATUSTYPE)i;

                if (type == CharacterAfflictions.STATUSTYPE.Weight ||
                    type == CharacterAfflictions.STATUSTYPE.Thorns ||
                    type == CharacterAfflictions.STATUSTYPE.Curse ||
                    type == CharacterAfflictions.STATUSTYPE.Hunger ||
                    type == CharacterAfflictions.STATUSTYPE.Drowsy)
                    continue;

                float before = affs.GetCurrentStatus(type);
                if (before <= 0f) continue;

                affs.SetStatus(type, 0f);
                removedSum += before;
            }

            if (removedSum <= 0f)
            {
                return;
            }

            float drowsyForTriggerer = removedSum * 2f;
            float drowsyForVictim = Math.Clamp(removedSum * 1.25f, 0f, 1.5f);

            try
            {
                affs.AddStatus(CharacterAfflictions.STATUSTYPE.Drowsy, drowsyForTriggerer, fromRPC: false);
            }
            catch (System.Exception ex)
            {
                ModLogger.Log("[CleanseEffect] Failed applying drowsy locally to triggerer: " + ex);
            }

            if (victimId != -1)
            {
                var players = PlayerHandler.GetAllPlayerCharacters();
                var victim = players.FirstOrDefault(p => p != null && p.photonView != null && p.photonView.Owner != null && p.photonView.Owner.ActorNumber == victimId);
                if (victim != null)
                {
                    try
                    {
                        victim.photonView.RPC("RPCA_Chaos_AddStatus", victim.photonView.Owner, (int)CharacterAfflictions.STATUSTYPE.Drowsy, drowsyForVictim);
                    }
                    catch (System.Exception ex)
                    {
                        ModLogger.Log("[CleanseEffect] Failed to request drowsy on victim: " + ex);
                    }
                }
                else
                {
                    ModLogger.Log($"[CleanseEffect] Victim with actor id {victimId} not found locally on triggerer.");
                }
            }
        }

        public void Apply(string? triggererName = null)
        {
            ModLogger.Log("[CleanseEffect] Warning: Apply(triggererName) called without victimId. Effect skipped to avoid desync.");
        }
    }
}
