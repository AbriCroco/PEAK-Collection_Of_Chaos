using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.Rendering.Universal;
using UnityEngine.UI;
using Photon.Pun;
using Chaos.Utils;
using Chaos.Gameplay;

namespace Chaos.UI
{
    // Drunk effect UI:
    // - Scales look input via SensitivityScaler for the duration, then eases back to 1 at the end.
    // - Adds a ragdoll-camera blend that is independent from sensitivity:
    //     Begin: blends in from 0 -> (RagdollCamMaxStrength * RagdollStrengthFactor)
    //     Hold: maintains the target blend
    //     Recovery: fades ragdoll from current -> 0 with the same easing used for sensitivity
    public sealed class DrunkEffectUI : MonoBehaviourPunCallbacks
    {
        public struct Config
        {
            public float DurationSeconds;
            public float SensitivityScale;     // 0..1
            public int InitialBlinkCount;
            public float InitialCloseSeconds;
            public float InitialOpenSeconds;
            public float InitialLastOpenToHalfSeconds;
            public float HalfOpenFraction;         // 0..1
            public float EndHalfToCloseSeconds;    // half -> closed
            public float EndCloseToFullSeconds;    // closed -> full open
            public float SensitivityCurveIndex;    // smaller -> slower start, faster finish

            public bool EnableGhostOverlayTrail;
            public float GhostOverlayAlpha;
            public float GhostOverlayLifetime;
            public float GhostOverlayInterval;
            public int GhostOverlayMax;

            public bool EnableRagdollCamDuringDrunk;
            public float RagdollCamMaxStrength;     // 0..1 cap per effect
            public float RagdollCamBlendInSeconds;  // seconds to blend in at start
            public float RagdollStrengthFactor;     // 0..1, provided by Boost from negative modifier
        }

        public static Config Default => new Config
        {
            DurationSeconds = 10f,
            SensitivityScale = 0.30f,

            InitialBlinkCount = 2,
            InitialCloseSeconds = 0.13f,
            InitialOpenSeconds = 0.13f,
            InitialLastOpenToHalfSeconds = 1.00f,

            HalfOpenFraction = 0.37f,

            EndHalfToCloseSeconds = 0.26f,
            EndCloseToFullSeconds = 2.00f,
            SensitivityCurveIndex = 0.6f,

            EnableGhostOverlayTrail = false,
            GhostOverlayAlpha = 0.20f,
            GhostOverlayLifetime = 0.60f,
            GhostOverlayInterval = 0.22f,
            GhostOverlayMax = 5,

            EnableRagdollCamDuringDrunk = true,
            RagdollCamMaxStrength = 0.35f,
            RagdollCamBlendInSeconds = 0.35f,
            RagdollStrengthFactor = 1f,
        };

        static DrunkEffectUI? _instance;

        public static void StartLocal(Config? cfg = null)
        {
            try
            {
                Ensure();
                _instance?.Run(cfg ?? Default);
            }
            catch (Exception ex) { ModLogger.Log("[DrunkEffectUI] StartLocal exception: " + ex); }
        }

        public static void StopLocal()
        {
            if (_instance == null) return;
            _instance.StopEffect();
        }

        static void Ensure()
        {
            if (_instance != null) return;
            var go = GameObject.Find("Chaos_DrunkEffectUI") ?? new GameObject("Chaos_DrunkEffectUI");
            UnityEngine.Object.DontDestroyOnLoad(go);
            _instance = go.GetComponent<DrunkEffectUI>() ?? go.AddComponent<DrunkEffectUI>();
        }

        // Runtime config/state
        Config cfg;
        bool running;
        float effectElapsed;

        enum Phase { BeginBlinking, Hold, EndHalfToClose, EndCloseToFull, Done }
        Phase phase;
        float phaseT;
        int blinksCompleted;
        enum BlinkSub { Close, OpenToOpen }
        BlinkSub blinkSub;

        float aperture = 1f;

        EyeBlinkController? eye;
        bool eyeWasEnabled;
        ScriptableRendererFeature? eyeFeature;
        Material? eyeMat;

        float finalOpeningTotalSeconds;
        float finalOpeningElapsed;
        float sensStart;
        float sensNow;

        // Ragdoll cam
        float ragTargetMax;
        float ragBlendInElapsed;
        float ragStartAtEnd;

        // Ghost overlay (optional)
        float nextGhostOverlayAt;
        Canvas? ghostCanvas;
        readonly List<RawImage> ghosts = new List<RawImage>(8);
        readonly List<float> ghostEndTimes = new List<float>(8);

