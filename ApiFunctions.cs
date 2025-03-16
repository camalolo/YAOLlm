using System;
using System.Text.Json;
using System.Threading.Tasks;
using RestSharp;

namespace Gemini
{
    public static class ApiFunctions
    {
        private const int GenerationRpmLimit = 15;
        private static readonly SemaphoreSlim GenerationRateLimiter = new(GenerationRpmLimit, GenerationRpmLimit);

        public static async Task<string> SendToLLM(GeminiClient client, object payload, string? imageBase64 = null, bool useTools = true)
        {
            var logger = client.Logger;
            var tools = new List<object> { new { google_search = new { } } };
            var generationConfig = new { responseModalities = new[] { "TEXT", "IMAGE" } };

            object[] contents = payload switch
            {
                List<Dictionary<string, string>> messages => messages.Select(m =>
                {
                    var parts = new List<object>();
                    if (m.ContainsKey("content")) parts.Add(new { text = m["content"] });
                    if (m.ContainsKey("image")) parts.Add(new { inlineData = new { mimeType = "image/png", data = m["image"] } });
                    return (object)new { role = m["role"], parts = parts.ToArray() };
                }).ToArray(),
                string text => new[] { (object)new { role = "user", parts = new[] { new { text } } } },
                _ => throw new ArgumentException("Invalid payload type")
            };

            object enhancedPayload;

            if (useTools)
            {
                enhancedPayload = new { contents, tools };
            }
            else
            {
                enhancedPayload = new { contents, generationConfig };
            }
            var jsonPayload = JsonSerializer.Serialize(enhancedPayload);
            logger.Log($"Preparing LLM request with payload: {jsonPayload.Substring(0, Math.Min(500, jsonPayload.Length))}...");

            var (content, response) = await SendRequest(client, enhancedPayload, imageBase64 != null);
            if (content == null)
            {
                logger.Log($"LLM request failed: StatusCode={response.StatusCode}, Error={response.ErrorMessage}");
                return string.Empty;
            }

            var data = JsonSerializer.Deserialize<JsonElement>(content);
            string message = ExtractResponse(logger, data, client);
            logger.Log($"Response extracted: '{message.Substring(0, Math.Min(100, message.Length))}...'");
            return message;
        }

        private static async Task<(string?, RestResponse)> SendRequest(GeminiClient client, object payload, bool isImageRequest)
        {
            var url = client.GetGenerateUrl();
            var restClient = new RestClient(url);
            var request = new RestRequest { Method = Method.Post };
            request.AddHeader("Content-Type", "application/json");
            request.AddJsonBody(payload);

            client.UpdateStatus(Status.Receiving);
            const int maxRetries = 3;

            await GenerationRateLimiter.WaitAsync();
            try
            {
                for (int retry = 0; retry <= maxRetries; retry++)
                {
                    var response = await restClient.ExecuteAsync(request);

                    client.Logger.Log(response.Content ?? "Empty response !");

                    if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests || response.StatusCode == System.Net.HttpStatusCode.ServiceUnavailable)
                    {
                        if (retry == maxRetries)
                        {
                            client.Logger.Log("Max retries reached for 429 error");
                            return (null, response);
                        }
                        int delay = (int)Math.Pow(2, retry) * 1000;
                        client.Logger.Log($"429 Too Many Requests, retrying in {delay}ms (attempt {retry + 1}/{maxRetries})");
                        await Task.Delay(delay);
                        continue;
                    }
                    response.ThrowIfError();
                    client.Logger.Log($"Response received: {response.Content?.Substring(0, Math.Min(500, response.Content?.Length ?? 0))}...");
                    return (response.Content, response);
                }
            }
            catch (Exception ex)
            {
                client.Logger.Log($"SendRequest error: {ex.Message}");
                return (null, new RestResponse());
            }
            finally
            {
                GenerationRateLimiter.Release();
                client.UpdateStatus(Status.Idle);
            }
            return (null, new RestResponse());
        }

        private static string ExtractResponse(Logger logger, JsonElement data, GeminiClient client)
        {
            if (!data.TryGetProperty("candidates", out var candidates) || !candidates.EnumerateArray().Any())
            {
                logger.Log("No candidates in LLM response");
                return string.Empty;
            }

            var messageParts = new List<string>();
            foreach (var candidate in candidates.EnumerateArray())
            {
                foreach (var part in candidate.GetProperty("content").GetProperty("parts").EnumerateArray())
                {
                    if (part.TryGetProperty("text", out var text) && text.GetString() is string textValue)
                    {
                        messageParts.Add(textValue);
                    }
                    else if (part.TryGetProperty("inlineData", out var inlineData) &&
                             inlineData.TryGetProperty("mimeType", out var mimeType) &&
                             inlineData.TryGetProperty("data", out var dataValue) &&
                             mimeType.GetString() is string mime && dataValue.GetString() is string base64)
                    {
                        base64 = client.ResizeImageBase64(base64);

                        messageParts.Add($"<img src='data:{mime};base64,{base64}' style='max-width:100%;'>");
                    }
                }
            }

            return string.Join("<br><br>", messageParts);
        }
    }
}