using FusionRpg.Injector.Host;
using UnityEngine;

namespace FusionRpg.Injector.Fx;

/// <summary>
/// LIVE <see cref="Shader.Find"/> of shaders Fusion likely shipped.
/// IL2CPP cannot compile ShaderLab strings; unused shaders are stripped from the player.
/// </summary>
public static class OverlayShaderProbe
{
    public static readonly string[] CandidateNames =
    {
        // Alpha-blended first: additive washed pale element colors to white over the bright
        // lawn (LIVE finding 2026-08-21). Sprites/Default renders true vertex colors.
        "Sprites/Default",
        "Particles/Standard Unlit",
        "Particles/Additive",
        "Legacy Shaders/Particles/Additive",
        "Mobile/Particles/Additive",
        "Unlit/Transparent",
        "Unlit/Color",
        "Unlit/Texture",
        "UI/Default",
        "Legacy Shaders/Transparent/Diffuse"
    };

    static List<Result>? _last;
    static Shader? _draw;
    static string _drawName = "";

    public readonly struct Result
    {
        public Result(string name, bool found)
        {
            Name = name;
            Found = found;
        }

        public string Name { get; }
        public bool Found { get; }
    }

    public static IReadOnlyList<Result> Probe(bool force = false)
    {
        if (_last != null && !force) return _last;

        var list = new List<Result>(CandidateNames.Length);
        Shader? first = null;
        var firstName = "";
        foreach (var name in CandidateNames)
        {
            Shader? shader = null;
            try { shader = Shader.Find(name); }
            catch { shader = null; }

            var found = shader != null;
            list.Add(new Result(name, found));
            var line = found ? "found" : "null";
            try { RpgHost.Log.Info("[fx.shader] " + name + " = " + line); }
            catch { }

            if (found && first == null)
            {
                first = shader;
                firstName = name;
            }
        }

        _last = list;
        _draw = first;
        _drawName = firstName;
        return list;
    }

    public static Shader? DrawShader()
    {
        if (_last == null) Probe();
        return _draw;
    }

    public static string DrawShaderName()
    {
        if (_last == null) Probe();
        return _drawName;
    }

    public static Dictionary<string, object> ToEventPayload(bool force = true)
    {
        var results = Probe(force);
        var found = new List<string>();
        var missing = new List<string>();
        foreach (var r in results)
        {
            if (r.Found) found.Add(r.Name);
            else missing.Add(r.Name);
        }

        return new Dictionary<string, object>
        {
            ["found"] = found,
            ["missing"] = missing,
            ["foundCount"] = found.Count,
            ["drawShader"] = DrawShaderName()
        };
    }
}