        void Awake()
        {
            UnityEngine.Object.DontDestroyOnLoad(gameObject);
            SceneManager.sceneLoaded += OnSceneLoaded;
        }

        void OnDestroy()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
        }

        void OnSceneLoaded(Scene s, LoadSceneMode m)
        {
            try { StopEffect(); } catch { }
        }

        public override void OnLeftRoom() { try { StopEffect(); } catch { } }
        public override void OnJoinedRoom() { try { StopEffect(); } catch { } }

        void Run(Config c)
        {
            cfg = c;
            running = true;
            effectElapsed = 0f;
            finalOpeningElapsed = 0f;

            phase = cfg.InitialBlinkCount > 0 ? Phase.BeginBlinking : Phase.Hold;
            blinkSub = BlinkSub.Close;
            phaseT = 0f;
            blinksCompleted = 0;
            aperture = 1f;

            // Sensitivity
            sensStart = Mathf.Clamp01(cfg.SensitivityScale);
            sensNow = sensStart;
            SensitivityScaler.Begin(sensStart);

            // Ragdoll: independent target
            ragTargetMax = Mathf.Clamp01(cfg.RagdollCamMaxStrength) * Mathf.Clamp01(cfg.RagdollStrengthFactor);
            ragBlendInElapsed = 0f;
            ragStartAtEnd = 0f;
            if (cfg.EnableRagdollCamDuringDrunk)
                DrunkRagdollCam.Begin(0f); // start from 0 to avoid a pop

            finalOpeningTotalSeconds = Mathf.Max(0.0001f, cfg.EndCloseToFullSeconds);
            finalOpeningElapsed = 0f;

            if (!TryTakeoverEyeBlink())
            {
                ModLogger.Log("[DrunkEffectUI] EyeBlinkController not found; abort UI.");
                running = false;
                return;
            }
        }

        void StopEffect()
        {
            if (!running) return;
            running = false;

            SensitivityScaler.End();
            DrunkRagdollCam.End();
            RestoreEyeBlink();
            CleanupGhostOverlay();

            phase = Phase.Done;
        }

        void Update()
        {
            if (!running) return;

            float dt = Time.deltaTime;
            effectElapsed += dt;

            switch (phase)
            {
                case Phase.BeginBlinking:
                    UpdateBeginBlinks(dt);
                    if (cfg.EnableRagdollCamDuringDrunk)
                    {
                        ragBlendInElapsed += dt;
                        float inT = cfg.RagdollCamBlendInSeconds <= 0f ? 1f
                                   : Mathf.Clamp01(ragBlendInElapsed / Mathf.Max(0.0001f, cfg.RagdollCamBlendInSeconds));
                        DrunkRagdollCam.SetMinBlend(Mathf.Lerp(0f, ragTargetMax, inT));
                    }
                    if (blinksCompleted >= Mathf.Max(0, cfg.InitialBlinkCount))
                    {
                        aperture = Mathf.Clamp01(cfg.HalfOpenFraction);
                        phase = Phase.Hold;
                        phaseT = 0f;
                    }
                    break;

                case Phase.Hold:
                    ApplyEyeBlink(aperture);
                    if (cfg.EnableRagdollCamDuringDrunk)
                    {
                        ragBlendInElapsed += dt;
                        float inT = cfg.RagdollCamBlendInSeconds <= 0f ? 1f
                                   : Mathf.Clamp01(ragBlendInElapsed / Mathf.Max(0.0001f, cfg.RagdollCamBlendInSeconds));
                        DrunkRagdollCam.SetMinBlend(Mathf.Lerp(0f, ragTargetMax, inT));
                    }
                    if (effectElapsed >= Mathf.Max(0.01f, cfg.DurationSeconds))
                    {
                        finalOpeningElapsed = 0f;
                        sensStart = sensNow;

                        phase = Phase.EndHalfToClose;
                        phaseT = 0f;
                    }
                    break;

                case Phase.EndHalfToClose:
                    {
                        phaseT += dt;
                        float dur = Mathf.Max(0.0001f, cfg.EndHalfToCloseSeconds);
                        float t = Mathf.Clamp01(phaseT / dur);
                        aperture = Mathf.Lerp(Mathf.Clamp01(cfg.HalfOpenFraction), 0f, t);
                        ApplyEyeBlink(aperture);

                        if (t >= 1f)
                        {
                            phase = Phase.EndCloseToFull;
                            phaseT = 0f;
                            finalOpeningElapsed = 0f;

                            // Capture current ragdoll blend as the starting point for fade-out
                            ragStartAtEnd = DrunkRagdollCam.CurrentMinBlend;
                        }
                        break;
                    }

                case Phase.EndCloseToFull:
                    {
                        phaseT += dt;
                        finalOpeningElapsed += dt;
                        float t = Mathf.Clamp01(finalOpeningElapsed / finalOpeningTotalSeconds);
                        float eased = EaseCurve(t, cfg.SensitivityCurveIndex);

                        // Eye opening and sensitivity ramp
                        aperture = Mathf.Lerp(0f, 1f, eased);
                        ApplyEyeBlink(aperture);

                        sensNow = Mathf.Lerp(sensStart, 1f, eased);
                        SensitivityScaler.SetMultiplier(sensNow);

                        // Ragdoll fades from captured value to 0 with the same easing
                        if (cfg.EnableRagdollCamDuringDrunk)
                        {
                            float s = Mathf.Lerp(ragStartAtEnd, 0f, eased);
                            DrunkRagdollCam.SetMinBlend(s);
                        }

                        if (t >= 1f) { StopEffect(); return; }
                        break;
                    }

                case Phase.Done:
                    return;
            }

            HandleGhostOverlay();
            UpdateGhostOverlayFade();
        }

