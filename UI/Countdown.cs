using TMPro;
using UnityEngine;
using DG.Tweening;
using Chaos.Utils;
using UnityEngine.UI;

namespace Chaos.UI
{
    public class Countdown : MonoBehaviour
    {
        public CanvasGroup counterGroup = null!;
        public TextMeshProUGUI counter = null!;

        float backgroundAlpha = 0f;
        Image? backgroundImage;

        // Lava intro refs
        GameObject? _lavaIntroGO;
        CanvasGroup? _lavaIntroCG;
        TextMeshProUGUI? _lavaIntroTMP;
        Animator? _lavaIntroAnimator;
        RectTransform? _lavaIntroRT;

        // Scaling for cloned counter
        public static bool ScaleWithResolution = true;
        public static Vector2 ReferenceResolution = new Vector2(2560, 1440);
        private static float ScalePx(float px)
        {
            if (!ScaleWithResolution) return px;
            float refH = Mathf.Max(1f, ReferenceResolution.y);
            return px * (Screen.height / refH);
        }

        public static float CounterHorizontalCenterOffsetNormalized = 0f;

        // Intro placement (normalized, single fixed position)
        public static Vector2 LavaNormalizedAnchor = new Vector2(0.5f, 0.40f);
        public static Vector2 LavaPivot = new Vector2(0.5f, 0f);
        public static Vector2 LavaSize = new Vector2(400f, 120f);

        // Text sizing
        public static bool LavaAutoSize = true;
        public static float LavaFontSizeMin = 80f;
        public static float LavaFontSizeMax = 160f;
        public static float LavaFontSizeDefault = 100f;

        // Runtime setter to adjust the numbers' horizontal offset and apply it immediately if the counter exists.
        public static void SetCounterHorizontalCenterOffset(float normalized)
        {
            CounterHorizontalCenterOffsetNormalized = Mathf.Clamp(normalized, -0.45f, 0.45f);
            var inst = Object.FindFirstObjectByType<Countdown>(FindObjectsInactive.Include);
            if (inst != null) inst.ApplyCounterHorizontalOffset();
        }

        private void EnsureCounterExists()
        {
            if (counter != null) return;

            EndgameCounter? originalCounter = FindFirstObjectByType<EndgameCounter>();
            if (originalCounter == null)
            {
                ModLogger.Log("[Countdown] Original EndgameCounter not found!");
                return;
            }

            var groupClone = Instantiate(originalCounter.counterGroup.gameObject, originalCounter.counterGroup.transform.parent);
            if (groupClone == null)
            {
                ModLogger.Log("[Countdown] Failed to clone counter group!");
                return;
            }
            groupClone.name = "ModCounterGroup";

            var imgs = groupClone.GetComponentsInChildren<Image>(true);
            if (imgs != null && imgs.Length > 0)
            {
                Image? bg = null;
                foreach (var im in imgs)
                {
                    if (im == null) continue;
                    var nm = im.gameObject.name.ToLowerInvariant();
                    if (nm.Contains("bg") || nm.Contains("background") || nm.Contains("panel"))
                    {
                        bg = im;
                        break;
                    }
                }
                if (bg == null) bg = imgs[0];
                if (bg != null)
                {
                    backgroundImage = bg;
                    var col = bg.color;
                    col.a = Mathf.Clamp01(backgroundAlpha);
                    bg.color = col;
                }
            }

            counter = groupClone.GetComponentInChildren<TextMeshProUGUI>();
            if (counter == null)
            {
                ModLogger.Log("[Countdown] Cloned counter has no TMP!");
                try { Destroy(groupClone); } catch { }
                return;
            }

            try
            {
                counter.textWrappingMode = TextWrappingModes.NoWrap;
                counter.overflowMode = TextOverflowModes.Overflow;
            }
            catch { }

            var rt = counter.rectTransform;
            if (rt != null)
            {
                // Anchor at center-bottom and size the width area we expect to use
                rt.anchorMin = new Vector2(0.5f, 0f);
                rt.anchorMax = new Vector2(0.5f, 0f);
                rt.sizeDelta = new Vector2(ScalePx(400f), rt.sizeDelta.y);
                rt.anchoredPosition = new Vector2(0, ScalePx(140f));
            }

            counter.transform.localScale = Vector3.one * 1.2f;
            counter.gameObject.SetActive(true);

            counterGroup = groupClone.GetComponent<CanvasGroup>();
            if (counterGroup != null)
            {
                counterGroup.alpha = 1f;
                counterGroup.gameObject.SetActive(true);
            }

            // Apply initial horizontal offset to the numbers (does not affect intro message)
            ApplyCounterHorizontalOffset();
        }

