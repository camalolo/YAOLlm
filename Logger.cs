namespace YAOLlm;

public class Logger
{
    private readonly object _lock = new();
    private string? _lastMessage;
    private int _repeatCount;

    public Logger()
    {
    }

    public void Log(string message)
    {
        lock (_lock)
        {
            if (message == _lastMessage)
            {
                _repeatCount++;
                return;
            }

            if (_repeatCount > 0)
            {
                Console.WriteLine($"(last line repeated {_repeatCount} times)");
                _repeatCount = 0;
            }

            var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            Console.WriteLine($"{timestamp} - {message}");
            _lastMessage = message;
        }
    }

    public void Flush()
    {
        lock (_lock)
        {
            if (_repeatCount > 0)
            {
                Console.WriteLine($"(last line repeated {_repeatCount} times)");
                _repeatCount = 0;
            }
        }
    }
}