using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using CmlLib.Core;
using CmlLib.Core.Installer.Forge;
using CmlLib.Core.Installer.NeoForge;
using CmlLib.Core.Installers;
using CmlLib.Core.VersionMetadata;
using Daybreak.Core.Game;

namespace Daybreak.Core.Mod;

/// <summary>加载器版本列表项。</summary>
public record LoaderVersionInfo(string Name, bool Recommended, bool Latest);

/// <summary>
/// Mod 加载器安装服务:Fabric(Fabric Meta profile/json)/ Forge / NeoForge(CmlLib Forge 安装器)。
/// </summary>
public class LoaderService
{
    private const string FabricMeta = "https://meta.fabricmc.net/v2/";

    private readonly string? _gameDirectory;
    private readonly string _mirror;

    public LoaderService(string? gameDirectory, string mirror)
    {
        _gameDirectory = string.IsNullOrWhiteSpace(gameDirectory) ? null : gameDirectory;
        _mirror = DownloadMirror.IsValid(mirror) ? mirror : DownloadMirror.Official;
    }

    private static HttpClient CreateHttp() => new(new ResilientHttpHandler("official"));

    private MinecraftLauncher CreateLauncher() => LaunchService.CreateLauncher(_gameDirectory, _mirror);

    private static void HookProgress(MinecraftLauncher launcher, IProgress<LaunchProgress>? progress)
    {
        if (progress is null)
            return;

        launcher.FileProgressChanged += (_, e) => progress.Report(new LaunchProgress
        {
            CurrentFile = e.Name,
            ProgressedTasks = e.ProgressedTasks,
            TotalTasks = e.TotalTasks,
        });
        launcher.ByteProgressChanged += (_, e) => progress.Report(new LaunchProgress
        {
            ProgressedBytes = e.ProgressedBytes,
            TotalBytes = e.TotalBytes,
        });
    }

    // ===== Fabric =====

    public async Task<IReadOnlyList<string>> GetFabricLoaderVersionsAsync()
    {
        var json = await CreateHttp().GetStringAsync(FabricMeta + "versions/loader");
        using var document = JsonDocument.Parse(json);
        var names = new List<string>();
        foreach (var entry in document.RootElement.EnumerateArray())
        {
            var version = entry.GetProperty("version").GetString();
            if (!string.IsNullOrEmpty(version))
                names.Add(version);
        }
        return names;
    }

    /// <summary>
    /// 安装 Fabric:从 Fabric Meta 拉取现成的版本 JSON(profile/json),
    /// 改写 id 后写入 versions 目录,再走 CmlLib 安装依赖库。
    /// </summary>
    public async Task<string> InstallFabricAsync(
        string gameVersion, string loaderVersion, IProgress<LaunchProgress>? progress)
    {
        var profileJson = await CreateHttp().GetStringAsync(
            $"{FabricMeta}versions/loader/{gameVersion}/{loaderVersion}/profile/json");

        var versionName = $"{gameVersion}-fabric-{loaderVersion}";
        profileJson = RewriteVersionId(profileJson, versionName);

        var launcher = CreateLauncher();
        var versionsDir = Path.Combine(new MinecraftPath(launcher.MinecraftPath.BasePath).Versions, versionName);
        Directory.CreateDirectory(versionsDir);
        await File.WriteAllTextAsync(
            Path.Combine(versionsDir, versionName + ".json"), profileJson, Encoding.UTF8);

        HookProgress(launcher, progress);
        await launcher.InstallAsync(versionName);
        return versionName;
    }

    /// <summary>把 Fabric profile JSON 的 id 字段改写为自定义版本名。</summary>
    private static string RewriteVersionId(string profileJson, string versionName)
    {
        using var document = JsonDocument.Parse(profileJson);
        var options = new JsonWriterOptions { Indented = true };
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, options))
        {
            writer.WriteStartObject();
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.Name == "id")
                    writer.WriteString("id", versionName);
                else
                    property.WriteTo(writer);
            }
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    // ===== Forge =====

    public async Task<IReadOnlyList<LoaderVersionInfo>> GetForgeVersionsAsync(string gameVersion)
    {
        var launcher = CreateLauncher();
        var installer = new ForgeInstaller(launcher);
        var versions = await installer.GetForgeVersions(gameVersion);

        return versions
            .Select(v => new LoaderVersionInfo(
                v.ForgeVersionName,
                v.IsRecommendedVersion,
                v.IsLatestVersion))
            .OrderByDescending(v => v.Name)
            .ToList();
    }

    public async Task<string> InstallForgeAsync(
        string gameVersion, string forgeVersion, string? javaPath, IProgress<LaunchProgress>? progress)
    {
        var launcher = CreateLauncher();
        var installer = new ForgeInstaller(launcher);

        IProgress<InstallerProgressChangedEventArgs>? fileProgress = null;
        IProgress<ByteProgress>? byteProgress = null;
        IProgress<string>? output = null;
        if (progress is not null)
        {
            fileProgress = new Progress<InstallerProgressChangedEventArgs>(e => progress.Report(new LaunchProgress
            {
                CurrentFile = e.Name,
                ProgressedTasks = e.ProgressedTasks,
                TotalTasks = e.TotalTasks,
            }));
            byteProgress = new Progress<ByteProgress>(e => progress.Report(new LaunchProgress
            {
                ProgressedBytes = e.ProgressedBytes,
                TotalBytes = e.TotalBytes,
            }));
            output = new Progress<string>(s => progress.Report(new LaunchProgress { CurrentFile = s }));
        }

        var options = new ForgeInstallOptions
        {
            JavaPath = javaPath,
            FileProgress = fileProgress,
            ByteProgress = byteProgress,
            InstallerOutput = output,
            SkipIfAlreadyInstalled = true,
        };

        return await installer.Install(gameVersion, forgeVersion, options);
    }

    // ===== NeoForge =====

    public async Task<IReadOnlyList<LoaderVersionInfo>> GetNeoForgeVersionsAsync(string gameVersion)
    {
        var launcher = CreateLauncher();
        var installer = new NeoForgeInstaller(launcher);
        var versions = await installer.GetNeoForgeVersions(gameVersion);

        return versions
            .Select(v => new LoaderVersionInfo(v.NeoForgeVersionName, false, false))
            .OrderByDescending(v => v.Name)
            .ToList();
    }

    public async Task<string> InstallNeoForgeAsync(
        string gameVersion, string neoForgeVersion, string? javaPath, IProgress<LaunchProgress>? progress)
    {
        var launcher = CreateLauncher();
        var installer = new NeoForgeInstaller(launcher);

        IProgress<InstallerProgressChangedEventArgs>? fileProgress = null;
        if (progress is not null)
        {
            fileProgress = new Progress<InstallerProgressChangedEventArgs>(e => progress.Report(new LaunchProgress
            {
                CurrentFile = e.Name,
                ProgressedTasks = e.ProgressedTasks,
                TotalTasks = e.TotalTasks,
            }));
        }

        var options = new ForgeInstallOptions
        {
            JavaPath = javaPath,
            FileProgress = fileProgress,
            SkipIfAlreadyInstalled = true,
        };

        return await installer.Install(gameVersion, neoForgeVersion, options);
    }
}
