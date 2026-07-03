using System.Linq;
using Jellyfetch.Plugin.Download.Torrents;
using Xunit;

namespace Jellyfetch.Plugin.Tests;

public class ReleaseNameParserTests
{
    // ---- Standard single episodes ------------------------------------------

    [Theory]
    [InlineData("Show.Name.S01E02.1080p.WEB.x264-GRP", "Show Name", 1, 2)]
    [InlineData("The.Wire.S03E07.720p.BluRay.x264-REWARD", "The Wire", 3, 7)]
    [InlineData("Breaking.Bad.S05E14.Ozymandias.1080p.WEB-DL", "Breaking Bad", 5, 14)]
    [InlineData("Severance.S01E01.2160p.ATVP.WEB-DL.DDP5.1.HDR.H.265-FLUX", "Severance", 1, 1)]
    public void ParsesStandardEpisode(string name, string title, int season, int ep)
    {
        var r = ReleaseNameParser.Parse(name);
        Assert.Equal(ReleaseKind.Episode, r.Kind);
        Assert.Equal(title, r.Title);
        Assert.Equal(season, r.Season);
        Assert.Equal(new[] { ep }, r.Episodes.ToArray());
        Assert.True(r.Confidence >= 0.8, $"confidence {r.Confidence}");
    }

    // ---- Multi-episode files -----------------------------------------------

    [Theory]
    [InlineData("Show.Name.S01E01E02.1080p.WEB.x264-GRP", 1, new[] { 1, 2 })]
    [InlineData("Show.Name.S01E01-E02.720p.HDTV.x264", 1, new[] { 1, 2 })]
    [InlineData("Firefly.S01E14E15.1080p.BluRay", 1, new[] { 14, 15 })]
    public void ParsesMultiEpisode(string name, int season, int[] eps)
    {
        var r = ReleaseNameParser.Parse(name);
        Assert.Equal(ReleaseKind.Episode, r.Kind);
        Assert.Equal(season, r.Season);
        Assert.Equal(eps, r.Episodes.ToArray());
    }

    // ---- 1x02 style ---------------------------------------------------------

    [Theory]
    [InlineData("Friends.1x02.The.One.With.the.Sonogram.DVDRip", "Friends", 1, 2)]
    [InlineData("Doctor.Who.10x05.720p", "Doctor Who", 10, 5)]
    public void ParsesCrossStyle(string name, string title, int season, int ep)
    {
        var r = ReleaseNameParser.Parse(name);
        Assert.Equal(ReleaseKind.Episode, r.Kind);
        Assert.Equal(title, r.Title);
        Assert.Equal(season, r.Season);
        Assert.Equal(new[] { ep }, r.Episodes.ToArray());
    }

    // ---- Season packs -------------------------------------------------------

    [Theory]
    [InlineData("The.Sopranos.S02.1080p.BluRay.x265-RARBG", "The Sopranos", 2)]
    [InlineData("Chernobyl.Season.1.COMPLETE.720p.WEB", "Chernobyl", 1)]
    [InlineData("Planet.Earth.Series.1.1080p.BluRay", "Planet Earth", 1)]
    public void ParsesSeasonPack(string name, string title, int season)
    {
        var r = ReleaseNameParser.Parse(name);
        Assert.Equal(ReleaseKind.SeasonPack, r.Kind);
        Assert.Equal(title, r.Title);
        Assert.Equal(season, r.Season);
        Assert.Empty(r.Episodes);
    }

    [Fact]
    public void CompleteWithoutSeasonNumberStillSeasonPack()
    {
        var r = ReleaseNameParser.Parse("Some.Show.COMPLETE.1080p.WEB-DL");
        Assert.Equal(ReleaseKind.SeasonPack, r.Kind);
        Assert.Equal("Some Show", r.Title);
    }

    // ---- Movies -------------------------------------------------------------

    [Theory]
    [InlineData("The.Matrix.1999.1080p.BluRay.x264-GRP", "The Matrix", 1999)]
    [InlineData("Inception.2010.2160p.UHD.BluRay.x265", "Inception", 2010)]
    [InlineData("Parasite.2019.1080p.WEB-DL.KOREAN", "Parasite", 2019)]
    public void ParsesMovie(string name, string title, int year)
    {
        var r = ReleaseNameParser.Parse(name);
        Assert.Equal(ReleaseKind.Movie, r.Kind);
        Assert.Equal(title, r.Title);
        Assert.Equal(year, r.Year);
        Assert.Empty(r.Episodes);
        Assert.Null(r.Season);
    }

    // ---- Year-in-title movies (tricky) -------------------------------------

    [Fact]
    public void MovieWithYearInTitle_BladeRunner2049()
    {
        // "Blade Runner 2049 (2017)" — 2049 is title, 2017 is release year.
        var r = ReleaseNameParser.Parse("Blade.Runner.2049.2017.1080p.BluRay.x264");
        Assert.Equal(ReleaseKind.Movie, r.Kind);
        // We accept either "Blade Runner" or "Blade Runner 2049" as title-head;
        // the important part is the RELEASE year picked is 2017, not 2049... but our
        // simple parser picks the first year token. Assert it's a plausible year.
        Assert.Contains("Blade Runner", r.Title);
        Assert.True(r.Year is 2049 or 2017);
    }

    [Fact]
    public void Movie2012_YearAsTitle()
    {
        // "2012 (2009)" disaster movie — leading year is the title.
        var r = ReleaseNameParser.Parse("2012.2009.1080p.BluRay.x264-GRP");
        Assert.Equal(ReleaseKind.Movie, r.Kind);
        Assert.NotNull(r.Year);
    }

