using System.Collections.ObjectModel;
using System.Management.Automation;
using System.Management.Automation.Provider;
using MailDrive.Models;
using MailKit;
using MailKit.Search;
using MimeKit;

namespace MailDrive.Provider;

[CmdletProvider("Imap", ProviderCapabilities.ShouldProcess)]
[OutputType(typeof(MailFolderInfo), ProviderCmdlet = ProviderCmdlet.GetChildItem)]
[OutputType(typeof(MailMessageInfo), ProviderCmdlet = ProviderCmdlet.GetChildItem)]
public class ImapDriveProvider : NavigationCmdletProvider, IContentCmdletProvider, IPropertyCmdletProvider
{
    private ImapDriveInfo Drive => (ImapDriveInfo)PSDriveInfo;

    // ── Path helpers ────────────────────────────────────────────

    private string NormalizePath(string path)
    {
        if (path.Contains(':'))
            path = path[(path.IndexOf(':') + 1)..];
        return path.Replace('\\', '/').Trim('/');
    }

    private string EnsureDrivePrefix(string path)
    {
        var prefix = $"{PSDriveInfo.Name}:\\";
        if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return path;
        var norm = path.Replace('\\', '/').Trim('/');
        return string.IsNullOrEmpty(norm) ? prefix : $"{prefix}{norm.Replace('/', '\\')}";
    }

    // ── Path overrides ──────────────────────────────────────────

    protected override string MakePath(string parent, string child)
    {
        string result = base.MakePath(parent, child);
        if (result.EndsWith('\\') && result.Length > 1 && result[^2] != ':')
            result = result[..^1];
        return result;
    }

    protected override string NormalizeRelativePath(string path, string basePath)
    {
        string result = base.NormalizeRelativePath(path, basePath);
        if (result.StartsWith('\\') && result.Length > 1)
            result = result[1..];
        return result;
    }

    protected override bool IsValidPath(string path) => true;

    // ── Drive management ────────────────────────────────────────

