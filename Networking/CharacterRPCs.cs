using System;
using System.Collections;
using System.Reflection;
using UnityEngine;
using Zorro.Core;
using Photon.Pun;
using Chaos.Networking;
using Chaos.Utils;

public class CharacterRPCs : MonoBehaviourPun
{
    static readonly FieldInfo UnPassOutCalledField = typeof(Character).GetField("UnPassOutCalled", BindingFlags.Instance | BindingFlags.NonPublic);

    [PunRPC]
    public void RPCA_Chaos_AddStatus(int statusType, float amount)
    {
        try
        {
            var ch = GetComponent<Character>();
            var aff = ch?.refs?.afflictions;
            if (aff != null) aff.AddStatus((CharacterAfflictions.STATUSTYPE)statusType, amount, fromRPC: true);
        }
        catch (System.Exception ex)
        {
            ModLogger.Log("[ChaosCharacterRPCs] RPCA_Chaos_AddStatus exception: " + ex);
        }
    }
    [PunRPC]
    public void RPCA_Chaos_AjustStatus(int statusType, float amount)
    {
        try
        {
            var ch = GetComponent<Character>();
            var aff = ch?.refs?.afflictions;
            if (aff != null) aff.AdjustStatus((CharacterAfflictions.STATUSTYPE)statusType, amount, fromRPC: true);
        }
        catch (System.Exception ex)
        {
            ModLogger.Log("[ChaosCharacterRPCs] RPCA_Chaos_AddjustStatus exception: " + ex);
        }
    }

    [PunRPC]
    public void RPCA_Chaos_EquipSlotRPC(byte slotID)
    {
        var character = GetComponent<Character>();

        if (slotID == 179)
        {
            character.refs.items.EquipSlot(Optionable<byte>.None);
        }
        else
        {
            character.refs.items.EquipSlot(Optionable<byte>.Some(slotID));
        }
    }

    [PunRPC]
    public void RPCA_Chaos_AddAffliction(float moveSpeedMod, float climbSpeedMod, float totalTime, float drowsyOnEnd)
    {
        try
        {
            var ch = GetComponent<Character>();
            if (ch == null) return;

            // Only apply on the owner client
            if (!this.photonView.IsMine)
            {
                // Not the owner - ignore
                return;
            }

            var aff = new Peak.Afflictions.Affliction_FasterBoi
            {
                moveSpeedMod = moveSpeedMod,
                climbSpeedMod = climbSpeedMod,
                totalTime = totalTime,
                drowsyOnEnd = drowsyOnEnd
            };

            ch.refs.afflictions.AddAffliction(aff, fromRPC: true);
            ModLogger.Log($"[ChaosCharacterRPCs] RPCA_Chaos_AddAffliction applied to {ch.name} (moveMod={moveSpeedMod:F2}).");

            if (ch.IsLocal && moveSpeedMod <= 0f)
            {
                try { GUIManager.instance.EndEnergyDrink(); } catch { }
            }
        }
        catch (System.Exception ex)
        {
            ModLogger.Log("[ChaosCharacterRPCs] RPCA_Chaos_AddAffliction exception: " + ex);
        }
    }

    [PunRPC]
    public void RPCA_Chaos_IntroMessage(byte effectId, string triggererName, string previewText, float numericValue, float ttlSeconds)
    {
        try
        {
            EffectIntroHandler.Set(effectId, triggererName, previewText, numericValue, ttlSeconds);
        }
        catch (Exception ex)
        {
            ModLogger.Log("[ChaosCharacterRPCs] RPCA_Chaos_IntroMessage exception: " + ex);
        }
    }

    [PunRPC]
    public void RPCA_Chaos_ClearScoutmasterHUD()
    {
        try
        {
            int strengthId = Shader.PropertyToID("_Strength");
            int grainId = Shader.PropertyToID("_GrainMult");
            var all = UnityEngine.Object.FindObjectsByType<Scoutmaster>(FindObjectsSortMode.None);

            foreach (var sm in all)
            {
                if (sm != null)
                {
                    sm.SetCurrentTarget(null);
                }


                if (sm?.mat != null)
                {
                    sm.mat.SetFloat(strengthId, 0f);
                    sm.mat.SetFloat(grainId, 0f);
                }
            }
            ModLogger.Log($"[ChaosCharacterRPCs] RPCA_Chaos_ClearScoutmasterHUD: cleared");
        }
        catch (System.Exception ex)
        {
            ModLogger.Log("[ChaosCharacterRPCs] RPCA_Chaos_ClearScoutmasterHUD exception: " + ex);
        }
    }

