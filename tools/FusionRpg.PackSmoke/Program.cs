using System.IO;
using FusionRpg.Launcher.Services;

namespace FusionRpg.PackSmoke;

static class Program
{
    static int Main(string[] args)
    {
        var packDir = args.Length > 0 ? args[0] : Path.Combine("dist", "FusionRpg");
        if (args.Length >= 2 && (args[0] is "-PackDir" or "--pack-dir"))
            packDir = args[1];

        var probe = new PlayerPackProbe();
        var result = probe.Run(packDir);
        Console.WriteLine(result.ToJson());
        return result.Ok ? 0 : 1;
    }
}
