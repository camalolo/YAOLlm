using System;
using System.Collections.Generic;

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
                            name = "search_memory",
                            description = "Searches stored memories for relevant information.",
                            parameters = new
                            {
                                type = "object",
                                properties = new { search_terms = new { type = "string", description = "Terms to search memories." } },
                                required = new[] { "search_terms" }
                            }
                        },
                        new
                        {
                            name = "search_google",
                            description = "Performs a Google search for fresh online results.",
                            parameters = new
                            {
                                type = "object",
                                properties = new { search_terms = new { type = "string", description = "Terms to search online." } },
                                required = new[] { "search_terms" }
                            }
                        },
                        new
                        {
                            name = "delete_memories",
                            description = "Deletes specified memories by their IDs.",
                            parameters = new
                            {
                                type = "object",
                                properties = new { ids = new { type = "array", items = new { type = "integer" }, description = "List of memory IDs to delete." } },
                                required = new[] { "ids" }
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
                   "**Tool Usage**: For every query, evaluate whether stored memories or online data can assist. Use 'search_memory' to retrieve relevant information from local memory first. If the query requires more data, a comprehensive list, or up-to-date information, use 'search_google' autonomously to supplement your response.\n" +
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