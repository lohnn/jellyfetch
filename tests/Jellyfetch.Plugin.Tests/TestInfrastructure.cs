using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfetch.Plugin;
using Jellyfetch.Plugin.Configuration;
using Jellyfetch.Plugin.Download;
using Jellyfetch.Plugin.Jobs;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfetch.Plugin.Tests;

/// <summary>
/// Minimal <see cref="IApplicationPaths"/> pointing every path at one temp root, so
/// <see cref="JobStore"/> and a real <see cref="Plugin"/> can be constructed hermetically.
/// </summary>
internal sealed class FakeApplicationPaths : IApplicationPaths
{
    private readonly string _root;

    public FakeApplicationPaths(string root)
    {
        _root = root;
        Directory.CreateDirectory(root);
    }

    public string ProgramDataPath => _root;

    public string WebPath => _root;

    public string ProgramSystemPath => _root;

    public string DataPath => _root;

    public string ImageCachePath => _root;

    public string PluginsPath => _root;

    public string PluginConfigurationsPath => _root;

    public string LogDirectoryPath => _root;

    public string ConfigurationDirectoryPath => _root;

    public string SystemConfigurationFilePath => Path.Combine(_root, "system.xml");

    public string CachePath => _root;

    public string TempDirectory => _root;

    public string VirtualDataPath => _root;

    public string TrickplayPath => _root;

    public string BackupPath => _root;

    public void MakeSanityCheckOrThrow()
    {
    }

    public void CreateAndCheckMarker(string directory, string marker, bool recursive = false)
    {
    }
}

/// <summary>
/// A no-op <see cref="IXmlSerializer"/>: it never finds a config file on disk, so a real
/// <see cref="Plugin"/>'s <c>Configuration</c> getter returns a fresh default we can mutate.
/// </summary>
internal sealed class FakeXmlSerializer : IXmlSerializer
{
    public object? DeserializeFromStream(Type type, Stream stream) => Activator.CreateInstance(type);

    public void SerializeToStream(object obj, Stream stream)
    {
    }

    public void SerializeToFile(object obj, string file)
    {
    }

    public object? DeserializeFromFile(Type type, string file) => Activator.CreateInstance(type);

    public object? DeserializeFromBytes(Type type, byte[] buffer) => Activator.CreateInstance(type);
}

/// <summary>
/// Sets <see cref="Plugin.Instance"/> to a real Plugin backed by temp paths so the manager's
/// live-config reads (<c>Plugin.Instance.Configuration</c>) resolve to a config we control.
/// Because <see cref="Plugin.Instance"/> is a static singleton, tests that use this must belong
/// to the <see cref="PluginStateCollection"/> so xUnit runs them serially.
/// Restores the previous instance on dispose.
/// </summary>
internal sealed class PluginConfigScope : IDisposable
{
    private readonly Plugin? _previous;
    private readonly string _tempRoot;

    public PluginConfigScope(string dataRoot)
    {
        _tempRoot = dataRoot;
        _previous = Plugin.Instance;
        Plugin = new Plugin(new FakeApplicationPaths(dataRoot), new FakeXmlSerializer());
    }

    public Plugin Plugin { get; }

    public PluginConfiguration Configuration => Plugin.Configuration;

    public void Dispose()
    {
        // Restore the previously installed instance (usually null) so parallel suites see a clean slate.
        typeof(Plugin).GetProperty(nameof(Plugin.Instance))!.SetValue(null, _previous);
    }
}

/// <summary>xUnit collection that serializes every test touching the static Plugin.Instance / config.</summary>
[CollectionDefinition("PluginState", DisableParallelization = true)]
public sealed class PluginStateCollection
{
}

/// <summary>A no-op library monitor — records reported paths for assertions.</summary>
internal sealed class FakeLibraryMonitor : ILibraryMonitor
{
    public List<string> Reported { get; } = new();

    public void ReportFileSystemChangeBeginning(string path)
    {
    }

    public void ReportFileSystemChangeComplete(string path, bool refreshPath)
    {
    }

    public void ReportFileSystemChanged(string path) => Reported.Add(path);

    public void Stop()
    {
    }

    public void Start()
    {
    }
}

/// <summary>
/// A programmable <see cref="IDownloadHandler"/> test double. Every phase (CanHandle / Resolve /
/// Execute) is a delegate the test supplies, so a single class covers all manager scenarios.
/// </summary>
internal sealed class FakeDownloadHandler : IDownloadHandler
{
    public FakeDownloadHandler(string kind = "fake")
    {
        Kind = kind;
    }

    public string Kind { get; }

    public Func<DownloadRequest, bool> CanHandleFunc { get; set; } = _ => true;

    public Func<DownloadRequest, CancellationToken, Task<ResolveResult>> ResolveFunc { get; set; } =
        (req, _) => Task.FromResult(new ResolveResult
        {
            Items = new List<DownloadItem> { new() { Title = "resolved", SourceUrl = req.SourceUrl } },
        });

    public Func<DownloadItem, string, IProgress<JobProgress>, CancellationToken, Task<DownloadResult>> ExecuteFunc { get; set; } =
        (item, _, _, _) => Task.FromResult(new DownloadResult
        {
            Files = Array.Empty<string>(),
            Metadata = new MediaMetadata { Title = item.Title, Category = MediaCategory.Other },
        });

    public bool CanHandle(DownloadRequest request) => CanHandleFunc(request);

    public Task<ResolveResult> ResolveAsync(DownloadRequest request, CancellationToken cancellationToken) =>
        ResolveFunc(request, cancellationToken);

    public Task<DownloadResult> ExecuteAsync(DownloadItem item, string stagingDirectory, IProgress<JobProgress> progress, CancellationToken cancellationToken) =>
        ExecuteFunc(item, stagingDirectory, progress, cancellationToken);
}

/// <summary>A media placer double that echoes staging files back as final paths.</summary>
internal sealed class FakeMediaPlacer : IMediaPlacer
{
    public Func<DownloadResult, string, CancellationToken, Task<PlacementResult>>? PlaceFunc { get; set; }

    public int Calls { get; private set; }

    public Task<PlacementResult> PlaceAsync(DownloadResult result, string stagingDirectory, CancellationToken cancellationToken)
    {
        Calls++;
        if (PlaceFunc is not null)
        {
            return PlaceFunc(result, stagingDirectory, cancellationToken);
        }

        return Task.FromResult(new PlacementResult
        {
            FinalPaths = new List<string>(result.Files),
            LibraryRootUsed = stagingDirectory,
        });
    }
}

/// <summary>Builds a <see cref="DownloadJobManager"/> around fakes and a real (temp) <see cref="JobStore"/>.</summary>
internal static class ManagerFactory
{
    public static JobStore NewStore(string dataRoot) =>
        new(new FakeApplicationPaths(dataRoot), NullLogger<JobStore>.Instance);

    public static DownloadJobManager New(
        JobStore store,
        IEnumerable<IDownloadHandler> handlers,
        IMediaPlacer? placer = null,
        FakeLibraryMonitor? monitor = null) =>
        new(
            handlers,
            placer ?? new FakeMediaPlacer(),
            store,
            monitor ?? new FakeLibraryMonitor(),
            NullLogger<DownloadJobManager>.Instance);
}
