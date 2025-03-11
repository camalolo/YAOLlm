public class Logger
{
    private readonly string _logFilePath;
    private readonly object _lock = new();
    private const string LogDirectory = "log";

    public Logger(string logFileName)
    {
        if (string.IsNullOrEmpty(logFileName))
            throw new ArgumentNullException(nameof(logFileName));

        _logFilePath = Path.Combine(LogDirectory, logFileName);
        EnsureLogDirectoryExists();
        Log($"Logger initialized. Log file: {_logFilePath}");
    }

    private void EnsureLogDirectoryExists()
    {
        var directory = Path.GetDirectoryName(_logFilePath);
        if (directory != null && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
            Log($"Created log directory: {directory}");
        }
    }

    public void Log(string message)
    {
        try
        {
            lock (_lock)
            {
                var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                var logEntry = $"{timestamp} - {message}";
                File.AppendAllText(_logFilePath, logEntry + Environment.NewLine);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Logging error: {ex.Message} - Message: {message}");
        }
    }
}