    protected override Collection<PSDriveInfo> InitializeDefaultDrives()
    {
        var drives = new Collection<PSDriveInfo>();
        try
        {
            var configPath = MailConfig.DefaultPath;
            if (!File.Exists(configPath)) return drives;
            MailConfig.ConfigLastWriteTimeUtc = File.GetLastWriteTimeUtc(configPath);

            var config = MailConfig.Load(configPath);
            if (config?.PSDrives == null) return drives;

            foreach (var s in config.PSDrives)
            {
                s.CascadeFrom(config);
                var protocol = s.Protocol ?? "IMAP";
                if (!string.Equals(protocol, "IMAP", StringComparison.OrdinalIgnoreCase)) continue;
                if (string.IsNullOrEmpty(s.Name) || string.IsNullOrEmpty(s.Host)) continue;

                var ssl = MailConfig.ParseSsl(s.Ssl);
                var port = s.Port ?? 993;
                var desc = string.IsNullOrEmpty(s.Description)
                    ? $"{s.Username}@{s.Host}:{port}" : s.Description;

                var driveParams = new PSDriveInfo(s.Name, ProviderInfo, s.Name + @":\", desc, null);
                var driveInfo = new ImapDriveInfo(driveParams, s.Host, port, ssl,
                    s.Username ?? "", s.Password ?? "",
                    s.SmtpHost, s.SmtpPort ?? 587, MailConfig.ParseSsl(s.SmtpSsl));
                drives.Add(driveInfo);
            }
        }
        catch { }
        return drives;
    }

    protected override PSDriveInfo NewDrive(PSDriveInfo drive)
    {
        if (drive is ImapDriveInfo)
            return drive;
        ThrowTerminatingError(new ErrorRecord(
            new ArgumentException("Use New-ImapDrive to create an IMAP drive."),
            "InvalidDrive", ErrorCategory.InvalidArgument, drive));
        return drive;
    }

    protected override PSDriveInfo RemoveDrive(PSDriveInfo drive)
    {
        if (drive is ImapDriveInfo imap)
            imap.Dispose();
        return drive;
    }

    // ── Item existence ──────────────────────────────────────────

    protected override bool ItemExists(string path)
    {
        var norm = NormalizePath(path);
        var info = ImapPathInfo.Parse(norm);

        switch (info.Type)
        {
            case PathType.Root:
                return true;
            case PathType.Folder:
                return Drive.TryGetImapFolder(info.ImapFolderPath) != null;
            case PathType.Message:
                var uid = info.GetMessageUid();
                if (uid == null) return false;
                var folder = Drive.TryGetImapFolder(info.ImapFolderPath);
                if (folder == null) return false;
                try
                {
                    OpenFolderIfNeeded(folder, FolderAccess.ReadOnly);
                    var uids = folder.Search(SearchQuery.Uids(new[] { new UniqueId(uid.Value) }));
                    return uids.Count > 0;
                }
                catch { return false; }
                finally { TryClose(folder); }
        }
        return false;
    }

    protected override bool IsItemContainer(string path)
        => ImapPathInfo.Parse(NormalizePath(path)).IsContainer;

    protected override bool HasChildItems(string path)
    {
        var info = ImapPathInfo.Parse(NormalizePath(path));
        return info.Type != PathType.Message;
    }

    // ── GetItem ─────────────────────────────────────────────────

    protected override void GetItem(string path)
    {
        var norm = NormalizePath(path);
        var info = ImapPathInfo.Parse(norm);

        switch (info.Type)
        {
            case PathType.Root:
                WriteItemObject(new { Root = "\\", Provider = "IMAP" }, path, true);
                break;
            case PathType.Folder:
                var folder = Drive.GetImapFolder(info.ImapFolderPath);
                var fi = BuildFolderInfo(folder, path);
                WriteItemObject(fi, path, true);
                break;
            case PathType.Message:
                var msg = FetchMessage(info);
                if (msg != null)
                    WriteItemObject(msg, path, false);
                break;
        }
    }

    // ── GetChildItems ───────────────────────────────────────────

    protected override void GetChildItems(string path, bool recurse)
    {
        var norm = NormalizePath(path);
        var info = ImapPathInfo.Parse(norm);
        var paging = DynamicParameters as MailPaginationParameters;
        int first = paging?.First ?? 50;
        if (info.Type == PathType.Message) return;

        var directory = EnsureDrivePrefix(path);

        var subfolders = GetCachedSubfolders(norm, directory);
        List<string>? containers = recurse ? new() : null;
        foreach (var fi in subfolders)
        {
            WriteItemObject(fi, fi.Path, true);
            if (recurse) containers!.Add(fi.Path);
        }

        if (info.Type != PathType.Root)
        {
            var search = paging?.Search;
            var messages = string.IsNullOrEmpty(search)
                ? GetCachedMessages(norm, directory, first)
                : SearchMessages(norm, directory, search, first);
            foreach (var msg in messages)
                WriteItemObject(msg, msg.Path, false);
        }

        if (containers != null)
            foreach (var p in containers)
                GetChildItems(p, true);
    }

    protected override object? GetChildItemsDynamicParameters(string path, bool recurse)
        => new MailPaginationParameters();

    // ── GetChildNames ───────────────────────────────────────────

    protected override void GetChildNames(string path, ReturnContainers returnContainers)
    {
        var norm = NormalizePath(path);
        var info = ImapPathInfo.Parse(norm);
        if (info.Type == PathType.Message) return;

        var directory = EnsureDrivePrefix(path);

        var subfolders = GetCachedSubfolders(norm, directory);
        foreach (var fi in subfolders)
            WriteItemObject(fi.Name, MakePath(path, fi.Name), true);

        if (info.Type != PathType.Root)
        {
            var messages = GetCachedMessages(norm, directory, 50);
            foreach (var msg in messages)
                WriteItemObject(msg.FileName, MakePath(path, msg.FileName), false);
        }
    }

    // ── RemoveItem ──────────────────────────────────────────────

    protected override void RemoveItem(string path, bool recurse)
    {
        var norm = NormalizePath(path);
        var info = ImapPathInfo.Parse(norm);

        if (info.Type == PathType.Message)
        {
            var uid = info.GetMessageUid();
            if (uid == null) return;
            if (!ShouldProcess($"UID {uid} in {info.ImapFolderPath}", "Delete message")) return;

            var folder = Drive.GetImapFolder(info.ImapFolderPath);
            OpenFolderIfNeeded(folder, FolderAccess.ReadWrite);
            try
            {
                folder.AddFlags(new UniqueId(uid.Value), MessageFlags.Deleted, true);
                folder.Expunge();
            }
            finally { TryClose(folder); }
            Drive.InvalidateMessages(info.ImapFolderPath);
        }
        else if (info.Type == PathType.Folder)
        {
            if (!ShouldProcess(info.ImapFolderPath, "Delete IMAP folder")) return;
            Drive.GetImapFolder(info.ImapFolderPath).Delete();
            int lastSlash = info.ImapFolderPath.LastIndexOf('/');
            Drive.InvalidateFolders(lastSlash < 0 ? "" : info.ImapFolderPath[..lastSlash]);
        }
    }

    // ── NewItem ─────────────────────────────────────────────────

    protected override void NewItem(string path, string itemTypeName, object? newItemValue)
    {
        var norm = NormalizePath(path);
        var info = ImapPathInfo.Parse(norm);
        if (info.Type != PathType.Folder) return;

        int lastSlash = info.ImapFolderPath.LastIndexOf('/');
        IMailFolder parent;
        string folderName;
        if (lastSlash < 0)
        {
            parent = Drive.Client.GetFolder(Drive.Client.PersonalNamespaces[0]);
            folderName = info.ImapFolderPath;
        }
        else
        {
            parent = Drive.GetImapFolder(info.ImapFolderPath[..lastSlash]);
            folderName = info.ImapFolderPath[(lastSlash + 1)..];
        }

        var created = parent.Create(folderName, true);
        var fi = BuildFolderInfo(created, path);
        WriteItemObject(fi, path, true);
        Drive.InvalidateFolders(lastSlash < 0 ? "" : info.ImapFolderPath[..lastSlash]);
    }

    // ── MoveItem ─────────────────────────────────────────────────

    protected override void MoveItem(string path, string destination)
    {
        var srcInfo = ImapPathInfo.Parse(NormalizePath(path));
        if (srcInfo.Type != PathType.Message) return;
        var uid = srcInfo.GetMessageUid();
        if (uid == null) return;

        var dstNorm = NormalizePath(destination);
        var dstInfo = ImapPathInfo.Parse(dstNorm);
        var dstFolderPath = dstInfo.IsContainer ? dstInfo.ImapFolderPath : srcInfo.ImapFolderPath;
        if (string.IsNullOrEmpty(dstFolderPath)) return;

        if (!ShouldProcess($"UID {uid} → {dstFolderPath}", "Move message")) return;

        var srcFolder = Drive.GetImapFolder(srcInfo.ImapFolderPath);
        var dstFolder = Drive.GetImapFolder(dstFolderPath);
        OpenFolderIfNeeded(srcFolder, FolderAccess.ReadWrite);
        try
        {
            srcFolder.MoveTo(new UniqueId(uid.Value), dstFolder);
        }
        finally { TryClose(srcFolder); }

        Drive.InvalidateMessages(srcInfo.ImapFolderPath);
        Drive.InvalidateMessages(dstFolderPath);
    }

    // ── CopyItem ────────────────────────────────────────────────

    protected override void CopyItem(string path, string copyPath, bool recurse)
    {
        var srcInfo = ImapPathInfo.Parse(NormalizePath(path));
        if (srcInfo.Type != PathType.Message) return;
        var uid = srcInfo.GetMessageUid();
        if (uid == null) return;

        var dstNorm = NormalizePath(copyPath);
        var dstInfo = ImapPathInfo.Parse(dstNorm);
        var dstFolderPath = dstInfo.IsContainer ? dstInfo.ImapFolderPath : srcInfo.ImapFolderPath;
        if (string.IsNullOrEmpty(dstFolderPath)) return;

        if (!ShouldProcess($"UID {uid} → {dstFolderPath}", "Copy message")) return;

        var srcFolder = Drive.GetImapFolder(srcInfo.ImapFolderPath);
        var dstFolder = Drive.GetImapFolder(dstFolderPath);
        OpenFolderIfNeeded(srcFolder, FolderAccess.ReadOnly);
        try
        {
            srcFolder.CopyTo(new UniqueId(uid.Value), dstFolder);
        }
        finally { TryClose(srcFolder); }

        Drive.InvalidateMessages(dstFolderPath);
    }

    // ── InvokeDefaultAction ─────────────────────────────────────

    protected override void InvokeDefaultAction(string path)
    {
        var norm = NormalizePath(path);
        var info = ImapPathInfo.Parse(norm);
        if (info.Type != PathType.Message) return;

        var uid = info.GetMessageUid();
        if (uid == null) return;

        var folder = Drive.GetImapFolder(info.ImapFolderPath);
        OpenFolderIfNeeded(folder, FolderAccess.ReadOnly);
        try
        {
            var message = folder.GetMessage(new UniqueId(uid.Value));
            var tempFile = Path.Combine(Path.GetTempPath(),
                $"MailDrive_{uid}_{MailHelpers.SanitizeFileName(message.Subject)}.eml");
            message.WriteTo(tempFile);
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(tempFile) { UseShellExecute = true });
        }
        finally { TryClose(folder); }
    }

