using System.Collections.ObjectModel;
using System.Management.Automation;
using System.Management.Automation.Provider;
using MailDrive.Models;
using MimeKit;

namespace MailDrive.Provider;

[CmdletProvider("Pop", ProviderCapabilities.ShouldProcess)]
[OutputType(typeof(MailMessageInfo), ProviderCmdlet = ProviderCmdlet.GetChildItem)]
public class PopDriveProvider : NavigationCmdletProvider, IContentCmdletProvider
{
    private PopDriveInfo Drive => (PopDriveInfo)PSDriveInfo;

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

    private static bool IsMessagePath(string normalizedPath)
        => !string.IsNullOrEmpty(normalizedPath);

    private static int? ExtractMessageIndex(string normalizedPath)
    {
        int underscore = normalizedPath.IndexOf('_');
        if (underscore > 0 && int.TryParse(normalizedPath[..underscore], out int idx))
            return idx - 1; // 1-based display → 0-based MailKit index
        return null;
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
            // Imap provider already recorded timestamp; just load
            var config = MailConfig.Load(MailConfig.DefaultPath);
            if (config?.PSDrives == null) return drives;

            foreach (var s in config.PSDrives)
            {
                s.CascadeFrom(config);
                if (!string.Equals(s.Protocol, "POP3", StringComparison.OrdinalIgnoreCase)) continue;
                if (string.IsNullOrEmpty(s.Name) || string.IsNullOrEmpty(s.Host)) continue;

                var ssl = MailConfig.ParseSsl(s.Ssl);
                var port = s.Port ?? 995;
                var desc = string.IsNullOrEmpty(s.Description)
                    ? $"{s.Username}@{s.Host}:{port}" : s.Description;

                var dp = new PSDriveInfo(s.Name, ProviderInfo, s.Name + @":\", desc, null);
                drives.Add(new PopDriveInfo(dp, s.Host, port, ssl,
                    s.Username ?? "", s.Password ?? "",
                    s.SmtpHost, s.SmtpPort ?? 587, MailConfig.ParseSsl(s.SmtpSsl),
                    s.IsOAuth2, s.TenantId, s.ClientId));
            }
        }
        catch { }
        return drives;
    }

    protected override PSDriveInfo NewDrive(PSDriveInfo drive)
    {
        if (drive is PopDriveInfo) return drive;
        ThrowTerminatingError(new ErrorRecord(
            new ArgumentException("Use New-PopDrive to create a POP3 drive."),
            "InvalidDrive", ErrorCategory.InvalidArgument, drive));
        return drive;
    }

    protected override PSDriveInfo RemoveDrive(PSDriveInfo drive)
    {
        if (drive is PopDriveInfo pop) pop.Dispose();
        return drive;
    }

    // ── Item existence ──────────────────────────────────────────

    protected override bool ItemExists(string path)
    {
        var norm = NormalizePath(path);
        if (!IsMessagePath(norm)) return true;
        var idx = ExtractMessageIndex(norm);
        if (idx == null) return false;
        try { return idx >= 0 && idx < Drive.Client.Count; }
        catch { return false; }
    }

    protected override bool IsItemContainer(string path)
        => !IsMessagePath(NormalizePath(path));

    protected override bool HasChildItems(string path)
        => !IsMessagePath(NormalizePath(path));

    // ── GetItem ─────────────────────────────────────────────────

    protected override void GetItem(string path)
    {
        var norm = NormalizePath(path);
        if (!IsMessagePath(norm))
        {
            WriteItemObject(new { Root = "\\", Provider = "POP3" }, path, true);
            return;
        }

        var idx = ExtractMessageIndex(norm);
        if (idx == null || idx < 0 || idx >= Drive.Client.Count) return;

        // Full fetch for single message — accurate HasAttachments
        var message = Drive.Client.GetMessage(idx.Value);
        var size = Drive.Client.GetMessageSize(idx.Value);
        var msg = BuildMessageInfoFromMime(message, size, idx.Value, EnsureDrivePrefix(""));
        WriteItemObject(msg, path, false);
    }

    // ── GetChildItems ───────────────────────────────────────────

    protected override void GetChildItems(string path, bool recurse)
    {
        var norm = NormalizePath(path);
        if (IsMessagePath(norm)) return;

        var paging = DynamicParameters as MailPaginationParameters;
        int first = paging?.First ?? 50;
        var directory = EnsureDrivePrefix(path);

        var messages = GetCachedMessages(directory, first);
        foreach (var msg in messages)
        {
            if (Stopping) return;
            WriteItemObject(msg, msg.Path, false);
        }
    }

    protected override object? GetChildItemsDynamicParameters(string path, bool recurse)
        => new MailPaginationParameters();

    // ── GetChildNames ───────────────────────────────────────────

    protected override void GetChildNames(string path, ReturnContainers returnContainers)
    {
        var norm = NormalizePath(path);
        if (IsMessagePath(norm)) return;

        var directory = EnsureDrivePrefix(path);
        var messages = GetCachedMessages(directory, 50);
        foreach (var msg in messages)
        {
            if (Stopping) return;
            WriteItemObject(msg.FileName, MakePath(path, msg.FileName), false);
        }
    }

    // ── RemoveItem ──────────────────────────────────────────────

    protected override void RemoveItem(string path, bool recurse)
    {
        var norm = NormalizePath(path);
        if (!IsMessagePath(norm)) return;

        var idx = ExtractMessageIndex(norm);
        if (idx == null) return;
        if (!ShouldProcess($"Message {idx.Value + 1}", "Delete message")) return;

        try
        {
            Drive.Client.DeleteMessage(idx.Value);
        }
        catch (Exception ex)
        {
            WriteError(new ErrorRecord(ex, "RemoveItemError", ErrorCategory.WriteError, path));
            return;
        }
        Drive.InvalidateMessages();
        Drive.Reconnect();
    }

