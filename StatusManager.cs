namespace YAOLlm;

public enum Status
{
    Idle = 0,
    Sending,
    Receiving,
    Searching,
    Fetching
}

public class StatusManager
{
    public const string SearchingStatus = "searching";
    public const string FetchingStatus = "fetching";
    public const string TtsStatus = "tts";

    public event Action<Status>? StatusChanged;

    public void SetStatus(Status status)
    {
        var handler = StatusChanged;
        handler?.Invoke(status);
    }
}
