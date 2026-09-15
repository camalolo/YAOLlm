using Xunit;

namespace YAOLlm.Tests;

public class YouTubeCaptionServiceTests : IDisposable
{
    private readonly string _dir;

    public YouTubeCaptionServiceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "YAOLlmTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    // ─── yt-dlp discovery ─────────────────────────────────────────────

    [Fact]
    public void ResolveYtDlp_ExplicitExistingPath_Wins()
    {
        var fake = Path.Combine(_dir, "yt-dlp.exe");
        File.WriteAllText(fake, "");

        Assert.Equal(fake, YouTubeCaptionService.ResolveYtDlp(fake));
    }

    [Fact]
    public void ResolveYtDlp_ExplicitMissingPath_ReturnsNull()
    {
        Assert.Null(YouTubeCaptionService.ResolveYtDlp(Path.Combine(_dir, "nope.exe")));
    }

    [Fact]
    public void ResolveYtDlp_PathScan_ReturnsExistingFileOrNull()
    {
        var found = YouTubeCaptionService.ResolveYtDlp(null);

        Assert.True(found == null || File.Exists(found));
    }

    // ─── VTT parsing: manual captions ─────────────────────────────────

    [Fact]
    public void ParseCaptionFile_ManualVtt_JoinsWrappedLines()
    {
        const string vtt = """
            WEBVTT
            Kind: captions
            Language: en

            00:00:01.200 --> 00:00:03.360
            All right, so here we are, in front of the
            elephants

            00:00:05.318 --> 00:00:07.974
            the cool thing about these guys is that they
            have really...

            00:00:07.974 --> 00:00:12.616
            really really long trunks

            00:00:12.616 --> 00:00:14.367
            and that's cool
            """;

        var segments = YouTubeCaptionService.ParseCaptionFile(vtt);

        // Lines wrapped inside a cue are joined; the overlapping "really..." is
        // not duplicated in the plain transcript.
        Assert.Equal("All right, so here we are, in front of the elephants", segments[0].Text);
        Assert.Equal(TimeSpan.FromSeconds(1.2), segments[0].Start);
        Assert.Equal(
            "All right, so here we are, in front of the elephants the cool thing about these guys " +
            "is that they have really... really long trunks and that's cool",
            YouTubeCaptionService.RenderTranscript(segments, timestamps: false));
    }

    // ─── VTT parsing: auto-generated rolling captions ─────────────────

    [Fact]
    public void ParseCaptionFile_RollingAutoCaptions_CollapseDuplicates()
    {
        // Real-shaped sample: growing tagged cues + 10ms consolidation cues.
        const string vtt = """
            WEBVTT
            Kind: captions
            Language: en

            00:00:00.080 --> 00:00:02.030 align:start position:0%
             
            imagine<00:00:00.640><c> programming</c><00:00:01.120><c> is</c><00:00:01.280><c> a</c><00:00:01.400><c> journey</c><00:00:01.800><c> from</c>

            00:00:02.030 --> 00:00:02.040 align:start position:0%
            imagine programming is a journey from
             

            00:00:02.040 --> 00:00:04.789 align:start position:0%
            imagine programming is a journey from
            point<00:00:02.280><c> A</c><00:00:02.560><c> to</c><00:00:02.760><c> D</c><00:00:03.520><c> in</c><00:00:03.719><c> traditional</c><00:00:04.200><c> synchronous</c>

            00:00:04.789 --> 00:00:04.799 align:start position:0%
            point A to D in traditional synchronous
             

            00:00:04.799 --> 00:00:06.990 align:start position:0%
            point A to D in traditional synchronous
            programming<00:00:05.319><c> we</c><00:00:05.480><c> travel</c><00:00:05.839><c> in</c><00:00:05.960><c> a</c><00:00:06.120><c> straight</c><00:00:06.399><c> line</c>

            00:00:06.990 --> 00:00:07.000 align:start position:0%
            programming we travel in a straight line
             
            """;

        var segments = YouTubeCaptionService.ParseCaptionFile(vtt);
        var transcript = YouTubeCaptionService.RenderTranscript(segments, timestamps: false);

        // No text appears twice, and the transcript reads as one continuous line.
        Assert.Equal(
            "imagine programming is a journey from point A to D in traditional synchronous programming we travel in a straight line",
            transcript);
        Assert.Equal(1, transcript.Split("point A to D in traditional synchronous").Length - 1);
        Assert.Equal(1, transcript.Split("imagine programming is a journey from").Length - 1);
    }

    [Fact]
    public void ParseCaptionFile_GrowingLine_IsReplacedNotDuplicated()
    {
        const string vtt = """
            WEBVTT

            00:00:00.000 --> 00:00:02.000
            hello wor

            00:00:02.000 --> 00:00:04.000
            hello world
            """;

        var segments = YouTubeCaptionService.ParseCaptionFile(vtt);

        Assert.Single(segments);
        Assert.Equal("hello world", segments[0].Text);
    }

    [Fact]
    public void ParseCaptionFile_StripsTagsAndDecodesEntities()
    {
        const string vtt = """
            WEBVTT

            00:00:00.000 --> 00:00:02.000
            Tom &amp; Jerry &lt;3 &#39;quoted&#39; <v Bob>hi</v>
            """;

        var segments = YouTubeCaptionService.ParseCaptionFile(vtt);

        Assert.Single(segments);
        Assert.Equal("Tom & Jerry <3 'quoted' hi", segments[0].Text);
    }

