using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace YAOLlm;

/// <summary>
/// YouTube caption extraction for the youtube_captions tool. Shells out to
/// yt-dlp: one metadata probe (to list caption tracks), one subtitle download,
/// then the VTT/SRT is converted to a plain transcript. Enabled only when
/// yt-dlp is on PATH (or YTDLP_PATH points at it).
/// </summary>
public interface IYouTubeCaptionService
{
    bool IsEnabled { get; }

    /// <summary>
    /// Returns the video's transcript as text (with a small metadata header),
    /// or an "Error: ..." string for expected failures (no captions, bad URL,
    /// yt-dlp failure).
    /// </summary>
    Task<string> GetCaptionsAsync(string url, string? language, bool timestamps, CancellationToken cancellationToken = default);
}

public class YouTubeCaptionService : IYouTubeCaptionService
{
    /// <summary>Transcript cap — a 2h video can produce ~120k chars of text.</summary>
    internal const int MaxTranscriptChars = 60_000;
    private static readonly TimeSpan MetadataTimeout = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromSeconds(180);

    private readonly Logger _logger;
    private readonly string? _ytDlpPath;
    private readonly string _workRoot;

    public bool IsEnabled => _ytDlpPath != null;

    public YouTubeCaptionService(Logger? logger = null, string? ytDlpPath = null)
    {
        _logger = logger ?? new Logger();
        _ytDlpPath = ResolveYtDlp(ytDlpPath);
        _workRoot = Path.Combine(Path.GetTempPath(), "YAOLlm", "captions");
    }

