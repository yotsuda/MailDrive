using System.Collections.ObjectModel;
using System.Management.Automation;
using System.Management.Automation.Provider;
using System.Text;
using MailDrive.Models;
using MailKit;
using MimeKit;

namespace MailDrive.Provider;

public static class MailHelpers
{
    public static string SanitizeFileName(string? name)
    {
        if (string.IsNullOrEmpty(name)) return "unknown";
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(name.Length);
        foreach (var c in name)
        {
            if (Array.IndexOf(invalid, c) < 0 && c != '/' && c != '\\')
                sb.Append(c);
        }
        var result = sb.ToString().Trim();
        return result.Length > 60 ? result[..60] : (result.Length == 0 ? "unknown" : result);
    }

    public static string FormatAddress(InternetAddressList? addresses)
    {
        if (addresses == null || addresses.Count == 0) return "";
        return string.Join(", ", addresses.Mailboxes.Select(m =>
            string.IsNullOrEmpty(m.Name) ? m.Address : $"{m.Name} <{m.Address}>"));
    }

    public static string[] FormatMessage(MimeMessage message)
    {
        var lines = new List<string>();
        lines.Add($"From: {message.From}");
        lines.Add($"To: {message.To}");
        if (message.Cc.Count > 0)
            lines.Add($"Cc: {message.Cc}");
        lines.Add($"Date: {message.Date.LocalDateTime:yyyy-MM-dd HH:mm:ss}");
        lines.Add($"Subject: {message.Subject}");
        lines.Add("");
        lines.Add(new string('\u2500', 60));
        lines.Add("");

        var body = message.TextBody ?? message.HtmlBody ?? "";
        lines.AddRange(body.Split('\n').Select(l => l.TrimEnd('\r')));

        if (message.Attachments.Any())
        {
            lines.Add("");
            lines.Add(new string('\u2500', 60));
            lines.Add("Attachments:");
            foreach (var att in message.Attachments)
            {
                var name = att.ContentDisposition?.FileName ?? att.ContentType.Name ?? "(unnamed)";
                lines.Add($"  - {name}");
            }
        }

        return lines.ToArray();
    }

    /// <summary>
    /// Fetch the full MimeMessage for a MailMessageInfo by resolving its drive and UID.
    /// </summary>
    internal static (MailDriveInfoBase Drive, MimeMessage Message)? FetchFullMessage(
        MailMessageInfo msgInfo, SessionState sessionState)
    {
        var colonIdx = msgInfo.Path.IndexOf(':');
        if (colonIdx < 0) return null;
        var driveName = msgInfo.Path[..colonIdx];

        PSDriveInfo drive;
        try { drive = sessionState.Drive.Get(driveName); }
        catch { return null; }

        if (drive is ImapDriveInfo imap)
        {
            // Path: "Gmail:\INBOX\123_sender_Subject.eml" → rest: "INBOX/123_sender_Subject.eml"
            var rest = msgInfo.Path[(colonIdx + 2)..].Replace('\\', '/').Trim('/');
            var lastSlash = rest.LastIndexOf('/');
            // If no slash, message is directly under a folder whose name we can't determine from path alone.
            // This shouldn't happen — IMAP messages are always inside a folder.
            if (lastSlash < 0) return null;
            var folderPath = rest[..lastSlash];

            var folder = imap.GetImapFolder(folderPath);
            if (!folder.IsOpen) folder.Open(FolderAccess.ReadOnly);
            try
            {
                return (imap, folder.GetMessage(new UniqueId(msgInfo.Uid)));
            }
            finally
            {
                try { if (folder.IsOpen) folder.Close(false); } catch { }
            }
        }
        else if (drive is PopDriveInfo pop)
        {
            return (pop, pop.Client.GetMessage((int)(msgInfo.Uid - 1)));
        }

        return null;
    }
}

public class MailPaginationParameters
{
    [Parameter]
    public int? First { get; set; }

    [Parameter(HelpMessage = "IMAP SEARCH query (e.g. 'from:boss subject:meeting unread')")]
    public string? Search { get; set; }
}

public class MailContentWriter : IContentWriter
{
    private readonly ImapDriveInfo _drive;
    private readonly IMailFolder _folder;
    private readonly UniqueId _originalUid;
    private readonly string _folderPath;
    private readonly List<string> _lines = new();

    public MailContentWriter(ImapDriveInfo drive, IMailFolder folder, UniqueId originalUid, string folderPath)
    {
        _drive = drive;
        _folder = folder;
        _originalUid = originalUid;
        _folderPath = folderPath;
    }

    public System.Collections.IList Write(System.Collections.IList content)
    {
        foreach (var item in content)
            _lines.Add(item?.ToString() ?? "");
        return content;
    }

    public void Seek(long offset, SeekOrigin origin) { }

    public void Close()
    {
        if (_lines.Count == 0) return;

        // Fetch original to preserve headers
        if (!_folder.IsOpen) _folder.Open(FolderAccess.ReadWrite);
        try
        {
            var original = _folder.GetMessage(_originalUid);

            // Replace body with new content
            var newBody = string.Join("\n", _lines);
            var builder = new BodyBuilder { TextBody = newBody };
            original.Body = builder.ToMessageBody();

            // Append new first, then delete original (prevents data loss if Append fails)
            var appended = _folder.Append(original, MessageFlags.Draft | MessageFlags.Seen);
            if (appended.HasValue)
            {
                _folder.AddFlags(_originalUid, MessageFlags.Deleted, true);
                _folder.Expunge();
            }
        }
        finally
        {
            try { if (_folder.IsOpen) _folder.Close(false); } catch { }
        }

        _drive.InvalidateMessages(_folderPath);
    }

    public void Dispose() { }
}

public class MailContentReader : IContentReader
{
    private readonly string[] _lines;
    private int _current;

    public MailContentReader(string[] lines) => _lines = lines;

    public System.Collections.IList Read(long readCount)
    {
        var result = new Collection<string>();
        for (long i = 0; i < readCount && _current < _lines.Length; i++, _current++)
            result.Add(_lines[_current]);
        return result;
    }

    public void Seek(long offset, SeekOrigin origin) => _current = origin switch
    {
        SeekOrigin.Begin => (int)offset,
        SeekOrigin.Current => _current + (int)offset,
        SeekOrigin.End => _lines.Length + (int)offset,
        _ => _current,
    };

    public void Close() { }
    public void Dispose() { }
}
