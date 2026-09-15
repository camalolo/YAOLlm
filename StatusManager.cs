namespace YAOLlm;

public enum Status
{
    Idle = 0,
    Sending,
    Receiving,
    Searching,
    Fetching,
    Reading,
    Browsing,
    Captions,
    Writing
}

/// <summary>
/// Kind of status notification a provider raises during a streaming request.
/// </summary>
public enum ProviderStatusKind
{
    /// <summary>Provider is (back to) sending a request — e.g. after a tool round-trip.</summary>
    Sending,

    /// <summary>A web search is starting. <see cref="ProviderStatus.Detail"/> = query, <see cref="ProviderStatus.ServiceName"/> = search service.</summary>
    Searching,

    /// <summary>A URL fetch is starting. <see cref="ProviderStatus.Detail"/> = URL.</summary>
    Fetching,

    /// <summary>A local file read is starting. <see cref="ProviderStatus.Detail"/> = file path.</summary>
    ReadingFile,

    /// <summary>A browse_* tool call is starting. <see cref="ProviderStatus.Detail"/> = short action label (URL for navigations).</summary>
    Browsing,

    /// <summary>YouTube caption extraction is starting. <see cref="ProviderStatus.Detail"/> = video URL.</summary>
    Captions,

    /// <summary>A file/memory write is starting. <see cref="ProviderStatus.Detail"/> = target path.</summary>
    WritingFile,

    /// <summary>The model produced a spoken summary. <see cref="ProviderStatus.Detail"/> = text to speak.</summary>
    Tts
}

/// <summary>
/// Structured provider status payload (replaces the old "prefix:payload" string protocol).
/// </summary>
public readonly record struct ProviderStatus(ProviderStatusKind Kind, string? Detail = null, string? ServiceName = null)
{
    public static ProviderStatus Sending => new(ProviderStatusKind.Sending);
}

public class StatusManager
{
    public event Action<Status>? StatusChanged;

    public void SetStatus(Status status)
    {
        var handler = StatusChanged;
        handler?.Invoke(status);
    }
}