    // ── IContentCmdletProvider ──────────────────────────────────

    public IContentReader GetContentReader(string path)
    {
        var norm = NormalizePath(path);
        var info = ImapPathInfo.Parse(norm);
        if (info.Type != PathType.Message)
            throw new PSNotSupportedException("Get-Content is only supported for messages.");
        var uid = info.GetMessageUid()
            ?? throw new PSNotSupportedException("Cannot determine message UID.");

        var folder = Drive.GetImapFolder(info.ImapFolderPath);
        OpenFolderIfNeeded(folder, FolderAccess.ReadOnly);
        try
        {
            var message = folder.GetMessage(new UniqueId(uid));
            return new MailContentReader(MailHelpers.FormatMessage(message));
        }
        finally { TryClose(folder); }
    }

    public object? GetContentReaderDynamicParameters(string path) => null;
    public IContentWriter GetContentWriter(string path)
        => throw new PSNotSupportedException("Writing content is not supported.");
    public object? GetContentWriterDynamicParameters(string path) => null;
    public void ClearContent(string path)
        => throw new PSNotSupportedException("Clearing content is not supported.");
    public object? ClearContentDynamicParameters(string path) => null;

    // ── IPropertyCmdletProvider ─────────────────────────────────

    public void GetProperty(string path, Collection<string>? providerSpecificPickList)
    {
        var norm = NormalizePath(path);
        var info = ImapPathInfo.Parse(norm);
        if (info.Type != PathType.Message) return;

        var msg = FetchMessage(info);
        if (msg == null) return;

        var pso = new PSObject();
        var pick = providerSpecificPickList?.Count > 0 ? providerSpecificPickList : null;
        if (pick == null || pick.Contains("IsRead"))
            pso.Properties.Add(new PSNoteProperty("IsRead", msg.IsRead));
        if (pick == null || pick.Contains("Subject"))
            pso.Properties.Add(new PSNoteProperty("Subject", msg.Subject));
        if (pick == null || pick.Contains("From"))
            pso.Properties.Add(new PSNoteProperty("From", msg.From));
        if (pick == null || pick.Contains("To"))
            pso.Properties.Add(new PSNoteProperty("To", msg.To));
        if (pick == null || pick.Contains("Date"))
            pso.Properties.Add(new PSNoteProperty("Date", msg.Date));
        if (pick == null || pick.Contains("Size"))
            pso.Properties.Add(new PSNoteProperty("Size", msg.Size));
        if (pick == null || pick.Contains("HasAttachments"))
            pso.Properties.Add(new PSNoteProperty("HasAttachments", msg.HasAttachments));
        WritePropertyObject(pso, path);
    }

