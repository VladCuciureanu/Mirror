using System.Text.Json;
using System.Text.RegularExpressions;

using Serilog;
using Mirror.Plugins.Interfaces;
using Mirror.Plugins.Resolvers;
using Mirror.Resources.Framework.Base;

namespace Mirror.Plugins.Fortnite.Resolvers;

/* Fortnite 43.00's client ships no editor copies: none of its containers, local or on demand, lists an optional segment.
   UEFN (Fortnite_Studio, beside the game in the Epic Games folder) ships them for the same build as
   pakchunk*-WindowsClient.o.utoc, one per game chunk, and they pair with the game's packages by package id. Only those
   are mounted; UEFN's own content (UEFNFortniteGame-WindowsUEFN) stays out. The segments are written for one build, so
   a UEFN install of another changelist is left alone. 43.00's UEFN segments hold material graphs; Niagara systems have
   none there either. */
public sealed class FortniteEditorSegmentsPlugin : IEditorSegmentsPlugin, IGameIdPlugin
{
    public string Name => "Fortnite Editor Data (UEFN)";
    public string GameId => "Fortnite";

    public bool DoesCharacteristicsMatch(BaseProfile profile) => StudioPaks(profile) is not null;

    public IEnumerable<FileInfo> ResolveContainers(BaseProfile profile)
    {
        if (StudioPaks(profile) is not { } paks) return [];

        var studioBuild = StudioChangelist(paks);
        var gameBuild = GameChangelist(profile);

        if (studioBuild is not null && gameBuild is not null && studioBuild != gameBuild)
        {
            Log.Warning($"{Name}: UEFN is CL {studioBuild} and the game CL {gameBuild}; its editor copies are another build's, so they stay unmounted");

            return [];
        }

        if (gameBuild is null) Log.Warning($"{Name}: the game's changelist is unknown, so UEFN's (CL {studioBuild}) is taken to match");

        return paks.EnumerateFiles("pakchunk*.o.utoc").OrderBy(file => file.Name).ToList();
    }

    /* The editor copies import editor-only engine content the client doesn't carry: a Niagara system's module calls
       name /Niagara/Modules/..., and without a name the reference comes across empty and the system doesn't
       compile. The names are the UE install's own content (UE_ENGINE, else the newest UE_* the launcher lists), which
       is also the editor the copies are rebuilt in: Engine\Content as /Engine, each plugin's Content as /<Plugin>. */
    public IEnumerable<string> ExternalPackageNames(BaseProfile profile)
    {
        if (StudioPaks(profile) is null || EngineRoot() is not { } engine) yield break;

        var engineContent = new DirectoryInfo(Path.Combine(engine.FullName, "Content"));
        if (engineContent.Exists)
        {
            foreach (var name in PackageNames(engineContent, "/Engine")) yield return name;
        }

        var plugins = new DirectoryInfo(Path.Combine(engine.FullName, "Plugins"));
        if (!plugins.Exists) yield break;

        foreach (var descriptor in plugins.EnumerateFiles("*.uplugin", SearchOption.AllDirectories))
        {
            var content = new DirectoryInfo(Path.Combine(descriptor.DirectoryName!, "Content"));
            if (!content.Exists) continue;

            foreach (var name in PackageNames(content, "/" + Path.GetFileNameWithoutExtension(descriptor.Name))) yield return name;
        }
    }

    private static IEnumerable<string> PackageNames(DirectoryInfo content, string mount)
    {
        foreach (var file in content.EnumerateFiles("*.*", SearchOption.AllDirectories))
        {
            if (!file.Extension.Equals(".uasset", StringComparison.OrdinalIgnoreCase) && !file.Extension.Equals(".umap", StringComparison.OrdinalIgnoreCase)) continue;

            var relative = Path.GetRelativePath(content.FullName, file.FullName).Replace('\\', '/');

            yield return $"{mount}/{relative[..^file.Extension.Length]}";
        }
    }

    /* …\UE_5.8\Engine */
    private static DirectoryInfo? EngineRoot()
    {
        var named = Environment.GetEnvironmentVariable("UE_ENGINE");
        if (!string.IsNullOrEmpty(named) && Directory.Exists(Path.Combine(named, "Engine"))) return new DirectoryInfo(Path.Combine(named, "Engine"));

        var installed = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Epic", "UnrealEngineLauncher", "LauncherInstalled.dat");

        try
        {
            using var json = JsonDocument.Parse(File.ReadAllText(installed));

            var newest = json.RootElement.GetProperty("InstallationList").EnumerateArray()
                .Where(install => install.GetProperty("AppName").GetString()?.StartsWith("UE_") == true)
                .Select(install => install.GetProperty("InstallLocation").GetString()!)
                .Where(location => Directory.Exists(Path.Combine(location, "Engine")))
                .OrderByDescending(location => Version.TryParse(Path.GetFileName(location)[3..], out var version) ? version : new Version())
                .FirstOrDefault();

            return newest is null ? null : new DirectoryInfo(Path.Combine(newest, "Engine"));
        }
        catch (Exception)
        {
            return null;
        }
    }

    /* Fortnite_Studio\FortniteGame\Content\Paks beside the game's install (…\Epic Games\Fortnite\FortniteGame\Content\Paks) */
    private static DirectoryInfo? StudioPaks(BaseProfile profile)
    {
        if (string.IsNullOrEmpty(profile.ArchiveDirectory)) return null;

        var gameRoot = new DirectoryInfo(profile.ArchiveDirectory).Parent?.Parent?.Parent;
        if (gameRoot?.Parent is not { } epicGames) return null;

        var paks = new DirectoryInfo(Path.Combine(epicGames.FullName, "Fortnite_Studio", "FortniteGame", "Content", "Paks"));

        return paks.Exists && paks.EnumerateFiles("pakchunk*.o.utoc").Any() ? paks : null;
    }

    /* UEFN's Engine\Build\Build.version names its changelist */
    private static string? StudioChangelist(DirectoryInfo paks)
    {
        var buildVersion = Path.Combine(paks.Parent!.Parent!.Parent!.FullName, "Engine", "Build", "Build.version");

        try
        {
            using var json = JsonDocument.Parse(File.ReadAllText(buildVersion));

            return json.RootElement.GetProperty("Changelist").GetInt64().ToString();
        }
        catch (Exception)
        {
            return null;
        }
    }

    /* The game names its changelist only in the launcher's install list ("++Fortnite+Release-43.00-CL-59596594-Windows") */
    private static string? GameChangelist(BaseProfile profile)
    {
        var gameRoot = new DirectoryInfo(profile.ArchiveDirectory).Parent?.Parent?.Parent?.FullName.TrimEnd('\\', '/');
        var installed = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Epic", "UnrealEngineLauncher", "LauncherInstalled.dat");

        try
        {
            using var json = JsonDocument.Parse(File.ReadAllText(installed));

            foreach (var install in json.RootElement.GetProperty("InstallationList").EnumerateArray())
            {
                var location = install.GetProperty("InstallLocation").GetString()?.TrimEnd('\\', '/');
                if (!string.Equals(location, gameRoot, StringComparison.OrdinalIgnoreCase)) continue;

                var match = Regex.Match(install.GetProperty("AppVersion").GetString() ?? string.Empty, @"-CL-(\d+)");
                if (match.Success) return match.Groups[1].Value;
            }
        }
        catch (Exception)
        {
            /* No launcher list, or one this can't read: the build is unknown */
        }

        return null;
    }
}
