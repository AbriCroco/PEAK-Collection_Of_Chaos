using System.Collections;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Chaos.Manager;

namespace Chaos.UI
{
    // LocalNotice:
    // - Single bottom band layout.
    // - Animates "up" (taller band) while countdown window is active.
    // - Optional slight horizontal center offset to shift the band a bit left/right.
    // - Smooth anchor transitions using animation curve.
    // - Fade in (with optional wobble) and fade out after duration or EarlyFade().
    public sealed class LocalNotice : MonoBehaviour
    {
        private static LocalNotice? _inst;
        private RandomEventManager? _randomEventManager;

        // Components
        private Canvas? _canvas;
        private CanvasGroup? _group;
        private RectTransform? _groupRT;
        private TextMeshProUGUI? _tmp;

        // Coroutines
        private Coroutine? _showCR;
        private Coroutine? _layoutCR;
        private Coroutine? _fadeCR;

        // State
        private bool _built;
        private bool _styleAdopted;
        private bool _lastWindowActive;

        // Countdown window (time-based)
        private static float _windowUntilUnscaled = -1f;

        // Display / layout
        public static float DefaultDurationSeconds = 3f;
        public static int SortingOrder = 100;
        public static bool ReplaceActiveOnShow = true;

        // Scaling
        public static bool ApplyScalerSettings = true;
        public static Vector2 ReferenceResolution = new Vector2(2560, 1440);
        public static float MatchWidthOrHeight = 1f;

        // Band layout (normal)
        public static float HorizontalPaddingNormalized = 0.15f;
        public static float BandHeightNormalized = 0.505f;

        // Band layout (window active)
        public static float HorizontalPaddingNormalizedDuringCountdown = 0.15f;
        public static float BandHeightNormalizedDuringCountdown = 0.73f;

        // Horizontal center offset (normalized): positive shifts slightly to the right, negative to the left.
        // Example: 0.03f shifts by ~3% of the screen width.
        public static float HorizontalCenterOffsetNormalized = 0f;

        // Text
        public static bool OverrideTextColor = true;
        public static Color TextColor = new Color32(0xEF, 0x1A, 0x28, 0xFF);
        public static bool AutoFit = true;
        public static int AutoFitMinSize = 75;
        public static int AutoFitMaxSize = 90;

        // Animations
        public static bool EnableWobble = true;
        public static float WobbleDuration = 0.25f;
        public static float WobbleAmplitude = 10f;
        public static float WobbleFrequency = 28f;
        public static float FadeOutSeconds = 0.3f;
        public static float TransitionUpDurationSeconds = 0.25f;
        public static float TransitionDownDurationSeconds = 0.85f;
        public static AnimationCurve TransitionCurve = AnimationCurve.EaseInOut(0, 0, 1, 1);

        // Window timing knobs (used externally)
        public static float CountdownWindowPaddingSeconds = 2.0f;
        public static float IntroWindowSeconds = 3.9f;

        private void Awake()
        {
            _randomEventManager = FindFirstObjectByType<RandomEventManager>();
        }

        // Public API ----------------------------------------------------------

        public static void Show(string message, float seconds = -1f)
        {
            var inst = Ensure();
            inst._randomEventManager?.StopCooldownCoroutine();
            inst.InternalShow(message, seconds > 0f ? seconds : DefaultDurationSeconds);
        }

        public static void ShowGlobal(string message, float seconds = -1f) => Show(message, seconds);
        public static void ShowTop(string message, float seconds = -1f) => Show(message, seconds); // compatibility alias

        public static void UpdateActiveText(string message)
        {
            var inst = Ensure();
            if (inst._tmp == null) return;
            inst._tmp.text = message ?? string.Empty;
        }

        public static void ClearActive()
        {
            var inst = _inst;
            if (inst == null) return;

            if (inst._showCR != null) { try { inst.StopCoroutine(inst._showCR); } catch { } inst._showCR = null; }
            if (inst._layoutCR != null) { try { inst.StopCoroutine(inst._layoutCR); } catch { } inst._layoutCR = null; }
            if (inst._fadeCR != null) { try { inst.StopCoroutine(inst._fadeCR); } catch { } inst._fadeCR = null; }

            if (inst._group != null) inst._group.alpha = 0f;
            if (inst._tmp != null) inst._tmp.text = string.Empty;

            inst._randomEventManager?.StopCooldownCoroutine();
        }

