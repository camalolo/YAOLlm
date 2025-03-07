using System;
using System.Threading.Tasks;

namespace Gemini
{
    public static class SummaryFunctions
    {
        public static async Task<string> GenerateSummary(GeminiClient client, string content)
        {
            try
            {
                var prompt = $"Summarize the following content into a concise, keyword-rich one-sentence summary:\n\n{content}";
                return await ApiFunctions.SendToLLM(client, prompt, null, false) ?? string.Empty;
            }
            catch (Exception ex)
            {
                client.Logger.Log($"Error generating summary: {ex.Message}"); // Use public getter
                return string.Empty;
            }
        }
    }
}