        void UpdateBeginBlinks(float dt)
        {
            if (blinksCompleted >= Mathf.Max(0, cfg.InitialBlinkCount)) return;
            phaseT += dt;

            if (blinkSub == BlinkSub.Close)
            {
                float dur = Mathf.Max(0.001f, cfg.InitialCloseSeconds);
                float t = Mathf.Clamp01(phaseT / dur);
                aperture = Mathf.Lerp(1f, 0f, t);
                ApplyEyeBlink(aperture);
                if (t >= 1f) { blinkSub = BlinkSub.OpenToOpen; phaseT = 0f; }
            }
            else
            {
                float dur = (blinksCompleted == cfg.InitialBlinkCount - 1)
                    ? Mathf.Max(0.001f, cfg.InitialLastOpenToHalfSeconds)
                    : Mathf.Max(0.001f, cfg.InitialOpenSeconds);

                float t = Mathf.Clamp01(phaseT / dur);
                float half = Mathf.Clamp01(cfg.HalfOpenFraction);
                float target = (blinksCompleted == cfg.InitialBlinkCount - 1) ? half : 1f;
                aperture = Mathf.Lerp(0f, target, t);
                ApplyEyeBlink(aperture);
                if (t >= 1f)
                {
                    blinksCompleted++;
                    phaseT = 0f;
                    if (blinksCompleted < Mathf.Max(0, cfg.InitialBlinkCount))
                        blinkSub = BlinkSub.Close;
                }
            }
        }

        static float EaseCurve(float t, float index)
        {
            index = Mathf.Max(0.01f, index);
            float p = 1f + (1f / index);
            return Mathf.Pow(Mathf.Clamp01(t), p);
        }

        bool TryTakeoverEyeBlink()
        {
            try
            {
                var local = Character.localCharacter;
                if (local == null) return false;

                eye = local.GetComponentInChildren<EyeBlinkController>(includeInactive: true);
                if (eye == null) return false;

                var ch = eye.GetComponentInParent<Character>();
                if (ch == null || !ch.IsLocal) return false;

                eyeMat = eye.eyeBlinkMaterial;
                var rd = eye.rend;
                if (rd == null || eyeMat == null) return false;

                eyeFeature = null;
                foreach (var f in rd.rendererFeatures)
                {
                    if (f != null && f.name == "Eye Blink") { eyeFeature = f; break; }
                }
                if (eyeFeature == null) return false;

                eyeWasEnabled = eye.enabled;
                eye.enabled = false; // prevent built-in blinking

                try { eyeFeature.SetActive(true); } catch { }
                TrySetEyeOpen(1f);

                return true;
            }
            catch { return false; }
        }

        void RestoreEyeBlink()
        {
            TrySetEyeOpen(1f);
            try { eyeFeature?.SetActive(false); } catch { }
            try
            {
                if (eye != null)
                {
                    eye.enableEyeBlink = false;
                    eye.eyeOpenValue = 1f;
                    eye.enabled = eyeWasEnabled;
                }
            }
            catch { }
        }

