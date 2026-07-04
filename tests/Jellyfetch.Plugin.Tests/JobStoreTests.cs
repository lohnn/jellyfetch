using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfetch.Plugin.Download;
using Jellyfetch.Plugin.Jobs;

namespace Jellyfetch.Plugin.Tests;

/// <summary>
/// Persistence round-trip and corruption-resilience for <see cref="JobStore"/> — the on-disk
/// job history that lets downloads survive server restarts.
/// </summary>
public sealed class JobStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "jf-store-" + Guid.NewGuid().ToString("N"));

    private string JobsFile => Path.Combine(_root, "jellyfetch", "jobs.json");

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void Save_then_load_round_trips_all_fields()
    {
        var store = ManagerFactory.NewStore(_root);
        var job = new DownloadJob
        {
            Kind = "webMedia",
            State = JobState.Completed,
            Title = "My Show S01E02",
            SourceUrl = "https://example.test/x",
            Percent = 100,
            SeriesName = "My Show",
            SeasonNumber = 2024,
            EpisodeNumber = 2,
            EpisodeTitle = "Avsnitt 2",
            FinalPaths = new List<string> { "/library/My Show/S01E02.mkv" },
            Request = new DownloadRequest { SourceUrl = "https://example.test/x", CategoryHint = MediaCategory.Series },
            Item = new DownloadItem { Title = "ep", SourceUrl = "https://example.test/x", Category = MediaCategory.Series },
        };

        store.Save(new[] { job });

        var reloaded = ManagerFactory.NewStore(_root).Load();
        var back = Assert.Single(reloaded);
        Assert.Equal(job.Id, back.Id);
        Assert.Equal(job.Kind, back.Kind);
        Assert.Equal(JobState.Completed, back.State);
        Assert.Equal(job.Title, back.Title);
        Assert.Equal(job.SeriesName, back.SeriesName);
        Assert.Equal(2024, back.SeasonNumber);
        Assert.Equal(2, back.EpisodeNumber);
        Assert.Equal("Avsnitt 2", back.EpisodeTitle);
        Assert.Equal(job.FinalPaths, back.FinalPaths);
        Assert.NotNull(back.Item);
        Assert.Equal(MediaCategory.Series, back.Item!.Category);
    }

    [Fact]
    public void State_enum_is_persisted_as_a_string()
    {
        var store = ManagerFactory.NewStore(_root);
        store.Save(new[] { new DownloadJob { State = JobState.Downloading } });

        var json = File.ReadAllText(JobsFile);
        Assert.Contains("\"Downloading\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"State\": 2", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Save_is_atomic_leaving_no_tmp_file_behind()
    {
        var store = ManagerFactory.NewStore(_root);
        store.Save(new[] { new DownloadJob { Title = "a" } });

        var dir = Path.Combine(_root, "jellyfetch");
        Assert.True(File.Exists(JobsFile));
        Assert.Empty(Directory.GetFiles(dir, "*.tmp"));
    }

    [Fact]
    public void Load_returns_empty_when_no_file_exists()
    {
        var store = ManagerFactory.NewStore(_root);
        Assert.Empty(store.Load());
    }

    [Fact]
    public void Load_of_corrupt_json_does_not_throw_and_returns_empty()
    {
        Directory.CreateDirectory(Path.Combine(_root, "jellyfetch"));
        File.WriteAllText(JobsFile, "{ this is not valid json ][");

        var store = ManagerFactory.NewStore(_root);
        var jobs = store.Load(); // must not throw on startup
        Assert.Empty(jobs);
    }

    [Fact]
    public void Load_of_truncated_json_does_not_throw()
    {
        // Simulates a crash mid-write (before atomic rename existed / partial content).
        var store = ManagerFactory.NewStore(_root);
        store.Save(Enumerable.Range(0, 3).Select(_ => new DownloadJob { Title = "x" }).ToList());
        var good = File.ReadAllText(JobsFile);
        File.WriteAllText(JobsFile, good.Substring(0, good.Length / 2)); // chop it in half

        Assert.Empty(ManagerFactory.NewStore(_root).Load());
    }

    [Fact]
    public void Save_overwrites_previous_content()
    {
        var store = ManagerFactory.NewStore(_root);
        store.Save(new[] { new DownloadJob { Title = "first" }, new DownloadJob { Title = "second" } });
        store.Save(new[] { new DownloadJob { Title = "only" } });

        var back = Assert.Single(ManagerFactory.NewStore(_root).Load());
        Assert.Equal("only", back.Title);
    }
}
