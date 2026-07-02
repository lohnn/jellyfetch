using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfetch.Plugin.Jobs;

/// <summary>
/// Persists job state as a JSON file under the Jellyfin data path so jobs survive server restarts.
/// Writes are throttled by the caller (DownloadJobManager); this class is just safe file I/O.
/// </summary>
public class JobStore
{
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _filePath;
    private readonly ILogger<JobStore> _logger;
    private readonly object _lock = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="JobStore"/> class.
    /// </summary>
    /// <param name="applicationPaths">Jellyfin application paths.</param>
    /// <param name="logger">Logger.</param>
    public JobStore(IApplicationPaths applicationPaths, ILogger<JobStore> logger)
    {
        _logger = logger;
        DataDirectory = Path.Combine(applicationPaths.DataPath, "jellyfetch");
        Directory.CreateDirectory(DataDirectory);
        _filePath = Path.Combine(DataDirectory, "jobs.json");
    }

    /// <summary>Gets the plugin data directory (&lt;dataPath&gt;/jellyfetch).</summary>
    public string DataDirectory { get; }

    /// <summary>Loads all persisted jobs. Returns an empty list when no file exists or it is corrupt.</summary>
    /// <returns>The persisted jobs.</returns>
    public List<DownloadJob> Load()
    {
        lock (_lock)
        {
            try
            {
                if (!File.Exists(_filePath))
                {
                    return new List<DownloadJob>();
                }

                var json = File.ReadAllText(_filePath);
                return JsonSerializer.Deserialize<List<DownloadJob>>(json, _jsonOptions) ?? new List<DownloadJob>();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "JellyFetch: failed to load job store from {Path}; starting with empty job list", _filePath);
                return new List<DownloadJob>();
            }
        }
    }

    /// <summary>Atomically persists the given jobs (write temp + rename).</summary>
    /// <param name="jobs">Snapshot of all jobs.</param>
    public void Save(IReadOnlyCollection<DownloadJob> jobs)
    {
        lock (_lock)
        {
            try
            {
                var tmp = _filePath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(jobs, _jsonOptions));
                File.Move(tmp, _filePath, overwrite: true);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "JellyFetch: failed to persist job store to {Path}", _filePath);
            }
        }
    }
}
