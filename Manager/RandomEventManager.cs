using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Chaos.Effects;
using Chaos.Networking;
using Chaos.UI;
using Chaos.Utils;
using ExitGames.Client.Photon;
using Photon.Pun;
using Photon.Realtime;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Chaos.Manager
{
    public class RandomEventManager : MonoBehaviourPunCallbacks, IInRoomCallbacks, IOnEventCallback
    {

        // Configuration/state
        public static bool AllowBKeyForTesting = false;
        public static bool AllowMKeyForTesting = false;
        public static bool AllowVKeyForTesting = false;


        public static bool AllowScoutmasterTimer = true;
        public static bool AllowDynamiteTimer = true;
        public static bool AllowHasAtLeastTwoAliveCheck = true;
        private static bool ForceDifferentPlayerPerKey = true;
        public static RandomEventManager Instance = null!;

        // Global cooldown
        private float globalCooldown = 30f;
        private float lastGlobalPressTime = -Mathf.Infinity;

        // Global per-key cooldowns: key -> unblock time
        private static readonly Dictionary<KeyCode, float> s_globalPerKeyCooldownUntil = new();
        // Who pressed a key last (triggerer): key -> playerName
        private static readonly Dictionary<KeyCode, string> s_lastPresserByKey = new();
        private static string s_lastGlobalTriggerer = string.Empty;

        // Per-player usage tracking
        private readonly Dictionary<string, Dictionary<KeyCode, int>> playerUses = new();

        // Per-key cooldown durations (applied globally when effect is scheduled)
        private readonly Dictionary<KeyCode, float> keyCooldowns = new()
        {
            { KeyCode.T, 100f },
            { KeyCode.B, 100f },
            { KeyCode.H, 100f },
        };

        private readonly Dictionary<KeyCode, int> maxUsesPerButton = new()
        {
            { KeyCode.T, 99 },
            { KeyCode.B, 99 },
            { KeyCode.H, 99 },
        };

        private static readonly KeyCode[] s_actionKeys = { KeyCode.T, KeyCode.B, KeyCode.H };

        private static readonly KeyCode[] utilKeys = { KeyCode.M, KeyCode.U };

        private ScoutmasterTimer? scoutmasterTimer;
        private Coroutine? _cooldownNoticeCR; // live-updating 2s notices (global or per-key)

        // How long to display "0s" before early-fade
        public static float LocalNoticeZeroHoldSeconds = 0.5f;

        // ----------------------------
        // Static helpers/config toggles
        // ----------------------------

        public static void SetForceDifferentPlayerPerKey(KeyCode button, bool enforce)
        {
            ForceDifferentPlayerPerKey = enforce;
        }

        public static bool UtilKey(KeyCode key)
        {
            for (int i = 0; i < utilKeys.Length; i++)
                if (key == utilKeys[i]) return true;
            return false;
        }

        public static void SetGlobalCooldown()
        {
            if (Instance != null)
                Instance.lastGlobalPressTime = Time.time;
        }

        private static bool AnyActionKeyPressedThisFrame()
        {
            for (int i = 0; i < s_actionKeys.Length; i++)
                if (Input.GetKeyDown(s_actionKeys[i])) return true;
            return false;
        }

        private static string KeyName(KeyCode key) => key.ToString();

        private static string PrepareEffectIntroIfSupported(IEffect effect, string triggererName, float ttlSeconds = 60f)
        {
            try
            {
                var m = effect.GetType().GetMethod(
                    "PreparePreview",
                    BindingFlags.Instance | BindingFlags.Public,
                    binder: null,
                    types: new[] { typeof(string), typeof(float) },
                    modifiers: null
                );
                if (m != null)
                {
                    var result = m.Invoke(effect, new object[] { triggererName, ttlSeconds }) as string;
                    return result ?? string.Empty;
                }
            }
            catch (Exception ex)
            {
                ModLogger.Log("[RandomEventManager] PreparePreview invoke failed: " + ex);
            }
            return string.Empty;
        }

        private static bool IsPerKeyOnGlobalCooldown(KeyCode key, out float remaining)
        {
            float now = Time.time;
            if (s_globalPerKeyCooldownUntil.TryGetValue(key, out float until) && until > now)
            {
                remaining = until - now;
                return true;
            }
            remaining = 0f;
            return false;
        }

        // Gate a key press by per-key cooldown only when there is at least 1 whole second remaining.
        // Returns true only if we should block and show a cooldown notice (whole > 0).
        private static bool ShouldGatePerKeyPress(KeyCode key, out float remaining, out int wholeSeconds)
        {
            remaining = 0f;
            wholeSeconds = 0;
            if (!IsPerKeyOnGlobalCooldown(key, out remaining)) return false;

            wholeSeconds = Mathf.FloorToInt(Mathf.Max(0f, remaining));
            return wholeSeconds > 0; // treat "0s" as no longer gating the press
        }

        public static void SetGlobalCooldownForKey(KeyCode key, float seconds)
        {
            s_globalPerKeyCooldownUntil[key] = Time.time + Mathf.Max(0f, seconds);
        }

        // ----------------------------
        // Unity lifecycle
        // ----------------------------

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(this.gameObject);
                return;
            }
            Instance = this;
            DontDestroyOnLoad(this.gameObject);
        }

        private void Start()
        {
            foreach (var p in PhotonNetwork.PlayerList)
                InitPlayerData(p.NickName);

            SceneManager.sceneLoaded += OnSceneLoaded;

            var ST_go = GameObject.Find("Chaos_ScoutmasterTimer") ?? new GameObject("Chaos_ScoutmasterTimer");
            DontDestroyOnLoad(ST_go);
            scoutmasterTimer = ST_go.GetComponent<ScoutmasterTimer>() ?? ST_go.AddComponent<ScoutmasterTimer>();
        }

        private void OnDestroy()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
            scoutmasterTimer?.ResetAll();
            DynamiteManager.ClearDynamiteManager();
        }

        public override void OnEnable()
        {
            base.OnEnable();
            try { PhotonNetwork.AddCallbackTarget(this); } catch { }
        }

        public override void OnDisable()
        {
            try { PhotonNetwork.RemoveCallbackTarget(this); } catch { }
            base.OnDisable();
        }

        // ----------------------------
        // Scene/room events
        // ----------------------------
        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            ResetCooldownsAndUses();
            LocalNotice.ClearActive();
            DynamiteManager.ClearDynamiteManager();

            if (PhotonNetwork.IsMasterClient)
            {
                if (AllowScoutmasterTimer || AllowDynamiteTimer)
                {
                    scoutmasterTimer?.PassportCheckAfterLoad();
                }
            }
        }

        public override void OnLeftRoom()
        {
            base.OnLeftRoom();

            LocalNotice.ClearActive();
            ClearAllStateForNewGame();
            scoutmasterTimer?.ResetAll();
            DynamiteManager.ClearDynamiteManager();
        }

        public void OnMasterClientSwitched(Player newMasterClient)
        {
            string masterName = newMasterClient != null ? newMasterClient.name : "null";
            ModLogger.Log("[RandomEventManager] OnMasterClientSwitched: " + masterName);
            scoutmasterTimer?.OnMasterChanged(PhotonNetwork.IsMasterClient);
        }

        // ----------------------------
        // Update loop
        // ----------------------------

        private void Update()
        {
            // Within global cooldown window?
            float gcRemaining = Mathf.Max(0f, globalCooldown - (Time.time - lastGlobalPressTime));
            int gcWhole = Mathf.FloorToInt(gcRemaining);

            if (gcRemaining > 0f)
            {
                // When the user presses an action key during cooldown, show a 2s toast that updates while visible
                if (AnyActionKeyPressedThisFrame())
                {
                    // Do not show a global cooldown notice if only "0s" remains; instead, fade any existing notice.
                    if (gcWhole <= 0)
                    {
                        LocalNotice.EarlyFade(LocalNoticeZeroHoldSeconds);
                    }
                    else
                    {
                        _cooldownNoticeCR = StartCoroutine(GlobalCooldownNoticeRoutine());
                    }
                    return;
                }
            }

            // Handle keys (normal flow)
            if (Input.GetKeyDown(KeyCode.T))
            {
                var me = PlayerHandler.GetPlayerCharacter(PhotonNetwork.LocalPlayer);
                if (me != null && !me.data.dead)
                    HandleButtonPress(KeyCode.T, new CleanseEffect());
                else
                {
                    LocalNotice.Show($"Stop trying to act cool...\nYou're dead already!");
                }
            }

            if (Input.GetKeyDown(KeyCode.V))
            {
                if (AllowVKeyForTesting)
                    HandleButtonPress(KeyCode.V, new CatchDynamiteEffect());
                else
                {
                    LocalNotice.Show($"Testing for key isn't unabled!");
                }

            }


            if (Input.GetKeyDown(KeyCode.B))
            {
                var me = PlayerHandler.GetPlayerCharacter(PhotonNetwork.LocalPlayer);
                if ((me != null && me.data.dead) || AllowBKeyForTesting)
                {
                    HandleButtonPress(KeyCode.B, new BoostEffect());
                }
                else
                {
                    LocalNotice.Show($"You think you can boost yourself?\nOkay dude...");
                }
            }

            if (Input.GetKeyDown(KeyCode.H))
            {
                var me = PlayerHandler.GetPlayerCharacter(PhotonNetwork.LocalPlayer);
                if (me != null && !me.data.dead)
                    HandleButtonPress(KeyCode.H, new HungerEffect());
                else
                {
                    LocalNotice.Show($"Stop trying to act cool...\nYou're dead already!");
                }
            }

            if (Input.GetKeyDown(KeyCode.M))
            {
                if (PhotonNetwork.IsMasterClient && scoutmasterTimer != null && AllowMKeyForTesting)
                {
                    HandleButtonPress(KeyCode.M, new ScoutmasterEffect());
                }
                else
                {
                    LocalNotice.Show($"Testing for key isn't unabled!");
                }
            }

            if (Input.GetKeyDown(KeyCode.U))
            {
                Chaos.Effects.ClearScoutmasterHUD.BroadcastClearScoutmasterHUD();
            }
        }

        // ----------------------------
        // Core logic
        // ----------------------------

        private void HandleButtonPress(KeyCode button, IEffect effect)
        {
            string playerName = PhotonNetwork.LocalPlayer != null ? PhotonNetwork.LocalPlayer.NickName : "Unknown";
            InitPlayerData(playerName); // ensure tracking exists

            // Per-key global cooldown gate (show live-updating 2s notice only if >= 1 whole second remains)
            if (ShouldGatePerKeyPress(button, out float remaining, out int whole))
            {
                _cooldownNoticeCR = StartCoroutine(PerKeyCooldownNoticeRoutine(button));
                return;
            }

            // Same-player-twice rule
            if (ForceDifferentPlayerPerKey && s_lastPresserByKey.TryGetValue(button, out string lastPresser) && !string.IsNullOrEmpty(lastPresser) && lastPresser == playerName && !UtilKey(button))
            {
                LocalNotice.Show($"Let other players have fun...\nYou already pressed {KeyName(button)}.");
                return;
            }

            // Per-player max uses
            if (maxUsesPerButton.ContainsKey(button) && playerUses[playerName][button] >= maxUsesPerButton[button])
            {
                LocalNotice.Show($"You reached the max uses for {KeyName(button)}.\nYou really overdid it...");
                return;
            }

            // Try to schedule the effect; only if scheduled do we apply any cooldowns
            bool scheduled = TriggerEvent(effect, playerName, button);
            if (!scheduled) return;

            // Effect scheduled => apply cooldowns
            float perKeyDuration = keyCooldowns.TryGetValue(button, out float dur) ? Mathf.Max(0f, dur) : 0f;
            if (perKeyDuration > 0f)
                SetGlobalCooldownForKey(button, perKeyDuration);

            lastGlobalPressTime = Time.time;

            // Count uses only when effect is scheduled
            if (playerUses[playerName].ContainsKey(button))
                playerUses[playerName][button]++;
        }

        // ----------------------------
        // Timer Handlers
        // ----------------------------

        public void ScoutmasterTimerTrigger()
        {
            if (!PhotonNetwork.IsMasterClient || !HasAtLeastTwoAlive()) return;
            TriggerEvent(new ScoutmasterEffect(), "Timer", KeyCode.None);
        }

        public void DynamiteTimerTrigger()
        {
            if (!PhotonNetwork.IsMasterClient || !HasAtLeastTwoAlive()) return;
            TriggerEvent(new CatchDynamiteEffect(), "Timer", KeyCode.None);
        }


        // Returns true if the effect was actually scheduled (applies). Only then should cooldowns be set.
        private bool TriggerEvent(IEffect effect, string triggerer, KeyCode button)
        {
            if (PhotonNetwork.LocalPlayer == null)
                return false;

            // T/H require at least two alive (block if NOT at least two alive)
            if ((button == KeyCode.H || button == KeyCode.T) && !HasAtLeastTwoAlive())
            {
                LocalNotice.Show($"You thought it would be that easy?\n You're the last one so man the fuck up!!");
                return false;
            }

            if (button == KeyCode.M && !HasAtLeastTwoAlive())
            {
                return false;
            }

            int countdown = effect.CountdownSeconds;
            byte effectId = effect.Id;
            string triggererName = PhotonNetwork.LocalPlayer.NickName ?? string.Empty;
            if (string.IsNullOrEmpty(triggererName))
                return false;

            string preparedIntro = PrepareEffectIntroIfSupported(effect, triggererName, 60f);
            string intro = string.IsNullOrEmpty(preparedIntro) ? effect.IntroMessage : preparedIntro;

            int[] explicitTargets = Array.Empty<int>();
            int victimId = -1;

            if (button == KeyCode.B)
            {
                var spec = MainCameraMovement.specCharacter;

                // Solo test: fallback to self if enabled and effectively solo
                if ((spec == null || spec.photonView == null || spec.photonView.Owner == null) && AllowBKeyForTesting)
                {
                    var meChar = PlayerHandler.GetPlayerCharacter(PhotonNetwork.LocalPlayer);
                    if (meChar != null && meChar.photonView != null && meChar.photonView.Owner != null)
                    {
                        spec = meChar;
                        ModLogger.Log("[RandomEventManager] BOOST SOLO: Routing target to self for testing.");
                    }
                }

                if (spec == null || spec.photonView == null || spec.photonView.Owner == null)
                    return false;

                int triggererId = PhotonNetwork.LocalPlayer.ActorNumber;
                int targetId = spec.photonView.Owner.ActorNumber;
                explicitTargets = new[] { triggererId, targetId };
            }
            else if (button == KeyCode.T)
            {
                var targets = new List<Character>();
                var allPlayers = PlayerHandler.GetAllPlayerCharacters();
                if (allPlayers != null)
                {
                    foreach (var p in allPlayers)
                    {
                        if (p == null || p.photonView == null || p.photonView.Owner == null) continue;
                        if (p.photonView.Owner.NickName == triggererName) continue;
                        if (p.data.dead) continue;
                        targets.Add(p);
                    }
                }

                int triggererId = PhotonNetwork.LocalPlayer.ActorNumber;

                if (targets.Count == 0)
                {
                    victimId = -1;
                    explicitTargets = new[] { triggererId };

                }
                else
                {
                    var victim = targets[UnityEngine.Random.Range(0, targets.Count)];
                    victimId = victim.photonView.Owner.ActorNumber;
                    explicitTargets = new[] { triggererId, victimId };
                }
            }

            // Include per-key cooldown seconds so all clients use the configured duration
            float perKeyCooldownSeconds = 0f;
            if (keyCooldowns.TryGetValue(button, out float perDur)) perKeyCooldownSeconds = Mathf.Max(0f, perDur);

            object[] content = new object[] { countdown, intro, effectId, triggererName, explicitTargets, victimId, (int)button, perKeyCooldownSeconds };
            var sendOptions = new SendOptions { Reliability = true };
            var raiseOpts = new RaiseEventOptions { Receivers = ReceiverGroup.All };

            PhotonNetwork.RaiseEvent(EventCodes.StartCountdown, content, raiseOpts, sendOptions);
            return true;
        }

        // ----------------------------
        // Photon event syncing
        // ----------------------------

        public void OnEvent(EventData photonEvent)
        {
            if (photonEvent == null) return;

            if (photonEvent.Code == EventCodes.StartCountdown)
            {
                try
                {
                    var data = photonEvent.CustomData as object[];
                    if (data == null || data.Length < 8) return;

                    // payload: [0]=countdown, [1]=intro, [2]=effectId, [3]=triggererName, [4]=explicitTargets, [5]=victimId, [6]=buttonInt, [7]=perKeyCooldownSeconds
                    int countdownSeconds = (int)data[0];
                    var triggererName = data[3] as string;
                    int buttonInt = (int)data[6];
                    var key = (KeyCode)buttonInt;

                    // Maintain last presser
                    if (!string.IsNullOrEmpty(triggererName))
                        s_lastPresserByKey[key] = triggererName;

                    s_lastGlobalTriggerer = string.IsNullOrEmpty(triggererName) ? "Someone" : triggererName;

                    // Begin/extend the LocalNotice "countdown window" for the countdown + padding
                    LocalNotice.BeginOrExtendCountdownWindow(countdownSeconds + LocalNotice.CountdownWindowPaddingSeconds);
                }
                catch (Exception ex)
                {
                    ModLogger.Log("[RandomEventManager] OnEvent decode failed: " + ex);
                }
            }
        }

        // ----------------------------
        // Helpers
        // ----------------------------

        private static bool HasAtLeastTwoAlive()
        {
            if (AllowHasAtLeastTwoAliveCheck)
            {
                try
                {
                    int alive = 0;
                    var players = PlayerHandler.GetAllPlayerCharacters();
                    if (players != null)
                    {
                        foreach (var p in players)
                        {
                            if (p == null || p.data == null) continue;
                            if (p.data.dead) continue;
                            alive++;
                            if (alive > 1) return true;
                        }
                    }
                }
                catch { }
                return false;
            }
            else
                return true;
        }

        private void InitPlayerData(string playerName)
        {
            if (!playerUses.ContainsKey(playerName))
            {
                playerUses[playerName] = new Dictionary<KeyCode, int>
                {
                    { KeyCode.T, 0 },
                    { KeyCode.B, 0 },
                    { KeyCode.H, 0 },
                };
            }
        }

        // ----------------------------
        // Live-updating notices
        // ----------------------------

        // Global cooldown notice: shows for DefaultDurationSeconds, updates while visible, early-fades at 0
        private IEnumerator GlobalCooldownNoticeRoutine()
        {
            float window = Mathf.Max(0.1f, LocalNotice.DefaultDurationSeconds);
            float endAt = Time.time + window;

            string who = string.IsNullOrEmpty(s_lastGlobalTriggerer) ? "Someone" : s_lastGlobalTriggerer;

            float remaining = Mathf.Max(0f, globalCooldown - (Time.time - lastGlobalPressTime));
            int whole = Mathf.FloorToInt(remaining); if (whole < 0) whole = 0;

            LocalNotice.Show($"Chill, {who} just used an effect!\nRelax, climb and wait {whole}s!! Okay?", LocalNotice.DefaultDurationSeconds);

            int lastWhole = whole;

            while (Time.time < endAt && (Time.time - lastGlobalPressTime) < globalCooldown)
            {
                remaining = Mathf.Max(0f, globalCooldown - (Time.time - lastGlobalPressTime));
                whole = Mathf.FloorToInt(remaining); if (whole < 0) whole = 0;

                if (whole != lastWhole)
                {
                    LocalNotice.UpdateActiveText($"Chill, {who} just used an effect!\nRelax, climb and wait {whole}s!! Okay?");
                    lastWhole = whole;

                    if (whole == 0)
                    {
                        LocalNotice.EarlyFade(LocalNoticeZeroHoldSeconds);
                        break;
                    }
                }

                yield return null;
            }

            _cooldownNoticeCR = null;
        }

        // Per-key cooldown notice (uses last triggerer name): early-fades at 0
        private IEnumerator PerKeyCooldownNoticeRoutine(KeyCode key)
        {
            if (!s_globalPerKeyCooldownUntil.TryGetValue(key, out float until) || until <= Time.time)
            {
                _cooldownNoticeCR = null;
                yield break;
            }

            float window = Mathf.Max(0.1f, LocalNotice.DefaultDurationSeconds);
            float endAt = Time.time + window;

            float remaining = Mathf.Max(0f, until - Time.time);
            int whole = Mathf.FloorToInt(remaining); if (whole < 0) whole = 0;

            LocalNotice.Show($"Maybe let other people enjoy the game??\nBe a good boy and wait for {whole}s!", LocalNotice.DefaultDurationSeconds);

            int lastWhole = whole;

            while (Time.time < endAt && Time.time < until)
            {
                remaining = Mathf.Max(0f, until - Time.time);
                whole = Mathf.FloorToInt(remaining); if (whole < 0) whole = 0;

                if (whole != lastWhole)
                {
                    LocalNotice.UpdateActiveText($"Maybe let other people enjoy the game??\nBe a good boy and wait for {whole}s!");
                    lastWhole = whole;

                    if (whole == 0)
                    {
                        LocalNotice.EarlyFade(LocalNoticeZeroHoldSeconds);
                        break;
                    }
                }

                yield return null;
            }

            _cooldownNoticeCR = null;
        }

        // ----------------------------
        // State management
        // ----------------------------

        private void ResetCooldownsAndUses()
        {
            StopCooldownCoroutine();

            lastGlobalPressTime = -Mathf.Infinity;
            s_globalPerKeyCooldownUntil.Clear();
            s_lastPresserByKey.Clear();

            foreach (var player in PhotonNetwork.PlayerList)
            {
                string name = player.NickName;
                InitPlayerData(name);
                playerUses[name][KeyCode.T] = 0;
                playerUses[name][KeyCode.B] = 0;
                playerUses[name][KeyCode.H] = 0;
            }
        }

        private void ClearAllStateForNewGame()
        {
            playerUses.Clear();
            lastGlobalPressTime = -Mathf.Infinity;
            s_globalPerKeyCooldownUntil.Clear();
            s_lastPresserByKey.Clear();

            foreach (var player in PhotonNetwork.PlayerList)
                InitPlayerData(player.NickName);
        }

        public void StopCooldownCoroutine()
        {
            if (_cooldownNoticeCR != null)
            {
                try
                {
                    StopCoroutine(_cooldownNoticeCR);
                }
                catch { }
                _cooldownNoticeCR = null;
            }

        }
    }
}
