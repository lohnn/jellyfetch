using Jellyfetch.Plugin.Download;
using Xunit;

namespace Jellyfetch.Plugin.Tests;

/// <summary>Seed test proving the test project wiring; real suites live per-backend (WebMedia/, Torrents/) and per-core-area.</summary>
public class SmokeTests
{
    [Fact]
    public void DownloadRequest_Defaults_To_Auto_Category()
    {
        var request = new DownloadRequest();
        Assert.Equal(MediaCategory.Auto, request.CategoryHint);
        Assert.Null(request.SourceUrl);
        Assert.Null(request.TorrentFileBase64);
    }
}
