using System;
using System.IO;

namespace Gemini
{
    public class Logger
    {
        private readonly string _logFile;
        private readonly object _lock = new object(); // Added for thread safety

        public Logger(string logFile)
        {
            _logFile = Path.Combine("log", logFile);
            string? logDir = Path.GetDirectoryName(_logFile);
            if (!string.IsNullOrEmpty(logDir)) // Improved check
            {
                Directory.CreateDirectory(logDir);
            }
        }

        public void Log(string message)
        {
            try
            {
                lock (_lock) // Ensure thread-safe writes
                {
                    File.AppendAllText(_logFile, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - {message}\n");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error while logging message: {ex.Message}"); // Include exception details
            }
        }
    }
}