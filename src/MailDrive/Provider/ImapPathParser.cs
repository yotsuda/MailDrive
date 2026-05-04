using System.Text.RegularExpressions;

namespace MailDrive.Provider;

public enum PathType
{
    Root,
    Folder,
    Message,            // <folder>/<msg>.eml — also used for thread-qualified message paths
    ThreadsContainer,   // <folder>/Threads
    Thread,             // <folder>/Threads/T<id>...
}

public class ImapPathInfo
{
    public PathType Type { get; init; }
    public string ImapFolderPath { get; init; } = "";
    public string? MessageFileName { get; init; }
    public ulong? ThreadId { get; init; }
    public string? ThreadSegment { get; init; }
    public bool IsContainer => Type != PathType.Message;

    public const string ThreadsSegment = "Threads";

    public static ImapPathInfo Parse(string normalizedPath)
    {
        if (string.IsNullOrEmpty(normalizedPath))
            return new ImapPathInfo { Type = PathType.Root };

        // Detect Threads/ virtual segment
        var parts = normalizedPath.Split('/');
        int threadsIdx = Array.FindIndex(parts, s => s == ThreadsSegment);
        if (threadsIdx >= 0)
        {
            var folderPath = string.Join('/', parts.Take(threadsIdx));
            // ".../Threads"  → ThreadsContainer
            if (threadsIdx == parts.Length - 1)
                return new ImapPathInfo
                {
                    Type = PathType.ThreadsContainer,
                    ImapFolderPath = folderPath,
                };

            // ".../Threads/T<id>[_slug]" → Thread
            // ".../Threads/T<id>[_slug]/<msg>.eml" → ThreadMessage
            var threadSeg = parts[threadsIdx + 1];
            var threadId = ParseThreadId(threadSeg);
            if (threadsIdx + 1 == parts.Length - 1)
                return new ImapPathInfo
                {
                    Type = PathType.Thread,
                    ImapFolderPath = folderPath,
                    ThreadId = threadId,
                    ThreadSegment = threadSeg,
                };

            // Past the thread segment — only support a single .eml leaf.
            // Treat as a regular Message (ThreadId/ThreadSegment carried along
            // so display paths can be rebased) so all existing Message handlers
            // keep working without per-call-site changes.
            var leaf = parts[^1];
            if (leaf.EndsWith(".eml", StringComparison.OrdinalIgnoreCase))
                return new ImapPathInfo
                {
                    Type = PathType.Message,
                    ImapFolderPath = folderPath,
                    ThreadId = threadId,
                    ThreadSegment = threadSeg,
                    MessageFileName = leaf,
                };

            // Fallback: treat as thread (deeper navigation not supported)
            return new ImapPathInfo
            {
                Type = PathType.Thread,
                ImapFolderPath = folderPath,
                ThreadId = threadId,
                ThreadSegment = threadSeg,
            };
        }

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

    // Parse "T<16hex>[_<slug>]" → ulong. Returns null on malformed segments.
    public static ulong? ParseThreadId(string segment)
    {
        if (string.IsNullOrEmpty(segment) || segment[0] != 'T') return null;
        var rest = segment[1..];
        int underscore = rest.IndexOf('_');
        var hex = underscore > 0 ? rest[..underscore] : rest;
        return ulong.TryParse(hex, System.Globalization.NumberStyles.HexNumber,
            System.Globalization.CultureInfo.InvariantCulture, out var id) ? id : null;
    }

    // Build "T<16hex>_<slug>" segment for a thread.
    public static string BuildThreadSegment(ulong threadId, string subject)
    {
        var slug = MakeSlug(subject);
        return string.IsNullOrEmpty(slug) ? $"T{threadId:X16}" : $"T{threadId:X16}_{slug}";
    }

    private static string MakeSlug(string subject)
    {
        if (string.IsNullOrWhiteSpace(subject)) return "";
        // Strip Re:/Fwd: prefixes for cleaner naming
        var s = Regex.Replace(subject, @"^\s*((Re|Fwd|FW|RE|FWD|Fw)\s*:\s*)+", "", RegexOptions.IgnoreCase);
        // Replace path-unsafe and whitespace runs with '-'
        s = Regex.Replace(s, @"[\s/\\:*?""<>|]+", "-");
        // Trim trailing dashes/dots/spaces (Windows file naming)
        s = s.Trim('-', '.', ' ');
        return s.Length > 30 ? s[..30] : s;
    }
}
