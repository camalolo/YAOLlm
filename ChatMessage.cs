namespace YAOLlm;

public class ChatMessage
{
    public string Role { get; }
    public string? Content { get; set; }
    public byte[]? Image { get; }

    public ChatMessage(string role, string? content = null, byte[]? image = null)
    {
        Role = role ?? throw new ArgumentNullException(nameof(role));
        Content = content;
        Image = image;
    }
}
