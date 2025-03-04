using System;
using System.IO;

namespace Gemini
{
    public class Logger
    {
        private readonly string _logFile;

        public Logger(string logFile)
        {
            _logFile = Path.Combine("log", logFile);
            string? logDir = Path.GetDirectoryName(_logFile);
            if (logDir != null)
            {
                Directory.CreateDirectory(logDir);
            }
        }

        public void Log(string message)
        {
            try
            {
                File.AppendAllText(_logFile, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - {message}\n");
            }
            catch (Exception)
            {
                Console.WriteLine("Error while logging message");
            }
        }
    }
}