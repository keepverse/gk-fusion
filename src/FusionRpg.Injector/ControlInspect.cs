using System.Text.Json;
using FusionRpg.Contracts;
using FusionRpg.Injector.Host;
using Il2CppInterop.Runtime.InteropTypes;
using UnityEngine;
using FusionRpg.Core.Time;
using UObject = UnityEngine.Object;

namespace FusionRpg.Injector;

/// <summary>
/// Budgeted control-tree scan (game-control module <c>control-inspect</c>).
/// Closed type allowlist only — an open type-name resolver would be a new trust surface
/// for zero benefit (mirrors the <c>debug_call</c> and <c>UiNav</c> allowlists).
/// On-demand from the Drain case only; one full scan costs ~10ms per type
/// (<c>InjectorEntityRegistry.cs:10</c>), so this must never run per-frame or in a hook.
/// </summary>
public static class ControlInspect
{
    // Structural (not tunable): bounds main-thread scan + payload. Not the balance surface.
    internal const int InspectDefaultLimit = 50;

    /// <returns>Next cursor (last ref index as string), or null when complete.</returns>
    public static Dictionary<string, object> Inspect(string scope, int limit, string? cursor)
    {
        limit = Math.Clamp(limit, 1, InspectDefaultLimit);
        var from = 0;
        if (!string.IsNullOrWhiteSpace(cursor) && int.TryParse(cursor, out var parsed) && parsed >= 0)
            from = parsed;
        var controls = new List<Dictionary<string, object>>();
        var index = 0;
        bool Take()
        {
            if (index++ < from) return false;
            return controls.Count < limit;
        }

        if (scope is InspectScopes.Menu or InspectScopes.All)
        {
            foreach (var card in SafeFind<CardUI>())
            {
                if (card == null) continue;
                if (!Take()) break;
                var row = new Dictionary<string, object>
                {
                    ["ref"] = "c" + (index - 1),
                    ["type"] = "CardUI",
                    ["ptr"] = GameDumps.Ptr(card)
                };
                try { row["label"] = GameDumps.EnumName(card.thePlantType); } catch { }
                try { row["zombieType"] = (int)card.theZombieType; } catch { }
                controls.Add(row);
            }
            foreach (var card in SafeFind<AlmanacCardUI>())
            {
                if (card == null) continue;
                if (!Take()) break;
                var row = new Dictionary<string, object>
                {
                    ["ref"] = "c" + (index - 1),
                    ["type"] = "AlmanacCardUI",
                    ["ptr"] = GameDumps.Ptr(card)
                };
                try { row["plantType"] = (int)card.PlantType; } catch { }
                try { row["zombieType"] = (int)card.ZombieType; } catch { }
                controls.Add(row);
            }
            // Menu buttons are custom *Btn* scripts, never uGUI Button (measured live).
            // Name-based discovery needs no compile-time game type.
            foreach (var go in SafeFind<GameObject>())
            {
                if (go == null) continue;
                string? btnType = null;
                string goName = "";
                try
                {
                    goName = go.name ?? "";
                    var comps = go.GetComponents<Component>();
                    if (comps == null) continue;
                    foreach (var c in comps)
                    {
                        if (c == null) continue;
                        var real = RealTypeName(c);
                        if (real.IndexOf("Btn", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            btnType = real;
                            break;
                        }
                    }
                }
                catch { continue; }
                if (btnType == null) continue;
                if (!Take()) break;
                var brow = new Dictionary<string, object>
                {
                    ["ref"] = "c" + (index - 1),
                    ["type"] = btnType,
                    ["ptr"] = PtrHex(go),
                    ["name"] = goName,
                    ["path"] = HierarchyPath(go)
                };
                try
                {
                    var sp = ScreenPos(go);
                    if (sp != null) brow["screen"] = sp;
                }
                catch { }
                controls.Add(brow);
            }
        }
        if (scope is InspectScopes.Lawn or InspectScopes.All)
        {
            foreach (var p in SafeFind<Plant>())
            {
                if (p == null) continue;
                try
                {
                    if (p.thePlantType == PlantType.Nothing) continue;
                }
                catch { continue; }
                if (!Take()) break;
                var row = new Dictionary<string, object>
                {
                    ["ref"] = "c" + (index - 1),
                    ["type"] = "Plant",
                    ["ptr"] = GameDumps.Ptr(p)
                };
                try { row["typeId"] = (int)p.thePlantType; } catch { }
                try { row["col"] = p.thePlantColumn; row["row"] = p.thePlantRow; } catch { }
                controls.Add(row);
            }
            foreach (var z in SafeFind<Zombie>())
            {
                if (z == null) continue;
                try
                {
                    if (z.theZombieType == ZombieType.Nothing) continue;
                }
                catch { continue; }
                if (!Take()) break;
                var row = new Dictionary<string, object>
                {
                    ["ref"] = "c" + (index - 1),
                    ["type"] = "Zombie",
                    ["ptr"] = GameDumps.Ptr(z)
                };
                try { row["typeId"] = (int)z.theZombieType; } catch { }
                try { row["row"] = z.theZombieRow; } catch { }
                controls.Add(row);
            }
            foreach (var mw in SafeFind<Mower>())
            {
                if (mw == null) continue;
                if (!Take()) break;
                var row = new Dictionary<string, object>
                {
                    ["ref"] = "c" + (index - 1),
                    ["type"] = "Mower",
                    ["ptr"] = GameDumps.Ptr(mw)
                };
                try { row["typeId"] = (int)mw.theMowerType; } catch { }
                controls.Add(row);
            }
            foreach (var g in SafeFind<GridItem>())
            {
                if (g == null) continue;
                if (!Take()) break;
                var row = new Dictionary<string, object>
                {
                    ["ref"] = "c" + (index - 1),
                    ["type"] = "GridItem",
                    ["ptr"] = GameDumps.Ptr(g)
                };
                try { row["typeId"] = (int)g.theItemType; } catch { }
                try { row["col"] = g.theItemColumn; row["row"] = g.theItemRow; } catch { }
                controls.Add(row);
            }
            foreach (var b in SafeFind<Bullet>())
            {
                if (b == null) continue;
                if (!Take()) break;
                controls.Add(new Dictionary<string, object>
                {
                    ["ref"] = "c" + (index - 1),
                    ["type"] = "Bullet",
                    ["ptr"] = GameDumps.Ptr(b)
                });
            }
            foreach (var bucket in SafeFind<Bucket>())
            {
                if (bucket == null) continue;
                if (!Take()) break;
                controls.Add(new Dictionary<string, object>
                {
                    ["ref"] = "c" + (index - 1),
                    ["type"] = "Bucket",
                    ["ptr"] = GameDumps.Ptr(bucket)
                });
            }
            foreach (var pet in SafeFind<MiniPet>())
            {
                if (pet == null) continue;
                if (!Take()) break;
                controls.Add(new Dictionary<string, object>
                {
                    ["ref"] = "c" + (index - 1),
                    ["type"] = "MiniPet",
                    ["ptr"] = GameDumps.Ptr(pet)
                });
            }
        }

        var truncated = controls.Count >= limit;
        return new Dictionary<string, object>
        {
            ["scope"] = scope,
            ["controls"] = controls,
            ["truncated"] = truncated,
            ["next_cursor"] = truncated ? (index - 1).ToString() : null,
            ["note"] = truncated ? "[TRUNCATED] pass next_cursor to continue" : ""
        };
    }

    static List<T> SafeFind<T>() where T : UObject
    {
        try
        {
            var found = UObject.FindObjectsOfType<T>();
            return found != null ? new List<T>(found) : new List<T>();
        }
        catch
        {
            return new List<T>();
        }
    }

    static string? Str(JsonElement p, string name) =>
        p.ValueKind == JsonValueKind.Object && p.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.String
            ? e.GetString()
            : null;

    static int Int(JsonElement p, string name, int dflt) =>
        p.ValueKind == JsonValueKind.Object && p.TryGetProperty(name, out var e) && e.TryGetInt32(out var v) ? v : dflt;

    public static void Run(JsonElement p)
    {
        try
        {
            var scope = InspectScopes.Normalize(Str(p, "scope"));
            var payload = Inspect(scope, Int(p, "limit", InspectDefaultLimit), Str(p, "cursor"));
            if (payload.TryGetValue("controls", out var controls) &&
                controls is List<Dictionary<string, object>> list)
            {
                payload["snapshotId"] = ControlRefs.Remember(list);
            }
            if (p.TryGetProperty("tag", out var tagEl) && tagEl.ValueKind == JsonValueKind.String)
            {
                var tag = tagEl.GetString();
                if (!string.IsNullOrEmpty(tag))
                    payload["tag"] = tag!;
            }
            DebugRuntime.Emit("debug.inspect", payload);
        }
        catch (Exception ex)
        {
            CheatState.Error("debug.inspect: " + ex.Message);
        }
    }

    /// <summary>
    /// Census-first discovery: count + samples for every type on the closed roster, so an
    /// operator sees what the game actually returns before drilling in. One
    /// <c>FindObjectsOfType</c> per roster entry (~10ms each) — on-demand only.
    /// </summary>
    public static void RunCensus()
    {
        try
        {
            var entries = new List<Dictionary<string, object>>();
            CensusInto<CardUI>("CardUI", entries);
            CensusInto<AlmanacCardUI>("AlmanacCardUI", entries);
            CensusInto<Plant>("Plant", entries);
            CensusInto<Zombie>("Zombie", entries);
            CensusInto<Mower>("Mower", entries);
            CensusInto<Bullet>("Bullet", entries);
            CensusInto<GridItem>("GridItem", entries);
            CensusInto<Bucket>("Bucket", entries);
            CensusInto<MiniPet>("MiniPet", entries);
            CensusInto<CoinSun>("CoinSun", entries);
            CensusInto<CoinMoney>("CoinMoney", entries);
            CensusInto<DriverZombie>("DriverZombie", entries);
            CensusInto<Camera>("Camera", entries);
            // No Canvas entry: UnityEngine.Canvas lives in UIModule, unreferenced by the
            // injector hosts. Button/Text/Image counts below already prove UI presence.
            CensusInto<UnityEngine.UI.GraphicRaycaster>("GraphicRaycaster", entries);
            CensusInto<UnityEngine.EventSystems.EventSystem>("EventSystem", entries);
            CensusInto<UnityEngine.UI.Button>("Button", entries);
            CensusInto<UnityEngine.UI.Text>("Text", entries);
            CensusInto<UnityEngine.UI.Image>("Image", entries);
            CensusInto<TextMeshProUGUI>("TextMeshProUGUI", entries);
            CensusInto<TextMeshPro>("TextMeshPro", entries);
            CensusInto<Board>("Board", entries);
            CensusInto<UIMgr>("UIMgr", entries);
            CensusInto<Mouse>("Mouse", entries);
            CensusInto<InGameUI>("InGameUI", entries);
            DebugRuntime.Emit("debug.census", new Dictionary<string, object>
            {
                ["types"] = entries,
                ["typeCount"] = entries.Count
            });
        }
        catch (Exception ex)
        {
            CheatState.Error("debug.census: " + ex.Message);
        }
    }

    static void CensusInto<T>(string label, List<Dictionary<string, object>> entries) where T : Component
    {
        var found = SafeFind<T>();
        var samples = new List<Dictionary<string, object>>();
        foreach (var o in found)
        {
            if (o == null || samples.Count >= 3) break;
            try
            {
                samples.Add(new Dictionary<string, object>
                {
                    ["ptr"] = GameDumps.Ptr(o),
                    ["name"] = o.gameObject != null ? o.gameObject.name : ""
                });
            }
            catch { /* one bad sample never fails the census */ }
        }
        entries.Add(new Dictionary<string, object>
        {
            ["type"] = label,
            ["count"] = found.Count,
            ["samples"] = samples
        });
    }

    /// <summary>Full dump of one roster type: ptr, name, active, parent path. Capped.</summary>
    public static void RunScan(JsonElement p)
    {
        try
        {
            var type = (Str(p, "type") ?? "").Trim();
            var limit = Math.Clamp(Int(p, "limit", 200), 1, 200);
            var from = 0;
            var cursor = Str(p, "cursor");
            if (!string.IsNullOrWhiteSpace(cursor) && int.TryParse(cursor, out var parsed) && parsed >= 0)
                from = parsed;
            var objects = ResolveRoster(type);
            if (objects == null)
            {
                CheatState.Error("debug.scan: unknown type '" + type + "' (closed roster — see debug.census)");
                return;
            }
            var items = new List<Dictionary<string, object>>();
            for (var i = from; i < objects.Count && items.Count < limit; i++)
            {
                try
                {
                    var o = objects[i];
                    if (o == null) continue;
                    var go = o.gameObject;
                    items.Add(new Dictionary<string, object>
                    {
                        ["ref"] = "s" + i,
                        ["ptr"] = GameDumps.Ptr(o),
                        ["name"] = go != null ? go.name : "",
                        ["active"] = go != null && go.activeInHierarchy,
                        ["path"] = ParentPath(go)
                    });
                }
                catch { /* one bad object never fails the scan */ }
            }
            var end = from + items.Count;
            DebugRuntime.Emit("debug.scan", new Dictionary<string, object>
            {
                ["type"] = type,
                ["total"] = objects.Count,
                ["items"] = items,
                ["truncated"] = end < objects.Count,
                ["next_cursor"] = end < objects.Count ? end.ToString() : null
            });
        }
        catch (Exception ex)
        {
            CheatState.Error("debug.scan: " + ex.Message);
        }
    }

    static string ParentPath(GameObject? go)
    {
        var parts = new List<string>();
        var depth = 0;
        try
        {
            var t = go != null ? go.transform : null;
            while (t != null && depth++ < 6)
            {
                parts.Add(t.gameObject != null ? t.gameObject.name : "?");
                t = t.parent;
            }
        }
        catch { }
        parts.Reverse();
        return string.Join("/", parts);
    }

    static List<Component>? ResolveRoster(string type)
    {
        List<Component> Box<T>(List<T> items) where T : Component
        {
            var out_ = new List<Component>(items.Count);
            foreach (var o in items)
                if (o != null) out_.Add(o);
            return out_;
        }
        switch (type.Trim().ToLowerInvariant())
        {
            case "cardui": return Box(SafeFind<CardUI>());
            case "almanaccardui": return Box(SafeFind<AlmanacCardUI>());
            case "plant": return Box(SafeFind<Plant>());
            case "zombie": return Box(SafeFind<Zombie>());
            case "mower": return Box(SafeFind<Mower>());
            case "bullet": return Box(SafeFind<Bullet>());
            case "griditem": return Box(SafeFind<GridItem>());
            case "bucket": return Box(SafeFind<Bucket>());
            case "minipet": return Box(SafeFind<MiniPet>());
            case "coinsun": return Box(SafeFind<CoinSun>());
            case "coinmoney": return Box(SafeFind<CoinMoney>());
            case "driverzombie": return Box(SafeFind<DriverZombie>());
            case "camera": return Box(SafeFind<Camera>());
            case "graphicraycaster": return Box(SafeFind<UnityEngine.UI.GraphicRaycaster>());
            case "eventsystem": return Box(SafeFind<UnityEngine.EventSystems.EventSystem>());
            case "button": return Box(SafeFind<UnityEngine.UI.Button>());
            case "text": return Box(SafeFind<UnityEngine.UI.Text>());
            case "image": return Box(SafeFind<UnityEngine.UI.Image>());
            case "textmeshprougui": return Box(SafeFind<TextMeshProUGUI>());
            case "textmeshpro": return Box(SafeFind<TextMeshPro>());
            case "board": return Box(SafeFind<Board>());
            case "uimgr": return Box(SafeFind<UIMgr>());
            case "mouse": return Box(SafeFind<Mouse>());
            case "ingameui": return Box(SafeFind<InGameUI>());
            default: return null;
        }
    }

    /// <summary>
    /// Raw full-scene dump. No allowlist, no labels, no filtering, no shaping — every
    /// <c>GameObject</c> with its components, straight to JSON. This is the ground truth
    /// the shaped <c>inspect</c>/<c>census</c> paths are checked against, not the other
    /// way around. Capped (5000 objects) and rate-limited like screenshots: a big scene
    /// costs real main-thread time, reported honestly in the emit.
    /// </summary>
    public static void RunDumpAll(JsonElement p)
    {
        // Structural (not tunable): bounds the dump. Not the balance surface.
        const int DumpMaxObjects = 5000;
        try
        {
            var tag = ScreenshotCapture.SanitizeTag(Str(p, "tag"));
            var sw = System.Diagnostics.Stopwatch.StartNew();
            GameObject[]? all = null;
            try { all = UObject.FindObjectsOfType<GameObject>(); }
            catch (Exception ex)
            {
                CheatState.Error("debug.dump-all: enumerate failed: " + ex.Message);
                return;
            }
            if (all == null)
            {
                CheatState.Error("debug.dump-all: enumerate returned null");
                return;
            }
            var sb = new System.Text.StringBuilder(Math.Min(all.Length, DumpMaxObjects) * 220 + 256);
            sb.Append("{\"tag\":\"").Append(tag).Append("\",\"takenAtUtc\":\"")
              .Append(ServerClock.UtcNowDateTime.ToString("o")).Append("\",\"objects\":[");
            var wrote = 0;
            var total = 0;
            foreach (var go in all)
            {
                if (go == null) continue;
                total++;
                if (wrote >= DumpMaxObjects) continue;
                try
                {
                    if (wrote > 0) sb.Append(',');
                    sb.Append("{\"ptr\":\"").Append(PtrHex(go)).Append('"');
                    sb.Append(",\"name\":").Append(Json(go.name));
                    sb.Append(",\"activeSelf\":").Append(go.activeSelf ? "true" : "false");
                    sb.Append(",\"activeInHierarchy\":").Append(go.activeInHierarchy ? "true" : "false");
                    sb.Append(",\"tag\":").Append(Json(go.tag));
                    sb.Append(",\"layer\":").Append(go.layer);
                    sb.Append(",\"path\":").Append(Json(HierarchyPath(go)));
                    AppendRect(sb, go);
                    sb.Append(",\"components\":[");
                    Component[]? comps = null;
                    try { comps = go.GetComponents<Component>(); } catch { }
                    var cw = 0;
                    if (comps != null)
                    {
                        foreach (var c in comps)
                        {
                            if (c == null) continue;
                            try
                            {
                                if (cw++ > 0) sb.Append(',');
                                sb.Append("{\"type\":").Append(Json(RealTypeName(c)));
                                sb.Append(",\"ptr\":\"").Append(PtrHex(c)).Append("\"}");
                            }
                            catch { /* one bad component never fails the dump */ }
                        }
                    }
                    sb.Append("]}");
                    wrote++;
                }
                catch { /* one bad object never fails the dump */ }
            }
            sb.Append("],\"screenWidth\":").Append(SafeScreenWidth())
              .Append(",\"screenHeight\":").Append(SafeScreenHeight());
            sb.Append(",\"objectCount\":").Append(total);
            sb.Append(",\"dumpedCount\":").Append(wrote);
            sb.Append(",\"truncated\":").Append(total > wrote ? "true" : "false");
            sw.Stop();
            sb.Append(",\"elapsedMs\":").Append(sw.Elapsed.TotalMilliseconds.ToString("F1",
                System.Globalization.CultureInfo.InvariantCulture));
            sb.Append('}');
            var json = sb.ToString();
            RpgHost.Client?.EnqueueDump(json, tag);
            DebugRuntime.Emit("debug.dump.done", new Dictionary<string, object>
            {
                ["tag"] = tag,
                ["objectCount"] = total,
                ["dumpedCount"] = wrote,
                ["truncated"] = total > wrote,
                ["bytes"] = json.Length,
                ["elapsedMs"] = Math.Round(sw.Elapsed.TotalMilliseconds, 1)
            });
            CheatState.Note($"debug dump-all {tag} objects={total} dumped={wrote} {sw.Elapsed.TotalMilliseconds:F0}ms");
        }
        catch (Exception ex)
        {
            CheatState.Error("debug.dump-all: " + ex.Message);
        }
    }

    /// <summary>
    /// Screen geometry for any object that carries a <c>RectTransform</c>. Emitted by
    /// <c>dump-all</c> so a caller can answer "where is this on screen, and how big" from the
    /// dump alone instead of re-probing one field per boot.
    ///
    /// <para><c>screen</c> is the axis-aligned bound of the rect's four corners in Unity screen
    /// pixels, <b>origin bottom-left</b> — flip with the emitted <c>screenHeight</c> for a
    /// top-left coordinate. It is computed by transforming the four local corners itself rather
    /// than calling <c>RectTransform.GetWorldCorners</c>: that takes a <c>Vector3[]</c>, which
    /// crosses the Il2Cpp array-marshalling boundary, and <c>RectTransformUtility</c> lives in
    /// <c>UnityEngine.UIModule</c> which this assembly cannot reference. <c>Transform.TransformPoint</c>
    /// and <c>Camera.WorldToScreenPoint</c> are both CoreModule and need no array.</para>
    /// </summary>
    static void AppendRect(System.Text.StringBuilder sb, GameObject go)
    {
        RectTransform? rt = null;
        try { rt = go.GetComponent<RectTransform>(); } catch { }
        if (rt == null) return;
        try
        {
            var r = rt.rect;
            var aMin = rt.anchorMin;
            var aMax = rt.anchorMax;
            var pivot = rt.pivot;
            var ap = rt.anchoredPosition;
            var scale = rt.lossyScale;
            var cam = CanvasCameraOf(rt);

            float x0 = float.MaxValue, y0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue;
            for (var i = 0; i < 4; i++)
            {
                var lx = (i == 0 || i == 1) ? r.xMin : r.xMax;
                var ly = (i == 0 || i == 3) ? r.yMin : r.yMax;
                var world = rt.TransformPoint(new Vector3(lx, ly, 0f));
                var sp = cam != null ? cam.WorldToScreenPoint(world) : world;
                if (sp.x < x0) x0 = sp.x;
                if (sp.y < y0) y0 = sp.y;
                if (sp.x > x1) x1 = sp.x;
                if (sp.y > y1) y1 = sp.y;
            }

            sb.Append(",\"rect\":{\"w\":").Append(F(r.width)).Append(",\"h\":").Append(F(r.height))
              .Append(",\"anchorMin\":[").Append(F(aMin.x)).Append(',').Append(F(aMin.y))
              .Append("],\"anchorMax\":[").Append(F(aMax.x)).Append(',').Append(F(aMax.y))
              .Append("],\"pivot\":[").Append(F(pivot.x)).Append(',').Append(F(pivot.y))
              .Append("],\"anchoredPos\":[").Append(F(ap.x)).Append(',').Append(F(ap.y))
              .Append("],\"lossyScale\":[").Append(F(scale.x)).Append(',').Append(F(scale.y))
              .Append("],\"screen\":[").Append(F(x0)).Append(',').Append(F(y0)).Append(',')
              .Append(F(x1)).Append(',').Append(F(y1)).Append("]}");
        }
        catch { /* one bad rect never fails the dump */ }
    }

    /// <summary>
    /// Nearest ancestor camera. A Screen-Space-Camera canvas renders through the camera on its own
    /// GameObject, so walking up from the node finds it; an Overlay canvas has none and world
    /// coordinates already are screen pixels.
    /// </summary>
    static Camera? CanvasCameraOf(Transform t)
    {
        // Structural (not tunable): bounds the parent walk. Not the balance surface.
        const int MaxDepth = 16;
        var depth = 0;
        for (var p = t; p != null && depth++ < MaxDepth; p = p.parent)
        {
            Camera? c = null;
            try { c = p.GetComponent<Camera>(); } catch { }
            if (c != null) return c;
        }
        return null;
    }

    static string F(float v) =>
        v.ToString("F1", System.Globalization.CultureInfo.InvariantCulture);

    static int SafeScreenWidth() { try { return Screen.width; } catch { return 0; } }
    static int SafeScreenHeight() { try { return Screen.height; } catch { return 0; } }

    static string PtrHex(UObject o)
    {
        try { return GameDumps.Ptr((Il2CppObjectBase)(object)o); }
        catch { return ""; }
    }

    /// <summary>
    /// Real type name. <c>GetType().Name</c> lies on unhollowed objects (reports the
    /// interop base — measured live: 228 components all reading "Component"), and this
    /// interop version has no <c>GetIl2CppType()</c>. The engine's own
    /// <c>Object.ToString()</c> ("Name (Namespace.Type)") tells the truth instead.
    /// </summary>
    static string RealTypeName(Component c)
    {
        try
        {
            var s = c.ToString();
            if (!string.IsNullOrEmpty(s))
            {
                var open = s.LastIndexOf('(');
                var close = s.LastIndexOf(')');
                if (open >= 0 && close > open + 1)
                {
                    var full = s.Substring(open + 1, close - open - 1);
                    var dot = full.LastIndexOf('.');
                    return dot >= 0 ? full.Substring(dot + 1) : full;
                }
            }
        }
        catch { }
        try { return c.GetType().Name; } catch { return "?"; }
    }

    static string Json(string? s)
    {
        if (s == null) return "null";
        return "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"")
            .Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t") + "\"";
    }

    static string HierarchyPath(GameObject go)
    {
        var parts = new List<string>();
        try
        {
            var t = go.transform;
            var depth = 0;
            while (t != null && depth++ < 12)
            {
                var g = t.gameObject;
                parts.Add(g != null ? g.name : "?");
                t = t.parent;
            }
        }
        catch { }
        parts.Reverse();
        return string.Join("/", parts);
    }

    /// <summary>
    /// Evaluate-search: find controls by criteria, Playwright-locator style. Read-only —
    /// matches carry ptrs for the click module to resolve; nothing is invoked here.
    /// Empty criteria returns the top-level roots (navigation starting point).
    /// </summary>
    public static void RunEvaluateSearch(JsonElement p)
    {
        // Structural (not tunable): bounds the scan + payload. Not the balance surface.
        const int SearchMaxResults = 50;
        try
        {
            var nameContains = Str(p, "nameContains") ?? "";
            var type = Str(p, "type") ?? "";
            var pathContains = Str(p, "pathContains") ?? "";
            var activeOnly = true;
            if (p.TryGetProperty("activeOnly", out var ao) && ao.ValueKind == JsonValueKind.False)
                activeOnly = false;
            var limit = Math.Clamp(Int(p, "limit", SearchMaxResults), 1, SearchMaxResults);
            GameObject[]? all = null;
            try { all = UObject.FindObjectsOfType<GameObject>(); }
            catch (Exception ex)
            {
                CheatState.Error("debug.evaluate-search: enumerate failed: " + ex.Message);
                return;
            }
            var matches = new List<Dictionary<string, object>>();
            var scanned = 0;
            if (all != null)
            {
                foreach (var go in all)
                {
                    if (go == null) continue;
                    scanned++;
                    string name;
                    bool active;
                    string path;
                    try
                    {
                        name = go.name ?? "";
                        active = go.activeInHierarchy;
                        path = HierarchyPath(go);
                    }
                    catch { continue; }
                    if (activeOnly && !active) continue;
                    if (nameContains.Length > 0 &&
                        name.IndexOf(nameContains, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    if (pathContains.Length > 0 &&
                        path.IndexOf(pathContains, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    string? matchedType = null;
                    if (type.Length > 0)
                    {
                        matchedType = HasComponentOfType(go, type);
                        if (matchedType == null) continue;
                    }
                    if (matches.Count >= limit) break;
                    matches.Add(new Dictionary<string, object>
                    {
                        ["ptr"] = PtrHex(go),
                        ["name"] = name,
                        ["type"] = matchedType ?? GuessKind(go),
                        ["active"] = active,
                        ["path"] = path,
                        ["screen"] = ScreenPos(go)
                    });
                }
            }
            DebugRuntime.Emit("debug.evaluate.result", new Dictionary<string, object>
            {
                ["tag"] = Str(p, "tag") ?? "",
                ["scanned"] = scanned,
                ["matchCount"] = matches.Count,
                ["capped"] = matches.Count >= limit,
                ["matches"] = matches
            });
        }
        catch (Exception ex)
        {
            CheatState.Error("debug.evaluate-search: " + ex.Message);
        }
    }

    static string? HasComponentOfType(GameObject go, string type)
    {
        Component[]? comps = null;
        try { comps = go.GetComponents<Component>(); } catch { return null; }
        if (comps == null) return null;
        foreach (var c in comps)
        {
            if (c == null) continue;
            var real = RealTypeName(c);
            if (string.Equals(real, type, StringComparison.OrdinalIgnoreCase) ||
                real.EndsWith("." + type, StringComparison.OrdinalIgnoreCase) ||
                real.EndsWith("_" + type, StringComparison.OrdinalIgnoreCase))
                return real;
        }
        return null;
    }

    static string GuessKind(GameObject go)
    {
        try
        {
            Component[]? comps = go.GetComponents<Component>();
            if (comps != null)
            {
                foreach (var c in comps)
                {
                    if (c == null) continue;
                    var real = RealTypeName(c);
                    if (!string.Equals(real, "Transform", StringComparison.Ordinal) &&
                        !string.Equals(real, "RectTransform", StringComparison.Ordinal) &&
                        !string.Equals(real, "CanvasRenderer", StringComparison.Ordinal))
                        return real;
                }
            }
        }
        catch { }
        return "GameObject";
    }

    /// <summary>
    /// Method enumeration for evaluate-call design: resolve an object by ptr, list public
    /// instance methods per component via real-Type reflection. Read-only.
    /// </summary>
    public static void RunEvaluateMethods(JsonElement p)
    {
        // Structural (not tunable): bounds reflection output. Not the balance surface.
        const int MethodMaxPerType = 100;
        try
        {
            var ptr = (Str(p, "ptr") ?? "").Trim();
            if (string.IsNullOrEmpty(ptr))
            {
                CheatState.Error("debug.evaluate-methods: ptr is required");
                return;
            }
            var found = FindByPtr(ptr);
            if (found == null)
            {
                CheatState.Error("debug.evaluate-methods: no live object for ptr " + ptr);
                return;
            }
            var tables = new List<Dictionary<string, object>>();
            foreach (var c in found)
            {
                if (c == null) continue;
                var real = RealTypeName(c);
                var t = ResolveType(real);
                var methods = new List<Dictionary<string, object>>();
                if (t != null)
                {
                    try
                    {
                        foreach (var m in t.GetMethods(
                            System.Reflection.BindingFlags.Public |
                            System.Reflection.BindingFlags.Instance |
                            System.Reflection.BindingFlags.DeclaredOnly))
                        {
                            if (methods.Count >= MethodMaxPerType) break;
                            if (m.IsSpecialName) continue;
                            var ps = new List<string>();
                            try
                            {
                                foreach (var prm in m.GetParameters())
                                    ps.Add(prm.ParameterType.Name + " " + prm.Name);
                            }
                            catch { }
                            string ret;
                            try { ret = m.ReturnType.Name; } catch { ret = "?"; }
                            methods.Add(new Dictionary<string, object>
                            {
                                ["name"] = m.Name,
                                ["params"] = ps,
                                ["returns"] = ret
                            });
                        }
                    }
                    catch (Exception ex)
                    {
                        methods.Add(new Dictionary<string, object>
                        {
                            ["name"] = "!reflect-error",
                            ["params"] = new List<string>(),
                            ["returns"] = ex.GetType().Name
                        });
                    }
                }
                tables.Add(new Dictionary<string, object>
                {
                    ["type"] = real,
                    ["typeResolved"] = t != null,
                    ["methods"] = methods
                });
            }
            DebugRuntime.Emit("debug.evaluate.methods", new Dictionary<string, object>
            {
                ["tag"] = Str(p, "tag") ?? "",
                ["ptr"] = ptr,
                ["components"] = tables
            });
        }
        catch (Exception ex)
        {
            CheatState.Error("debug.evaluate-methods: " + ex.Message);
        }
    }

    static List<Component>? FindByPtr(string ptr)
    {
        GameObject[]? all = null;
        try { all = UObject.FindObjectsOfType<GameObject>(); }
        catch { return null; }
        if (all == null) return null;
        foreach (var go in all)
        {
            if (go == null) continue;
            try
            {
                if (string.Equals(PtrHex(go), ptr, StringComparison.OrdinalIgnoreCase))
                    return new List<Component>(go.GetComponents<Component>());
                var comps = go.GetComponents<Component>();
                if (comps == null) continue;
                foreach (var c in comps)
                {
                    if (c == null) continue;
                    if (string.Equals(PtrHex(c), ptr, StringComparison.OrdinalIgnoreCase))
                        return new List<Component> { c };
                }
            }
            catch { /* keep scanning */ }
        }
        return null;
    }

    static Type? ResolveType(string realName)
    {
        if (string.IsNullOrEmpty(realName)) return null;
        try
        {
            var t = Type.GetType(realName + ", Assembly-CSharp");
            if (t != null) return t;
        }
        catch { }
        System.Reflection.Assembly[]? assemblies = null;
        try { assemblies = AppDomain.CurrentDomain.GetAssemblies(); }
        catch { return null; }
        if (assemblies == null) return null;
        foreach (var asm in assemblies)
        {
            if (asm == null) continue;
            Type? hit = null;
            try { hit = asm.GetType(realName); } catch { continue; }
            if (hit != null) return hit;
            try
            {
                Type[]? types = null;
                try { types = asm.GetTypes(); } catch { continue; }
                if (types == null) continue;
                foreach (var cand in types)
                {
                    if (cand == null) continue;
                    string? nm = null;
                    try { nm = cand.Name; } catch { continue; }
                    if (string.Equals(nm, realName, StringComparison.Ordinal))
                        return cand;
                }
            }
            catch { /* keep scanning assemblies */ }
        }
        return null;
    }

    /// <summary>
    /// Evaluate-call: invoke one public instance method on a resolved component.
    /// Closed world: the target resolves from a live ptr (never a type name alone), the
    /// method must be public/instance/declared on the resolved type, and args coerce from
    /// JSON primitives only (int, float, bool, string, enum-by-name, null). Anything else
    /// — overload ambiguity, uncoercible args, void vs value — is a loud refusal, and the
    /// emit always carries what ran and what it returned.
    /// </summary>
    public static void RunEvaluateCall(JsonElement p)
    {
        try
        {
            var ptr = (Str(p, "ptr") ?? "").Trim();
            var method = (Str(p, "method") ?? "").Trim();
            var wantType = (Str(p, "type") ?? "").Trim();
            if (string.IsNullOrEmpty(ptr) || string.IsNullOrEmpty(method))
            {
                CheatState.Error("debug.evaluate-call: ptr and method are required");
                return;
            }
            var found = FindByPtr(ptr);
            if (found == null || found.Count == 0)
            {
                CheatState.Error("debug.evaluate-call: no live object for ptr " + ptr);
                return;
            }
            Component? target = null;
            string real = "";
            foreach (var c in found)
            {
                if (c == null) continue;
                var rn = RealTypeName(c);
                if (wantType.Length > 0 &&
                    !string.Equals(rn, wantType, StringComparison.OrdinalIgnoreCase) &&
                    !rn.EndsWith("." + wantType, StringComparison.OrdinalIgnoreCase) &&
                    !rn.EndsWith("_" + wantType, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (wantType.Length == 0 && IsInfrastructure(rn)) continue;
                target = c;
                real = rn;
                break;
            }
            if (target == null)
            {
                CheatState.Error("debug.evaluate-call: no component"
                    + (wantType.Length > 0 ? " of type " + wantType : "")
                    + " on ptr " + ptr);
                return;
            }
            var t = ResolveType(real);
            if (t == null)
            {
                CheatState.Error("debug.evaluate-call: cannot resolve type " + real);
                return;
            }
            System.Reflection.MethodInfo? mi = null;
            try
            {
                mi = t.GetMethod(method,
                    System.Reflection.BindingFlags.Public |
                    System.Reflection.BindingFlags.Instance);
            }
            catch (Exception ex)
            {
                CheatState.Error("debug.evaluate-call: method lookup failed: " + ex.Message);
                return;
            }
            if (mi == null)
            {
                CheatState.Error($"debug.evaluate-call: {real} has no public instance method {method}");
                return;
            }
            var pars = mi.GetParameters();
            JsonElement argsEl = default;
            var hasArgs = p.TryGetProperty("args", out argsEl) && argsEl.ValueKind == JsonValueKind.Array;
            if (!hasArgs && pars.Length > 0)
            {
                CheatState.Error($"debug.evaluate-call: {method} needs {pars.Length} args, none given");
                return;
            }
            if (hasArgs && argsEl.GetArrayLength() != pars.Length)
            {
                CheatState.Error($"debug.evaluate-call: {method} needs {pars.Length} args, got {argsEl.GetArrayLength()}");
                return;
            }
            var argv = new object?[pars.Length];
            for (var i = 0; i < pars.Length; i++)
            {
                if (!TryCoerce(argsEl[i], pars[i].ParameterType, out argv[i]))
                {
                    CheatState.Error($"debug.evaluate-call: arg {i} does not coerce to {pars[i].ParameterType.Name}");
                    return;
                }
            }
            object? result;
            string invokePath;
            try { result = mi.Invoke(target, argv); invokePath = "invoke"; }
            catch (Exception invokeEx)
            {
                // Unhollowed mirror mismatch (the resolved Type object is not the mirror the
                // live object was created from): fall back to engine dispatch by name for
                // 0-arg calls, which needs no Type at all. (1-arg SendMessage would need
                // Il2Cpp boxing the interop cannot express from System.object — refused
                // instead, loudly.)
                if (pars.Length != 0)
                {
                    CheatState.Error("debug.evaluate-call: invoke threw: " + (invokeEx.InnerException ?? invokeEx).Message);
                    return;
                }
                try
                {
                    target.SendMessage(method);
                    invokePath = "sendmessage";
                    result = null;
                }
                catch (Exception sendEx)
                {
                    CheatState.Error("debug.evaluate-call: invoke threw: " + (invokeEx.InnerException ?? invokeEx).Message
                        + "; sendmessage threw: " + (sendEx.InnerException ?? sendEx).Message);
                    return;
                }
            }
            string? resultText = null;
            if (mi.ReturnType != typeof(void))
            {
                try { resultText = result != null ? result.ToString() : "null"; }
                catch { resultText = "<unprintable>"; }
            }
            var dump = new Dictionary<string, object>
            {
                ["tag"] = Str(p, "tag") ?? "",
                ["ptr"] = ptr,
                ["type"] = real,
                ["method"] = method,
                ["via"] = invokePath,
                ["ok"] = true
            };
            if (resultText != null) dump["result"] = resultText;
            DebugRuntime.Emit("debug.evaluate.called", dump);
            CheatState.Note($"debug evaluate-call {real}.{method} ok via {invokePath}"
                + (resultText != null ? " -> " + resultText : ""));
        }
        catch (Exception ex)
        {
            CheatState.Error("debug.evaluate-call: " + ex.Message);
        }
    }

    static bool IsInfrastructure(string real) =>
        string.Equals(real, "Transform", StringComparison.Ordinal) ||
        string.Equals(real, "RectTransform", StringComparison.Ordinal) ||
        string.Equals(real, "CanvasRenderer", StringComparison.Ordinal);

    static bool TryCoerce(JsonElement el, Type to, out object? value)
    {
        value = null;
        try
        {
            if (el.ValueKind == JsonValueKind.Null)
                return !to.IsValueType || Nullable.GetUnderlyingType(to) != null;
            if (to == typeof(string))
            {
                if (el.ValueKind != JsonValueKind.String) return false;
                value = el.GetString();
                return true;
            }
            if (to == typeof(bool))
            {
                if (el.ValueKind != JsonValueKind.True && el.ValueKind != JsonValueKind.False) return false;
                value = el.ValueKind == JsonValueKind.True;
                return true;
            }
            if (to == typeof(int) || to == typeof(long))
            {
                if (!el.TryGetInt32(out var iv)) return false;
                value = to == typeof(int) ? iv : (long)iv;
                return true;
            }
            if (to == typeof(float) || to == typeof(double))
            {
                if (!el.TryGetDouble(out var dv)) return false;
                value = to == typeof(float) ? (float)dv : dv;
                return true;
            }
            if (to.IsEnum && el.ValueKind == JsonValueKind.String)
            {
                value = Enum.Parse(to, el.GetString()!, ignoreCase: true);
                return true;
            }
            return false;
        }
        catch { return false; }
    }

    /// <summary>
    /// Text-anchored search: find visible text, resolve the clickable. Engine text content
    /// is read directly off <c>Text</c>/<c>TextMeshProUGUI</c> components — no OCR, no pixels.
    /// Each text match reports its own node plus the nearest clickable ancestor (first
    /// ancestor-or-self carrying a <c>*Btn*</c>/<c>*Button*</c> component or a 2D collider
    /// alongside a non-chrome component), or neither when the text is bare paint. When no
    /// clickable resolves, the match still carries the nearby tree (parent path), so the
    /// agent sees structure instead of a dead end.
    /// </summary>
    public static void RunEvaluateText(JsonElement p)
    {
        // Structural (not tunable): bounds the scan + payload. Not the balance surface.
        const int TextMaxResults = 50;
        try
        {
            var text = (Str(p, "text") ?? "").Trim();
            if (string.IsNullOrEmpty(text))
            {
                CheatState.Error("debug.evaluate-text: text is required");
                return;
            }
            var activeOnly = true;
            if (p.TryGetProperty("activeOnly", out var ao) && ao.ValueKind == JsonValueKind.False)
                activeOnly = false;
            var limit = Math.Clamp(Int(p, "limit", TextMaxResults), 1, TextMaxResults);
            TextHarvest.Refresh();
            GameObject[]? all = null;
            try { all = UObject.FindObjectsOfType<GameObject>(); }
            catch (Exception ex)
            {
                CheatState.Error("debug.evaluate-text: enumerate failed: " + ex.Message);
                return;
            }
            var matches = new List<Dictionary<string, object>>();
            var scanned = 0;
            if (all != null)
            {
                foreach (var go in all)
                {
                    if (go == null) continue;
                    try
                    {
                        if (activeOnly && !go.activeInHierarchy) continue;
                    }
                    catch { continue; }
                    // The harvest map is keyed by COMPONENT ptr (text lives on components);
                    // join each of this object's components against it.
                    TextHarvest.Hit? hit = null;
                    try
                    {
                        var comps = go.GetComponents<Component>();
                        if (comps != null)
                        {
                            foreach (var c in comps)
                            {
                                if (c == null) continue;
                                hit = TextHarvest.Lookup(PtrHex(c));
                                if (hit != null) break;
                            }
                        }
                    }
                    catch { continue; }
                    if (hit == null) continue;
                    var content = hit.Text;
                    var nodeType = hit.Type;
                    if (content.IndexOf(text, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    if (matches.Count >= limit) break;
                    string path;
                    try { path = HierarchyPath(go); } catch { path = ""; }
                    var clickable = ResolveClickable(go);
                    var row = new Dictionary<string, object>
                    {
                        ["ptr"] = PtrHex(go),
                        ["name"] = SafeName(go),
                        ["nodeType"] = nodeType,
                        ["text"] = content.Length > 160 ? content.Substring(0, 160) : content,
                        ["active"] = go.activeInHierarchy,
                        ["path"] = path,
                        ["screen"] = ScreenPos(go)
                    };
                    if (clickable != null)
                    {
                        row["clickablePtr"] = clickable.Ptr;
                        row["clickableName"] = clickable.Name;
                        row["clickableType"] = clickable.Type;
                        row["clickablePath"] = clickable.Path;
                    }
                    matches.Add(row);
                }
            }
            DebugRuntime.Emit("debug.evaluate.text", new Dictionary<string, object>
            {
                ["tag"] = Str(p, "tag") ?? "",
                ["text"] = text,
                ["scanned"] = scanned,
                ["textObjects"] = TextHarvest.HarvestedTypes,
                ["textsNonEmpty"] = TextHarvest.HarvestedTexts,
                ["matchCount"] = matches.Count,
                ["capped"] = matches.Count >= limit,
                ["matches"] = matches
            });
        }
        catch (Exception ex)
        {
            CheatState.Error("debug.evaluate-text: " + ex.Message);
        }
    }

    static string? ReadTextContent(GameObject go, out string nodeType)
    {
        // Provenance note (measured live 2026-09-14): `is`-pattern matching and direct
        // `.text` reads on per-object components returned nothing on this interop, while
        // type-filtered FindObjectsOfType (the census path) resolves the same objects
        // fine. So text is harvested type-first into a ptr map, then joined per object.
        nodeType = "";
        var hit = TextHarvest.Lookup(PtrHex(go));
        if (hit == null) return null;
        nodeType = hit.Type;
        return hit.Text;
    }

    /// <summary>
    /// Type-first text harvest: <c>FindObjectsOfType</c> resolves TMP/uGUI text objects
    /// reliably on this interop (census-proven); per-object component reads do not.
    /// Refreshed on every search call — no cache to go stale across scene changes.
    /// </summary>
    static class TextHarvest
    {
        public sealed class Hit
        {
            public string Text = "";
            public string Type = "";
        }

        static Dictionary<string, Hit>? _map;
        public static int HarvestedTypes;
        public static int HarvestedTexts;

        public static Hit? Lookup(string ptr)
        {
            var map = Volatile.Read(ref _map);
            if (map == null) return null;
            return map.TryGetValue(ptr, out var hit) ? hit : null;
        }

        public static Dictionary<string, Hit> Refresh()
        {
            var map = new Dictionary<string, Hit>(StringComparer.OrdinalIgnoreCase);
            var types = 0;
            var texts = 0;
            foreach (var tmp in SafeFind<TextMeshProUGUI>())
            {
                if (tmp == null) continue;
                types++;
                try
                {
                    var s = tmp.text;
                    if (!string.IsNullOrEmpty(s))
                    {
                        texts++;
                        map[PtrHex(tmp)] = new Hit { Text = s, Type = "TextMeshProUGUI" };
                    }
                }
                catch { /* keep harvesting */ }
            }
            foreach (var ut in SafeFind<UnityEngine.UI.Text>())
            {
                if (ut == null) continue;
                types++;
                try
                {
                    var s = ut.text;
                    if (!string.IsNullOrEmpty(s))
                    {
                        texts++;
                        map[PtrHex(ut)] = new Hit { Text = s, Type = "Text" };
                    }
                }
                catch { /* keep harvesting */ }
            }
            HarvestedTypes = types;
            HarvestedTexts = texts;
            Volatile.Write(ref _map, map);
            return map;
        }
    }

    sealed class Clickable
    {
        public string Ptr = "";
        public string Name = "";
        public string Type = "";
        public string Path = "";
    }

    static Clickable? ResolveClickable(GameObject go)
    {
        try
        {
            var t = go.transform;
            var depth = 0;
            while (t != null && depth++ < 5)
            {
                var g = t.gameObject;
                if (g != null)
                {
                    var hit = ClickableOn(g);
                    if (hit != null) return hit;
                }
                t = t.parent;
            }
        }
        catch { }
        return null;
    }

    static Clickable? ClickableOn(GameObject go)
    {
        Component[]? comps = null;
        try { comps = go.GetComponents<Component>(); } catch { return null; }
        if (comps == null) return null;
        string? collider = null;
        foreach (var c in comps)
        {
            if (c == null) continue;
            string real;
            try { real = RealTypeName(c); } catch { continue; }
            if (real.IndexOf("Btn", StringComparison.OrdinalIgnoreCase) >= 0 ||
                real.IndexOf("Button", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return new Clickable
                {
                    Ptr = PtrHex(go),
                    Name = SafeName(go),
                    Type = real,
                    Path = HierarchyPath(go)
                };
            }
            if (collider == null &&
                (string.Equals(real, "BoxCollider2D", StringComparison.Ordinal) ||
                 string.Equals(real, "PolygonCollider2D", StringComparison.Ordinal)))
                collider = real;
        }
        if (collider != null)
        {
            return new Clickable
            {
                Ptr = PtrHex(go),
                Name = SafeName(go),
                Type = collider + "-hitbox",
                Path = HierarchyPath(go)
            };
        }
        return null;
    }

    static string SafeName(GameObject go)
    {
        try { return go.name ?? ""; } catch { return ""; }
    }

    /// <summary>
    /// Client-pixel position of an object for the cursor tier: object center to screen via
    /// the main camera, Y-flipped to top-left origin. Null when unresolvable (no camera) —
    /// the cursor tier refuses rather than guessing.
    /// </summary>
    static Dictionary<string, object>? ScreenPos(GameObject go)
    {
        try
        {
            Camera? cam = null;
            try { cam = Camera.main; } catch { cam = null; }
            if (cam == null) return null;
            var world = go.transform.position;
            var sp = cam.WorldToScreenPoint(world);
            if (sp.z < 0) return null;
            var h = Screen.height;
            if (h <= 0) return null;
            return new Dictionary<string, object>
            {
                ["x"] = Mathf.FloorToInt(sp.x),
                ["y"] = Mathf.FloorToInt(h - sp.y)
            };
        }
        catch { return null; }
    }
}
