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
            if (!File.Exists(configPath))
            {
                MailConfig.EnsureDefaultConfig(configPath);

                System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo("notepad.exe", configPath)
                    { UseShellExecute = true });

                WriteWarning($"Created default config: {configPath}");
                WriteWarning("Please edit the config file. After saving, run Import-MailConfig to load your drives.");
                return drives;
            }
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
                    s.SmtpHost, s.SmtpPort ?? 587, MailConfig.ParseSsl(s.SmtpSsl),
                    s.IsOAuth2, s.TenantId, s.ClientId);
                drives.Add(driveInfo);
            }
        }
        catch (Exception ex)
        {
            WriteWarning($"Failed to load config '{MailConfig.DefaultPath}': {ex.Message}");
        }
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
                // Check folder cache first
                var parentPath = info.ImapFolderPath.Contains('/')
                    ? info.ImapFolderPath[..info.ImapFolderPath.LastIndexOf('/')]
                    : "";
                var cachedFolders = Drive.GetCachedFolders(parentPath);
                if (cachedFolders != null)
                    return cachedFolders.Any(f => f.FullName == info.ImapFolderPath.Replace('/', Drive.DirectorySeparator));
                return Drive.TryGetImapFolder(info.ImapFolderPath) != null;
            case PathType.Message:
                var uid = info.GetMessageUid();
                if (uid == null) return false;
                // Check message cache first — avoids IMAP round-trip per message
                var cachedMessages = Drive.GetCachedMessages(info.ImapFolderPath);
                if (cachedMessages != null)
                    return cachedMessages.Any(m => m.Uid == uid.Value);
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
            case PathType.ThreadsContainer:
                // Virtual folder under any real IMAP folder.
                return Drive.TryGetImapFolder(info.ImapFolderPath) != null;
            case PathType.Thread:
                // Exists if at least one cached message has this ThreadId.
                if (info.ThreadId == null) return false;
                var cached = Drive.GetCachedMessages(info.ImapFolderPath);
                return cached?.Any(m => MatchesThread(m, info.ThreadId)) ?? false;
        }
        return false;
    }

    protected override bool IsItemContainer(string path)
        => ImapPathInfo.Parse(NormalizePath(path)).IsContainer;

    protected override bool HasChildItems(string path)
    {
        var info = ImapPathInfo.Parse(NormalizePath(path));
        return info.IsContainer;
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
                var folder = Drive.TryGetImapFolder(info.ImapFolderPath);
                if (folder == null) return;
                var fi = BuildFolderInfo(folder, path);
                WriteItemObject(fi, path, true);
                break;
            case PathType.Message:
                var msg = FetchMessage(info);
                if (msg != null)
                {
                    if (info.ThreadId.HasValue)
                    {
                        msg.Path = path;
                        msg.Directory = path[..path.LastIndexOf('\\')];
                    }
                    WriteItemObject(msg, path, false);
                }
                break;
            case PathType.ThreadsContainer:
                WriteItemObject(new MailFolderInfo
                {
                    Path = path,
                    Name = ImapPathInfo.ThreadsSegment,
                    FullName = ImapPathInfo.ThreadsSegment,
                    Attributes = "Virtual",
                    SubfolderCount = 1,
                }, path, true);
                break;
            case PathType.Thread:
                if (info.ThreadId.HasValue)
                {
                    var threadMsgs = GetThreadMessages(info, EnsureDrivePrefix(path), 100);
                    if (threadMsgs.Count > 0)
                    {
                        var latest = threadMsgs.MaxBy(m => m.Date) ?? threadMsgs[0];
                        WriteItemObject(new MailThreadInfo
                        {
                            Path = path,
                            ThreadId = info.ThreadId.Value,
                            Name = info.ThreadSegment ?? "",
                            Subject = latest.Subject,
                            MessageCount = threadMsgs.Count,
                            LatestDate = latest.Date,
                            HasUnread = threadMsgs.Any(m => !m.IsRead),
                            HasAttachments = threadMsgs.Any(m => m.HasAttachments),
                            HasFlagged = threadMsgs.Any(m => m.IsFlagged),
                        }, path, true);
                    }
                }
                break;
        }
    }

    // ── GetChildItems ───────────────────────────────────────────

    protected override void GetChildItems(string path, bool recurse)
    {
        var norm = NormalizePath(path);
        var info = ImapPathInfo.Parse(norm);
        var paging = DynamicParameters as MailPaginationParameters;
        int first = paging?.First ?? 20;
        if (info.Type == PathType.Message) return;

        var directory = EnsureDrivePrefix(path);

        if (Force)
        {
            Drive.InvalidateFolders(info.ImapFolderPath);
            Drive.InvalidateMessages(info.ImapFolderPath);
        }

        // Threads/ virtual folder — list MailThreadInfo grouped from cached messages.
        if (info.Type == PathType.ThreadsContainer)
        {
            var threads = BuildThreads(info.ImapFolderPath, directory, first);
            foreach (var t in threads)
            {
                if (Stopping) return;
                WriteItemObject(t, t.Path, true);
            }
            return;
        }

        // Inside a specific thread — list its messages with rebased path.
        if (info.Type == PathType.Thread)
        {
            var msgs = GetThreadMessages(info, directory, first);
            foreach (var m in msgs)
            {
                if (Stopping) return;
                WriteItemObject(m, m.Path, false);
            }
            return;
        }

        var subfolders = GetCachedSubfolders(info.ImapFolderPath, directory, skipStatus: true);
        List<string>? containers = recurse ? new() : null;
        foreach (var fi in subfolders)
        {
            if (Stopping) return;
            WriteItemObject(fi, fi.Path, true);
            if (recurse) containers!.Add(fi.Path);
        }

        if (info.Type == PathType.Folder)
        {
            // Inject the Threads/ virtual subfolder at the top of the message list.
            var threadsPath = EnsureDrivePrefix(MakePath(path, ImapPathInfo.ThreadsSegment));
            var threadsFolder = new MailFolderInfo
            {
                Path = threadsPath,
                Name = ImapPathInfo.ThreadsSegment,
                FullName = ImapPathInfo.ThreadsSegment,
                Attributes = "Virtual",
                SubfolderCount = 1,
                Directory = directory,
            };
            WriteItemObject(threadsFolder, threadsPath, true);
            if (recurse) containers!.Add(threadsPath);
        }

        if (info.Type != PathType.Root)
        {
            var search = paging?.Search;
            var messages = string.IsNullOrEmpty(search)
                ? GetCachedMessages(info.ImapFolderPath, directory, first)
                : SearchMessages(info.ImapFolderPath, directory, search, first);
            foreach (var msg in messages)
            {
                if (Stopping) return;
                WriteItemObject(msg, msg.Path, false);
            }
        }

        if (containers != null)
            foreach (var p in containers)
            {
                if (Stopping) return;
                GetChildItems(p, true);
            }
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

        // Use cache if available; otherwise just list folder names (skip STATUS for speed)
        var cached = Drive.GetCachedFolders(norm);
        if (cached != null)
        {
            foreach (var fi in cached)
            {
                if (Stopping) return;
                WriteItemObject(fi.Name, MakePath(path, fi.Name), true);
            }
        }
        else
        {
            try
            {
                IMailFolder parent = string.IsNullOrEmpty(norm)
                    ? Drive.Client.GetFolder(Drive.Client.PersonalNamespaces[0])
                    : Drive.GetImapFolder(norm);
                foreach (var sub in parent.GetSubfolders(false))
                {
                    if (Stopping) return;
                    WriteItemObject(sub.Name, MakePath(path, sub.Name), true);
                }
            }
            catch (Exception ex)
            {
                WriteWarning($"GetSubfolders failed for '{norm}': {ex.Message}");
            }
        }

        if (info.Type != PathType.Root)
        {
            var messages = Drive.GetCachedMessages(norm);
            if (messages != null)
            {
                foreach (var msg in messages)
                {
                    if (Stopping) return;
                    WriteItemObject(msg.FileName, MakePath(path, msg.FileName), false);
                }
            }
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

            // Default: move to Trash. -Force: permanent delete (expunge).
            if (!Force)
            {
                var trash = Drive.GetTrashFolder();
                if (trash != null)
                {
                    // Check if already in Trash
                    var trashPath = trash.FullName.Replace(Drive.DirectorySeparator, '/');
                    if (!info.ImapFolderPath.Equals(trashPath, StringComparison.OrdinalIgnoreCase))
                    {
                        if (!ShouldProcess($"UID {uid} in {info.ImapFolderPath}", "Move to Trash")) return;

                        var folder = Drive.GetImapFolder(info.ImapFolderPath);
                        OpenFolderIfNeeded(folder, FolderAccess.ReadWrite);
                        try
                        {
                            folder.MoveTo(new UniqueId(uid.Value), trash);
                        }
                        catch (Exception ex)
                        {
                            WriteError(new ErrorRecord(ex, "RemoveItemError", ErrorCategory.WriteError, path));
                            return;
                        }
                        finally { TryClose(folder); }

                        Drive.InvalidateMessages(info.ImapFolderPath);
                        Drive.InvalidateMessages(trashPath);
                        return;
                    }
                    // Already in Trash — fall through to permanent delete
                }
                else
                {
                    WriteWarning("Trash folder not found. Message will be permanently deleted.");
                }
            }

            if (!ShouldProcess($"UID {uid} in {info.ImapFolderPath}", "Delete message permanently")) return;

            var srcFolder = Drive.GetImapFolder(info.ImapFolderPath);
            OpenFolderIfNeeded(srcFolder, FolderAccess.ReadWrite);
            try
            {
                srcFolder.AddFlags(new UniqueId(uid.Value), MessageFlags.Deleted, true);
                srcFolder.Expunge();
            }
            catch (Exception ex)
            {
                WriteError(new ErrorRecord(ex, "RemoveItemError", ErrorCategory.WriteError, path));
                return;
            }
            finally { TryClose(srcFolder); }
            Drive.InvalidateMessages(info.ImapFolderPath);
        }
        else if (info.Type == PathType.Folder)
        {
            if (!ShouldProcess(info.ImapFolderPath, "Delete IMAP folder")) return;
            try
            {
                Drive.GetImapFolder(info.ImapFolderPath).Delete();
            }
            catch (Exception ex)
            {
                WriteError(new ErrorRecord(ex, "RemoveItemError", ErrorCategory.WriteError, path));
                return;
            }
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

        if (!ShouldProcess(info.ImapFolderPath, "Create IMAP folder")) return;

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

        IMailFolder created;
        try
        {
            created = parent.Create(folderName, true);
        }
        catch (Exception ex)
        {
            WriteError(new ErrorRecord(ex, "NewItemError", ErrorCategory.WriteError, path));
            return;
        }
        var parentDir = lastSlash < 0 ? "" : info.ImapFolderPath[..lastSlash];
        var fi = BuildFolderInfo(created, path);
        fi.Directory = EnsureDrivePrefix(parentDir);
        WriteItemObject(fi, path, true);
        Drive.InvalidateFolders(parentDir);
    }

    // ── RenameItem ───────────────────────────────────────────────

    protected override void RenameItem(string path, string newName)
    {
        var norm = NormalizePath(path);
        var info = ImapPathInfo.Parse(norm);
        if (info.Type != PathType.Folder) return;

        if (!ShouldProcess(info.ImapFolderPath, $"Rename to '{newName}'")) return;

        var folder = Drive.GetImapFolder(info.ImapFolderPath);
        var parent = folder.ParentFolder;
        folder.Rename(parent, newName);

        // Invalidate parent's folder cache
        var parentPath = parent.FullName.Replace(Drive.DirectorySeparator, '/');
        if (parentPath == Drive.Client.GetFolder(Drive.Client.PersonalNamespaces[0]).FullName)
            parentPath = "";
        Drive.InvalidateFolders(parentPath);
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
        catch (Exception ex)
        {
            WriteError(new ErrorRecord(ex, "MoveItemError", ErrorCategory.WriteError, path));
            return;
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
        catch (Exception ex)
        {
            WriteError(new ErrorRecord(ex, "CopyItemError", ErrorCategory.WriteError, path));
            return;
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
    {
        var norm = NormalizePath(path);
        var info = ImapPathInfo.Parse(norm);
        if (info.Type != PathType.Message)
            throw new PSNotSupportedException("Set-Content is only supported for draft messages.");

        var uid = info.GetMessageUid()
            ?? throw new PSNotSupportedException("Cannot determine message UID.");

        // Only allow writing in Drafts folder
        var folder = Drive.GetImapFolder(info.ImapFolderPath);
        if (!folder.Attributes.HasFlag(FolderAttributes.Drafts))
        {
            bool isDrafts = false;
            try
            {
                var drafts = Drive.Client.GetFolder(SpecialFolder.Drafts);
                isDrafts = drafts != null && drafts.FullName == folder.FullName;
            }
            catch { }
            if (!isDrafts)
            {
                var name = folder.Name.ToLowerInvariant();
                isDrafts = name is "drafts" or "[gmail]/drafts";
            }
            if (!isDrafts)
                throw new PSNotSupportedException("Set-Content is only supported for messages in the Drafts folder.");
        }

        return new MailContentWriter(Drive, folder, new UniqueId(uid), info.ImapFolderPath);
    }
    public object? GetContentWriterDynamicParameters(string path) => null;
    public void ClearContent(string path)
        => throw new PSNotSupportedException("Clearing content is not supported.");
    public object? ClearContentDynamicParameters(string path) => null;

    // ── IPropertyCmdletProvider ─────────────────────────────────

    public void GetProperty(string path, Collection<string>? providerSpecificPickList)
    {
        var norm = NormalizePath(path);
        var info = ImapPathInfo.Parse(norm);

        // Folder properties
        if (info.Type == PathType.Folder)
        {
            var folder = Drive.TryGetImapFolder(info.ImapFolderPath);
            if (folder == null) return;
            var folderPso = new PSObject();
            var folderPick = providerSpecificPickList?.Count > 0 ? providerSpecificPickList : null;
            if (folderPick == null || folderPick.Contains("IsSubscribed"))
                folderPso.Properties.Add(new PSNoteProperty("IsSubscribed", folder.IsSubscribed));
            WritePropertyObject(folderPso, path);
            return;
        }

        if (info.Type != PathType.Message) return;

        var msg = FetchMessage(info);
        if (msg == null) return;

        var pso = new PSObject();
        var pick = providerSpecificPickList?.Count > 0 ? providerSpecificPickList : null;
        if (pick == null || pick.Contains("IsRead"))
            pso.Properties.Add(new PSNoteProperty("IsRead", msg.IsRead));
        if (pick == null || pick.Contains("IsFlagged"))
            pso.Properties.Add(new PSNoteProperty("IsFlagged", msg.IsFlagged));
        if (pick == null || pick.Contains("IsAnswered"))
            pso.Properties.Add(new PSNoteProperty("IsAnswered", msg.IsAnswered));
        if (pick == null || pick.Contains("IsDraft"))
            pso.Properties.Add(new PSNoteProperty("IsDraft", msg.IsDraft));
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

        // Folder: IsSubscribed
        if (info.Type == PathType.Folder)
        {
            var subProp = propertyValue.Properties["IsSubscribed"];
            if (subProp == null) return;
            bool subscribe = LanguagePrimitives.ConvertTo<bool>(subProp.Value);
            if (!ShouldProcess(info.ImapFolderPath, subscribe ? "Subscribe" : "Unsubscribe")) return;

            var subFolder = Drive.GetImapFolder(info.ImapFolderPath);
            if (subscribe) subFolder.Subscribe();
            else subFolder.Unsubscribe();
            return;
        }

        if (info.Type != PathType.Message) return;

        var uid = info.GetMessageUid();
        if (uid == null) return;

        var flagMap = new (string Name, MessageFlags Flag)[]
        {
            ("IsRead", MessageFlags.Seen),
            ("IsFlagged", MessageFlags.Flagged),
            ("IsAnswered", MessageFlags.Answered),
            ("IsDraft", MessageFlags.Draft),
        };

        var folder = Drive.GetImapFolder(info.ImapFolderPath);
        bool changed = false;

        foreach (var (name, flag) in flagMap)
        {
            var prop = propertyValue.Properties[name];
            if (prop == null) continue;

            bool value = LanguagePrimitives.ConvertTo<bool>(prop.Value);
            if (!ShouldProcess($"UID {uid}", value ? $"Set {name}" : $"Clear {name}")) continue;

            if (!changed)
            {
                OpenFolderIfNeeded(folder, FolderAccess.ReadWrite);
                changed = true;
            }

            if (value)
                folder.AddFlags(new UniqueId(uid.Value), flag, true);
            else
                folder.RemoveFlags(new UniqueId(uid.Value), flag, true);
        }

        if (changed)
        {
            TryClose(folder);
            Drive.InvalidateMessages(info.ImapFolderPath);
        }
    }

    public object? SetPropertyDynamicParameters(string path, PSObject propertyValue) => null;

    public void ClearProperty(string path, Collection<string> propertyToClear)
    {
        var norm = NormalizePath(path);
        var info = ImapPathInfo.Parse(norm);
        if (info.Type != PathType.Message) return;

        var uid = info.GetMessageUid();
        if (uid == null) return;

        var flagMap = new Dictionary<string, MessageFlags>(StringComparer.OrdinalIgnoreCase)
        {
            ["IsRead"] = MessageFlags.Seen,
            ["IsFlagged"] = MessageFlags.Flagged,
            ["IsAnswered"] = MessageFlags.Answered,
            ["IsDraft"] = MessageFlags.Draft,
        };

        MessageFlags toClear = MessageFlags.None;
        foreach (var prop in propertyToClear)
        {
            if (flagMap.TryGetValue(prop, out var flag))
                toClear |= flag;
        }

        if (toClear == MessageFlags.None) return;
        if (!ShouldProcess($"UID {uid}", $"Clear flags: {toClear}")) return;

        var folder = Drive.GetImapFolder(info.ImapFolderPath);
        OpenFolderIfNeeded(folder, FolderAccess.ReadWrite);
        try
        {
            folder.RemoveFlags(new UniqueId(uid.Value), toClear, true);
        }
        finally { TryClose(folder); }

        Drive.InvalidateMessages(info.ImapFolderPath);
    }

    public object? ClearPropertyDynamicParameters(string path, Collection<string> propertyToClear) => null;

    // ── Cache helpers ───────────────────────────────────────────

    private List<MailFolderInfo> GetCachedSubfolders(string normalizedPath, string directory, bool skipStatus = false)
    {
        var cached = Drive.GetCachedFolders(normalizedPath);
        if (cached != null) return cached;

        IMailFolder parent = string.IsNullOrEmpty(normalizedPath)
            ? Drive.Client.GetFolder(Drive.Client.PersonalNamespaces[0])
            : Drive.GetImapFolder(normalizedPath);

        var result = new List<MailFolderInfo>();
        for (int attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                if (attempt > 0)
                {
                    Drive.Reconnect();
                    parent = string.IsNullOrEmpty(normalizedPath)
                        ? Drive.Client.GetFolder(Drive.Client.PersonalNamespaces[0])
                        : Drive.GetImapFolder(normalizedPath);
                }
                foreach (var sub in parent.GetSubfolders(false))
                {
                    if (Stopping) return result;
                    var subNorm = string.IsNullOrEmpty(normalizedPath)
                        ? sub.Name : $"{normalizedPath}/{sub.Name}";
                    var fi = BuildFolderInfo(sub, EnsureDrivePrefix(subNorm), skipStatus);
                    fi.Directory = directory;
                    result.Add(fi);
                }
                Drive.SetCachedFolders(normalizedPath, result);
                break;
            }
            catch when (attempt == 0) { result.Clear(); }
            catch (Exception ex)
            {
                WriteWarning($"GetSubfolders failed for '{normalizedPath}': {ex.Message}");
            }
        }

        return result;
    }

    private List<MailMessageInfo> GetCachedMessages(string normalizedPath, string directory, int first)
    {
        var cached = Drive.GetCachedMessages(normalizedPath);
        // Cache hit: honor `first` by slicing. If the request exceeds what's cached,
        // fall through and refetch — otherwise -First N would forever be capped to
        // whatever the first call happened to fetch.
        if (cached != null && cached.Count >= first)
            return cached.Take(first).ToList();

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
                var summaries = folder.Fetch(start, -1, ListSummaryItems());

                var parentPath = EnsureDrivePrefix(normalizedPath);
                foreach (var summary in summaries.Reverse())
                {
                    if (Stopping) return result;
                    var msg = BuildMessageInfo(summary, parentPath);
                    msg.Directory = directory;
                    result.Add(msg);
                }
            }
            Drive.SetCachedMessages(normalizedPath, result);
        }
        catch (Exception ex)
        {
            WriteWarning($"Fetch messages failed for '{normalizedPath}': {ex.Message}");
        }
        finally { TryClose(folder); }

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
            // Gmail: use X-GM-RAW for native Gmail search syntax
            var query = Drive.IsGMail
                ? SearchQuery.GMailRawSearch(search)
                : ParseSearch(search);
            var uids = folder.Search(query);
            var targetUids = uids.Reverse().Take(first).ToList();
            if (targetUids.Count == 0) return [];

            var summaries = folder.Fetch(targetUids, ListSummaryItems());

            var parentPath = EnsureDrivePrefix(normalizedPath);
            var result = new List<MailMessageInfo>();
            foreach (var s in summaries)
            {
                if (Stopping) return [];
                var msg = BuildMessageInfo(s, parentPath);
                msg.Directory = directory;
                result.Add(msg);
            }
            return result;
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
                    "answered" => SearchQuery.Answered,
                    "draft" => SearchQuery.Draft,
                    "has:attachment" => SearchQuery.HeaderContains("Content-Type", "multipart/mixed"),
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

    // ── Threads (virtual folder over messages grouped by ThreadId) ───────

    private List<MailThreadInfo> BuildThreads(string imapFolderPath, string directory, int first)
    {
        // Source from cached message listing — Gmail X-GM-THRID is included in
        // the standard fetch when IsGmail. For non-Gmail without thread support
        // ThreadId will be null and each message becomes its own singleton thread.
        var msgs = GetCachedMessages(imapFolderPath, directory, first);
        var folderDirPath = EnsureDrivePrefix(imapFolderPath);
        var threadsBase = MakePath(folderDirPath, ImapPathInfo.ThreadsSegment);

        // Group by ThreadId, fall back to MessageId for messages without ThreadId.
        var groups = msgs.GroupBy(m => m.ThreadId ?? FallbackThreadKey(m));
        var result = new List<MailThreadInfo>();
        foreach (var g in groups)
        {
            ulong threadId = g.Key;
            var members = g.ToList();
            var latest = members.MaxBy(m => m.Date) ?? members[0];
            var seg = ImapPathInfo.BuildThreadSegment(threadId, latest.Subject);
            var path = EnsureDrivePrefix(MakePath(threadsBase, seg));
            var participants = string.Join(", ", members
                .SelectMany(m => new[] { m.From }).Where(s => !string.IsNullOrEmpty(s))
                .Distinct().Take(3));
            result.Add(new MailThreadInfo
            {
                Path = path,
                Directory = threadsBase,
                ThreadId = threadId,
                Name = seg,
                Subject = latest.Subject,
                Participants = participants,
                MessageCount = members.Count,
                LatestDate = latest.Date,
                HasUnread = members.Any(m => !m.IsRead),
                HasAttachments = members.Any(m => m.HasAttachments),
                HasFlagged = members.Any(m => m.IsFlagged),
            });
        }
        return result.OrderByDescending(t => t.LatestDate).ToList();
    }

    private List<MailMessageInfo> GetThreadMessages(ImapPathInfo info, string directory, int first)
    {
        var folderDirPath = EnsureDrivePrefix(info.ImapFolderPath);
        var threadDirPath = EnsureDrivePrefix(
            MakePath(MakePath(folderDirPath, ImapPathInfo.ThreadsSegment), info.ThreadSegment ?? ""));
        var msgs = GetCachedMessages(info.ImapFolderPath, directory, first);
        var members = msgs.Where(m => MatchesThread(m, info.ThreadId)).ToList();
        // Rebase paths so cat/mv/del work via the thread-qualified path.
        foreach (var m in members)
        {
            m.Directory = threadDirPath;
            m.Path = EnsureDrivePrefix(MakePath(threadDirPath, m.FileName));
        }
        return members.OrderBy(m => m.Date).ToList();
    }

    private static bool MatchesThread(MailMessageInfo m, ulong? threadId)
    {
        if (threadId == null) return false;
        if (m.ThreadId.HasValue) return m.ThreadId.Value == threadId.Value;
        return FallbackThreadKey(m) == threadId.Value;
    }

    private static ulong FallbackThreadKey(MailMessageInfo m)
    {
        // Stable hash from MessageId so non-Gmail singletons get a unique key.
        var key = m.MessageId ?? m.Uid.ToString();
        unchecked
        {
            ulong h = 14695981039346656037UL;
            foreach (var c in key)
            {
                h ^= c;
                h *= 1099511628211UL;
            }
            return h;
        }
    }

    private MailFolderInfo BuildFolderInfo(IMailFolder folder, string path, bool skipStatus = false)
    {
        var status = skipStatus ? (count: 0, unread: 0) : GetFolderStatus(folder);
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
        // Use LIST attributes from the parent's GetSubfolders response — no extra round trip.
        // 1 = "has at least one subfolder" (exact count would require N+1 LIST commands).
        return folder.Attributes.HasFlag(FolderAttributes.HasChildren) ? 1 : 0;
    }

    // Standard summary items for listing — BodyStructure for HasAttachments,
    // GMailThreadId conditionally for thread grouping. References enables
    // client-side conversation threading on non-Gmail servers.
    private MessageSummaryItems ListSummaryItems()
    {
        var items = MessageSummaryItems.UniqueId | MessageSummaryItems.Envelope
                  | MessageSummaryItems.Flags | MessageSummaryItems.Size
                  | MessageSummaryItems.BodyStructure | MessageSummaryItems.References;
        if (Drive.IsGMail)
            items |= MessageSummaryItems.GMailThreadId;
        return items;
    }

    private MailMessageInfo BuildMessageInfo(IMessageSummary summary, string parentPath)
    {
        var envelope = summary.Envelope;
        var fileName = BuildMessageFileName(summary);
        var msg = new MailMessageInfo
        {
            Path = EnsureDrivePrefix(MakePath(parentPath, fileName)),
            Uid = summary.UniqueId.Id,
            FileName = fileName,
            Subject = envelope.Subject ?? "(no subject)",
            From = MailHelpers.FormatAddress(envelope.From),
            To = MailHelpers.FormatAddress(envelope.To),
            Date = envelope.Date?.LocalDateTime ?? DateTime.MinValue,
            IsRead = summary.Flags?.HasFlag(MessageFlags.Seen) ?? false,
            IsFlagged = summary.Flags?.HasFlag(MessageFlags.Flagged) ?? false,
            IsAnswered = summary.Flags?.HasFlag(MessageFlags.Answered) ?? false,
            IsDraft = summary.Flags?.HasFlag(MessageFlags.Draft) ?? false,
            HasAttachments = summary.Attachments?.Any() ?? false,
            Size = (int)(summary.Size ?? 0),
            MessageId = envelope.MessageId,
            InReplyTo = envelope.InReplyTo,
            References = summary.References?.Count > 0 ? summary.References.ToArray() : null,
            ThreadId = summary.GMailThreadId,
        };
        return msg;
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
