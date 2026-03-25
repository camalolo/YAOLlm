namespace YAOLlm;

public enum ChatRole
{
    User,
    System,
    Model,
    Error
}

public static class ChatRoleExtensions
{
    public static string ToApiString(this ChatRole role) => role.ToString().ToLowerInvariant();
}

public class ChatMessage
{
    public ChatRole Role { get; }
    public string? Content { get; set; }
    public byte[]? Image { get; }

    public ChatMessage(ChatRole role, string? content = null, byte[]? image = null)
    {
        Role = role;
        Content = content;
        Image = image;
    }
}