    // ── InvokeDefaultAction ─────────────────────────────────────

    protected override void InvokeDefaultAction(string path)
    {
        var norm = NormalizePath(path);
        if (!IsMessagePath(norm)) return;

        var idx = ExtractMessageIndex(norm);
        if (idx == null) return;

        var message = Drive.Client.GetMessage(idx.Value);
        var tempFile = Path.Combine(Path.GetTempPath(),
            $"MailDrive_{idx.Value + 1}_{MailHelpers.SanitizeFileName(message.Subject)}.eml");
        message.WriteTo(tempFile);
        System.Diagnostics.Process.Start(
            new System.Diagnostics.ProcessStartInfo(tempFile) { UseShellExecute = true });
    }

    // ── IContentCmdletProvider ──────────────────────────────────

    public IContentReader GetContentReader(string path)
    {
        var norm = NormalizePath(path);
        if (!IsMessagePath(norm))
            throw new PSNotSupportedException("Get-Content is only supported for messages.");
        var idx = ExtractMessageIndex(norm)
            ?? throw new PSNotSupportedException("Cannot determine message index.");

        var message = Drive.Client.GetMessage(idx);
        return new MailContentReader(MailHelpers.FormatMessage(message));
    }

    public object? GetContentReaderDynamicParameters(string path) => null;
    public IContentWriter GetContentWriter(string path)
        => throw new PSNotSupportedException("Writing content is not supported.");
    public object? GetContentWriterDynamicParameters(string path) => null;
    public void ClearContent(string path)
        => throw new PSNotSupportedException("Clearing content is not supported.");
    public object? ClearContentDynamicParameters(string path) => null;

    // ── Cache helper ────────────────────────────────────────────

    private List<MailMessageInfo> GetCachedMessages(string directory, int first)
    {
        var cached = Drive.GetCachedMessages();
        if (cached != null) return cached;

        var client = Drive.Client;
        int count = client.Count;
        if (count == 0) return [];

        var result = new List<MailMessageInfo>();
        try
        {
            var sizes = client.GetMessageSizes();
            int start = Math.Max(0, count - first);

            for (int i = count - 1; i >= start; i--)
            {
                if (Stopping) return result;
                try
                {
                    var headers = client.GetMessageHeaders(i);
                    result.Add(BuildMessageInfo(headers, sizes[i], i, directory));
                }
                catch (Exception ex)
                {
                    WriteVerbose($"Failed to fetch headers for message {i}: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            WriteVerbose($"Failed to list messages: {ex.Message}");
        }

        Drive.SetCachedMessages(result);
        return result;
    }

    private MailMessageInfo BuildMessageInfo(HeaderList headers, int size, int index, string directory)
    {
        var subjectStr = headers[HeaderId.Subject] ?? "(no subject)";

        InternetAddressList? fromList = null;
        if (headers[HeaderId.From] is string fromStr)
            InternetAddressList.TryParse(fromStr, out fromList);

        InternetAddressList? toList = null;
        if (headers[HeaderId.To] is string toStr)
            InternetAddressList.TryParse(toStr, out toList);

        DateTimeOffset date = DateTimeOffset.MinValue;
        if (headers[HeaderId.Date] is string dateStr)
            MimeKit.Utils.DateUtils.TryParse(dateStr, out date);

        var fromMailbox = fromList?.Mailboxes.FirstOrDefault();
        var fromName = fromMailbox?.Name ?? fromMailbox?.Address ?? "unknown";
        var displayIndex = (uint)(index + 1);
        var fileName = $"{displayIndex}_{MailHelpers.SanitizeFileName(fromName)}_{MailHelpers.SanitizeFileName(subjectStr)}.eml";

        // Detect attachments from Content-Type header
        var hasAttachments = false;
        var contentType = headers[HeaderId.ContentType];
        if (contentType != null)
        {
            // multipart/mixed typically indicates attachments
            hasAttachments = contentType.Contains("multipart/mixed", StringComparison.OrdinalIgnoreCase);
        }

        return new MailMessageInfo
        {
            Path = EnsureDrivePrefix(fileName),
            Directory = directory,
            Uid = displayIndex,
            FileName = fileName,
            Subject = subjectStr,
            From = MailHelpers.FormatAddress(fromList),
            To = MailHelpers.FormatAddress(toList),
            Date = date.LocalDateTime,
            IsRead = false,
            HasAttachments = hasAttachments,
            Size = size,
        };
    }

    private MailMessageInfo BuildMessageInfoFromMime(MimeMessage message, int size, int index, string directory)
    {
        var subjectStr = message.Subject ?? "(no subject)";
        var fromMailbox = message.From.Mailboxes.FirstOrDefault();
        var fromName = fromMailbox?.Name ?? fromMailbox?.Address ?? "unknown";
        var displayIndex = (uint)(index + 1);
        var fileName = $"{displayIndex}_{MailHelpers.SanitizeFileName(fromName)}_{MailHelpers.SanitizeFileName(subjectStr)}.eml";

        return new MailMessageInfo
        {
            Path = EnsureDrivePrefix(fileName),
            Directory = directory,
            Uid = displayIndex,
            FileName = fileName,
            Subject = subjectStr,
            From = MailHelpers.FormatAddress(message.From),
            To = MailHelpers.FormatAddress(message.To),
            Date = message.Date.LocalDateTime,
            IsRead = false,
            HasAttachments = message.Attachments.Any(),
            Size = size,
        };
    }
}
