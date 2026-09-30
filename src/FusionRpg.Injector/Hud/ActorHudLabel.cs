using UnityEngine;
#if FUSIONRPG_MELON
using Il2CppTMPro;
#else
using TMPro;
#endif

namespace FusionRpg.Bridge.Hud;

/// <summary>Screen-space TMP glyph used by the actor HUD Canvas.</summary>
public sealed class ActorHudLabel
{
    readonly GameObject _go;
    readonly TextMeshProUGUI? _tmp;

    ActorHudLabel(GameObject go, TextMeshProUGUI? tmp)
    {
        _go = go;
        _tmp = tmp;
    }

    public static ActorHudLabel? Create(GameObject root, string name)
    {
        try
        {
            var go = new GameObject(name);
            go.hideFlags = HideFlags.HideAndDontSave;
            go.transform.SetParent(root.transform, false);
            var rect = go.GetComponent<RectTransform>() ?? go.AddComponent<RectTransform>();
            rect.anchorMin = new Vector2(0.5f, 1f);
            rect.anchorMax = new Vector2(0.5f, 1f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = new Vector2(64f, 24f);

            var tmp = go.AddComponent<TextMeshProUGUI>();
            if (tmp == null)
            {
                UnityEngine.Object.Destroy(go);
                return null;
            }

            TryAssignSceneFont(tmp);
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.enableWordWrapping = false;
            tmp.overflowMode = TextOverflowModes.Overflow;
            tmp.richText = false;
            tmp.raycastTarget = false;
            tmp.color = Color.white;
            go.SetActive(false);
            return new ActorHudLabel(go, tmp);
        }
        catch (Exception ex)
        {
            try { Host.RpgHost.Log.Warning("[actor-hud] screen label create: " + ex.Message); } catch { }
            return null;
        }
    }

    public bool TryPlace(float localX, float localY, string text, float fontSize, Color color)
    {
        if (_tmp == null) return false;
        try
        {
            if (_tmp.font == null) TryAssignSceneFont(_tmp);
            if (_tmp.font == null) return false;
            _go.SetActive(true);
            _tmp.text = text;
            _tmp.color = color;
            _tmp.fontSize = Mathf.Clamp(fontSize, 8f, 48f);
            var rect = _go.GetComponent<RectTransform>();
            if (rect == null) return false;
            rect.anchoredPosition = new Vector2(localX, localY);
            return true;
        }
        catch { return false; }
    }

    public void Hide()
    {
        try { _go.SetActive(false); } catch { }
    }

    static TMP_FontAsset? _cachedFont;

    static void TryAssignSceneFont(TextMeshProUGUI tmp)
    {
        if (tmp == null || tmp.font != null) return;
        if (_cachedFont != null)
        {
            tmp.font = _cachedFont;
            return;
        }

        try
        {
            var labels = UnityEngine.Object.FindObjectsOfType<TextMeshProUGUI>();
            if (labels != null)
                foreach (var label in labels)
                {
                    if (label == null || label == tmp || label.font == null) continue;
                    _cachedFont = label.font;
                    tmp.font = _cachedFont;
                    return;
                }
        }
        catch { }

        try
        {
            var labels = UnityEngine.Object.FindObjectsOfType<TextMeshPro>();
            if (labels != null)
                foreach (var label in labels)
                {
                    if (label == null || label.font == null) continue;
                    _cachedFont = label.font;
                    tmp.font = _cachedFont;
                    return;
                }
        }
        catch { }
    }
}
