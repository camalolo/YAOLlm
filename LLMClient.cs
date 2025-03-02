// LLMClient.cs
namespace Gemini
{
    public abstract class LLMClient
    {
        protected string conversationHistory = "";

        public abstract string SendPrompt(string prompt);
        public abstract string SendPromptWithImage(string prompt, byte[] image);
        protected abstract void DefineTools();
    }
}