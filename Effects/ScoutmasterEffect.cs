using HarmonyLib;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Photon.Pun;
using Chaos.Utils;
using Chaos.Networking;

namespace Chaos.Effects
{
    public class ScoutmasterEffect : IEffect
    {
        public byte Id => EffectIds.Scoutmaster;
        public string IntroMessage => "It's coming!!!";
        public int CountdownSeconds => 0;

        private const float InitialSeconds = 60f;
        private const float RetargetCooldownSeconds = 10f;
        private const float TooCloseDistance = 5.0f;

        public void Apply(string? triggererName = null)
        {
            if (!PhotonNetwork.IsMasterClient)
            {
                return;
            }

            if (!Scoutmaster.GetPrimaryScoutmaster(out var scoutmaster) || scoutmaster == null)
            {
                ModLogger.Log("[ScoutmasterEffect] No Scoutmaster instance found in scene. Cannot apply scoutmaster effect.");
                return;
            }

            // select random alive player
            var all = PlayerHandler.GetAllPlayerCharacters();
            var candidates = new List<Character>();
            foreach (var c in all)
            {
                if (c == null) continue;
                if (c.isBot) continue;
                if (c.data.dead) continue;
                if (c.data.fullyPassedOut) continue;
                candidates.Add(c);
            }

            var victim = candidates[Random.Range(0, candidates.Count)];
            if (victim == null)
            {
                ModLogger.Log("[ScoutmasterEffect] Random victim null.");
                return;
            }

            scoutmaster.SetCurrentTarget(victim, InitialSeconds);

            scoutmaster.StartCoroutine(MonitorTargetAndRetarget(scoutmaster));
        }

        private IEnumerator MonitorTargetAndRetarget(Scoutmaster scoutmaster)
        {
            if (scoutmaster == null)
            {
                scoutmaster?.SetCurrentTarget(null);
                yield break;
            }

            float endTime = Time.time + InitialSeconds;
            float lastRetargetTime = -Mathf.Infinity;

            Character scoutChar = scoutmaster.GetComponent<Character>();

            while (Time.time < endTime)
            {
                float retargetTime = endTime - Time.time;

                if (scoutmaster == null || scoutChar == null)
                    yield break;

                var currentTarget = scoutmaster.currentTarget;
                if (currentTarget == null)
                {
                    yield return new WaitForSeconds(1f);
                    continue;
                }

                Character? closest = null;
                float closestDist = float.MaxValue;

                // Measure distance to the scoutmaster itself (not distance to the current target)
                List<Character> all = PlayerHandler.GetAllPlayerCharacters();
                foreach (var c in all)
                {
                    if (c == null) continue;
                    if (c.isBot) continue;
                    if (c.data.dead) continue;
                    if (c.data.fullyPassedOut) continue;
                    if (c == currentTarget) continue;

                    float d = Vector3.Distance(c.Center, scoutChar.Center);
                    if (d < closestDist)
                    {
                        closestDist = d;
                        closest = c;
                    }
                }

                if (closest != null && closestDist <= TooCloseDistance && Time.time - lastRetargetTime > RetargetCooldownSeconds)
                {
                    AccessTools.Field(typeof(Scoutmaster), "_currentTarget").SetValue(scoutmaster, closest);

                    lastRetargetTime = Time.time;
                    yield return new WaitForSeconds(RetargetCooldownSeconds);
                    continue;
                }

                yield return new WaitForSeconds(1f);
            }
            var AllScoutMasters = UnityEngine.Object.FindObjectsByType<Scoutmaster>(FindObjectsSortMode.None);
            foreach (var ScoutMaster in AllScoutMasters)
            {
                if (ScoutMaster != null)
                {
                    ScoutMaster.SetCurrentTarget(null);
                }
            }
        }

        private void BroadcastClearHUD()
        {

            try
            {
                PhotonView? pv = Character.localCharacter?.photonView;

                if (pv == null)
                {
                    var all = PlayerHandler.GetAllPlayerCharacters();
                    if (all != null)
                    {
                        foreach (var c in all)
                        {
                            if (c?.photonView != null && c.photonView.IsMine)
                            {
                                pv = c.photonView;
                                break;
                            }
                        }
                    }
                }

                if (pv != null)
                {
                    pv.RPC("RPCA_Chaos_ClearScoutmasterHUD", RpcTarget.All);
                }
                else
                {
                    Chaos.Utils.ModLogger.Log("[ScoutmasterEffect] No PhotonView available to broadcast HUD clear.");
                }
            }
            catch (System.Exception ex)
            {
                Chaos.Utils.ModLogger.Log("[ScoutmasterEffect] Failed to broadcast HUD clear: " + ex);
            }
        }
        private void OnDisable()
        {
            BroadcastClearHUD();
        }

        private void OnDestroy()
        {
            BroadcastClearHUD();
        }

        private void EndScoutmaster()
        {
            BroadcastClearHUD();
        }
    }
}
