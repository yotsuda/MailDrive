using System.Management.Automation;
using MailDrive.Models;
using MailKit.Net.Pop3;
using MailKit.Security;

namespace MailDrive.Provider;

public class PopDriveInfo : MailDriveInfoBase
{
    private Pop3Client? _client;
    private readonly object _clientLock = new();
    private readonly string _host;
    private readonly int _port;
    private readonly SecureSocketOptions _ssl;

    private List<MailMessageInfo>? _messageCache;
    private DateTime _messageCacheExpiry;
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(30);

    public PopDriveInfo(PSDriveInfo driveInfo, string host, int port, SecureSocketOptions ssl,
        string username, string password,
        string? smtpHost = null, int smtpPort = 587, SecureSocketOptions smtpSsl = SecureSocketOptions.Auto,
        bool useOAuth2 = false, string? tenantId = null, string? clientId = null,
        bool useDeviceCode = false)
        : base(driveInfo, username, password, smtpHost, smtpPort, smtpSsl, $"pop3s://{host}:{port}",
               useOAuth2, tenantId, clientId, useDeviceCode)
    {
        _host = host;
        _port = port;
        _ssl = ssl;
    }

    public override string Protocol => "POP3";
    public override string Host => _host;
    public override int Port => _port;
    public override SecureSocketOptions Ssl => _ssl;
    public override bool IsConnected => _client?.IsConnected ?? false;
    public override bool IsAuthenticated => _client?.IsAuthenticated ?? false;

    public Pop3Client Client
    {
        get
        {
            if (_client == null || !_client.IsConnected)
            {
                lock (_clientLock)
                {
                    if (_client == null || !_client.IsConnected)
                    {
                        _client?.Dispose();
                        _client = new Pop3Client();
                        _client.Connect(_host, _port, _ssl);
                        if (UseOAuth2)
                        {
                            var token = OAuth2Helper.AcquireToken(MailUsername, TenantId, ClientId, UseDeviceCode);
                            _client.Authenticate(new MailKit.Security.SaslMechanismOAuth2(MailUsername, token));
                        }
                        else
                        {
                            _client.Authenticate(MailUsername, MailPassword);
                        }
                    }
                }
            }
            return _client;
        }
    }

    internal void Reconnect()
    {
        lock (_clientLock)
        {
            if (_client != null)
            {
                if (_client.IsConnected)
                    try { _client.Disconnect(true); } catch { }
                _client.Dispose();
                _client = null;
            }
        }
        _ = Client;
    }

    public List<MailMessageInfo>? GetCachedMessages()
    {
        if (_messageCache != null && _messageCacheExpiry > DateTime.UtcNow)
            return _messageCache;
        _messageCache = null;
        return null;
    }

    public void SetCachedMessages(List<MailMessageInfo> items)
    {
        _messageCache = items;
        _messageCacheExpiry = DateTime.UtcNow + CacheTtl;
    }

    public void InvalidateMessages() => _messageCache = null;

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