    /// <summary>
    /// Finds yt-dlp: an explicit path wins, otherwise PATH is scanned for
    /// yt-dlp(.exe/.cmd/.bat). Null = feature disabled.
    /// </summary>
    internal static string? ResolveYtDlp(string? overridePath)
    {
        if (!string.IsNullOrWhiteSpace(overridePath))
            return File.Exists(overridePath) ? overridePath : null;

        var pathVar = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathVar)) return null;

        foreach (var dir in pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string candidate;
            foreach (var fileName in new[] { "yt-dlp.exe", "yt-dlp.cmd", "yt-dlp.bat", "yt-dlp" })
            {
                try
                {
                    candidate = Path.Combine(dir, fileName);
                    if (File.Exists(candidate)) return candidate;
                }
                catch
                {
                    // malformed PATH entry — skip
                }
            }
        }
        return null;
    }

    public async Task<string> GetCaptionsAsync(string url, string? language, bool timestamps, CancellationToken cancellationToken = default)
    {
        if (!IsEnabled)
            return "Error: YouTube captions unavailable — yt-dlp was not found on PATH.";

        var cleanedUrl = CleanUrl(url);
        if (cleanedUrl == null)
            return "Error: A valid http(s) video URL is required.";

        try
        {
            // 1. Metadata probe — title/duration plus the available caption tracks.
            var (exitCode, stdout, stderr) = await RunAsync(
                new[] { "-J", "--skip-download", "--no-warnings", "--no-playlist", cleanedUrl }, MetadataTimeout, cancellationToken);
            if (exitCode != 0)
                return $"Error: yt-dlp could not read the video: {SummarizeError(stderr)}";

            string? title = null, uploader = null, videoLanguage = null;
            double? duration = null;
            var manualLangs = new List<string>();
            var autoLangs = new List<string>();
            try
            {
                using var doc = JsonDocument.Parse(stdout);
                var root = doc.RootElement;
                title = GetString(root, "title");
                uploader = GetString(root, "uploader") ?? GetString(root, "channel");
                videoLanguage = GetString(root, "language");
                if (root.TryGetProperty("duration", out var dur) && dur.ValueKind == JsonValueKind.Number)
                    duration = dur.GetDouble();
                manualLangs = GetKeys(root, "subtitles");
                autoLangs = GetKeys(root, "automatic_captions");
            }
            catch (JsonException ex)
            {
                return $"Error: Could not parse video metadata: {ex.Message}";
            }

            // 2. Pick the best track for the request.
            var (langKey, isAuto, langError) = SelectLanguage(language, manualLangs, autoLangs, videoLanguage);
            if (langKey == null)
                return $"Error: {langError}";

            // 3. Download that single track as VTT into a private temp dir.
            var workDir = Path.Combine(_workRoot, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(workDir);
            try
            {
                var (dlExit, _, dlErr) = await RunAsync(new[]
                {
                    "--skip-download", "--write-subs", "--write-auto-subs",
                    "--sub-langs", langKey, "--sub-format", "vtt/best",
                    "--no-warnings", "--no-playlist",
                    "-o", Path.Combine(workDir, "%(id)s.%(ext)s"),
                    cleanedUrl,
                }, DownloadTimeout, cancellationToken);
                if (dlExit != 0)
                    return $"Error: yt-dlp could not download captions: {SummarizeError(dlErr)}";

                var captionFile = Directory.EnumerateFiles(workDir)
                    .FirstOrDefault(f => f.EndsWith(".vtt", StringComparison.OrdinalIgnoreCase)
                                      || f.EndsWith(".srt", StringComparison.OrdinalIgnoreCase));
                if (captionFile == null)
                    return $"Error: yt-dlp produced no caption file for language '{langKey}'.";

                var captionContent = await File.ReadAllTextAsync(captionFile, cancellationToken);
                var segments = ParseCaptionFile(captionContent);
                if (segments.Count == 0)
                    return $"Error: The '{langKey}' caption track contains no text.";

                _logger.Log($"[captions] {langKey}{(isAuto ? " (auto)" : "")}: {segments.Count} segments, {captionContent.Length} raw chars");

                var transcript = RenderTranscript(segments, timestamps);
                if (transcript.Length > MaxTranscriptChars)
                    transcript = transcript[..LastSpaceBefore(transcript, MaxTranscriptChars)]
                        + $"\n[Transcript truncated at {MaxTranscriptChars:N0} characters]";

                var header = new StringBuilder();
                if (!string.IsNullOrEmpty(title)) header.AppendLine($"Title: {title}");
                if (!string.IsNullOrEmpty(uploader)) header.AppendLine($"Channel: {uploader}");
                if (duration is { } d) header.AppendLine($"Duration: {FormatDuration(TimeSpan.FromSeconds(d))}");
                header.AppendLine($"Captions: {langKey}{(isAuto ? " (auto-generated)" : "")}");
                return $"{header}\n{transcript}";
            }
            finally
            {
                TryDeleteDirectory(workDir);
            }
        }
        catch (OperationCanceledException)
        {
            throw; // Stop button — propagate to the provider's tool loop
        }
        catch (Exception ex)
        {
            _logger.Log($"[captions] failed: {ex.Message}");
            return $"Error: {ex.Message}";
        }
    }

    // ─── yt-dlp process plumbing ──────────────────────────────────────

    private async Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(
        IEnumerable<string> args, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _ytDlpPath!,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start yt-dlp.");

        var stdoutTask = ReadCappedAsync(process.StandardOutput, 32 * 1024 * 1024);
        var stderrTask = ReadCappedAsync(process.StandardError, 64 * 1024);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
            if (cancellationToken.IsCancellationRequested)
                throw;
            return (-1, await stdoutTask, $"yt-dlp timed out after {timeout.TotalSeconds:0}s.");
        }

        return (process.ExitCode, await stdoutTask, await stderrTask);
    }

    private static async Task<string> ReadCappedAsync(StreamReader reader, int maxChars)
    {
        var sb = new StringBuilder();
        var buffer = new char[8192];
        int read;
        while ((read = await reader.ReadAsync(buffer, 0, buffer.Length)) > 0)
        {
            if (sb.Length >= maxChars) continue; // drain, keep reading so the child never blocks
            sb.Append(buffer, 0, Math.Min(read, maxChars - sb.Length));
        }
        return sb.ToString();
    }

    /// <summary>First ERROR line(s) from yt-dlp stderr, trimmed for the model.</summary>
    internal static string SummarizeError(string stderr)
    {
        var lines = stderr.Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.StartsWith("ERROR:", StringComparison.OrdinalIgnoreCase))
            .ToList();
        var message = lines.Count > 0 ? string.Join(" ", lines) : stderr.Trim();
        if (message.Length == 0) message = "unknown yt-dlp failure";
        return message.Length > 300 ? message[..300] + "…" : message;
    }

    private static string? CleanUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        var cleaned = url.Trim().Trim('"', '\'');
        return Uri.TryCreate(cleaned, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            ? cleaned
            : null;
    }

    // ─── Language selection ───────────────────────────────────────────

    /// <summary>
    /// Chooses a caption track. Requested language matches exactly first, then
    /// by prefix ("en" → "en-US"); manual tracks win over auto-generated ones.
    /// Without a request: English, then the video's own language, then the
    /// first manual track, then the first auto track.
    /// </summary>
    internal static (string? Key, bool IsAuto, string? Error) SelectLanguage(
        string? requested, IReadOnlyList<string> manual, IReadOnlyList<string> auto, string? videoLanguage)
    {
        if (manual.Count == 0 && auto.Count == 0)
            return (null, false, "This video has no captions (neither manual nor auto-generated).");

        if (!string.IsNullOrWhiteSpace(requested))
        {
            var wanted = requested.Trim().ToLowerInvariant();
            var key = FindLanguage(wanted, manual);
            if (key != null) return (key, false, null);
            key = FindLanguage(wanted, auto);
            if (key != null) return (key, true, null);

            var available = manual.Concat(auto).Distinct().OrderBy(l => l);
            return (null, false, $"No '{requested}' captions. Available: {string.Join(", ", available)}");
        }

        var preferred = new[] { "en", videoLanguage?.ToLowerInvariant() ?? "" };
        foreach (var candidate in preferred.Where(c => c.Length > 0))
        {
            var key = FindLanguage(candidate, manual);
            if (key != null) return (key, false, null);
            key = FindLanguage(candidate, auto);
            if (key != null) return (key, true, null);
        }

        if (manual.Count > 0) return (manual[0], false, null);
        return (auto[0], true, null);
    }

    /// <summary>Exact match, else prefix match ("en" matches "en-US" / "en-orig").</summary>
    private static string? FindLanguage(string wanted, IReadOnlyList<string> available)
    {
        foreach (var lang in available)
            if (string.Equals(lang, wanted, StringComparison.OrdinalIgnoreCase))
                return lang;
        foreach (var lang in available)
            if (lang.StartsWith(wanted + "-", StringComparison.OrdinalIgnoreCase) ||
                lang.StartsWith(wanted + "_", StringComparison.OrdinalIgnoreCase))
                return lang;
        return null;
    }

    // ─── Caption parsing (VTT / SRT) ──────────────────────────────────

    internal sealed record CaptionSegment(TimeSpan Start, string Text);

    private static readonly Regex TimingRegex = new(
        @"^(?<start>\d{1,3}:\d{2}:\d{2}[.,]\d{1,3}|\d{1,3}:\d{2}[.,]\d{1,3})\s+-->",
        RegexOptions.Compiled);

    private static readonly Regex TagRegex = new(@"<[^>]*>", RegexOptions.Compiled);
    private static readonly Regex WhitespaceRegex = new(@"\s+", RegexOptions.Compiled);

    /// <summary>
    /// Converts a VTT/SRT file into ordered text segments. Cue text is
    /// tag-stripped, HTML-decoded, whitespace-collapsed and, within one cue,
    /// joined into a single line. Consecutive cues are then reconciled against
    /// the accumulated utterance, which handles YouTube's auto-caption quirks:
    /// pure repeats are dropped, growing cues replace the previous line, and
    /// rolling continuations contribute only their new part.
    /// </summary>
    internal static List<CaptionSegment> ParseCaptionFile(string content)
    {
        var segments = new List<CaptionSegment>();
        var utterance = "";
        TimeSpan? cueStart = null;
        var cueLines = new List<string>();

        void EndCue()
        {
            if (cueStart == null) return;
            var start = cueStart.Value;
            cueStart = null;

            var text = string.Join(" ", cueLines.Select(CleanLine).Where(l => l.Length > 0)).Trim();
            cueLines.Clear();
            if (text.Length == 0) return;

            if (utterance.Length == 0)
            {
                segments.Add(new CaptionSegment(start, text));
                utterance = text;
                return;
            }

            // Same line repeated (rolling caption consolidation cue).
            if (string.Equals(text, utterance, StringComparison.Ordinal))
                return;

            // The line grew — or the cue restates the utterance and extends it.
            if (text.StartsWith(utterance, StringComparison.Ordinal))
            {
                segments[^1] = segments[^1] with { Text = text };
                utterance = text;
                return;
            }

            // Rolling continuation: the cue begins with a tail of the utterance,
            // so only the remainder is new content.
            var skip = OverlapChars(utterance, text);
            if (skip > 0)
            {
                var remainder = text[skip..];
                utterance += remainder;
                var newPart = remainder.Trim();
                if (newPart.Length > 0)
                    segments.Add(new CaptionSegment(start, newPart));
                return;
            }

            // Unrelated content — a new utterance.
            segments.Add(new CaptionSegment(start, text));
            utterance = text;
        }

        foreach (var rawLine in content.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            var line = rawLine.TrimEnd();
            if (cueStart == null)
            {
                var match = TimingRegex.Match(line);
                if (match.Success && TryParseTimestamp(match.Groups["start"].Value, out var start))
                    cueStart = start;
                // Anything else outside a cue (WEBVTT, NOTE, cue numbers, styles) is ignored.
                continue;
            }

            if (line.Length == 0)
            {
                EndCue();
                continue;
            }

            cueLines.Add(line);
        }

        EndCue(); // file may end without a trailing blank line

        return segments;
    }

    /// <summary>
    /// Minimum overlap for a rolling continuation — short coincidences at word
    /// boundaries ("the", "and") must not swallow a fresh utterance.
    /// </summary>
    private const int MinOverlapChars = 4;

    private readonly record struct Word(string Value, int Start, int End);

    /// <summary>
    /// Character offset in <paramref name="text"/> just past the last leading
    /// word that also ends <paramref name="accumulated"/>, or 0 when there is no
    /// usable overlap. Comparison is word-based and punctuation-insensitive
    /// because caption cues repeat text with differing punctuation
    /// ("have really..." then "really really long trunks").
    /// </summary>
    internal static int OverlapChars(string accumulated, string text)
    {
        // Utterances never legitimately overlap by more than a line or two —
        // cap the search window so long transcripts stay cheap.
        const int windowSize = 400;
        if (accumulated.Length == 0 || text.Length == 0) return 0;

        var windowStart = Math.Max(0, accumulated.Length - windowSize);
        var accumulatedWords = Tokenize(accumulated[windowStart..]);
        var textWords = Tokenize(text);
        if (accumulatedWords.Count == 0 || textWords.Count == 0) return 0;

        var maxWords = Math.Min(accumulatedWords.Count, textWords.Count);
        for (var count = maxWords; count >= 1; count--)
        {
            var matches = true;
            for (var i = 0; i < count; i++)
            {
                var accumulatedWord = Normalize(accumulatedWords[accumulatedWords.Count - count + i].Value);
                var textWord = Normalize(textWords[i].Value);
                if (!string.Equals(accumulatedWord, textWord, StringComparison.OrdinalIgnoreCase))
                {
                    matches = false;
                    break;
                }
            }
            if (!matches) continue;

            // Too-short overlaps are coincidences, not continuations.
            if (textWords[count - 1].End - textWords[0].Start < MinOverlapChars)
                return 0;

            return textWords[count - 1].End;
        }
        return 0;
    }

    private static List<Word> Tokenize(string text)
    {
        var words = new List<Word>();
        var i = 0;
        while (i < text.Length)
        {
            while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
            var start = i;
            while (i < text.Length && !char.IsWhiteSpace(text[i])) i++;
            if (i > start) words.Add(new Word(text[start..i], start, i));
        }
        return words;
    }

    /// <summary>Word comparison key: trimmed of surrounding punctuation.</summary>
    private static string Normalize(string word) => word.Trim('.', ',', '!', '?', '"', '\'', '…', '(', ')', '[', ']', '-', ';', ':');

    private static string CleanLine(string line)
    {
        var text = TagRegex.Replace(line, "");
        text = WebUtility.HtmlDecode(text);
        text = WhitespaceRegex.Replace(text, " ").Trim();
        return text;
    }

    private static bool TryParseTimestamp(string value, out TimeSpan result)
    {
        result = TimeSpan.Zero;
        var normalized = value.Replace(',', '.');
        var parts = normalized.Split(':');
        if (parts.Length is not (2 or 3)) return false;
        if (!double.TryParse(parts[^1], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var seconds)) return false;
        if (!int.TryParse(parts[^2], out var minutes)) return false;
        var hours = 0;
        if (parts.Length == 3 && !int.TryParse(parts[0], out hours)) return false;

        result = TimeSpan.FromSeconds(hours * 3600 + minutes * 60 + seconds);
        return true;
    }

    /// <summary>Plain transcript, or one "[time] text" line per segment.</summary>
    internal static string RenderTranscript(IReadOnlyList<CaptionSegment> segments, bool timestamps)
    {
        if (segments.Count == 0) return "(no caption text found)";

        var sb = new StringBuilder();
        foreach (var segment in segments)
        {
            if (sb.Length > 0)
                sb.Append(timestamps ? '\n' : ' ');
            if (timestamps)
                sb.Append('[').Append(FormatDuration(segment.Start)).Append("] ");
            sb.Append(segment.Text);
        }
        return sb.ToString();
    }

    internal static string FormatDuration(TimeSpan value)
    {
        return value.TotalHours >= 1
            ? $"{(int)value.TotalHours}:{value.Minutes:00}:{value.Seconds:00}"
            : $"{value.Minutes}:{value.Seconds:00}";
    }

    private static int LastSpaceBefore(string text, int limit)
    {
        var index = text.LastIndexOf(' ', Math.Min(limit, text.Length - 1));
        return index > 0 ? index : limit;
    }

    private static string? GetString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static List<string> GetKeys(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.Object)
            return new List<string>();
        return value.EnumerateObject().Select(p => p.Name).ToList();
    }

    private void TryDeleteDirectory(string path)
    {
        try { Directory.Delete(path, recursive: true); }
        catch (Exception ex) { _logger.Log($"[captions] temp cleanup failed: {ex.Message}"); }
    }
}