        void ApplyEyeBlink(float openFrac)
        {
            if (eyeFeature == null || eyeMat == null) return;
            try { eyeFeature.SetActive(true); } catch { }
            TrySetEyeOpen(openFrac);
        }

        void TrySetEyeOpen(float openFrac)
        {
            if (eyeMat == null) return;
            float v = Mathf.Clamp01(openFrac);
            try { eyeMat.SetFloat("_EyeOpen", v); } catch { }
        }

        // Optional ghost overlay (unchanged)
        void HandleGhostOverlay()
        {
            if (!cfg.EnableGhostOverlayTrail) return;
            if (Time.time < nextGhostOverlayAt) return;
            nextGhostOverlayAt = Time.time + Mathf.Max(0.05f, cfg.GhostOverlayInterval);
            StartCoroutine(CaptureGhostOverlay(cfg.GhostOverlayAlpha, cfg.GhostOverlayLifetime));
        }

        System.Collections.IEnumerator CaptureGhostOverlay(float alpha, float lifetime)
        {
            yield return new WaitForEndOfFrame();

            if (ghostCanvas == null)
            {
                var go = new GameObject("Chaos_Drunk_GhostCanvas");
                UnityEngine.Object.DontDestroyOnLoad(go);
                ghostCanvas = go.AddComponent<Canvas>();
                ghostCanvas.renderMode = RenderMode.ScreenSpaceOverlay;
                go.AddComponent<CanvasScaler>();
                go.AddComponent<GraphicRaycaster>();
            }

            var quadGo = new GameObject("Chaos_Drunk_Ghost");
            quadGo.transform.SetParent(ghostCanvas.transform, false);
            var img = quadGo.AddComponent<RawImage>();
            img.color = new Color(1f, 1f, 1f, 0f);
            var rt = img.rectTransform;
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
            rt.pivot = new Vector2(0.5f, 0.5f);

            Texture2D? tex = null;
            try { tex = ScreenCapture.CaptureScreenshotAsTexture(); } catch { }
            if (tex == null) { Destroy(quadGo); yield break; }

            img.texture = tex;
            ghosts.Add(img);
            ghostEndTimes.Add(Time.time + Mathf.Max(0.05f, lifetime));

            while (ghosts.Count > Mathf.Max(1, cfg.GhostOverlayMax))
            {
                var old = ghosts[0];
                ghosts.RemoveAt(0);
                ghostEndTimes.RemoveAt(0);
                try { if (old.texture != null) Destroy(old.texture); } catch { }
                try { Destroy(old.gameObject); } catch { }
            }

            float t = 0f, inDur = 0.06f, targetA = Mathf.Clamp01(alpha);
            while (t < inDur)
            {
                t += Time.deltaTime;
                float a = Mathf.Lerp(0f, targetA, t / inDur);
                img.color = new Color(1f, 1f, 1f, a);
                yield return null;
            }
            img.color = new Color(1f, 1f, 1f, targetA);
        }

        void UpdateGhostOverlayFade()
        {
            if (ghosts.Count == 0) return;
            float now = Time.time;

            for (int i = ghosts.Count - 1; i >= 0; i--)
            {
                var img = ghosts[i];
                if (img == null)
                {
                    ghosts.RemoveAt(i);
                    ghostEndTimes.RemoveAt(i);
                    continue;
                }

                float endT = ghostEndTimes[i];
                float remain = Mathf.Max(0f, endT - now);
                float a = (endT <= now) ? 0f
                    : Mathf.Clamp01(remain / Mathf.Max(0.05f, cfg.GhostOverlayLifetime)) * Mathf.Clamp01(cfg.GhostOverlayAlpha);
                var c = img.color; c.a = a; img.color = c;

                if (endT <= now)
                {
                    ghosts.RemoveAt(i);
                    ghostEndTimes.RemoveAt(i);
                    try { if (img.texture != null) Destroy(img.texture); } catch { }
                    try { Destroy(img.gameObject); } catch { }
                }
            }
        }

        void CleanupGhostOverlay()
        {
            for (int i = ghosts.Count - 1; i >= 0; i--)
            {
                var img = ghosts[i];
                try { if (img != null && img.texture != null) Destroy(img.texture); } catch { }
                try { if (img != null) Destroy(img.gameObject); } catch { }
            }
            ghosts.Clear();
            ghostEndTimes.Clear();

            if (ghostCanvas != null)
            {
                try { Destroy(ghostCanvas.gameObject); } catch { }
                ghostCanvas = null;
            }
        }
    }
}
