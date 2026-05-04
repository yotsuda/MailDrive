using System.Text.Json.Serialization;

namespace MailDrive.Models;

public abstract class MailItemBase
{
    [JsonIgnore] public string Path { get; set; } = "";
    [JsonIgnore] public string Directory { get; set; } = "";
    public abstract bool IsContainer { get; }
}

public class MailFolderInfo : MailItemBase
{
    public override bool IsContainer => true;

    public string Name { get; set; } = "";
    public string FullName { get; set; } = "";
    public int MessageCount { get; set; }
    public int UnreadCount { get; set; }
    public int SubfolderCount { get; set; }
    public string Attributes { get; set; } = "";
}

public class MailMessageInfo : MailItemBase
{
    public override bool IsContainer => false;

    public uint Uid { get; set; }
    public string FileName { get; set; } = "";
    public string Subject { get; set; } = "";
    public string From { get; set; } = "";
    public string To { get; set; } = "";
    public DateTime Date { get; set; }
    public bool IsRead { get; set; }
    public bool IsFlagged { get; set; }
    public bool IsAnswered { get; set; }
    public bool IsDraft { get; set; }
    public bool HasAttachments { get; set; }
    public int Size { get; set; }

    public string? MessageId { get; set; }
    public string? InReplyTo { get; set; }
    public string[]? References { get; set; }
    public ulong? ThreadId { get; set; }
}

public class MailThreadInfo : MailItemBase
{
    public override bool IsContainer => true;

    public ulong ThreadId { get; set; }
    public string Name { get; set; } = "";
    public string Subject { get; set; } = "";
    public string Participants { get; set; } = "";
    public int MessageCount { get; set; }
    public DateTime LatestDate { get; set; }
    public bool HasUnread { get; set; }
    public bool HasAttachments { get; set; }
    public bool HasFlagged { get; set; }
}
