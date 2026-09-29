using System.Collections.Concurrent;
using System.Text;
using UnityEngine;
using Object = UnityEngine.Object;

using FusionRpg.Injector.Host;

namespace FusionRpg.Injector;

/// <summary>Dump almanac pedia text (PlantInfo/ZombieInfo + visible TMP) for later FE promote.</summary>
public static class AlmanacTextCapture
{
    static readonly ConcurrentDictionary<string, byte> Sent = new(StringComparer.OrdinalIgnoreCase);

    public static void TryCapture(string side, int typeId, PlantType? plantType, ZombieType? zombieType,
        bool includeWindowText = true)
    {
        if (typeId < 0) return;
        if (side is not ("plant" or "zombie")) return;
        var key = side + ":" + typeId;
        if (!Sent.TryAdd(key, 0)) return;

        try
        {
            var fields = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            var sources = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            if (side == "plant" && plantType is { } pt)
            {
                Put(fields, sources, "enumName", GameDumps.EnumName(pt), "Enum.ToString");
                Put(fields, sources, "displayName", GameDumps.PlantName(pt), "Lawnf.GetName");
                CapturePlant(pt, fields, sources);
            }
            else if (side == "zombie" && zombieType is { } zt)
            {
                Put(fields, sources, "enumName", GameDumps.EnumName(zt), "Enum.ToString");
                Put(fields, sources, "displayName", GameDumps.ZombieName(zt), "Lawnf.GetName");
                CaptureZombie(zt, fields, sources);
            }

            if (includeWindowText)
                CaptureWindowTmp(side, fields, sources);

            if (fields.Count == 0)
            {
                Sent.TryRemove(key, out _);
                try { RpgHost.Log.Warning($"[almanac-text] empty {side}/{typeId}"); } catch { }
                return;
            }

            RpgHost.Client?.EnqueueAlmanacTextDump(side, typeId, fields, sources);
            try { RpgHost.Log.Info($"[almanac-text] queued {side}/{typeId} fields={fields.Count}"); } catch { }
        }
        catch (Exception ex)
        {
            Sent.TryRemove(key, out _);
            try { RpgHost.Log.Warning("[almanac-text] capture failed: " + ex.Message); } catch { }
        }
    }

    static void CapturePlant(PlantType pt, Dictionary<string, string?> fields, Dictionary<string, string> sources)
    {
        try
        {
            var map = AlmanacDataLoader.plantDatas;
            if (map == null || !map.ContainsKey(pt)) return;
            var info = map[pt];
            if (info == null) return;
            try { Put(fields, sources, "name", info.name, "PlantInfo.name"); } catch { }
            try { Put(fields, sources, "info", info.info, "PlantInfo.info"); } catch { }
            try { Put(fields, sources, "cost", info.cost, "PlantInfo.cost"); } catch { }
            try { Put(fields, sources, "seedType", info.seedType.ToString(), "PlantInfo.seedType"); } catch { }
        }
        catch (Exception ex)
        {
            try { RpgHost.Log.Warning("[almanac-text] PlantInfo: " + ex.Message); } catch { }
        }
    }

    static void CaptureZombie(ZombieType zt, Dictionary<string, string?> fields, Dictionary<string, string> sources)
    {
        try
        {
            var map = AlmanacDataLoader.zombieDatas;
            if (map == null || !map.ContainsKey(zt)) return;
            var info = map[zt];
            if (info == null) return;
            try { Put(fields, sources, "name", info.name, "ZombieInfo.name"); } catch { }
            try { Put(fields, sources, "info", info.info, "ZombieInfo.info"); } catch { }
            try { Put(fields, sources, "introduce", info.introduce, "ZombieInfo.introduce"); } catch { }
            try { Put(fields, sources, "theZombieType", ((int)info.theZombieType).ToString(), "ZombieInfo.theZombieType"); } catch { }
        }
        catch (Exception ex)
        {
            try { RpgHost.Log.Warning("[almanac-text] ZombieInfo: " + ex.Message); } catch { }
        }
    }

    static void CaptureWindowTmp(string side, Dictionary<string, string?> fields, Dictionary<string, string> sources)
    {
        try
        {
            GameObject? root = null;
            string prefix;
            if (side == "plant")
            {
                var win = Object.FindObjectOfType<AlmanacPlantWindow>();
                root = win != null ? win.gameObject : null;
                prefix = "AlmanacPlantWindow";
            }
            else
            {
                var win = Object.FindObjectOfType<AlmanacZombieWindow>();
                root = win != null ? win.gameObject : null;
                prefix = "AlmanacZombieWindow";
            }
            if (root == null) return;
            ScoopTmp(root, prefix, fields, sources);
        }
        catch (Exception ex)
        {
            try { RpgHost.Log.Warning("[almanac-text] window TMP: " + ex.Message); } catch { }
        }
    }

    static void ScoopTmp(GameObject root, string prefix, Dictionary<string, string?> fields, Dictionary<string, string> sources)
    {
        var tmps = root.GetComponentsInChildren<TextMeshProUGUI>(true);
        if (tmps == null) return;
        var i = 0;
        foreach (var tmp in tmps)
        {
            if (tmp == null) { i++; continue; }
            string? text = null;
            try { text = tmp.text; } catch { }
            if (string.IsNullOrWhiteSpace(text)) { i++; continue; }
            var go = tmp.gameObject != null ? tmp.gameObject.name : ("tmp" + i);
            var lowered = go.ToLowerInvariant();
            if (lowered.Contains("name") && !fields.ContainsKey("uiName"))
                Put(fields, sources, "uiName", text, prefix + "." + go);
            else if (lowered.Contains("cost") && !fields.ContainsKey("uiCost"))
                Put(fields, sources, "uiCost", text, prefix + "." + go);
            else if ((lowered.Contains("intro") || lowered.Contains("introduce")) && !fields.ContainsKey("uiIntroduce"))
                Put(fields, sources, "uiIntroduce", text, prefix + "." + go);
            else if ((lowered.Contains("info") || lowered.Contains("desc")) && !fields.ContainsKey("uiInfo"))
                Put(fields, sources, "uiInfo", text, prefix + "." + go);
            else
                Put(fields, sources, "ui_" + Sanitize(go) + "_" + i, text, prefix + "." + go);
            i++;
        }
    }

    static void Put(Dictionary<string, string?> fields, Dictionary<string, string> sources, string key, string? value, string source)
    {
        if (string.IsNullOrWhiteSpace(key) || value == null) return;
        var v = value.Trim();
        if (v.Length == 0) return;
        if (v.Length > 8000) v = v[..8000];
        if (fields.ContainsKey(key) && !string.IsNullOrWhiteSpace(fields[key])) return;
        fields[key] = v;
        sources[key] = source;
    }

    static string Sanitize(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (var c in name)
        {
            if (char.IsLetterOrDigit(c) || c is '_' or '-') sb.Append(c);
            else sb.Append('_');
        }
        var s = sb.ToString().Trim('_');
        return string.IsNullOrEmpty(s) ? "field" : s;
    }
}