        public static void ForceReacquireStyle()
        {
            var inst = _inst;
            if (inst == null) return;
            inst._styleAdopted = false;
        }

        // Adjust the horizontal center offset at runtime and animate to the new position if visible.
        public static void SetHorizontalCenterOffset(float normalized)
        {
            // Clamp aggressively to keep within screen with typical paddings
            HorizontalCenterOffsetNormalized = Mathf.Clamp(normalized, -0.45f, 0.45f);
            if (_inst != null && _inst._group != null && _inst._group.alpha > 0.01f)
                _inst.StartWindowTransition(IsCountdownWindowActive());
        }

        public static void BeginOrExtendCountdownWindow(float seconds)
        {
            float until = Time.unscaledTime + Mathf.Max(0f, seconds);
            if (until > _windowUntilUnscaled) _windowUntilUnscaled = until;
            _inst?.RequestWindowStateCheck(immediate: false);
        }

        public static void ForceEndCountdownWindow()
        {
            _windowUntilUnscaled = -1f;
            _inst?.RequestWindowStateCheck(immediate: true);
        }

        public static void EarlyFade(float delaySeconds = 0f)
        {
            var inst = _inst;
            if (inst == null || inst._group == null) return;
            if (inst._fadeCR != null) return;
            if (inst._group.alpha <= 0f) return;
            inst._fadeCR = inst.StartCoroutine(inst.FadeOutThenClear(delaySeconds));
        }

        public static bool IsCountdownWindowActive() => Time.unscaledTime < _windowUntilUnscaled;

        // Internals ----------------------------------------------------------

        private static LocalNotice Ensure()
        {
            if (_inst != null) return _inst;
            var root = GameObject.Find("Chaos_LocalNoticeCanvas") ?? new GameObject("Chaos_LocalNoticeCanvas");
            Object.DontDestroyOnLoad(root);
            _inst = root.GetComponent<LocalNotice>() ?? root.AddComponent<LocalNotice>();
            if (!_inst._built) _inst.Build();
            return _inst;
        }

        private void Build()
        {
            _canvas = gameObject.GetComponent<Canvas>() ?? gameObject.AddComponent<Canvas>();
            _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _canvas.sortingOrder = SortingOrder;

            var scaler = gameObject.GetComponent<CanvasScaler>() ?? gameObject.AddComponent<CanvasScaler>();
            if (ApplyScalerSettings)
            {
                scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
                scaler.referenceResolution = ReferenceResolution;
                scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
                scaler.matchWidthOrHeight = Mathf.Clamp01(MatchWidthOrHeight);
            }

            if (gameObject.GetComponent<GraphicRaycaster>() == null)
                gameObject.AddComponent<GraphicRaycaster>();

            var groupGO = new GameObject("Group");
            groupGO.transform.SetParent(transform, false);
            _group = groupGO.AddComponent<CanvasGroup>();
            _group.alpha = 0f;
            _group.interactable = false;
            _group.blocksRaycasts = false;
            _group.ignoreParentGroups = true;

            _groupRT = groupGO.GetComponent<RectTransform>() ?? groupGO.AddComponent<RectTransform>();

            var textGO = new GameObject("TextTMP");
            textGO.transform.SetParent(groupGO.transform, false);
            _tmp = textGO.AddComponent<TextMeshProUGUI>();
            _tmp.alignment = TextAlignmentOptions.Center;
            _tmp.textWrappingMode = TextWrappingModes.Normal;
            _tmp.overflowMode = TextOverflowModes.Overflow;
            _tmp.raycastTarget = false;

            ApplyAutoFit();

            ApplyLayoutNow(ComputeTargetLayout(IsCountdownWindowActive()));
            _lastWindowActive = IsCountdownWindowActive();

            _built = true;
        }

        private void InternalShow(string message, float seconds)
        {
            if (!_built || _canvas == null || _group == null || _groupRT == null || _tmp == null)
                Build();

            if (ReplaceActiveOnShow)
                ClearActive();

            _canvas!.sortingOrder = SortingOrder;

            if (!_styleAdopted) TryAdoptCountdownStyle();
            if (OverrideTextColor) _tmp!.color = TextColor;

            ApplyAutoFit();
            _tmp!.text = message ?? string.Empty;

            ApplyLayoutNow(ComputeTargetLayout(IsCountdownWindowActive()));
            _lastWindowActive = IsCountdownWindowActive();

            if (_showCR != null) StopCoroutine(_showCR);
            _showCR = StartCoroutine(ShowRoutine(seconds));
        }

