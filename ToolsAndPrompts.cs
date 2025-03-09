namespace Gemini
{
    public static class ToolsAndPrompts
    {
        public static List<object> DefineTools()
        {
            return new List<object>
            {
                new
                {
                    function_declarations = new object[]
                    {
                        new
                        {
                            name = "search",
                            description = "Searches for information, first in local memories and then online if necessary.",
                            parameters = new
                            {
                                type = "object",
                                properties = new { query = new { type = "string", description = "The search query." } },
                                required = new[] { "query" }
                            }
                        }
                    }
                }
            };
        }

        public static string GetInitialPrompt()
        {
            return $"Current Date: {DateTime.Now:yyyy-MM-dd}\n" +
                   "You are an AI assistant designed to provide accurate, complete, and helpful responses to any user query using available tools.\n" +
                   "**Tool Usage**: For every query, use the 'search' tool to retrieve relevant information. The 'search' tool will first check local memories and, if no relevant results are found, it will automatically search online.\n" +
                   "**Completeness**: Always aim to fully address the user’s request. If the query implies a need for a complete dataset (e.g., lists, summaries, or detailed answers), gather all relevant information using tools and present it cohesively.\n" +
                   "**Flexibility**: Adapt your response style to the query—provide concise answers for simple questions, detailed explanations for complex topics, or structured lists when requested. Do not assume limitations unless explicitly stated by the user.\n" +
                   "**Autonomy**: Do not ask for clarification or confirmation to use tools unless the query is genuinely ambiguous. Make logical decisions based on context and proceed with the best available action.\n" +
                   "**Output**: Combine all gathered data into a clear, user-friendly response. Use formatting (e.g., bullet points, numbered lists) when appropriate to enhance readability.";
        }

        public static string GetProcessedContentPrompt(string searchTerms, string originalQuery, List<(string content, string url)> contentUrlPairs)
        {
            var contentString = string.Join("\n\n", contentUrlPairs.Select(p => $"Source: {p.url}\n{p.content}"));
            return $"*Processed Search Results*\n" +
                   $"Searched for '{searchTerms}' to answer: '{originalQuery}'.\n" +
                   "Results:\n" +
                   $"<content>\n{contentString}\n</content>\n" +
                   $"Generate a response to '{originalQuery}' using this content. Ensure the answer is complete, accurate, and formatted for clarity based on the query’s intent.";
        }

        public static string GetNoRelevantResultsPrompt(string searchRequest, string originalQuery)
        {
            return $"No relevant results found for '{searchRequest}' in memory.\n" +
                   $"To fully address '{originalQuery}', immediately use 'search_google' with optimized terms derived from the query. Proceed without delay and compile a complete response.";
        }

        public static string GetImageDescriptionPrompt(string windowTitle)
        {
            return $"Describe this screenshot from '{windowTitle}' accurately, including all visible text and components, for future reference.";
        }

        public static string GetImageDescriptionDefaultUserPrompt()
        {
            return "If this screenshot is from a video game, it may depict a puzzle, missing person, or collectibles list—suggest the most likely solution or locate missing items.\n" +
                   "If it’s from another application showing an error or code issue, provide a solution.\n" +
                   "If it’s a cute or unusual image, respond in a fun, engaging way.";
        }
    }
}