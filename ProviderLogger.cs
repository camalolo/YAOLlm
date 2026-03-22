using System.Text.Json;

namespace YAOLlm;

public static class ProviderLogger
{
    public static void LogRequest(Logger logger, string provider, string model, int messageCount, bool hasTools)
    {
        logger.Log($"[{provider}] Request: model={model}, messages={messageCount}, tools={hasTools.ToString().ToLower()}");
    }

    public static void LogResponse(Logger logger, string provider, string response, int maxLength = 500)
    {
        var trimmed = response.TrimStart();
        var truncated = trimmed.Length > maxLength ? trimmed.Substring(0, maxLength) + "..." : trimmed;
        logger.Log($"[{provider}] Response: {truncated}");
    }

    public static void LogRetry(Logger logger, string provider, int attempt, int maxAttempts, int delayMs)
    {
        logger.Log($"[{provider}] Retry: attempt {attempt}/{maxAttempts}, waiting {delayMs}ms");
    }

    public static void LogToolCallReceived(Logger logger, string provider, string toolName, Dictionary<string, object?> args)
    {
        var argsJson = JsonSerializer.Serialize(args);
        logger.Log($"[{provider}] Tool call: {toolName}({argsJson})");
    }

    public static void LogToolExecution(Logger logger, string provider, string toolName)
    {
        logger.Log($"[{provider}] Tool executing: {toolName}");
    }

    public static void LogToolResult(Logger logger, string provider, string toolName, string result, int maxLength = 200)
    {
        var truncated = result.Length > maxLength ? result.Substring(0, maxLength) + "..." : result;
        logger.Log($"[{provider}] Tool result: {toolName} -> \"{truncated}\"");
    }

    public static void LogError(Logger logger, string provider, string operation, string error)
    {
        logger.Log($"[{provider}] Error in {operation}: {error}");
    }

    public static void LogCancelled(Logger logger, string provider)
    {
        logger.Log($"[{provider}] Request cancelled");
    }
}
