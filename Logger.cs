using System.IO;

namespace YAOLlm;

public class Logger : IDisposable
{
    private readonly object _lock = new();
    private string? _lastMessage;
    private int _repeatCount;
    private readonly StreamWriter? _fileWriter;
    private bool _disposed;

    public Logger()
    {
        try
        {
            var logDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "log");
            Directory.CreateDirectory(logDir);
            var logFile = Path.Combine(logDir, $"yaollm_{DateTime.Now:yyyy-MM-dd}.log");
            _fileWriter = new StreamWriter(logFile, append: true);
        }
        catch
        {
            _fileWriter = null;
        }
    }

    public void Log(string message)
    {
        lock (_lock)
        {
            if (_disposed) return;

            if (message == _lastMessage)
            {
                _repeatCount++;
                return;
            }

            if (_repeatCount > 0)
            {
                WriteToAllTargets($"(last line repeated {_repeatCount} times)");
                _repeatCount = 0;
            }

            var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            WriteToAllTargets($"{timestamp} - {message}");
            _lastMessage = message;
        }
    }

    public void Flush()
    {
        lock (_lock)
        {
            if (_repeatCount > 0)
            {
                WriteToAllTargets($"(last line repeated {_repeatCount} times)");
                _repeatCount = 0;
            }
            _fileWriter?.Flush();
        }
    }

    private void WriteToAllTargets(string line)
    {
        Console.WriteLine(line);
        try
        {
            _fileWriter?.WriteLine(line);
        }
        catch
        {
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            _fileWriter?.Dispose();
        }
    }
}
