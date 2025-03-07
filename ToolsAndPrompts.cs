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
                            name = "search_memory_summaries",
                            description = "Searches summaries of locally stored memories. Use first to avoid loading full content.",
                            parameters = new
                            {
                                type = "object",
                                properties = new { query = new { type = "string", description = "Query to search summaries." } },
                                required = new[] { "query" }
                            }
                        },
                        new
                        {
                            name = "search_memory_content",
                            description = "Fetches full content of memories by IDs after searching summaries.",
                            parameters = new
                            {
                                type = "object",
                                properties = new
                                {
                                    ids = new { type = "array", items = new { type = "integer" }, description = "List of memory IDs to fetch content for." },
                                    query = new { type = "string", description = "Optional query to search within content." }
                                },
                                required = new[] { "ids" }
                            }
                        },
                        new
                        {
                            name = "search_google",
                            description = "Searches information on google search and returns fresh live online results. Use it if you have no memories that can help.",
                            parameters = new
                            {
                                type = "object",
                                properties = new { search_terms = new { type = "string", description = "The search query to use." } },
                                required = new[] { "search_terms" }
                            }
                        },
                        new
                        {
                            name = "delete_memories",
                            description = "Deletes memories specified by a list of IDs.",
                            parameters = new
                            {
                                type = "object",
                                properties = new
                                {
                                    ids = new { type = "array", items = new { type = "integer" }, description = "List of memory IDs to delete." }
                                },
                                required = new[] { "ids" }
                            }
                        }
                    }
                }
            };
        }

        public static string GetInitialPrompt() =>
            $"Current Date: {DateTime.Now:yyyy-MM-dd}\n" +
            "You have access to tools to search local memory summaries, fetch memory content, and perform Google searches.\n" +
            "**Prioritize Summaries:** Always start by using 'search_memory_summaries' to quickly identify relevant memories.\n" +
            "**Analyze and Extract:** Review memory summaries carefully to determine if crucial details are present. If essential details are missing, use 'search_memory_content' to fetch additional content.\n" +
            "**Adaptive Search:** If no relevant memories are found or details are insufficient, immediately use 'search_google' with refined search terms derived from the original query.\n" +
            "**Efficiency vs Completeness:** Balance token usage and response accuracy. Prefer concise summary searches, but do not hesitate to resort to more detailed searches when necessary.\n" +
            "If a memory search yields no results, refine the query by considering alternative phrasings, synonyms, and contextual clues before defaulting to online search.\n" +
            "NEVER ask for user confirmation to use tools. Decide autonomously based on context.";

        public static string GetProcessedContentPrompt(string searchTerms, string originalUserQuery, List<(string content, string url)> contentUrlPairs)
        {
            var contentWithUrls = string.Join("\n\n", contentUrlPairs.Select(p => $"Content from: {p.url}\n\n{p.content}"));
            return $"*processed search request result*\n" +
                   $"I searched for '{searchTerms}' to help answer '{originalUserQuery}'.\n\n" +
                   "Here is the resulting content: \n\n" +
                   $"<content>\n{contentWithUrls}\n</content>\n\n" +
                   $"Based on this content, please provide a helpful response to '{originalUserQuery}'.\n\n";
        }

        public static string GetNoRelevantResultsPrompt(string pendingSearchRequest, string originalUserQuery) =>
            $"The previous search produced no relevant results for '{pendingSearchRequest}'. " +
            "Please use the 'search_google' tool immediately to try again with different, more specific, or refined search terms. " +
            $"Base your new search terms on the original user query: '{originalUserQuery}'. " +
            "Do not ask the user for confirmation or to repeat their question — proceed directly with the new search.";

        public static string GetImageDescriptionPrompt(string windowTitle) =>
            string.IsNullOrEmpty(windowTitle)
                ? "This is a screenshot."
                : $"This is a screenshot. The screenshot was taken from an application with the window title '{windowTitle}'. " +
                  "Describe accurately the screenshot components in an organized way, including all the text you can read, " +
                  "for future reference, as we might need to use that information later.";

        public static string GetImageDescriptionDefaultUserPrompt() =>
            "If the screenshot is from a video game, it likely is a puzzle, a missing person or a list of collectibles. " +
            "Please help me find the most likely solution to the question/puzzle/enigma, or find the missing collectibles. " +
            "If the screenshot is from another type of application and shows an error message, or a coding language issue, " +
            "please find how to solve it. If it's just a cute or strange picture, please respond to it in a fun and engaging way.";
    }
}