    [Fact]
    public void ParseCaptionFile_Srt_IsSupported()
    {
        const string srt = """
            1
            00:00:01,000 --> 00:00:02,000
            first line

            2
            00:00:03,500 --> 00:00:04,000
            second line
            """;

        var segments = YouTubeCaptionService.ParseCaptionFile(srt);

        Assert.Equal(2, segments.Count);
        Assert.Equal("first line", segments[0].Text);
        Assert.Equal(TimeSpan.FromSeconds(1), segments[0].Start);
        Assert.Equal(TimeSpan.FromSeconds(3.5), segments[1].Start);
    }

    [Fact]
    public void ParseCaptionFile_NoCues_ReturnsEmpty()
    {
        Assert.Empty(YouTubeCaptionService.ParseCaptionFile("WEBVTT\n\nNOTE metadata only\n"));
        Assert.Empty(YouTubeCaptionService.ParseCaptionFile(""));
    }

    // ─── Rendering ────────────────────────────────────────────────────

    [Fact]
    public void RenderTranscript_TimestampsMode_PrefixesTimes()
    {
        var segments = new List<YouTubeCaptionService.CaptionSegment>
        {
            new(TimeSpan.FromSeconds(5), "first"),
            new(TimeSpan.FromSeconds(75), "second"),
            new(TimeSpan.FromSeconds(3725), "third"),
        };

        Assert.Equal("first second third", YouTubeCaptionService.RenderTranscript(segments, false));
        Assert.Equal("[0:05] first\n[1:15] second\n[1:02:05] third",
            YouTubeCaptionService.RenderTranscript(segments, true));
    }

    [Fact]
    public void FormatDuration_FormatsMinutesAndHours()
    {
        Assert.Equal("0:00", YouTubeCaptionService.FormatDuration(TimeSpan.Zero));
        Assert.Equal("1:30", YouTubeCaptionService.FormatDuration(TimeSpan.FromSeconds(90)));
        Assert.Equal("1:01:01", YouTubeCaptionService.FormatDuration(TimeSpan.FromSeconds(3661)));
    }

    // ─── Language selection ───────────────────────────────────────────

    [Fact]
    public void SelectLanguage_Requested_PrefersManualThenAuto()
    {
        var (key, isAuto, error) = YouTubeCaptionService.SelectLanguage("en",
            new[] { "de", "en-US" }, new[] { "en" }, null);
        Assert.Equal("en-US", key);
        Assert.False(isAuto);
        Assert.Null(error);

        (key, isAuto, error) = YouTubeCaptionService.SelectLanguage("pl", Array.Empty<string>(), new[] { "pl" }, null);
        Assert.Equal("pl", key);
        Assert.True(isAuto);
        Assert.Null(error);
    }

    [Fact]
    public void SelectLanguage_RequestedMissing_ListsAvailable()
    {
        var (key, _, error) = YouTubeCaptionService.SelectLanguage("xx",
            new[] { "en" }, new[] { "fr" }, null);

        Assert.Null(key);
        Assert.Contains("No 'xx' captions", error);
        Assert.Contains("en", error);
        Assert.Contains("fr", error);
    }

    [Fact]
    public void SelectLanguage_Defaults_PreferEnglishThenVideoLanguage()
    {
        var (key, isAuto, _) = YouTubeCaptionService.SelectLanguage(null,
            Array.Empty<string>(), new[] { "en" }, "pl");
        Assert.Equal("en", key);
        Assert.True(isAuto);

        (key, isAuto, _) = YouTubeCaptionService.SelectLanguage(null,
            new[] { "pl" }, Array.Empty<string>(), "pl");
        Assert.Equal("pl", key);
        Assert.False(isAuto);

        (key, isAuto, _) = YouTubeCaptionService.SelectLanguage(null,
            new[] { "de", "fr" }, Array.Empty<string>(), null);
        Assert.Equal("de", key);
        Assert.False(isAuto);
    }

    [Fact]
    public void SelectLanguage_NoCaptionsAtAll_ReturnsError()
    {
        var (key, _, error) = YouTubeCaptionService.SelectLanguage(null,
            Array.Empty<string>(), Array.Empty<string>(), null);

        Assert.Null(key);
        Assert.Contains("no captions", error, StringComparison.OrdinalIgnoreCase);
    }

    // ─── Disabled service ─────────────────────────────────────────────

    [Fact]
    public async Task DisabledService_RefusesCalls()
    {
        var service = new YouTubeCaptionService(ytDlpPath: Path.Combine(_dir, "missing-yt-dlp.exe"));

        Assert.False(service.IsEnabled);
        var result = await service.GetCaptionsAsync("https://www.youtube.com/watch?v=abc", null, false);
        Assert.StartsWith("Error:", result);
        Assert.Contains("yt-dlp", result);
    }

    // ─── Live round-trip (needs yt-dlp + network) ─────────────────────

    [Fact]
    public async Task Live_KnownVideo_ReturnsTranscript()
    {
        var service = new YouTubeCaptionService();
        if (!service.IsEnabled) return; // yt-dlp not installed — nothing to test here

        var result = await service.GetCaptionsAsync(
            "https://www.youtube.com/watch?v=jNQXAC9IVRw", language: null, timestamps: false);

        Assert.False(result.StartsWith("Error:", StringComparison.Ordinal), result);
        Assert.Contains("Captions: en", result);
        Assert.Contains("elephants", result); // the video's famous line
    }
}