    public object? GetPropertyDynamicParameters(string path, Collection<string>? providerSpecificPickList) => null;

    public void SetProperty(string path, PSObject propertyValue)
    {
        var norm = NormalizePath(path);
        var info = ImapPathInfo.Parse(norm);
        if (info.Type != PathType.Message) return;

        var uid = info.GetMessageUid();
        if (uid == null) return;

        var isReadProp = propertyValue.Properties["IsRead"];
        if (isReadProp == null) return;

        bool isRead = System.Management.Automation.LanguagePrimitives.ConvertTo<bool>(isReadProp.Value);

        if (!ShouldProcess($"UID {uid}", isRead ? "Mark as read" : "Mark as unread")) return;

        var folder = Drive.GetImapFolder(info.ImapFolderPath);
        OpenFolderIfNeeded(folder, FolderAccess.ReadWrite);
        try
        {
            if (isRead)
                folder.AddFlags(new UniqueId(uid.Value), MessageFlags.Seen, true);
            else
                folder.RemoveFlags(new UniqueId(uid.Value), MessageFlags.Seen, true);
        }
        finally { TryClose(folder); }

        Drive.InvalidateMessages(info.ImapFolderPath);
    }

    public object? SetPropertyDynamicParameters(string path, PSObject propertyValue) => null;