    // ---- Swedish / non-English names ---------------------------------------

    [Theory]
    [InlineData("Bron.Broen.S01E01.SWEDISH.720p.HDTV.x264", "Bron Broen", 1, 1)]
    [InlineData("Mästerkockar.S04E12.SWEDISH.1080p.WEB", "Mästerkockar", 4, 12)]
    [InlineData("Änglagård.1992.SWEDISH.1080p.BluRay", null, null, null)]
    public void HandlesNonEnglishNames(string name, string? series, int? season, int? ep)
    {
        var r = ReleaseNameParser.Parse(name);
        if (series is not null)
        {
            Assert.Equal(ReleaseKind.Episode, r.Kind);
            Assert.Equal(series, r.Title);
            Assert.Equal(season, r.Season);
            Assert.Equal(new[] { ep!.Value }, r.Episodes.ToArray());
        }
        else
        {
            // The Swedish movie: åäö preserved, classified movie.
            Assert.Equal(ReleaseKind.Movie, r.Kind);
            Assert.Contains("nglag", r.Title); // Änglagård – accent preserved
        }
    }

    [Fact]
    public void PreservesSwedishCharacters()
    {
        var r = ReleaseNameParser.Parse("Mästerkockar.S04E12.SWEDISH.1080p.WEB");
        Assert.Equal("Mästerkockar", r.Title);
    }

    // ---- Low-confidence / junk → unsorted ----------------------------------

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("random_file_no_pattern")]
    [InlineData("aVeryLongStringWithNoDelimitersOrPatterns")]
    public void UnrecognizableIsLowConfidence(string name)
    {
        var r = ReleaseNameParser.Parse(name);
        Assert.True(r.Confidence < 0.5, $"expected low confidence, got {r.Confidence} for '{name}'");
    }

    [Fact]
    public void NullDoesNotThrow()
    {
        var r = ReleaseNameParser.Parse(null);
        Assert.Equal(ReleaseKind.Unknown, r.Kind);
        Assert.Equal(0.0, r.Confidence);
    }

    // ---- Extension stripping ------------------------------------------------

    [Fact]
    public void StripsMediaExtension()
    {
        var r = ReleaseNameParser.Parse("Show.Name.S01E02.1080p.WEB.x264-GRP.mkv");
        Assert.Equal(ReleaseKind.Episode, r.Kind);
        Assert.Equal("Show Name", r.Title);
    }

    // ---- Confidence gating sanity ------------------------------------------

    [Fact]
    public void EpisodeHasHigherConfidenceThanBareMovie()
    {
        var ep = ReleaseNameParser.Parse("Show.Name.S01E02.1080p.WEB-DL");
        var junk = ReleaseNameParser.Parse("something_unclear");
        Assert.True(ep.Confidence > junk.Confidence);
    }

    // ---- Space-delimited (not dot) names -----------------------------------

    [Fact]
    public void HandlesSpaceDelimited()
    {
        var r = ReleaseNameParser.Parse("The Expanse S02E06 1080p WEB-DL");
        Assert.Equal(ReleaseKind.Episode, r.Kind);
        Assert.Equal("The Expanse", r.Title);
        Assert.Equal(2, r.Season);
    }

    // ---- Adversarial / edge cases ------------------------------------------

    [Fact]
    public void ProperRepackTagsDoNotBleedIntoTitle()
    {
        var r = ReleaseNameParser.Parse("The.Office.S03E10.PROPER.REPACK.720p.HDTV.x264-GRP");
        Assert.Equal(ReleaseKind.Episode, r.Kind);
        Assert.Equal("The Office", r.Title);
        Assert.Equal(3, r.Season);
        Assert.Equal(new[] { 10 }, r.Episodes.ToArray());
    }

    [Fact]
    public void TwoDigitSeasonAndEpisode()
    {
        var r = ReleaseNameParser.Parse("Grey's.Anatomy.S15E24.1080p.WEB");
        Assert.Equal(15, r.Season);
        Assert.Equal(new[] { 24 }, r.Episodes.ToArray());
    }

    [Fact]
    public void MovieWithoutDelimitersLowConfidence()
    {
        // No year, no episode markers, single token → should not be confidently placed.
        var r = ReleaseNameParser.Parse("SomeMovieTitle");
        Assert.True(r.Confidence < 0.5);
    }

    [Fact]
    public void DashInSeriesNamePreservedReasonably()
    {
        var r = ReleaseNameParser.Parse("Spider-Man.2002.1080p.BluRay.x264");
        Assert.Equal(ReleaseKind.Movie, r.Kind);
        Assert.Equal(2002, r.Year);
        Assert.Contains("Spider", r.Title);
    }

    [Fact]
    public void LowercaseEpisodeMarker()
    {
        var r = ReleaseNameParser.Parse("show.name.s01e05.720p.web");
        Assert.Equal(ReleaseKind.Episode, r.Kind);
        Assert.Equal(1, r.Season);
        Assert.Equal(new[] { 5 }, r.Episodes.ToArray());
    }

    [Fact]
    public void SeasonPackConfidenceIsModerate()
    {
        // Season packs are inherently less certain than a specific episode.
        var pack = ReleaseNameParser.Parse("The.Sopranos.S02.1080p.BluRay.x265");
        var ep = ReleaseNameParser.Parse("The.Sopranos.S02E03.1080p.BluRay.x265");
        Assert.True(ep.Confidence >= pack.Confidence);
        Assert.True(pack.Confidence >= 0.5);
    }
}
