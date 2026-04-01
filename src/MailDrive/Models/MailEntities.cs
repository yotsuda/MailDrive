using System.Text.Json.Serialization;

namespace MailDrive.Models;

public class MailFolderInfo
{
    [JsonIgnore] public string Path { get; set; } = "";
    [JsonIgnore] public string Directory { get; set; } = "";

    public string Name { get; set; } = "";
    public string FullName { get; set; } = "";
    public int MessageCount { get; set; }
    public int UnreadCount { get; set; }
    public int SubfolderCount { get; set; }
    public string Attributes { get; set; } = "";
}

public class MailMessageInfo
{
    [JsonIgnore] public string Path { get; set; } = "";
    [JsonIgnore] public string Directory { get; set; } = "";

    public uint Uid { get; set; }
    public string FileName { get; set; } = "";
    public string Subject { get; set; } = "";
    public string From { get; set; } = "";
    public string To { get; set; } = "";
    public DateTime Date { get; set; }
    public bool IsRead { get; set; }
    public bool HasAttachments { get; set; }
    public int Size { get; set; }
}
