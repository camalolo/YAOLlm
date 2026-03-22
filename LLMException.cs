using System;

public class LLMException : Exception
{
    public int StatusCode { get; }
    public string UserMessage { get; }
    public string? Details { get; }

    public LLMException(int statusCode, string userMessage, string? details)
        : base(userMessage)
    {
        StatusCode = statusCode;
        UserMessage = userMessage;
        Details = details;
    }

    private LLMException(int statusCode, string userMessage, string? details, string fullMessage)
        : base(fullMessage)
    {
        StatusCode = statusCode;
        UserMessage = userMessage;
        Details = details;
    }

    public static LLMException CreateWithStatusCode(int statusCode, string? responseBody = null, string? providerName = null)
    {
        string userMessage = statusCode switch
        {
            401 => "Invalid API key",
            403 => "Access denied",
            404 => "API endpoint not found",
            429 => "Rate limited - please wait",
            >= 500 and <= 504 => "Server error - try again later",
            0 => "Connection failed",
            _ => "Request failed"
        };

        string fullMessage = providerName is not null
            ? $"[{providerName}] {userMessage}"
            : userMessage;

        return new LLMException(statusCode, userMessage, responseBody, fullMessage);
    }

    /// <summary>
    /// Creates an LLMException with a pre-formatted user message (for response body errors).
    /// </summary>
    public static LLMException CreateWithMessage(string userMessage, string? providerName = null, string? details = null)
    {
        string fullMessage = providerName is not null
            ? $"[{providerName}] {userMessage}"
            : userMessage;

        return new LLMException(0, userMessage, details, fullMessage);
    }
}
