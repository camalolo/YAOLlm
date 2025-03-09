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
                   "You are an AI assistant with access to tools for searching local memories, Google, and deleting memories.\n" +
                   "**Memory Search**: Use 'search_memory' to find relevant stored information when responding to queries.\n" +
                   "**Online Search**: If memory lacks sufficient data, use 'search_google' with refined terms derived from the query.\n" +
                   "**Efficiency**: Prefer memory over online searches, but switch to Google if memory results are inadequate.\n" +
                   "**Query Refinement**: If memory search fails, refine terms (synonyms, context) before resorting to Google.\n" +
                   "**Autonomy**: Do not ask for user confirmation to use tools; decide based on query context.\n" +
                   "**Chunking**: Responses and data may be split into chunks; process each chunk and provide a cohesive answer.";
        }

        public static string GetProcessedContentPrompt(string searchTerms, string originalQuery, List<(string content, string url)> contentUrlPairs)
        {
            var contentString = string.Join("\n\n", contentUrlPairs.Select(p => $"Source: {p.url}\n{p.content}"));
            return $"*Processed Search Results*\n" +
                   $"Searched for '{searchTerms}' to answer: '{originalQuery}'.\n" +
                   "Results:\n" +
                   $"<content>\n{contentString}\n</content>\n" +
                   $"Provide a helpful response to '{originalQuery}' based on this content.";
        }

        public static string GetNoRelevantResultsPrompt(string searchRequest, string originalQuery)
        {
            return $"No relevant results found for '{searchRequest}'.\n" +
                   $"Immediately use 'search_google' with refined terms based on: '{originalQuery}'.\n" +
                   "Do not ask for confirmation; proceed with the new search.";
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