        private IEnumerator ShowRoutine(float seconds)
        {
            if (_group == null) yield break;
            var rt = _tmp!.rectTransform;
            Vector2 basePos = rt.anchoredPosition;

            // Fade in + wobble
            float t = 0f;
            const float fadeIn = 0.15f;
            float wobbleWindow = Mathf.Max(fadeIn, EnableWobble ? WobbleDuration : 0f);

            while (t < wobbleWindow)
            {
                HandleWindowTransitionIfNeeded();
                t += Time.unscaledDeltaTime;
                float fadeT = Mathf.Clamp01(t / fadeIn);
                _group.alpha = Mathf.SmoothStep(0f, 1f, fadeT);

                if (EnableWobble && t <= WobbleDuration)
                {
                    float norm = Mathf.Clamp01(t / WobbleDuration);
                    float decay = 1f - norm;
                    float x = Mathf.Sin(t * Mathf.PI * 2f * WobbleFrequency) * WobbleAmplitude * decay;
                    rt.anchoredPosition = new Vector2(basePos.x + x, basePos.y);
                }
                yield return null;
            }
            rt.anchoredPosition = basePos;
            _group.alpha = 1f;

            // Hold
            float hold = Mathf.Max(0.01f, seconds);
            float c = 0f;
            while (c < hold)
            {
                HandleWindowTransitionIfNeeded();
                c += Time.unscaledDeltaTime;
                yield return null;
            }

            // Normal fade out
            yield return FadeOutThenClear(0f);
            _showCR = null;
        }

        private void RequestWindowStateCheck(bool immediate)
        {
            if (immediate)
            {
                StartWindowTransition(IsCountdownWindowActive());
            }
            else
            {
                HandleWindowTransitionIfNeeded();
            }
        }

        private void HandleWindowTransitionIfNeeded()
        {
            bool active = IsCountdownWindowActive();
            if (active != _lastWindowActive)
            {
                StartWindowTransition(active);
                _lastWindowActive = active;
            }
        }

        private void StartWindowTransition(bool toActive)
        {
            float dur = toActive ? TransitionUpDurationSeconds : TransitionDownDurationSeconds;
            if (_layoutCR != null) { try { StopCoroutine(_layoutCR); } catch { } _layoutCR = null; }
            _layoutCR = StartCoroutine(AnimateLayout(ComputeTargetLayout(toActive), dur));
        }

        private IEnumerator AnimateLayout(TargetLayout to, float duration)
        {
            if (_groupRT == null) yield break;

            Vector2 fromMin = _groupRT.anchorMin;
            Vector2 fromMax = _groupRT.anchorMax;
            Vector2 fromPivot = _groupRT.pivot;
            Vector2 fromSize = _groupRT.sizeDelta;

            float t = 0f;
            duration = Mathf.Max(0.001f, duration);

            while (t < duration)
            {
                t += Time.unscaledDeltaTime;
                float a = Mathf.Clamp01(t / duration);
                float e = TransitionCurve != null ? TransitionCurve.Evaluate(a) : a;

                _groupRT.anchorMin = Vector2.Lerp(fromMin, to.anchorMin, e);
                _groupRT.anchorMax = Vector2.Lerp(fromMax, to.anchorMax, e);
                _groupRT.pivot = Vector2.Lerp(fromPivot, to.pivot, e);
                if (to.useSize) _groupRT.sizeDelta = Vector2.Lerp(fromSize, to.sizeDelta, e);
                _groupRT.anchoredPosition = Vector2.zero;

                yield return null;
            }

            ApplyLayoutNow(to);
            _layoutCR = null;
        }

        private IEnumerator FadeOutThenClear(float delaySeconds)
        {
            if (_group == null) yield break;

            if (delaySeconds > 0f)
            {
                float end = Time.unscaledTime + delaySeconds;
                while (Time.unscaledTime < end) yield return null;
            }

            float outT = 0f;
            float outDur = Mathf.Max(0.01f, FadeOutSeconds);
            float start = _group.alpha;

            while (outT < outDur)
            {
                HandleWindowTransitionIfNeeded();
                outT += Time.unscaledDeltaTime;
                _group.alpha = Mathf.Lerp(start, 0f, outT / outDur);
                yield return null;
            }

            _group.alpha = 0f;
            if (_tmp != null) _tmp.text = string.Empty;
            _fadeCR = null;
        }

