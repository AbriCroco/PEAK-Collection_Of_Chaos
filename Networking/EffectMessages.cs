using System.Collections.Generic;
using UnityEngine;
using Photon.Pun;
using Photon.Realtime;
using ExitGames.Client.Photon;
using Chaos.UI;
using Chaos.Effects;
using Chaos.Utils;

namespace Chaos.Networking
{
    public class EffectMessages : MonoBehaviourPun, IOnEventCallback
    {
        private readonly Dictionary<byte, IEffect> _effects = new();

        private void Awake()
        {
            RegisterEffect(new BoostEffect());
            RegisterEffect(new HungerEffect());
            RegisterEffect(new CleanseEffect());
            RegisterEffect(new ScoutmasterEffect());
            RegisterEffect(new CatchDynamiteEffect());
        }

        private void RegisterEffect(IEffect effect)
        {
            if (effect == null) return;
            if (!_effects.ContainsKey(effect.Id))
            {
                _effects.Add(effect.Id, effect);
            }
        }

        private void OnEnable()
        {
            PhotonNetwork.AddCallbackTarget(this);
        }

        private void OnDisable()
        {
            PhotonNetwork.RemoveCallbackTarget(this);
        }

        public void OnEvent(EventData photonEvent)
        {
            if (photonEvent.Code != EventCodes.StartCountdown) return;

            object[]? data = photonEvent.CustomData as object[];
            if (data == null || data.Length < 4) return;

            int seconds = (int)data[0];
            string message = (string)data[1];
            byte effectId = (byte)data[2];
            string triggerer = (string)data[3];
            int[]? targetActorIds = data.Length >= 5 ? data[4] as int[] : null;
            int victimId = data.Length >= 6 ? (int)data[5] : -1;
            int keyCodeInt = data.Length >= 7 ? (int)data[6] : -1;

            // NEW: per-key cooldown seconds appended by RandomEventManager (fallback to countdown if missing)
            float perKeyCooldownSeconds = 0f;
            if (data.Length >= 8)
            {
                try { perKeyCooldownSeconds = System.Convert.ToSingle(data[7]); } catch { perKeyCooldownSeconds = 0f; }
            }
            if (perKeyCooldownSeconds <= 0f) perKeyCooldownSeconds = seconds; // safe fallback

            string formattedMessage = message ?? "";
            if (!string.IsNullOrEmpty(triggerer))
            {
                formattedMessage = formattedMessage.Replace("@triggerer@", triggerer);
            }

            bool shouldDisplay = targetActorIds == null || targetActorIds.Length == 0 || System.Array.IndexOf(targetActorIds, PhotonNetwork.LocalPlayer.ActorNumber) >= 0;

            if (shouldDisplay)
            {
                HandleCountdown(seconds, formattedMessage, effectId, triggerer, targetActorIds, victimId);
            }

            // Apply cooldowns for everyone only if we have a valid keyCode
            if (keyCodeInt != -1)
            {
                try
                {
                    var key = (UnityEngine.KeyCode)keyCodeInt;

                    // Use the configured per-key cooldown (not the countdown duration)
                    Chaos.Manager.RandomEventManager.SetGlobalCooldownForKey(key, perKeyCooldownSeconds);

                    // Start global cooldown (duration is enforced by RandomEventManager’s globalCooldown)
                    Chaos.Manager.RandomEventManager.SetGlobalCooldown();
                }
                catch (System.Exception ex)
                {
                    ModLogger.Log("[EffectMessages] Failed to set cooldowns from event: " + ex);
                }
            }
        }

        private Coroutine _runningCountdown = null!;
        private void HandleCountdown(int seconds, string finalMessage, byte effectId, string triggerer, int[]? targetActorIds, int victimId)
        {
            var counter = FindFirstObjectByType<Countdown>();
            if (counter == null)
            {
                ModLogger.Log("[EffectMessages] Countdown object not found! Aborting display and applying effect now.");
                ApplyEffectNow(effectId, triggerer, victimId);
                return;
            }

            if (_runningCountdown != null)
                StopCoroutine(_runningCountdown);

            _runningCountdown = StartCoroutine(RunCountdown(counter, seconds, finalMessage, effectId, triggerer, targetActorIds, victimId));
        }

        private System.Collections.IEnumerator RunCountdown(Countdown counter, int seconds, string finalMessage, byte effectId, string triggerer, int[]? targetActorIds, int victimId)
        {
            bool amTriggerer = !string.IsNullOrEmpty(triggerer) && PhotonNetwork.LocalPlayer != null && PhotonNetwork.LocalPlayer.NickName == triggerer;
            bool triggererShouldPassOut = amTriggerer && effectId == EffectIds.Cleanse;

            if (triggererShouldPassOut)
            {
                Character ch = Character.localCharacter ?? FindFirstObjectByType<Character>();
                if (ch != null && ch.photonView != null)
                {
                    try
                    {
                        float secondsF = (float)seconds;
                        ch.photonView.RPC("RPCA_Chaos_PassOut", RpcTarget.All, secondsF);
                    }
                    catch (System.Exception ex)
                    {
                        ModLogger.Log("[EffectMessages] Failed to request pass-out: " + ex);
                    }
                }
                else
                {
                    ModLogger.Log("[EffectMessages] Triggerer Character not available locally - cannot request passout.");
                }
            }

            for (int i = seconds; i > 0; i--)
            {
                counter.UpdateCounter(i);
                yield return new WaitForSeconds(1f);
            }

            if (!string.IsNullOrEmpty(finalMessage))
            {
                counter.ShowFinalMessage(finalMessage);
            }
            else
            {
                counter.Disable();
            }

            ApplyEffectNow(effectId, triggerer, victimId);
        }

        private void ApplyEffectNow(byte effectId, string triggerer, int victimId)
        {
            if (!_effects.TryGetValue(effectId, out var effect))
            {
                ModLogger.Log($"[EffectMessages] Effect {effectId} not found!");
                return;
            }

            try
            {
                if (effect is CleanseEffect cleanse)
                    cleanse.Apply(triggerer, victimId);
                else
                    effect.Apply(triggerer);
            }
            catch (System.Exception ex)
            {
                ModLogger.Log($"[EffectMessages] Exception while applying effect {effectId}: {ex}");
            }
        }
    }
}
