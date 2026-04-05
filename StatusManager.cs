namespace YAOLlm;

public enum Status
{
    Idle = 0,
    Sending,
    Receiving,
    Searching
}

public class StatusManager
{
    public const string SearchingStatus = "searching";

    public event Action<Status>? StatusChanged;

    public void SetStatus(Status status)
    {
        var handler = StatusChanged;
        handler?.Invoke(status);
    }
}
