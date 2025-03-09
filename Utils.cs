namespace Gemini
{
    public static class Utils
    {
        public const int ExpectedEmbeddingDimension = 768;
        public const int MaxCharsForEmbedding = 8192;
        public const int MaxCharsForGenerating = 32000;
        public static float CosineSimilarity(float[] a, float[] b)
        {
            float dot = 0, magA = 0, magB = 0;
            for (int i = 0; i < Math.Min(a.Length, b.Length); i++)
            {
                dot += a[i] * b[i];
                magA += a[i] * a[i];
                magB += b[i] * b[i];
            }
            magA = (float)Math.Sqrt(magA);
            magB = (float)Math.Sqrt(magB);
            return magA * magB == 0 ? 0 : dot / (magA * magB);
        }

        public static List<string> ChunkText(string content, int maxLength)
        {
            if (string.IsNullOrEmpty(content)) return new List<string>();
            if (content.Length <= maxLength) return new List<string> { content };

            var chunks = new List<string>();
            int start = 0;
            while (start < content.Length)
            {
                int length = Math.Min(maxLength, content.Length - start);
                int end = start + length;

                if (end < content.Length)
                {
                    int lastPeriod = content.LastIndexOf('.', end - 1, length);
                    int lastNewline = content.LastIndexOf('\n', end - 1, length);
                    int splitPoint = Math.Max(lastPeriod, lastNewline);
                    if (splitPoint > start) end = splitPoint + 1;
                }

                chunks.Add(content.Substring(start, end - start).Trim());
                start = end;
            }

            return chunks;
        }
    }
}