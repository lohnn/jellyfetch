using Jellyfetch.Plugin.Api;
using Jellyfetch.Plugin.Download;
using Jellyfetch.Plugin.Jobs;

namespace Jellyfetch.Plugin.Tests;

/// <summary>
/// Pins the <see cref="JobDto.FromJob"/> wire mapping — pure POCO mapping, no infrastructure.
/// Focus: the additive <c>Category</c> field (Movie/Series/Other as a stable string, null when
/// unknown) that the Android app renders as a movie-vs-series badge. This proves mapping only; the
/// end-to-end oracle is the phone showing the right type after a real download.
/// </summary>
public sealed class JobDtoMappingTests
{
    [Theory]
    [InlineData(MediaCategory.Movie, "Movie")]
    [InlineData(MediaCategory.Series, "Series")]
    [InlineData(MediaCategory.Other, "Other")]
    public void FromJob_maps_category_to_stable_string(MediaCategory category, string expected)
    {
        var job = new DownloadJob { Category = category };

        var dto = JobDto.FromJob(job);

        Assert.Equal(expected, dto.Category);
    }

    [Fact]
    public void FromJob_maps_null_category_to_null()
    {
        // Old jobs (pre-field), torrents, and still-in-flight jobs carry no category.
        var job = new DownloadJob { Category = null };

        var dto = JobDto.FromJob(job);

        Assert.Null(dto.Category);
    }

    [Fact]
    public void FromJob_children_inherit_category_mapping()
    {
        // The single static mapping is reused for children, so a movie child serializes identically.
        var child = JobDto.FromJob(new DownloadJob { Category = MediaCategory.Movie });
        var parent = new DownloadJob { IsGroup = true, Category = MediaCategory.Series };

        var dto = JobDto.FromJob(parent, childCount: 1, children: new[] { child });

        Assert.Equal("Series", dto.Category);
        Assert.NotNull(dto.Children);
        Assert.Equal("Movie", dto.Children![0].Category);
    }
}
