using System.Collections.Generic;

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
        private Status _status = Status.Idle;
        private readonly Queue<Status> _queue = new Queue<Status>();

        public void SetStatus(Status status)
        {
            _status = status;
            lock (_queue) { _queue.Enqueue(status); }
        }

        public Status GetStatus() => _status;

        public Queue<Status> GetQueue() => _queue;
    }
}