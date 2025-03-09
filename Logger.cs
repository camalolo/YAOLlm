namespace Gemini
{
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
            Log($"Logger initialized. Log file: {_logFilePath}", isInternal: true);
        }

        private void EnsureLogDirectoryExists()
        {
            var directory = Path.GetDirectoryName(_logFilePath);
            if (directory != null && !Directory.Exists(directory)) // Simplified check
            {
                Directory.CreateDirectory(directory);
                Log($"Created log directory: {directory}", isInternal: true);
            }
        }

        public void Log(string message, bool isInternal = false)
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
                var errorType = isInternal ? "Internal logging error" : "Logging error";
                Console.WriteLine($"{errorType}: {ex.Message} - Message: {message}");
            }
        }
    }
}