    public void ClearProperty(string path, Collection<string> propertyToClear)
        => throw new PSNotSupportedException("ClearProperty is not supported.");

    public object? ClearPropertyDynamicParameters(string path, Collection<string> propertyToClear) => null;

    // ── Cache helpers ───────────────────────────────────────────

    private List<MailFolderInfo> GetCachedSubfolders(string normalizedPath, string directory)
    {
        var cached = Drive.GetCachedFolders(normalizedPath);
        if (cached != null) return cached;

        IMailFolder parent = string.IsNullOrEmpty(normalizedPath)
            ? Drive.Client.GetFolder(Drive.Client.PersonalNamespaces[0])
            : Drive.GetImapFolder(normalizedPath);

        var result = new List<MailFolderInfo>();
        try
        {
            foreach (var sub in parent.GetSubfolders(false))
            {
                var subNorm = string.IsNullOrEmpty(normalizedPath)
                    ? sub.Name : $"{normalizedPath}/{sub.Name}";
                var fi = BuildFolderInfo(sub, EnsureDrivePrefix(subNorm));
                fi.Directory = directory;
                result.Add(fi);
            }
        }
        catch (Exception ex)
        {
            WriteVerbose($"GetSubfolders failed for '{normalizedPath}': {ex.Message}");
        }

        Drive.SetCachedFolders(normalizedPath, result);
        return result;
    }

