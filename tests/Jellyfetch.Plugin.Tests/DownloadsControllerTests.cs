using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfetch.Plugin.Api;
using Jellyfetch.Plugin.Download;
using Jellyfetch.Plugin.Jobs;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfetch.Plugin.Tests;

/// <summary>
/// Status-code / validation behaviour of <see cref="DownloadsController"/>. The controller is a thin
/// shell over <see cref="DownloadJobManager"/>, so these pin the HTTP contract (201/Location, 400,
/// 204/404/409) that android-share depends on — including the 10 MiB torrent cap and the
/// "never deletes media, only terminal jobs" delete guard.
/// </summary>
[Collection("PluginState")]
public sealed class DownloadsControllerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "jf-ctrl-" + Guid.NewGuid().ToString("N"));
    private readonly PluginConfigScope _scope;
    private readonly JobStore _store;

    public DownloadsControllerTests()
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

    private DownloadJobManager ManagerWith(IDownloadHandler handler, IMediaPlacer? placer = null) =>
        ManagerFactory.New(_store, new[] { handler }, placer);

    private static DownloadsController ControllerFor(DownloadJobManager manager, byte[]? body = null, long? contentLength = null)
    {
        var ctrl = new DownloadsController(manager);
        var http = new DefaultHttpContext();
        if (body is not null)
        {
            http.Request.Body = new MemoryStream(body);
            http.Request.ContentLength = contentLength ?? body.Length;
        }

        ctrl.ControllerContext = new Microsoft.AspNetCore.Mvc.ControllerContext { HttpContext = http };
        return ctrl;
    }

    private static async Task<bool> Eventually(Func<bool> predicate, int timeoutMs = 5000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
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

    // A handler that parks in Downloading forever so jobs stay non-terminal for guard tests.
    private static FakeDownloadHandler ParkingHandler(TaskCompletionSource gate) => new()
    {
        ExecuteFunc = async (item, _, _, ct) =>
        {
            await gate.Task.WaitAsync(ct).ConfigureAwait(false);
            return new DownloadResult { Metadata = new MediaMetadata { Title = item.Title } };
        },
    };

    // --- Submit ------------------------------------------------------------

    [Fact]
    public void Submit_returns_201_with_location_header()
    {
        var manager = ManagerWith(new FakeDownloadHandler { CanHandleFunc = _ => true });
        var ctrl = ControllerFor(manager);

        var result = ctrl.Submit(new SubmitDownloadRequest { Url = "https://example.test/v" });

        var created = Assert.IsType<CreatedResult>(result.Result);
        Assert.Equal(StatusCodes.Status201Created, created.StatusCode);
        var dto = Assert.IsType<JobDto>(created.Value);
        Assert.Equal($"/Jellyfetch/Downloads/{dto.Id}", created.Location);
    }

    [Fact]
    public void Submit_with_blank_url_returns_400()
    {
        var manager = ManagerWith(new FakeDownloadHandler());
        var ctrl = ControllerFor(manager);

        var result = ctrl.Submit(new SubmitDownloadRequest { Url = "   " });
        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public void Submit_with_unknown_category_returns_400()
    {
        var manager = ManagerWith(new FakeDownloadHandler());
        var ctrl = ControllerFor(manager);

        var result = ctrl.Submit(new SubmitDownloadRequest { Url = "https://x", Category = "Bogus" });
        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public void Submit_with_no_accepting_handler_returns_400()
    {
        var manager = ManagerWith(new FakeDownloadHandler { CanHandleFunc = _ => false });
        var ctrl = ControllerFor(manager);

        var result = ctrl.Submit(new SubmitDownloadRequest { Url = "ftp://nope" });
        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    // --- SubmitTorrent -----------------------------------------------------

    [Fact]
    public async Task SubmitTorrent_returns_201_for_a_valid_body()
    {
        var manager = ManagerWith(new FakeDownloadHandler { CanHandleFunc = _ => true });
        var body = Encoding.ASCII.GetBytes("d8:announce...e");
        var ctrl = ControllerFor(manager, body);

        var result = await ctrl.SubmitTorrent();
        var created = Assert.IsType<CreatedResult>(result.Result);
        Assert.Equal(StatusCodes.Status201Created, created.StatusCode);
    }

    [Fact]
    public async Task SubmitTorrent_rejects_empty_body_with_400()
    {
        var manager = ManagerWith(new FakeDownloadHandler());
        var ctrl = ControllerFor(manager, Array.Empty<byte>());

        var result = await ctrl.SubmitTorrent();
        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task SubmitTorrent_rejects_oversize_via_ContentLength_without_reading_body()
    {
        var manager = ManagerWith(new FakeDownloadHandler { CanHandleFunc = _ => true });
        // Declare 11 MiB via ContentLength but send a tiny body: the cap must trip on the header.
        var ctrl = ControllerFor(manager, new byte[] { 1, 2, 3 }, contentLength: 11L * 1024 * 1024);

        var result = await ctrl.SubmitTorrent();
        var bad = Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Contains("too large", bad.Value!.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SubmitTorrent_rejects_oversize_actual_body_with_400()
    {
        var manager = ManagerWith(new FakeDownloadHandler { CanHandleFunc = _ => true });
        // ContentLength unset, but the real body exceeds 10 MiB → the post-read length check trips.
        var big = new byte[(10 * 1024 * 1024) + 1];
        var http = new DefaultHttpContext();
        http.Request.Body = new MemoryStream(big);
        // leave ContentLength null
        var ctrl = new DownloadsController(manager)
        {
            ControllerContext = new Microsoft.AspNetCore.Mvc.ControllerContext { HttpContext = http },
        };

        var result = await ctrl.SubmitTorrent();
        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task SubmitTorrent_with_unknown_category_returns_400()
    {
        var manager = ManagerWith(new FakeDownloadHandler());
        var ctrl = ControllerFor(manager, Encoding.ASCII.GetBytes("d..e"));

        var result = await ctrl.SubmitTorrent("Bogus");
        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    // --- Detail / List -----------------------------------------------------

    [Fact]
    public void Detail_of_unknown_job_returns_404()
    {
        var manager = ManagerWith(new FakeDownloadHandler());
        var ctrl = ControllerFor(manager);

        var result = ctrl.Detail(Guid.NewGuid());
        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public void List_with_unknown_state_returns_400()
    {
        var manager = ManagerWith(new FakeDownloadHandler());
        var ctrl = ControllerFor(manager);

        var result = ctrl.List(state: "Nonsense");
        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    // --- Cancel ------------------------------------------------------------

    [Fact]
    public void CancelJob_unknown_returns_404()
    {
        var manager = ManagerWith(new FakeDownloadHandler());
        var ctrl = ControllerFor(manager);

        Assert.IsType<NotFoundResult>(ctrl.CancelJob(Guid.NewGuid()));
    }

    [Fact]
    public async Task CancelJob_on_terminal_job_returns_409()
    {
        var manager = ManagerWith(new FakeDownloadHandler());
        await manager.StartAsync(CancellationToken.None);
        var ctrl = ControllerFor(manager);

        var job = manager.Submit(new DownloadRequest { SourceUrl = "https://x" });
        await Eventually(() => manager.GetJob(job.Id)!.State == JobState.Completed);

        var result = ctrl.CancelJob(job.Id);
        Assert.IsType<ConflictObjectResult>(result);
        await manager.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task CancelJob_on_active_job_returns_204()
    {
        _scope.Configuration.MaxConcurrentDownloads = 1;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var manager = ManagerWith(ParkingHandler(gate));
        await manager.StartAsync(CancellationToken.None);
        var ctrl = ControllerFor(manager);

        var job = manager.Submit(new DownloadRequest { SourceUrl = "https://x" });
        await Eventually(() => manager.GetJob(job.Id)!.State == JobState.Downloading);

        Assert.IsType<NoContentResult>(ctrl.CancelJob(job.Id));

        gate.SetResult();
        await manager.StopAsync(CancellationToken.None);
    }

    // --- Retry -------------------------------------------------------------

    [Fact]
    public void RetryJob_unknown_returns_404()
    {
        var manager = ManagerWith(new FakeDownloadHandler());
        var ctrl = ControllerFor(manager);

        Assert.IsType<NotFoundResult>(ctrl.RetryJob(Guid.NewGuid()).Result);
    }

    [Fact]
    public async Task RetryJob_on_completed_job_returns_409()
    {
        var manager = ManagerWith(new FakeDownloadHandler());
        await manager.StartAsync(CancellationToken.None);
        var ctrl = ControllerFor(manager);

        var job = manager.Submit(new DownloadRequest { SourceUrl = "https://x" });
        await Eventually(() => manager.GetJob(job.Id)!.State == JobState.Completed);

        Assert.IsType<ConflictObjectResult>(ctrl.RetryJob(job.Id).Result);
        await manager.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task RetryJob_on_failed_job_returns_200_with_dto()
    {
        _scope.Configuration.MaxConcurrentDownloads = 1;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = 0;
        var handler = new FakeDownloadHandler
        {
            ExecuteFunc = async (item, _, _, ct) =>
            {
                if (Interlocked.Increment(ref attempts) == 1)
                {
                    throw new InvalidOperationException("first fails");
                }

                await gate.Task.WaitAsync(ct).ConfigureAwait(false);
                return new DownloadResult { Metadata = new MediaMetadata { Title = item.Title } };
            },
        };
        var manager = ManagerWith(handler);
        await manager.StartAsync(CancellationToken.None);
        var ctrl = ControllerFor(manager);

        var job = manager.Submit(new DownloadRequest { SourceUrl = "https://x" });
        await Eventually(() => manager.GetJob(job.Id)!.State == JobState.Failed);

        var result = ctrl.RetryJob(job.Id);
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.IsType<JobDto>(ok.Value);

        gate.SetResult();
        await manager.StopAsync(CancellationToken.None);
    }

    // --- Delete ------------------------------------------------------------

    [Fact]
    public void DeleteJob_unknown_returns_404()
    {
        var manager = ManagerWith(new FakeDownloadHandler());
        var ctrl = ControllerFor(manager);

        Assert.IsType<NotFoundResult>(ctrl.DeleteJob(Guid.NewGuid()));
    }

    [Fact]
    public async Task DeleteJob_on_active_job_returns_409_and_keeps_the_job()
    {
        _scope.Configuration.MaxConcurrentDownloads = 1;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var manager = ManagerWith(ParkingHandler(gate));
        await manager.StartAsync(CancellationToken.None);
        var ctrl = ControllerFor(manager);

        var job = manager.Submit(new DownloadRequest { SourceUrl = "https://x" });
        await Eventually(() => manager.GetJob(job.Id)!.State == JobState.Downloading);

        Assert.IsType<ConflictObjectResult>(ctrl.DeleteJob(job.Id));
        Assert.NotNull(manager.GetJob(job.Id));

        gate.SetResult();
        await manager.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task DeleteJob_on_completed_job_returns_204_and_never_touches_media()
    {
        // Place a real file to prove Delete removes the job record but leaves media on disk.
        var mediaDir = Path.Combine(_root, "library");
        Directory.CreateDirectory(mediaDir);
        var mediaFile = Path.Combine(mediaDir, "keep-me.mkv");
        File.WriteAllText(mediaFile, "payload");

        var placer = new FakeMediaPlacer
        {
            PlaceFunc = (_, _, _, _) => Task.FromResult(new PlacementResult
            {
                FinalPaths = new System.Collections.Generic.List<string> { mediaFile },
                LibraryRootUsed = mediaDir,
            }),
        };
        var manager = ManagerWith(new FakeDownloadHandler(), placer);
        await manager.StartAsync(CancellationToken.None);
        var ctrl = ControllerFor(manager);

        var job = manager.Submit(new DownloadRequest { SourceUrl = "https://x" });
        await Eventually(() => manager.GetJob(job.Id)!.State == JobState.Completed);

        Assert.IsType<NoContentResult>(ctrl.DeleteJob(job.Id));
        Assert.Null(manager.GetJob(job.Id));
        Assert.True(File.Exists(mediaFile), "Delete must never remove downloaded media files");

        await manager.StopAsync(CancellationToken.None);
    }

    // --- Ping --------------------------------------------------------------

    [Fact]
    public void Ping_returns_ok()
    {
        var manager = ManagerWith(new FakeDownloadHandler());
        var ctrl = ControllerFor(manager);

        var result = ctrl.Ping();
        Assert.IsType<OkObjectResult>(result.Result);
    }
}
