public class Logger
{
    public Logger()
    {
    }

    public void Log(string message)
    {
        var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        var logEntry = $"{timestamp} - {message}";
        Console.WriteLine(logEntry);
    }
}