    private List<MailMessageInfo> GetCachedMessages(string normalizedPath, string directory, int first)
    {
        var cached = Drive.GetCachedMessages(normalizedPath);
        if (cached != null) return cached;

        var folder = Drive.TryGetImapFolder(normalizedPath);
        if (folder == null || !folder.Exists) return [];

        var result = new List<MailMessageInfo>();
        OpenFolderIfNeeded(folder, FolderAccess.ReadOnly);
        try
        {
            int count = folder.Count;
            if (count > 0)
            {
                int start = Math.Max(0, count - first);
                var summaries = folder.Fetch(start, -1,
                    MessageSummaryItems.UniqueId | MessageSummaryItems.Envelope |
                    MessageSummaryItems.Flags | MessageSummaryItems.Size |
                    MessageSummaryItems.BodyStructure);

                var parentPath = EnsureDrivePrefix(normalizedPath);
                foreach (var summary in summaries.Reverse())
                {
                    var msg = BuildMessageInfo(summary, parentPath);
                    msg.Directory = directory;
                    result.Add(msg);
                }
            }
        }
        catch (Exception ex)
        {
            WriteVerbose($"Fetch messages failed for '{normalizedPath}': {ex.Message}");
        }
        finally { TryClose(folder); }

        Drive.SetCachedMessages(normalizedPath, result);
        return result;
    }

    // ── Search ──────────────────────────────────────────────────

    private List<MailMessageInfo> SearchMessages(string normalizedPath, string directory,
        string search, int first)
    {
        var folder = Drive.TryGetImapFolder(normalizedPath);
        if (folder == null || !folder.Exists) return [];

        OpenFolderIfNeeded(folder, FolderAccess.ReadOnly);
        try
        {
            var query = ParseSearch(search);
            var uids = folder.Search(query);
            var targetUids = uids.Reverse().Take(first).ToList();
            if (targetUids.Count == 0) return [];

            var summaries = folder.Fetch(targetUids,
                MessageSummaryItems.UniqueId | MessageSummaryItems.Envelope |
                MessageSummaryItems.Flags | MessageSummaryItems.Size |
                MessageSummaryItems.BodyStructure);

            var parentPath = EnsureDrivePrefix(normalizedPath);
            return summaries.Select(s =>
            {
                var msg = BuildMessageInfo(s, parentPath);
                msg.Directory = directory;
                return msg;
            }).ToList();
        }
        catch (Exception ex)
        {
            WriteVerbose($"Search failed: {ex.Message}");
            return [];
        }
        finally { TryClose(folder); }
    }

    private static SearchQuery ParseSearch(string search)
    {
        SearchQuery? query = null;
        foreach (var token in search.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            SearchQuery term;
            var colonIdx = token.IndexOf(':');
            if (colonIdx > 0)
            {
                var key = token[..colonIdx].ToLowerInvariant();
                var value = token[(colonIdx + 1)..];
                term = key switch
                {
                    "from" => SearchQuery.FromContains(value),
                    "to" => SearchQuery.ToContains(value),
                    "subject" => SearchQuery.SubjectContains(value),
                    "body" => SearchQuery.BodyContains(value),
                    "since" => SearchQuery.DeliveredAfter(DateTime.Parse(value)),
                    "before" => SearchQuery.DeliveredBefore(DateTime.Parse(value)),
                    _ => SearchQuery.SubjectContains(token),
                };
            }
            else
            {
                term = token.ToLowerInvariant() switch
                {
                    "unread" => SearchQuery.NotSeen,
                    "read" => SearchQuery.Seen,
                    "flagged" => SearchQuery.Flagged,
                    _ => SearchQuery.SubjectContains(token),
                };
            }
            query = query == null ? term : query.And(term);
        }
        return query ?? SearchQuery.All;
    }

    // ── IMAP helpers ────────────────────────────────────────────

    private static void OpenFolderIfNeeded(IMailFolder folder, FolderAccess access)
    {
        if (!folder.IsOpen)
            folder.Open(access);
        else if (folder.Access < access)
        {
            folder.Close(false);
            folder.Open(access);
        }
    }

    private static void TryClose(IMailFolder folder)
    {
        try { if (folder.IsOpen) folder.Close(false); } catch { }
    }

    private MailFolderInfo BuildFolderInfo(IMailFolder folder, string path)
    {
        var status = GetFolderStatus(folder);
        return new MailFolderInfo
        {
            Path = EnsureDrivePrefix(path),
            Name = folder.Name,
            FullName = folder.FullName,
            MessageCount = status.count,
            UnreadCount = status.unread,
            SubfolderCount = GetSubfolderCount(folder),
            Attributes = FormatAttributes(folder.Attributes),
        };
    }

