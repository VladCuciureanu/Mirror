using System.Text.Json;
using System.Text.RegularExpressions;

using Serilog;
using Mirror.Plugins.Interfaces;
using Mirror.Plugins.Resolvers;
using Mirror.Resources.Framework.Base;

namespace Mirror.Plugins.Fortnite.Resolvers;

/* Fortnite's client ships no editor copies (43.00 and 43.10): none of its containers, local or on demand, lists an
   optional segment. UEFN ships them for its build as pakchunk*-WindowsClient.o.utoc, one per game chunk, and they pair
   with the game's packages by package id. UEFN is wherever the Epic Games Launcher records it (AppName Fortnite_Studio
   in its .item manifests or LauncherInstalled.dat): 43.00 installed it in Fortnite_Studio beside the game, 43.10 in
   the game's own folder.
   - In its own folder, only the .o.utoc containers are mounted (UEFN's own content, UEFNFortniteGame-WindowsUEFN, stays
     out), and only when UEFN's changelist is the game's: the segments are written for one build.
   - In the game's folder, the game's own mount has already registered them (and UEFN's own content), so nothing is
     added. They are the segments UEFN itself loads over that folder's containers, so they pair with them even though
     the launcher gives UEFN a later changelist than the game (43.10: UEFN CL 60458657, the game CL 60118918).
   43.00's UEFN segments hold material graphs; Niagara systems have none there either. */
public sealed class FortniteEditorSegmentsPlugin : IEditorSegmentsPlugin, IGameIdPlugin
{
    public string Name => "Fortnite Editor Data (UEFN)";
    public string GameId => "Fortnite";

    public bool DoesCharacteristicsMatch(BaseProfile profile) => StudioPaks(profile) is not null;

    public IEnumerable<FileInfo> ResolveContainers(BaseProfile profile)
    {
        if (StudioPaks(profile) is not { } paks) return [];

        if (IsGameFolder(profile, paks))
        {
            Log.Information($"{Name}: UEFN is installed in the game's folder ({paks.FullName}); its editor data containers mount with the game's");

            return [];
        }

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
       compile. The names are the UE install's own content (UE_ENGINE, else the newest UE_* the launcher records, else the default UE 5.8 install), which
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

        var newest = LauncherRecords()
            .Where(record => record.App.StartsWith("UE_") && Directory.Exists(Path.Combine(record.Location, "Engine")))
            .OrderByDescending(record => Version.TryParse(record.App[3..], out var version) ? version : new Version())
            .Select(record => record.Location)
            .FirstOrDefault();

        /* The launcher's records can be empty while it rewrites them (2026-11-12: LauncherInstalled.dat listed nothing
           and UE_5.8's manifest was set aside as .item.old); the default UE 5.8 install is what viewer/scripts/core/ue.py
           and the checks use then too */
        newest ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Epic Games", "UE_5.8");

        return Directory.Exists(Path.Combine(newest, "Engine")) ? new DirectoryInfo(Path.Combine(newest, "Engine")) : null;
    }

    /* <UEFN's install>\FortniteGame\Content\Paks, the install the launcher records for Fortnite_Studio */
    private static DirectoryInfo? StudioPaks(BaseProfile profile)
    {
        if (string.IsNullOrEmpty(profile.ArchiveDirectory)) return null;
        if (LauncherRecord(record => record.App == StudioApp) is not { } studio) return null;

        var paks = new DirectoryInfo(Path.Combine(studio.Location, "FortniteGame", "Content", "Paks"));

        return paks.Exists && paks.EnumerateFiles("pakchunk*.o.utoc").Any() ? paks : null;
    }

    private const string StudioApp = "Fortnite_Studio";
    private static readonly char[] Separators = ['\\', '/'];

    private static bool IsGameFolder(BaseProfile profile, DirectoryInfo paks) =>
        SamePath(Path.GetFullPath(profile.ArchiveDirectory), paks.FullName);

    private static bool SamePath(string? a, string? b) =>
        a is not null && b is not null && string.Equals(a.TrimEnd(Separators), b.TrimEnd(Separators), StringComparison.OrdinalIgnoreCase);

    /* UEFN's changelist: the launcher's version string for it, else its Engine\Build\Build.version */
    private static string? StudioChangelist(DirectoryInfo paks)
    {
        if (LauncherRecord(record => record.App == StudioApp) is { } studio && Changelist(studio.Version) is { } recorded) return recorded;

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

    /* The game names its changelist only in the launcher's records ("++Fortnite+Release-43.10-CL-60118918-Windows"): the
       record of the app installed where the profile reads, other than UEFN */
    private static string? GameChangelist(BaseProfile profile)
    {
        var gameRoot = new DirectoryInfo(profile.ArchiveDirectory).Parent?.Parent?.Parent?.FullName;

        return LauncherRecord(record => record.App != StudioApp && SamePath(record.Location, gameRoot)) is { } game ? Changelist(game.Version) : null;
    }

    private static string? Changelist(string? version)
    {
        var match = Regex.Match(version ?? string.Empty, @"-CL-(\d+)");

        return match.Success ? match.Groups[1].Value : null;
    }

    private sealed record InstallRecord(string App, string Location, string? Version);

    /* The launcher's install records: its .item manifests (…\EpicGamesLauncher\Data\Manifests, AppName,
       InstallLocation, AppVersionString), then LauncherInstalled.dat (…\UnrealEngineLauncher, InstallationList) */
    private static InstallRecord? LauncherRecord(Func<InstallRecord, bool> match) => LauncherRecords().FirstOrDefault(match);

    private static IEnumerable<InstallRecord> LauncherRecords()
    {
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        var records = new List<InstallRecord>();

        var manifests = new DirectoryInfo(Path.Combine(programData, "Epic", "EpicGamesLauncher", "Data", "Manifests"));
        if (manifests.Exists)
        {
            foreach (var item in manifests.EnumerateFiles("*.item").OrderBy(file => file.Name))
            {
                try
                {
                    using var json = JsonDocument.Parse(File.ReadAllText(item.FullName));
                    var root = json.RootElement;

                    if (root.TryGetProperty("bIsIncompleteInstall", out var incomplete) && incomplete.ValueKind == JsonValueKind.True) continue;
                    if (root.TryGetProperty("AppName", out var app) && root.TryGetProperty("InstallLocation", out var location)
                        && app.GetString() is { Length: > 0 } appName && location.GetString() is { Length: > 0 } locationName)
                    {
                        records.Add(new InstallRecord(appName, locationName,
                            root.TryGetProperty("AppVersionString", out var version) ? version.GetString() : null));
                    }
                }
                catch (Exception)
                {
                    /* A manifest this can't read */
                }
            }
        }

        try
        {
            var installed = Path.Combine(programData, "Epic", "UnrealEngineLauncher", "LauncherInstalled.dat");
            using var json = JsonDocument.Parse(File.ReadAllText(installed));

            foreach (var install in json.RootElement.GetProperty("InstallationList").EnumerateArray())
            {
                if (install.GetProperty("AppName").GetString() is { Length: > 0 } appName && install.GetProperty("InstallLocation").GetString() is { Length: > 0 } location)
                {
                    records.Add(new InstallRecord(appName, location,
                        install.TryGetProperty("AppVersion", out var version) ? version.GetString() : null));
                }
            }
        }
        catch (Exception)
        {
            /* No launcher list, or one this can't read */
        }

        return records;
    }
}
