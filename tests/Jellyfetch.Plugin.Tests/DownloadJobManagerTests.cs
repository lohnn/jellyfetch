using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfetch.Plugin.Download;
using Jellyfetch.Plugin.Jobs;

namespace Jellyfetch.Plugin.Tests;

/// <summary>
/// Behavioural tests for the central <see cref="DownloadJobManager"/> queue: concurrency,
/// fan-out/group aggregation, retry/cancel guards, restart recovery, exception safety, and the
/// shutdown-vs-user-cancel distinction (I-096). Uses fake handlers/placer and a temp JobStore.
/// Runs in the serialized PluginState collection because it drives the static Plugin.Instance
/// config (MaxConcurrentDownloads / StagingPath).
/// </summary>
[Collection("PluginState")]
public sealed class DownloadJobManagerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "jf-mgr-" + Guid.NewGuid().ToString("N"));
    private readonly PluginConfigScope _scope;
    private readonly JobStore _store;

    public DownloadJobManagerTests()
    {
        _scope = new PluginConfigScope(_root);
        _scope.Configuration.StagingPath = Path.Combine(_root, "staging");
        _store = ManagerFactory.NewStore(_root);
    }

    public void Dispose()
    {
        _scope.Dispose();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    // --- helpers -----------------------------------------------------------

    private static async Task<bool> Eventually(Func<bool> predicate, int timeoutMs = 5000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (predicate())
            {
                return true;
            }

            await Task.Delay(15).ConfigureAwait(false);
        }

        return predicate();
    }

    private DownloadRequest Req(string url = "https://example.test/v") => new() { SourceUrl = url };

    // --- concurrency -------------------------------------------------------

    [Fact]
    public async Task PumpQueue_never_exceeds_MaxConcurrentDownloads()
    {
        _scope.Configuration.MaxConcurrentDownloads = 2;

        var running = 0;
        var peak = 0;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new FakeDownloadHandler
        {
            ExecuteFunc = async (item, _, _, ct) =>
            {
                var now = Interlocked.Increment(ref running);
                InterlockedMax(ref peak, now);
                await gate.Task.WaitAsync(ct).ConfigureAwait(false);
                Interlocked.Decrement(ref running);
                return new DownloadResult { Metadata = new MediaMetadata { Title = item.Title } };
            },
        };

        var manager = ManagerFactory.New(_store, new[] { handler });
        await manager.StartAsync(CancellationToken.None);

        for (var i = 0; i < 5; i++)
        {
            manager.Submit(Req($"https://example.test/{i}"));
        }

        // Give the pump time to settle at its ceiling.
        await Eventually(() => Volatile.Read(ref running) >= 2);
        await Task.Delay(150);

        Assert.True(Volatile.Read(ref peak) <= 2, $"peak concurrency was {peak}, expected <= 2");
        Assert.Equal(2, Volatile.Read(ref running));

        gate.SetResult(); // release all
        await Eventually(() => manager.GetJobs().All(j => j.State == JobState.Completed));
        await manager.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Queued_jobs_start_as_slots_free_up()
    {
        _scope.Configuration.MaxConcurrentDownloads = 1;

        var gates = new List<TaskCompletionSource>();
        var handler = new FakeDownloadHandler
        {
            ExecuteFunc = async (item, _, _, ct) =>
            {
                var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                lock (gates)
                {
                    gates.Add(tcs);
                }

                await tcs.Task.WaitAsync(ct).ConfigureAwait(false);
                return new DownloadResult { Metadata = new MediaMetadata { Title = item.Title } };
            },
        };

        var manager = ManagerFactory.New(_store, new[] { handler });
        await manager.StartAsync(CancellationToken.None);

        manager.Submit(Req("https://example.test/a"));
        manager.Submit(Req("https://example.test/b"));

        await Eventually(() => gates.Count >= 1);
        await Task.Delay(100);
        Assert.Single(gates); // only one running at MaxConcurrent=1

        gates[0].SetResult(); // free the slot
        await Eventually(() => gates.Count >= 2);
        lock (gates)
        {
            gates[1].SetResult();
        }

        await Eventually(() => manager.GetJobs().All(j => j.State == JobState.Completed));
        await manager.StopAsync(CancellationToken.None);
    }

    // --- fan-out / group aggregation --------------------------------------

    [Fact]
    public async Task Resolve_yielding_multiple_items_fans_out_into_child_jobs()
    {
        _scope.Configuration.MaxConcurrentDownloads = 4;

        var handler = new FakeDownloadHandler
        {
            ResolveFunc = (_, _) => Task.FromResult(new ResolveResult
            {
                GroupTitle = "My Series",
                Items = new List<DownloadItem>
                {
                    new() { Title = "Ep 1", SourceUrl = "https://example.test/1" },
                    new() { Title = "Ep 2", SourceUrl = "https://example.test/2" },
                    new() { Title = "Ep 3", SourceUrl = "https://example.test/3" },
                },
            }),
        };

        var manager = ManagerFactory.New(_store, new[] { handler });
        await manager.StartAsync(CancellationToken.None);

        var parent = manager.Submit(Req());
        await Eventually(() => manager.GetJob(parent.Id)!.State == JobState.Completed);

        var reloadedParent = manager.GetJob(parent.Id)!;
        Assert.True(reloadedParent.IsGroup);
        Assert.Equal("My Series", reloadedParent.Title);

        var children = manager.GetChildren(parent.Id);
        Assert.Equal(3, children.Count);
        Assert.All(children, c => Assert.Equal(parent.Id, c.ParentId));
        Assert.All(children, c => Assert.Equal(JobState.Completed, c.State));
        Assert.Equal(100, reloadedParent.Percent);

        await manager.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Group_parent_fails_when_all_children_fail_but_reports_partial_completion()
    {
        _scope.Configuration.MaxConcurrentDownloads = 4;

        var handler = new FakeDownloadHandler
        {
            ResolveFunc = (_, _) => Task.FromResult(new ResolveResult
            {
                GroupTitle = "Mixed",
                Items = new List<DownloadItem>
                {
                    new() { Title = "ok", SourceUrl = "https://example.test/ok" },
                    new() { Title = "bad", SourceUrl = "https://example.test/bad" },
                },
            }),
            ExecuteFunc = (item, _, _, _) => item.Title == "bad"
                ? throw new InvalidOperationException("boom")
                : Task.FromResult(new DownloadResult { Metadata = new MediaMetadata { Title = item.Title } }),
        };

        var manager = ManagerFactory.New(_store, new[] { handler });
        await manager.StartAsync(CancellationToken.None);

        var parent = manager.Submit(Req());
        await Eventually(() => manager.GetJob(parent.Id)!.IsTerminal);

        var p = manager.GetJob(parent.Id)!;
        // One child completed, one failed → aggregate is Completed (any success wins), not Failed.
        Assert.Equal(JobState.Completed, p.State);
        var children = manager.GetChildren(parent.Id);
        Assert.Contains(children, c => c.State == JobState.Completed);
        Assert.Contains(children, c => c.State == JobState.Failed);

        await manager.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Group_parent_is_Failed_when_every_child_fails()
    {
        _scope.Configuration.MaxConcurrentDownloads = 4;

        var handler = new FakeDownloadHandler
        {
            ResolveFunc = (_, _) => Task.FromResult(new ResolveResult
            {
                GroupTitle = "AllBad",
                Items = new List<DownloadItem>
                {
                    new() { Title = "a" },
                    new() { Title = "b" },
                },
            }),
            ExecuteFunc = (_, _, _, _) => throw new InvalidOperationException("nope"),
        };

        var manager = ManagerFactory.New(_store, new[] { handler });
        await manager.StartAsync(CancellationToken.None);

        var parent = manager.Submit(Req());
        await Eventually(() => manager.GetJob(parent.Id)!.IsTerminal);

        var p = manager.GetJob(parent.Id)!;
        Assert.Equal(JobState.Failed, p.State);
        Assert.Contains("2 of 2", p.ErrorMessage);

        await manager.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Resolve_yielding_zero_items_fails_the_job()
    {
        var handler = new FakeDownloadHandler
        {
            ResolveFunc = (_, _) => Task.FromResult(new ResolveResult { Items = Array.Empty<DownloadItem>() }),
        };

        var manager = ManagerFactory.New(_store, new[] { handler });
        await manager.StartAsync(CancellationToken.None);

        var job = manager.Submit(Req());
        await Eventually(() => manager.GetJob(job.Id)!.State == JobState.Failed);
        Assert.Contains("zero downloadable items", manager.GetJob(job.Id)!.ErrorMessage);

        await manager.StopAsync(CancellationToken.None);
    }

    // --- submit / handler routing -----------------------------------------

    [Fact]
    public void Submit_with_no_accepting_handler_throws_InvalidOperation()
    {
        var handler = new FakeDownloadHandler { CanHandleFunc = _ => false };
        var manager = ManagerFactory.New(_store, new[] { handler });

        Assert.Throws<InvalidOperationException>(() => manager.Submit(Req()));
    }

    [Fact]
    public async Task SafeCanHandle_swallows_a_throwing_CanHandle_and_tries_the_next_handler()
    {
        var throwing = new FakeDownloadHandler("throwing")
        {
            CanHandleFunc = _ => throw new InvalidOperationException("handler blew up in CanHandle"),
        };
        var good = new FakeDownloadHandler("good") { CanHandleFunc = _ => true };

        var manager = ManagerFactory.New(_store, new IDownloadHandler[] { throwing, good });
        await manager.StartAsync(CancellationToken.None);

        // Must not propagate; the good handler wins.
        var job = manager.Submit(Req());
        Assert.Equal("good", job.Kind);

        await manager.StopAsync(CancellationToken.None);
    }

    // --- exception safety --------------------------------------------------

    [Fact]
    public async Task Throwing_ExecuteAsync_marks_job_Failed_and_never_propagates()
    {
        var handler = new FakeDownloadHandler
        {
            ExecuteFunc = (_, _, _, _) => throw new InvalidOperationException("disk exploded"),
        };

        var manager = ManagerFactory.New(_store, new[] { handler });
        await manager.StartAsync(CancellationToken.None);

        var job = manager.Submit(Req());
        Assert.True(await Eventually(() => manager.GetJob(job.Id)!.State == JobState.Failed));
        Assert.Equal("disk exploded", manager.GetJob(job.Id)!.ErrorMessage);
        Assert.NotNull(manager.GetJob(job.Id)!.CompletedAt);

        await manager.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Throwing_ResolveAsync_marks_job_Failed()
    {
        var handler = new FakeDownloadHandler
        {
            ResolveFunc = (_, _) => throw new InvalidOperationException("resolve failed"),
        };

        var manager = ManagerFactory.New(_store, new[] { handler });
        await manager.StartAsync(CancellationToken.None);

        var job = manager.Submit(Req());
        Assert.True(await Eventually(() => manager.GetJob(job.Id)!.State == JobState.Failed));
        Assert.Equal("resolve failed", manager.GetJob(job.Id)!.ErrorMessage);

        await manager.StopAsync(CancellationToken.None);
    }

    // --- retry / cancel guards --------------------------------------------

    [Fact]
    public async Task Cancel_of_a_running_job_transitions_to_Cancelled()
    {
        _scope.Configuration.MaxConcurrentDownloads = 1;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new FakeDownloadHandler
        {
            ExecuteFunc = async (item, _, _, ct) =>
            {
                started.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
                return new DownloadResult { Metadata = new MediaMetadata { Title = item.Title } };
            },
        };

        var manager = ManagerFactory.New(_store, new[] { handler });
        await manager.StartAsync(CancellationToken.None);

        var job = manager.Submit(Req());
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(manager.Cancel(job.Id));
        Assert.True(await Eventually(() => manager.GetJob(job.Id)!.State == JobState.Cancelled));

        await manager.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Cancel_of_a_queued_job_transitions_to_Cancelled_immediately()
    {
        _scope.Configuration.MaxConcurrentDownloads = 1;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new FakeDownloadHandler
        {
            ExecuteFunc = async (item, _, _, ct) =>
            {
                await gate.Task.WaitAsync(ct).ConfigureAwait(false);
                return new DownloadResult { Metadata = new MediaMetadata { Title = item.Title } };
            },
        };

        var manager = ManagerFactory.New(_store, new[] { handler });
        await manager.StartAsync(CancellationToken.None);

        manager.Submit(Req("https://example.test/running"));
        var queued = manager.Submit(Req("https://example.test/queued"));
        await Eventually(() => manager.GetJob(queued.Id)!.State == JobState.Queued);

        Assert.True(manager.Cancel(queued.Id));
        Assert.Equal(JobState.Cancelled, manager.GetJob(queued.Id)!.State);

        gate.SetResult();
        await manager.StopAsync(CancellationToken.None);
    }

    [Fact]
    public void Cancel_of_a_nonexistent_job_returns_false()
    {
        var manager = ManagerFactory.New(_store, new[] { new FakeDownloadHandler() });
        Assert.False(manager.Cancel(Guid.NewGuid()));
    }

    [Fact]
    public async Task Cancel_of_an_already_terminal_job_returns_false()
    {
        var handler = new FakeDownloadHandler();
        var manager = ManagerFactory.New(_store, new[] { handler });
        await manager.StartAsync(CancellationToken.None);

        var job = manager.Submit(Req());
        await Eventually(() => manager.GetJob(job.Id)!.State == JobState.Completed);

        Assert.False(manager.Cancel(job.Id)); // 409-equivalent

        await manager.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Retry_of_a_failed_job_requeues_and_clears_error()
    {
        var attempts = 0;
        var handler = new FakeDownloadHandler
        {
            ExecuteFunc = (item, _, _, _) =>
            {
                if (Interlocked.Increment(ref attempts) == 1)
                {
                    throw new InvalidOperationException("first attempt fails");
                }

                return Task.FromResult(new DownloadResult { Metadata = new MediaMetadata { Title = item.Title } });
            },
        };

        var manager = ManagerFactory.New(_store, new[] { handler });
        await manager.StartAsync(CancellationToken.None);

        var job = manager.Submit(Req());
        await Eventually(() => manager.GetJob(job.Id)!.State == JobState.Failed);

        Assert.True(manager.Retry(job.Id));
        Assert.True(await Eventually(() => manager.GetJob(job.Id)!.State == JobState.Completed));
        Assert.Null(manager.GetJob(job.Id)!.ErrorMessage);

        await manager.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Retry_of_a_non_terminal_job_returns_false()
    {
        _scope.Configuration.MaxConcurrentDownloads = 1;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new FakeDownloadHandler
        {
            ExecuteFunc = async (item, _, _, ct) =>
            {
                await gate.Task.WaitAsync(ct).ConfigureAwait(false);
                return new DownloadResult { Metadata = new MediaMetadata { Title = item.Title } };
            },
        };

        var manager = ManagerFactory.New(_store, new[] { handler });
        await manager.StartAsync(CancellationToken.None);

        var job = manager.Submit(Req());
        await Eventually(() => manager.GetJob(job.Id)!.State == JobState.Downloading);

        Assert.False(manager.Retry(job.Id)); // only Failed/Cancelled are retryable

        gate.SetResult();
        await manager.StopAsync(CancellationToken.None);
    }

    [Fact]
    public void Retry_of_a_nonexistent_job_returns_false()
    {
        var manager = ManagerFactory.New(_store, new[] { new FakeDownloadHandler() });
        Assert.False(manager.Retry(Guid.NewGuid()));
    }

    // --- delete guards -----------------------------------------------------

    [Fact]
    public async Task Delete_removes_a_terminal_job()
    {
        var handler = new FakeDownloadHandler();
        var manager = ManagerFactory.New(_store, new[] { handler });
        await manager.StartAsync(CancellationToken.None);

        var job = manager.Submit(Req());
        await Eventually(() => manager.GetJob(job.Id)!.State == JobState.Completed);

        Assert.True(manager.Delete(job.Id));
        Assert.Null(manager.GetJob(job.Id));

        await manager.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Delete_of_an_active_job_returns_false()
    {
        _scope.Configuration.MaxConcurrentDownloads = 1;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new FakeDownloadHandler
        {
            ExecuteFunc = async (item, _, _, ct) =>
            {
                await gate.Task.WaitAsync(ct).ConfigureAwait(false);
                return new DownloadResult { Metadata = new MediaMetadata { Title = item.Title } };
            },
        };

        var manager = ManagerFactory.New(_store, new[] { handler });
        await manager.StartAsync(CancellationToken.None);

        var job = manager.Submit(Req());
        await Eventually(() => manager.GetJob(job.Id)!.State == JobState.Downloading);

        Assert.False(manager.Delete(job.Id));
        Assert.NotNull(manager.GetJob(job.Id));

        gate.SetResult();
        await manager.StopAsync(CancellationToken.None);
    }

    // --- restart recovery --------------------------------------------------

    [Fact]
    public async Task StartAsync_marks_mid_flight_jobs_Failed_and_retryable()
    {
        // Seed the store directly with jobs frozen mid-flight, as a crash would leave them.
        var resolving = new DownloadJob { State = JobState.Resolving, Title = "resolving" };
        var downloading = new DownloadJob { State = JobState.Downloading, Title = "downloading" };
        var processing = new DownloadJob { State = JobState.Processing, Title = "processing" };
        var queued = new DownloadJob { State = JobState.Queued, Title = "queued", Kind = "fake" };
        var completed = new DownloadJob { State = JobState.Completed, Title = "done" };
        _store.Save(new[] { resolving, downloading, processing, queued, completed });

        // A handler that blocks so the recovered 'queued' job doesn't race to completion.
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new FakeDownloadHandler
        {
            ExecuteFunc = async (item, _, _, ct) =>
            {
                await gate.Task.WaitAsync(ct).ConfigureAwait(false);
                return new DownloadResult { Metadata = new MediaMetadata { Title = item.Title } };
            },
        };

        var manager = ManagerFactory.New(_store, new[] { handler });
        await manager.StartAsync(CancellationToken.None);

        foreach (var id in new[] { resolving.Id, downloading.Id, processing.Id })
        {
            var j = manager.GetJob(id)!;
            Assert.Equal(JobState.Failed, j.State);
            Assert.Contains("Interrupted by server restart", j.ErrorMessage);
            Assert.NotNull(j.CompletedAt);
        }

        // Completed stays completed; queued is resumed (not failed).
        Assert.Equal(JobState.Completed, manager.GetJob(completed.Id)!.State);
        Assert.NotEqual(JobState.Failed, manager.GetJob(queued.Id)!.State);

        gate.SetResult();
        await manager.StopAsync(CancellationToken.None);
    }

    // --- the critical trap: shutdown vs user cancel (I-096) ----------------

    [Fact]
    public async Task Server_shutdown_marks_in_flight_job_retryable_not_Cancelled()
    {
        _scope.Configuration.MaxConcurrentDownloads = 1;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new FakeDownloadHandler
        {
            ExecuteFunc = async (item, _, _, ct) =>
            {
                started.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
                return new DownloadResult { Metadata = new MediaMetadata { Title = item.Title } };
            },
        };

        var manager = ManagerFactory.New(_store, new[] { handler });
        await manager.StartAsync(CancellationToken.None);

        var job = manager.Submit(Req());
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Simulate a graceful SIGTERM: the hosted service is stopped.
        await manager.StopAsync(CancellationToken.None);

        // The in-flight job observed OperationCanceledException via the shutdown CTS. It must be
        // Failed+retryable ("Interrupted by restart"), NOT Cancelled — the I-096 distinction.
        Assert.True(await Eventually(() => manager.GetJob(job.Id)!.IsTerminal));
        var j = manager.GetJob(job.Id)!;
        Assert.Equal(JobState.Failed, j.State);
        Assert.Contains("Interrupted by server restart", j.ErrorMessage);
        Assert.NotEqual(JobState.Cancelled, j.State);
    }

    [Fact]
    public async Task User_cancel_and_shutdown_produce_distinct_terminal_states()
    {
        _scope.Configuration.MaxConcurrentDownloads = 2;
        var startedA = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var startedB = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new FakeDownloadHandler
        {
            ExecuteFunc = async (item, _, _, ct) =>
            {
                if (item.SourceUrl!.EndsWith("A", StringComparison.Ordinal))
                {
                    startedA.TrySetResult();
                }
                else
                {
                    startedB.TrySetResult();
                }

                await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
                return new DownloadResult { Metadata = new MediaMetadata { Title = item.Title } };
            },
        };

        var manager = ManagerFactory.New(_store, new[] { handler });
        await manager.StartAsync(CancellationToken.None);

        var jobA = manager.Submit(Req("https://example.test/A"));
        var jobB = manager.Submit(Req("https://example.test/B"));
        await startedA.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await startedB.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // User-cancel A only.
        Assert.True(manager.Cancel(jobA.Id));
        Assert.True(await Eventually(() => manager.GetJob(jobA.Id)!.State == JobState.Cancelled));

        // Now shut the server down; B was interrupted by shutdown.
        await manager.StopAsync(CancellationToken.None);
        Assert.True(await Eventually(() => manager.GetJob(jobB.Id)!.IsTerminal));

        var a = manager.GetJob(jobA.Id)!;
        var b = manager.GetJob(jobB.Id)!;
        Assert.Equal(JobState.Cancelled, a.State);
        Assert.Equal(JobState.Failed, b.State);
        Assert.Contains("Interrupted by server restart", b.ErrorMessage);
        // The user-cancel carries no shutdown wording.
        Assert.DoesNotContain("restart", a.ErrorMessage ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    // --- completion path: metadata + library scan -------------------------

    [Fact]
    public async Task Completion_copies_episode_metadata_and_reports_library_change()
    {
        var placedFile = Path.Combine(_root, "library", "Show", "ep.mkv");
        var monitor = new FakeLibraryMonitor();
        var placer = new FakeMediaPlacer
        {
            PlaceFunc = (_, _, _, _) => Task.FromResult(new PlacementResult
            {
                FinalPaths = new List<string> { placedFile },
                LibraryRootUsed = Path.Combine(_root, "library"),
            }),
        };
        var handler = new FakeDownloadHandler
        {
            ExecuteFunc = (item, _, _, _) => Task.FromResult(new DownloadResult
            {
                Metadata = new MediaMetadata
                {
                    Title = "Kaarina Kaikkonen",
                    Category = MediaCategory.Series,
                    SeriesName = "Bortom bilden",
                    SeasonNumber = 2024,
                    EpisodeNumber = 6,
                },
            }),
        };

        var manager = ManagerFactory.New(_store, new[] { handler }, placer, monitor);
        await manager.StartAsync(CancellationToken.None);

        var job = manager.Submit(Req());
        await Eventually(() => manager.GetJob(job.Id)!.State == JobState.Completed);

        var j = manager.GetJob(job.Id)!;
        Assert.Equal("Bortom bilden", j.SeriesName);
        Assert.Equal(2024, j.SeasonNumber);
        Assert.Equal(6, j.EpisodeNumber);
        Assert.Equal(new[] { placedFile }, j.FinalPaths);
        Assert.Contains(placedFile, monitor.Reported);

        await manager.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Completion_normalizes_NA_series_metadata_to_null()
    {
        var handler = new FakeDownloadHandler
        {
            ExecuteFunc = (_, _, _, _) => Task.FromResult(new DownloadResult
            {
                Metadata = new MediaMetadata { Title = "NA", SeriesName = "NA", Category = MediaCategory.Other },
            }),
        };

        var manager = ManagerFactory.New(_store, new[] { handler });
        await manager.StartAsync(CancellationToken.None);

        var job = manager.Submit(Req());
        await Eventually(() => manager.GetJob(job.Id)!.State == JobState.Completed);

        var j = manager.GetJob(job.Id)!;
        Assert.Null(j.SeriesName);
        Assert.Null(j.EpisodeTitle);

        await manager.StopAsync(CancellationToken.None);
    }

    // --- post-download fan-out (season-pack torrent → per-episode display children) -------

    [Fact]
    public async Task Post_download_children_materialize_as_a_group_of_completed_rows()
    {
        var libraryRoot = Path.Combine(_root, "library");
        var placer = new FakeMediaPlacer
        {
            PlaceFunc = (_, _, _, _) => Task.FromResult(new PlacementResult
            {
                FinalPaths = new List<string> { Path.Combine(libraryRoot, "Sopranos") },
                LibraryRootUsed = libraryRoot,
            }),
        };
        var handler = new FakeDownloadHandler
        {
            ExecuteFunc = (item, _, _, _) => Task.FromResult(new DownloadResult
            {
                PreLaidOut = true,
                Metadata = new MediaMetadata { Title = "Sopranos S01", Category = MediaCategory.Series },
                Children = new List<DownloadChild>
                {
                    new()
                    {
                        RelativePath = Path.Combine("Sopranos", "Season 01", "Sopranos - S01E01.mkv"),
                        Metadata = new MediaMetadata { Title = "Pilot", Category = MediaCategory.Series, SeriesName = "Sopranos", SeasonNumber = 1, EpisodeNumber = 1 },
                    },
                    new()
                    {
                        RelativePath = Path.Combine("Sopranos", "Season 01", "Sopranos - S01E02.mkv"),
                        Metadata = new MediaMetadata { Title = "46 Long", Category = MediaCategory.Series, SeriesName = "Sopranos", SeasonNumber = 1, EpisodeNumber = 2 },
                    },
                },
            }),
        };

        var monitor = new FakeLibraryMonitor();
        var manager = ManagerFactory.New(_store, new[] { handler }, placer, monitor);
        await manager.StartAsync(CancellationToken.None);

        var job = manager.Submit(Req());
        await Eventually(() => manager.GetJob(job.Id)!.IsTerminal);

        var parent = manager.GetJob(job.Id)!;
        Assert.True(parent.IsGroup);
        Assert.Equal(JobState.Completed, parent.State);
        Assert.Equal(100, parent.Percent);

        var children = manager.GetChildren(job.Id);
        Assert.Equal(2, children.Count);
        Assert.All(children, c => Assert.Equal(JobState.Completed, c.State));
        Assert.All(children, c => Assert.Equal(parent.Id, c.ParentId));

        // Library-relative RelativePath resolved against LibraryRootUsed → absolute.
        var e1 = children.Single(c => c.EpisodeNumber == 1);
        Assert.Equal("Pilot", e1.EpisodeTitle);
        Assert.Equal("Sopranos", e1.SeriesName);
        Assert.Equal(Path.Combine(libraryRoot, "Sopranos", "Season 01", "Sopranos - S01E01.mkv"), e1.FinalPaths.Single());

        // Each episode file reported for a scoped scan.
        Assert.Contains(e1.FinalPaths.Single(), monitor.Reported);

        await manager.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Single_child_breakdown_stays_a_flat_completed_job()
    {
        // Children with a single entry must NOT fan out — it's still one episode.
        var handler = new FakeDownloadHandler
        {
            ExecuteFunc = (_, _, _, _) => Task.FromResult(new DownloadResult
            {
                Metadata = new MediaMetadata { Title = "Solo", Category = MediaCategory.Series, SeriesName = "Show", SeasonNumber = 1, EpisodeNumber = 1 },
                Children = new List<DownloadChild>
                {
                    new() { RelativePath = "Show/S01E01.mkv", Metadata = new MediaMetadata { Title = "Solo", SeriesName = "Show" } },
                },
            }),
        };

        var manager = ManagerFactory.New(_store, new[] { handler });
        await manager.StartAsync(CancellationToken.None);

        var job = manager.Submit(Req());
        await Eventually(() => manager.GetJob(job.Id)!.State == JobState.Completed);

        var j = manager.GetJob(job.Id)!;
        Assert.False(j.IsGroup);
        Assert.Empty(manager.GetChildren(job.Id));
        // Single-job metadata copy still applies.
        Assert.Equal("Show", j.SeriesName);
        Assert.Equal(1, j.EpisodeNumber);

        await manager.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Post_download_children_survive_persistence_round_trip_as_completed()
    {
        var libraryRoot = Path.Combine(_root, "library");
        var placer = new FakeMediaPlacer
        {
            PlaceFunc = (_, _, _, _) => Task.FromResult(new PlacementResult
            {
                FinalPaths = new List<string> { libraryRoot },
                LibraryRootUsed = libraryRoot,
            }),
        };
        var handler = new FakeDownloadHandler
        {
            ExecuteFunc = (_, _, _, _) => Task.FromResult(new DownloadResult
            {
                Metadata = new MediaMetadata { Title = "Pack" },
                Children = new List<DownloadChild>
                {
                    new() { RelativePath = "S/e1.mkv", Metadata = new MediaMetadata { Title = "e1", SeriesName = "S", SeasonNumber = 1, EpisodeNumber = 1 } },
                    new() { RelativePath = "S/e2.mkv", Metadata = new MediaMetadata { Title = "e2", SeriesName = "S", SeasonNumber = 1, EpisodeNumber = 2 } },
                },
            }),
        };

        var manager = ManagerFactory.New(_store, new[] { handler }, placer);
        await manager.StartAsync(CancellationToken.None);
        var job = manager.Submit(Req());
        await Eventually(() => manager.GetJob(job.Id)!.IsTerminal);
        await manager.StopAsync(CancellationToken.None);

        // Reload from disk (fresh manager) — born-terminal children must NOT be resurrected as mid-flight.
        var manager2 = ManagerFactory.New(_store, new[] { handler }, placer);
        await manager2.StartAsync(CancellationToken.None);

        var parent = manager2.GetJob(job.Id)!;
        Assert.True(parent.IsGroup);
        Assert.Equal(JobState.Completed, parent.State);
        var children = manager2.GetChildren(job.Id);
        Assert.Equal(2, children.Count);
        Assert.All(children, c => Assert.Equal(JobState.Completed, c.State));

        await manager2.StopAsync(CancellationToken.None);
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int seen;
        do
        {
            seen = Volatile.Read(ref target);
            if (value <= seen)
            {
                return;
            }
        }
        while (Interlocked.CompareExchange(ref target, value, seen) != seen);
    }
}
