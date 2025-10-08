namespace GeminiDotnet
{
    public enum Status
    {
        Idle = 0,
        Sending,
        Receiving,
        Analyzing
    }

    public class StatusManager
    {
        public event Action<Status>? StatusChanged;

        public void SetStatus(Status status)
        {
            StatusChanged?.Invoke(status);
        }
    }
}