using System.Collections.Concurrent;
using System.Management.Automation;
using MailDrive.Models;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Security;

namespace MailDrive.Provider;

public class ImapDriveInfo : MailDriveInfoBase
{
    private ImapClient? _client;
    private readonly object _clientLock = new();
    private readonly string _host;
    private readonly int _port;
    private readonly SecureSocketOptions _ssl;
    private char? _directorySeparator;

    private readonly ConcurrentDictionary<string, (List<MailFolderInfo> Items, DateTime Expiry)> _folderCache = new();
    private readonly ConcurrentDictionary<string, (List<MailMessageInfo> Items, DateTime Expiry)> _messageCache = new();
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);

    public ImapDriveInfo(PSDriveInfo driveInfo, string host, int port, SecureSocketOptions ssl,
        string username, string password,
        string? smtpHost = null, int smtpPort = 587, SecureSocketOptions smtpSsl = SecureSocketOptions.Auto,
        bool useOAuth2 = false, string? tenantId = null, string? clientId = null)
        : base(driveInfo, username, password, smtpHost, smtpPort, smtpSsl, $"imaps://{host}:{port}",
               useOAuth2, tenantId, clientId)
    {
        _host = host;
        _port = port;
        _ssl = ssl;
    }

    public ImapClient Client
    {
        get
        {
            if (_client == null || !_client.IsConnected || !_client.IsAuthenticated)
            {
                Reconnect();
            }
            return _client!;
        }
    }

    public void Reconnect()
    {
        lock (_clientLock)
        {
            _client?.Dispose();
            _client = new ImapClient();
            _client.Connect(_host, _port, _ssl);
            if (UseOAuth2)
            {
                var token = OAuth2Helper.AcquireToken(MailUsername, TenantId, ClientId);
                _client.Authenticate(new MailKit.Security.SaslMechanismOAuth2(MailUsername, token));
            }
            else
            {
                _client.Authenticate(MailUsername, MailPassword);
            }
            _directorySeparator = null;
        }
    }

    public bool IsGMail => Client.Capabilities.HasFlag(
        MailKit.Net.Imap.ImapCapabilities.GMailExt1);

    public char DirectorySeparator =>
        _directorySeparator ??= Client.GetFolder(Client.PersonalNamespaces[0]).DirectorySeparator;

    public IMailFolder GetImapFolder(string normalizedPath)
    {
        if (string.IsNullOrEmpty(normalizedPath))
            return Client.GetFolder(Client.PersonalNamespaces[0]);
        var imapPath = normalizedPath.Replace('/', DirectorySeparator);
        return Client.GetFolder(imapPath);
    }

    public IMailFolder? TryGetImapFolder(string normalizedPath)
    {
        try { return GetImapFolder(normalizedPath); }
        catch { return null; }
    }

    // ── Folder cache ────────────────────────────────────────────

    public List<MailFolderInfo>? GetCachedFolders(string key)
    {
        if (_folderCache.TryGetValue(key, out var entry) && entry.Expiry > DateTime.UtcNow)
            return entry.Items;
        _folderCache.TryRemove(key, out _);
        return null;
    }

    public void SetCachedFolders(string key, List<MailFolderInfo> items)
        => _folderCache[key] = (items, DateTime.UtcNow + CacheTtl);

    public void InvalidateFolders(string key)
        => _folderCache.TryRemove(key, out _);

    // ── Message cache ───────────────────────────────────────────

    public List<MailMessageInfo>? GetCachedMessages(string key)
    {
        if (_messageCache.TryGetValue(key, out var entry) && entry.Expiry > DateTime.UtcNow)
            return entry.Items;
        _messageCache.TryRemove(key, out _);
        return null;
    }

    public void SetCachedMessages(string key, List<MailMessageInfo> items)
        => _messageCache[key] = (items, DateTime.UtcNow + CacheTtl);

    public void InvalidateMessages(string key)
        => _messageCache.TryRemove(key, out _);

    public void InvalidateAll()
    {
        _folderCache.Clear();
        _messageCache.Clear();
    }

    // ── Special folders ──────────────────────────────────────────

    public IMailFolder? GetTrashFolder()
    {
        try
        {
            // Try SpecialFolder first (XLIST / SPECIAL-USE)
            var trash = Client.GetFolder(SpecialFolder.Trash);
            if (trash != null && trash.Exists) return trash;
        }
        catch { }

        // Fallback: search by well-known names
        var root = Client.GetFolder(Client.PersonalNamespaces[0]);
        foreach (var sub in root.GetSubfolders(false))
        {
            var name = sub.Name.ToLowerInvariant();
            if (name is "trash" or "[gmail]/trash" or "deleted items" or "deleted messages")
                return sub;
            if (sub.Attributes.HasFlag(FolderAttributes.Trash))
                return sub;
        }

        return null;
    }

    // ── Dispose ─────────────────────────────────────────────────

    public override void Dispose()
    {
        if (_client != null)
        {
            if (_client.IsConnected)
                try { _client.Disconnect(true); } catch { }
            _client.Dispose();
            _client = null;
        }
    }
}
