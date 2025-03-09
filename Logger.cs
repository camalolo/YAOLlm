using System;
using System.IO;

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
            WriteInitialLog();
        }

        private void EnsureLogDirectoryExists()
        {
            var directory = Path.GetDirectoryName(_logFilePath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
                LogInternal($"Created log directory: {directory}");
            }
        }

        private void WriteInitialLog()
        {
            LogInternal($"Logger initialized. Log file: {_logFilePath}");
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
                Console.WriteLine($"Logging error: {ex.Message} - Original message: {message}");
            }
        }

        private void LogInternal(string message)
        {
            try
            {
                lock (_lock)
                {
                    File.AppendAllText(_logFilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - {message}" + Environment.NewLine);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Internal logging error: {ex.Message} - Message: {message}");
            }
        }
    }
}