        // Apply the current CounterHorizontalCenterOffsetNormalized to the countdown numbers (if available).
        private void ApplyCounterHorizontalOffset()
        {
            if (counter == null) return;
            var rt = counter.rectTransform; if (rt == null) return;

            // Offset in pixels relative to center. Using screen width keeps behavior consistent across resolutions.
            float xOffsetPx = Mathf.Round(Screen.width * CounterHorizontalCenterOffsetNormalized);
            rt.anchoredPosition = new Vector2(xOffsetPx, rt.anchoredPosition.y);
        }

        public void UpdateCounter(int value)
        {
            EnsureCounterExists();
            if (counter == null || counterGroup == null) return;

            // Re-apply offset in case it was changed at runtime before this update
            ApplyCounterHorizontalOffset();

            counterGroup.gameObject.SetActive(true);
            counterGroup.DOFade(1f, 0.25f);

            counter.color = Color.white;
            counter.text = value.ToString();
            counter.transform.localScale = Vector3.one * 1.2f;

            counter.transform.DOScale(1f, 0.25f).SetEase(Ease.OutCubic);
            counter.DOFade(1f, 0.25f).SetEase(Ease.OutCubic);
        }

        public void ShowFinalMessage(string message, float duration = 2f)
        {
            EnsureCounterExists();

            // Hide countdown digits
            if (counterGroup != null)
            {
                counterGroup.DOKill();
                counterGroup.alpha = 0f;
                counterGroup.gameObject.SetActive(false);
            }

            var gm = GUIManager.instance;
            if (gm != null && gm.lavaRises != null && gm.hudCanvas != null)
            {
                EnsureLavaIntro(gm);

                if (_lavaIntroGO != null && _lavaIntroTMP != null && _lavaIntroCG != null && _lavaIntroRT != null)
                {
                    _lavaIntroGO.transform.SetParent(gm.hudCanvas.transform, false);

                    // Fixed normalized anchor placement (no extra offsets) for the intro line
                    var a = Clamp01(LavaNormalizedAnchor);

                    _lavaIntroRT.anchorMin = a;
                    _lavaIntroRT.anchorMax = a;
                    _lavaIntroRT.pivot = LavaPivot;
                    _lavaIntroRT.anchoredPosition = Vector2.zero;

                    if (LavaSize.x > 0f || LavaSize.y > 0f)
                        _lavaIntroRT.sizeDelta = new Vector2(
                            LavaSize.x > 0f ? LavaSize.x : _lavaIntroRT.sizeDelta.x,
                            LavaSize.y > 0f ? LavaSize.y : _lavaIntroRT.sizeDelta.y
                        );

                    _lavaIntroRT.SetAsLastSibling();

                    // TMP
                    var tmp = _lavaIntroTMP;
                    var tr = tmp.rectTransform;
                    tr.anchorMin = Vector2.zero;
                    tr.anchorMax = Vector2.one;
                    tr.offsetMin = Vector2.zero;
                    tr.offsetMax = Vector2.zero;
                    tr.pivot = new Vector2(0.5f, 0.5f);
                    tr.anchoredPosition = Vector2.zero;

                    tmp.text = message ?? string.Empty;
                    tmp.alignment = TextAlignmentOptions.Center;
                    tmp.textWrappingMode = TextWrappingModes.NoWrap;
                    tmp.overflowMode = TextOverflowModes.Overflow;

                    tmp.enableAutoSizing = LavaAutoSize;
                    if (LavaAutoSize)
                    {
                        tmp.fontSizeMin = Mathf.Max(1f, LavaFontSizeMin);
                        tmp.fontSizeMax = Mathf.Max(tmp.fontSizeMin, LavaFontSizeMax);
                    }
                    else
                    {
                        tmp.fontSize = Mathf.Max(1f, LavaFontSizeDefault);
                    }

                    // Animation reset
                    if (_lavaIntroAnimator != null)
                    {
                        _lavaIntroAnimator.Rebind();
                        _lavaIntroAnimator.Update(0f);
                        _lavaIntroAnimator.Play(0, 0, 0f);
                    }

                    // Mute audio
                    var audios = _lavaIntroGO.GetComponentsInChildren<AudioSource>(true);
                    foreach (var a2 in audios)
                    {
                        if (a2 == null) continue;
                        a2.Stop();
                        a2.mute = true;
                        a2.playOnAwake = false;
                        a2.volume = 0f;
                    }

                    // Keep LocalNotice window "up"
                    LocalNotice.BeginOrExtendCountdownWindow(LocalNotice.IntroWindowSeconds);

                    // Fade out after duration
                    _lavaIntroCG.interactable = false;
                    _lavaIntroCG.blocksRaycasts = false;
                    _lavaIntroCG.ignoreParentGroups = true;
                    _lavaIntroCG.DOKill();
                    _lavaIntroCG.alpha = 1f;
                    _lavaIntroCG.DOFade(0f, 0.4f).SetDelay(duration);

                    return;
                }
            }

            // Fallback (if lava UI not available)
            if (counter == null || counterGroup == null) return;

            counter.text = message;
            counter.color = Color.red;

            float availableWidth = counter.rectTransform.sizeDelta.x;
            if (availableWidth <= 0f)
                availableWidth = ScalePx(400f);

            Vector2 preferred = counter.GetPreferredValues(message, 10000f, counter.rectTransform.rect.height);
            float preferredWidth = preferred.x;
            const float minScale = 0.6f;
            float scale = 1f;
            if (preferredWidth > availableWidth && preferredWidth > 0f)
                scale = Mathf.Max(minScale, availableWidth / preferredWidth);
            counter.transform.localScale = Vector3.one * scale;

            counterGroup.alpha = 0f;
            counterGroup.DOFade(1f, 0.25f);
            counterGroup.DOFade(0f, 0.5f).SetDelay(duration);
        }

