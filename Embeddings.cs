using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace Gemini
{
    public static class Embeddings
    {
        private static readonly ConcurrentDictionary<string, int> _vocabulary = new();
        private static readonly object _vocabularyLock = new();
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

        public static float[] ComputeEmbedding(string text)
        {
            try
            {
                // Preprocess text: tokenize, lowercase, remove punctuation, and filter stop words
                var words = text.ToLower()
                    .Split(new[] { ' ', '\n', '\r', '\t', '.', ',', ';', ':', '!', '?', '(', ')', '[', ']', '{', '}', '"', '\'', '/', '\\' },
                        StringSplitOptions.RemoveEmptyEntries)
                    .Where(w => w.Length > 2 && !_stopWords.Contains(w))
                    .ToList();

                // Update vocabulary
                lock (_vocabularyLock)
                {
                    foreach (var word in words.Distinct())
                    {
                        _vocabulary.TryAdd(word, _vocabulary.Count);
                    }
                }

                // Compute TF-IDF vector
                var vector = new float[_vocabulary.Count];
                var wordCounts = words.GroupBy(w => w)
                    .ToDictionary(g => g.Key, g => g.Count());

                foreach (var (word, count) in wordCounts)
                {
                    if (_vocabulary.TryGetValue(word, out int index))
                    {
                        float tf = 1 + (float)Math.Log(count);
                        vector[index] = tf;
                    }
                }

                // L2 normalization
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
                return Array.Empty<float>();
            }
        }

        public static float CosineSimilarity(float[] a, float[] b)
        {
            int length = Math.Min(a.Length, b.Length);
            if (length == 0) return 0;

            var aAdjusted = a.Length > length ? a.Take(length).ToArray() : a.Concat(Enumerable.Repeat(0f, length - a.Length)).ToArray();
            var bAdjusted = b.Length > length ? b.Take(length).ToArray() : b.Concat(Enumerable.Repeat(0f, length - b.Length)).ToArray();

            float dot = aAdjusted.Zip(bAdjusted, (x, y) => x * y).Sum();
            float magA = (float)Math.Sqrt(aAdjusted.Sum(x => x * x));
            float magB = (float)Math.Sqrt(bAdjusted.Sum(x => x * x));
            return magA * magB == 0 ? 0 : dot / (magA * magB);
        }
    }
}