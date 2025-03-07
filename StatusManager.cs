using System;

namespace Gemini
{
    public enum Status
    {
        Idle = 0,
        Searching,
        Scraping,
        Processing,
        SendingData,
        ReceivingData,
        AnalyzingImage
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