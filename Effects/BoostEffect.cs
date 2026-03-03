using System.Linq;
using Chaos.Utils;
using Chaos.Manager;
using Chaos.Networking;
using Photon.Pun;
using System;
using UnityEngine;

namespace Chaos.Effects
{
    public class BoostEffect : IEffect
    {
        public byte Id => EffectIds.Boost;

        public int CountdownSeconds => 3;

        public static float ModifierMin = -0.7f;
        public static float ModifierMax = 1.5f;

        // Sensitivity mapping for negative path
        public static float SensitivityDebuffMinModifier => ModifierMin;
        public static float SensitivityDebuffMinPercent = 0.05f;
        public static float SensitivityDebuffMaxPercent = 0.50f;

        public static float RagdollDebuffMinModifier => ModifierMin;
        public static float RagdollDebuffMinPercent = 1.00f;
        public static float RagdollDebuffMaxPercent = 0.40f;
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

            float rolled = UnityEngine.Random.Range(ModifierMin, ModifierMax);
            string text = rolled > 0f
                ? "You getting boosted now??"
                : (Mathf.Approximately(rolled, 0f) ? "Nothing happened..." : "I think you had too many drinks...");

            try
            {
                var ch = Character.localCharacter;
                var pv = ch?.photonView;
                if (pv != null)
                {
                    pv.RPC("RPCA_Chaos_IntroMessage", RpcTarget.All, Id, triggererName, text, rolled, ttlSeconds);
                }
            }
            catch (Exception ex)
            {
                ModLogger.Log("[BoostEffect] Failed to broadcast intro: " + ex);
            }
            return text;
        }

        public void Apply(string? triggererName = null)
        {
            string resolvedTriggerer = triggererName ?? PhotonNetwork.LocalPlayer?.NickName ?? string.Empty;
            if (string.IsNullOrEmpty(resolvedTriggerer))
            {
                ModLogger.Log("[BoostEffect] No triggerer name available.");
                return;
            }

            Character target = MainCameraMovement.specCharacter;
            if (target == null)
            {
                var local = Character.localCharacter;
                if (local != null && local.Ghost != null)
                {
                    target = local.Ghost.m_target;
                }
            }

            if (target == null && RandomEventManager.AllowBKeyForTesting)
            {
                target = Character.localCharacter;
            }

            if (target == null)
            {
                ModLogger.Log("[BoostEffect] No spectated player found to boost.");
                return;
            }

            var pv = target.photonView;
            var owner = pv != null ? pv.Owner : null;
            if (pv == null || owner == null)
            {
                ModLogger.Log("[BoostEffect] Spectated player has no PhotonView/Owner; cannot send RPC.");
                return;
            }

            int victimId = owner.ActorNumber;
            Apply(resolvedTriggerer, victimId);
        }

        public void Apply(string triggererName, int victimId)
        {
            if (string.IsNullOrEmpty(triggererName)) return;
            if (PhotonNetwork.LocalPlayer == null || PhotonNetwork.LocalPlayer.NickName != triggererName) return;

            // Strict: require a prepared value; do not reroll locally (prevents desync with intro)
            if (!EffectIntroHandler.Consume(Id, triggererName, out _, out float modifier))
            {
                ModLogger.Log("[BoostEffect] No prepared intro value found; aborting to avoid desync.");
                return;
            }

            var players = PlayerHandler.GetAllPlayerCharacters();
            var victim = players.FirstOrDefault(p => p != null && p.photonView != null && p.photonView.Owner != null && p.photonView.Owner.ActorNumber == victimId);
            if (victim == null)
            {
                ModLogger.Log($"[BoostEffect] Victim with actor id {victimId} not found locally on triggerer.");
                return;
            }

            float totalTime = 10f;
            float drowsyOnEnd = modifier * 0.5f;

            if (Mathf.Approximately(modifier, 0f))
            {
                return;
            }

            try
            {
                victim.photonView.RPC("RPCA_Chaos_AddAffliction", victim.photonView.Owner, modifier, modifier, totalTime, drowsyOnEnd);

                if (modifier < 0f)
                {
                    float minMod = Mathf.Min(SensitivityDebuffMinModifier, 0f);
                    float clamped = Mathf.Clamp(modifier, minMod, 0f);
                    float t = Mathf.InverseLerp(minMod, 0f, clamped);
                    float sensPercent = Mathf.Lerp(SensitivityDebuffMinPercent, SensitivityDebuffMaxPercent, t);

                    float ragMinMod = Mathf.Min(RagdollDebuffMinModifier, 0f);
                    float ragClamped = Mathf.Clamp(modifier, ragMinMod, 0f);
                    float ragT = Mathf.InverseLerp(ragMinMod, 0f, ragClamped);
                    float ragdollPercent = Mathf.Lerp(RagdollDebuffMinPercent, RagdollDebuffMaxPercent, ragT);

                    victim.photonView.RPC("RPCA_Chaos_StartDrunkUI", victim.photonView.Owner, totalTime, sensPercent, ragdollPercent);
                }
            }
            catch (Exception ex)
            {
                ModLogger.Log("[BoostEffect] Failed to request affliction/drunk UI on victim: " + ex);
            }
        }
    }
}