        private void EnsureLavaIntro(GUIManager gm)
        {
            if (_lavaIntroGO != null && _lavaIntroTMP != null && _lavaIntroCG != null && _lavaIntroRT != null) return;

            var clone = Instantiate(gm.lavaRises);
            clone.name = "CHAOS_LavaIntro_Clone";
            clone.transform.SetParent(gm.hudCanvas.transform, false);
            clone.SetActive(true);

            _lavaIntroGO = clone;
            _lavaIntroTMP = clone.GetComponentInChildren<TextMeshProUGUI>(true);
            _lavaIntroAnimator = clone.GetComponentInChildren<Animator>(true);
            _lavaIntroCG = clone.GetComponent<CanvasGroup>() ?? clone.AddComponent<CanvasGroup>();
            _lavaIntroRT = clone.GetComponent<RectTransform>();

            _lavaIntroCG.interactable = false;
            _lavaIntroCG.blocksRaycasts = false;
            _lavaIntroCG.ignoreParentGroups = true;
        }

        public void Disable()
        {
            if (counterGroup == null) return;
            counterGroup.DOFade(0f, 0.25f).OnComplete(() =>
            {
                try { counterGroup.gameObject.SetActive(false); } catch { }
            });
        }

        public void SetBackgroundAlpha(float a)
        {
            backgroundAlpha = Mathf.Clamp01(a);
            if (backgroundImage != null)
            {
                var c = backgroundImage.color;
                c.a = backgroundAlpha;
                backgroundImage.color = c;
            }
        }

        static Vector2 Clamp01(Vector2 v) => new Vector2(Mathf.Clamp01(v.x), Mathf.Clamp01(v.y));
    }
}
