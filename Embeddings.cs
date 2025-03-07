using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Gemini
{
    public static class Embeddings
    {
        private static readonly HashSet<string> _stopWords = new HashSet<string>
        {
            "the", "be", "to", "of", "and", "a", "in", "that", "have",
            "for", "not", "on", "with", "he", "as", "you", "do", "at",
            "this", "but", "his", "by", "from", "they", "we", "say", "her",
            "she", "or", "an", "will", "my", "one", "all", "would", "there",
            "their", "what", "so", "up", "out", "if", "about", "who", "get",
            "which", "go", "me", "when", "make", "can", "like", "time", "no",
            "just", "him", "know", "take", "into", "your", "some", "could",
            "them", "see", "other", "than", "then", "now", "look", "only",
            "come", "its", "over", "think", "also", "back", "after", "use",
            "two", "how", "our", "work", "first", "well", "way", "even",
            "new", "want", "because", "any", "these", "give", "day", "most"
        };

        public static float[] ComputeEmbedding(string text, string? query = null)
        {
            try
            {
                var cleanedText = Regex.Replace(text.ToLower(), @"[^\w\s-]", " ");
                var words = cleanedText.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .Where(w => w.Length > 2 && !_stopWords.Contains(w))
                    .ToList();

                if ((!words.Any() || text.Length < 200) && query != text)
                    return new float[0];

                var queryWords = query != null
                    ? Regex.Replace(query.ToLower(), @"[^\w\s-]", " ")
                        .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                        .Where(w => w.Length > 2 && !_stopWords.Contains(w))
                        .Distinct()
                        .ToList()
                    : new List<string>();
                var vocabulary = queryWords.Concat(words.Distinct())
                    .Distinct()
                    .Take(1000)
                    .ToDictionary(w => w, w => Array.IndexOf(words.Concat(queryWords).Distinct().ToArray(), w));

                var vector = new float[vocabulary.Count];
                var wordCounts = words.GroupBy(w => w)
                    .ToDictionary(g => g.Key, g => g.Count());

                foreach (var (word, count) in wordCounts)
                {
                    if (vocabulary.TryGetValue(word, out int index))
                    {
                        float weight = count;
                        if (queryWords.Contains(word)) weight += 1; // Small boost for query terms
                        vector[index] = weight;
                    }
                }

                float magnitude = (float)Math.Sqrt(vector.Sum(x => x * x));
                if (magnitude > 0)
                {
                    for (int i = 0; i < vector.Length; i++)
                    {
                        vector[i] /= magnitude;
                    }
                }

                return vector;
            }
            catch (Exception)
            {
                return new float[0];
            }
        }

        public static float CosineSimilarity(float[] a, float[] b)
        {
            int length = Math.Min(a.Length, b.Length);
            if (length == 0) return 0;

            float dot = 0, magA = 0, magB = 0;
            for (int i = 0; i < length; i++)
            {
                dot += a[i] * b[i];
                magA += a[i] * a[i];
                magB += b[i] * b[i];
            }
            magA = (float)Math.Sqrt(magA);
            magB = (float)Math.Sqrt(magB);
            return magA * magB == 0 ? 0 : dot / (magA * magB);
        }
    }
}