        // Layout ----------------------------------------------------------------

        private struct TargetLayout
        {
            public Vector2 anchorMin, anchorMax, pivot, sizeDelta;
            public bool useSize;
        }

        private TargetLayout ComputeTargetLayout(bool windowActive)
        {
            // Horizontal band width from padding
            float padX = windowActive ? HorizontalPaddingNormalizedDuringCountdown : HorizontalPaddingNormalized;
            float bandH = windowActive ? BandHeightNormalizedDuringCountdown : BandHeightNormalized;

            padX = Mathf.Clamp01(padX);
            bandH = Mathf.Clamp01(bandH);

            float width = Mathf.Clamp01(1f - 2f * padX);
            // Desired horizontal center (0.5 is true center). Allow slight offset.
            float desiredCenter = 0.5f + HorizontalCenterOffsetNormalized;

            // Compute anchors from desired center and band width
            float xMin = desiredCenter - width * 0.5f;
            float xMax = desiredCenter + width * 0.5f;

            // Keep within [0,1] by shifting as needed
            if (xMin < 0f) { float shift = -xMin; xMin += shift; xMax += shift; }
            if (xMax > 1f) { float shift = xMax - 1f; xMin -= shift; xMax -= shift; }

            // Ensure minimal width in pathological cases
            if (xMax <= xMin)
            {
                xMin = 0.49f;
                xMax = 0.51f;
            }

            // Bottom band vertically
            float yMin = 0f;
            float yMax = Mathf.Clamp01(yMin + bandH);
            if (yMax <= yMin) yMax = Mathf.Min(1f, yMin + 0.01f);

            return new TargetLayout
            {
                anchorMin = new Vector2(xMin, yMin),
                anchorMax = new Vector2(xMax, yMax),
                pivot = new Vector2(0.5f, 0.5f),
                sizeDelta = Vector2.zero,
                useSize = false
            };
        }

        private void ApplyLayoutNow(TargetLayout tl)
        {
            if (_groupRT == null) return;

            // Apply group band anchors
            _groupRT.anchorMin = tl.anchorMin;
            _groupRT.anchorMax = tl.anchorMax;
            _groupRT.pivot = tl.pivot;
            _groupRT.anchoredPosition = Vector2.zero;
            if (tl.useSize) _groupRT.sizeDelta = tl.sizeDelta;

            // CRITICAL: stretch the TMP child to fill the band (prevents "vertical" narrow text)
            if (_tmp != null)
            {
                var tr = _tmp.rectTransform;
                tr.anchorMin = new Vector2(0f, 0f);
                tr.anchorMax = new Vector2(1f, 1f);
                tr.offsetMin = Vector2.zero;
                tr.offsetMax = Vector2.zero;
                tr.pivot = new Vector2(0.5f, 0.5f);
                tr.anchoredPosition = Vector2.zero;
            }
        }

        // Text style helpers
        private void ApplyAutoFit()
        {
            if (_tmp == null) return;
            _tmp.enableAutoSizing = AutoFit;
            if (AutoFit)
            {
                _tmp.fontSizeMin = Mathf.Max(1, AutoFitMinSize);
                _tmp.fontSizeMax = Mathf.Max(_tmp.fontSizeMin, AutoFitMaxSize);
            }
        }

        private bool TryAdoptCountdownStyle()
        {
            if (_tmp == null) return false;

            TextMeshProUGUI? src = null;
            var countdown = Object.FindFirstObjectByType<Countdown>(FindObjectsInactive.Include);
            if (countdown != null && countdown.counter != null)
                src = countdown.counter;
            if (src == null)
            {
                var endgame = Object.FindFirstObjectByType<EndgameCounter>(FindObjectsInactive.Include);
                if (endgame != null)
                    src = endgame.GetComponentInChildren<TextMeshProUGUI>(true);
            }
            if (src == null) return false;

            _tmp.font = src.font;
            _tmp.fontSharedMaterial = src.fontSharedMaterial;
            _tmp.fontSize = src.fontSize;
            _tmp.fontStyle = src.fontStyle;
            _tmp.textWrappingMode = src.textWrappingMode;
            _tmp.richText = src.richText;
            _tmp.outlineWidth = src.outlineWidth;
            _tmp.outlineColor = src.outlineColor;
            _tmp.color = OverrideTextColor ? TextColor : src.color;

            _styleAdopted = true;
            return true;
        }
    }
}
