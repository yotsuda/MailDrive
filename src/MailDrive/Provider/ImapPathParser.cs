namespace MailDrive.Provider;

public enum PathType
{
    Root,
    Folder,
    Message,
}

public class ImapPathInfo
{
    public PathType Type { get; init; }
    public string ImapFolderPath { get; init; } = "";
    public string? MessageFileName { get; init; }
    public bool IsContainer => Type != PathType.Message;

    public static ImapPathInfo Parse(string normalizedPath)
    {
        if (string.IsNullOrEmpty(normalizedPath))
            return new ImapPathInfo { Type = PathType.Root };

        if (normalizedPath.EndsWith(".eml", StringComparison.OrdinalIgnoreCase))
        {
            int lastSlash = normalizedPath.LastIndexOf('/');
            if (lastSlash < 0)
            {
                return new ImapPathInfo
                {
                    Type = PathType.Message,
                    ImapFolderPath = "INBOX",
                    MessageFileName = normalizedPath,
                };
            }
            return new ImapPathInfo
            {
                Type = PathType.Message,
                ImapFolderPath = normalizedPath[..lastSlash],
                MessageFileName = normalizedPath[(lastSlash + 1)..],
            };
        }

        return new ImapPathInfo
        {
            Type = PathType.Folder,
            ImapFolderPath = normalizedPath,
        };
    }

    public uint? GetMessageUid()
    {
        if (MessageFileName == null) return null;
        int underscore = MessageFileName.IndexOf('_');
        if (underscore > 0 && uint.TryParse(MessageFileName[..underscore], out uint uid))
            return uid;
        return null;
    }
}
