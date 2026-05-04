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

    // True for paths shaped <folder>/<msg>.eml/<sibling>.eml — the user has
    // descended into a thread-as-container and is looking at a sibling.
    // Provider treats these as leaves so navigation doesn't recurse forever.
    public bool IsInThreadContext { get; init; }

    public bool IsContainer => Type != PathType.Message;

    public static ImapPathInfo Parse(string normalizedPath)
    {
        if (string.IsNullOrEmpty(normalizedPath))
            return new ImapPathInfo { Type = PathType.Root };

        if (!normalizedPath.EndsWith(".eml", StringComparison.OrdinalIgnoreCase))
        {
            return new ImapPathInfo
            {
                Type = PathType.Folder,
                ImapFolderPath = normalizedPath,
            };
        }

        // .eml leaf (possibly nested inside another .eml = thread-context view)
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

        var leaf = normalizedPath[(lastSlash + 1)..];
        var parent = normalizedPath[..lastSlash];

        // Detect thread context: the parent path itself ends in .eml.
        // Rewrite the IMAP folder to skip the message-as-container layer.
        if (parent.EndsWith(".eml", StringComparison.OrdinalIgnoreCase))
        {
            int parentSlash = parent.LastIndexOf('/');
            var realFolder = parentSlash < 0 ? "INBOX" : parent[..parentSlash];
            return new ImapPathInfo
            {
                Type = PathType.Message,
                ImapFolderPath = realFolder,
                MessageFileName = leaf,
                IsInThreadContext = true,
            };
        }

        return new ImapPathInfo
        {
            Type = PathType.Message,
            ImapFolderPath = parent,
            MessageFileName = leaf,
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