    private (int count, int unread) GetFolderStatus(IMailFolder folder)
    {
        try
        {
            folder.Status(StatusItems.Count | StatusItems.Unread);
            return (folder.Count, folder.Unread);
        }
        catch (Exception ex)
        {
            WriteVerbose($"Status failed for '{folder.FullName}': {ex.Message}");
            return (0, 0);
        }
    }

    private int GetSubfolderCount(IMailFolder folder)
    {
        try { return folder.GetSubfolders(false).Count; }
        catch { return 0; }
    }

    private MailMessageInfo BuildMessageInfo(IMessageSummary summary, string parentPath)
    {
        var envelope = summary.Envelope;
        var fileName = BuildMessageFileName(summary);
        return new MailMessageInfo
        {
            Path = EnsureDrivePrefix(MakePath(parentPath, fileName)),
            Uid = summary.UniqueId.Id,
            FileName = fileName,
            Subject = envelope.Subject ?? "(no subject)",
            From = MailHelpers.FormatAddress(envelope.From),
            To = MailHelpers.FormatAddress(envelope.To),
            Date = envelope.Date?.LocalDateTime ?? DateTime.MinValue,
            IsRead = summary.Flags?.HasFlag(MessageFlags.Seen) ?? false,
            HasAttachments = summary.Attachments?.Any() ?? false,
            Size = (int)(summary.Size ?? 0),
        };
    }

    private string BuildMessageFileName(IMessageSummary summary)
    {
        var from = summary.Envelope.From?.Mailboxes.FirstOrDefault();
        var fromName = from?.Name ?? from?.Address ?? "unknown";
        var subject = summary.Envelope.Subject ?? "no-subject";
        return $"{summary.UniqueId.Id}_{MailHelpers.SanitizeFileName(fromName)}_{MailHelpers.SanitizeFileName(subject)}.eml";
    }

    private MailMessageInfo? FetchMessage(ImapPathInfo info)
    {
        var uid = info.GetMessageUid();
        if (uid == null) return null;
        var folder = Drive.TryGetImapFolder(info.ImapFolderPath);
        if (folder == null) return null;

        OpenFolderIfNeeded(folder, FolderAccess.ReadOnly);
        try
        {
            var summaries = folder.Fetch(new[] { new UniqueId(uid.Value) },
                MessageSummaryItems.UniqueId | MessageSummaryItems.Envelope |
                MessageSummaryItems.Flags | MessageSummaryItems.Size |
                MessageSummaryItems.BodyStructure);
            if (summaries.Count == 0) return null;
            var parentPath = info.ImapFolderPath.Replace('/', '\\');
            return BuildMessageInfo(summaries[0], $"{PSDriveInfo.Name}:\\{parentPath}");
        }
        catch { return null; }
        finally { TryClose(folder); }
    }

    private static string FormatAttributes(FolderAttributes attr)
    {
        var parts = new List<string>();
        if (attr.HasFlag(FolderAttributes.Inbox)) parts.Add("Inbox");
        if (attr.HasFlag(FolderAttributes.Sent)) parts.Add("Sent");
        if (attr.HasFlag(FolderAttributes.Drafts)) parts.Add("Drafts");
        if (attr.HasFlag(FolderAttributes.Trash)) parts.Add("Trash");
        if (attr.HasFlag(FolderAttributes.Junk)) parts.Add("Junk");
        if (attr.HasFlag(FolderAttributes.Archive)) parts.Add("Archive");
        if (attr.HasFlag(FolderAttributes.Flagged)) parts.Add("Flagged");
        if (attr.HasFlag(FolderAttributes.All)) parts.Add("All");
        if (attr.HasFlag(FolderAttributes.NoSelect)) parts.Add("NoSelect");
        return parts.Count > 0 ? string.Join(", ", parts) : "";
    }
}