    [PunRPC]
    public void RPCA_Chaos_PassOut(float seconds)
    {
        try
        {
            var ch = GetComponent<Character>();
            if (this.photonView.IsMine)
            {
                ForcedPassoutManager.Add(this.photonView.ViewID);
                try
                {
                    if (ch != null && ch.data != null)
                    {
                        ch.data.passedOut = true;
                        ch.refs.stats.justPassedOut = true;
                        ch.data.lastPassedOut = Time.time;
                        GlobalEvents.OnCharacterPassedOut(ch);
                        if (UnPassOutCalledField != null)
                            UnPassOutCalledField.SetValue(ch, true);
                    }
                }
                catch { }
                StartCoroutine(ManageForcedPassout(seconds));
            }
            else
            {
                try { this.photonView.RPC("RPCA_PassOut", RpcTarget.Others); } catch { }
            }
        }
        catch { }
    }

    [PunRPC]
    public void RPCA_Chaos_StartDrunkUI(float duration, float sensitivityScale, float ragdollStrength)
    {
        if (!this.photonView.IsMine) return;

        var cfg = Chaos.UI.DrunkEffectUI.Default;
        cfg.DurationSeconds = Mathf.Max(0.1f, duration);
        cfg.SensitivityScale = Mathf.Clamp01(sensitivityScale);

        cfg.RagdollStrengthFactor = Mathf.Clamp01(ragdollStrength);

        Chaos.UI.DrunkEffectUI.StartLocal(cfg);
    }

    private IEnumerator ManageForcedPassout(float seconds)
    {
        var ch = GetComponent<Character>();
        if (ch == null) yield break;

        // small initial delay so the game applies passout locally
        float delay = Mathf.Min(0.9f, Mathf.Max(0f, seconds));
        yield return new WaitForSeconds(delay);

        // let one RPCA_PassOut run, then reassert the suppression flag
        try
        {
            ForcedPassoutManager.AllowOnce(this.photonView.ViewID);
            this.photonView.RPC("RPCA_PassOut", RpcTarget.All);
            if (UnPassOutCalledField != null) UnPassOutCalledField.SetValue(ch, true);
        }
        catch { }

        // camera lock for the remaining time (simple)
        float remaining = Mathf.Max(0f, seconds - delay);
        var cam = FindFirstObjectByType<Camera>();
        var lockComp = cam != null ? (cam.GetComponent<PassoutCameraLock>() ?? cam.gameObject.AddComponent<PassoutCameraLock>()) : null;
        if (lockComp != null)
        {
            try { lockComp.LockForDuration(remaining, 0.15f); } catch { }
        }

        bool skipFinalUnpass = false;
        bool keepCameraLockedOnEnd = false;
        float endTime = Time.time + remaining;

        while (Time.time < endTime)
        {
            if (ch == null || ch.data == null) break;

            // If player dies during passout
            if (ch.data.dead)
            {
                bool otherAlive = AnyOtherAlive(ch);
                if (otherAlive)
                {
                    // drop suppression so the game handles ghost camera; unlock and exit
                    try { ForcedPassoutManager.Remove(this.photonView.ViewID); } catch { }
                    try { if (UnPassOutCalledField != null) UnPassOutCalledField.SetValue(ch, false); } catch { }
                    try { lockComp?.Unlock(); } catch { }
                    yield break;
                }
                else
                {
                    // round ending (no other alive) -> keep passout; skip unpass and keep camera locked
                    skipFinalUnpass = true;
                    keepCameraLockedOnEnd = true;
                    // Wait out the remainder without doing anything else
                }
            }

            yield return null;
        }

        // End: clean up and optionally unpass
        if (!keepCameraLockedOnEnd)
        {
            try { lockComp?.Unlock(); } catch { }
        }

        // stop suppressing either way
        try { ForcedPassoutManager.Remove(this.photonView.ViewID); } catch { }
        try { if (ch != null && UnPassOutCalledField != null) UnPassOutCalledField.SetValue(ch, false); } catch { }

        if (!skipFinalUnpass && ch != null && ch.data != null && ch.data.fullyPassedOut && !ch.data.dead)
        {
            // let everyone wake normally if still fully passed out and alive
            try { this.photonView.RPC("RPCA_UnPassOut", RpcTarget.All); } catch { }
        }

        // local helpers
        static bool AnyOtherAlive(Character self)
        {
            try
            {
                var all = PlayerHandler.GetAllPlayerCharacters();
                if (all == null) return false;
                foreach (var c in all)
                {
                    if (c == null || c == self || c.data == null) continue;
                    if (!c.data.dead) return true;
                }
            }
            catch { }
            return false;
        }
    }
}
