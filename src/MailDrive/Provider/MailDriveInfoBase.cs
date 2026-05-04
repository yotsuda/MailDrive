using System.Management.Automation;
using MailKit.Security;

namespace MailDrive.Provider;

public abstract class MailDriveInfoBase : PSDriveInfo, IDisposable
{
    internal string MailUsername { get; }
    internal string MailPassword { get; }
    public string? SmtpHost { get; }
    public int SmtpPort { get; }
    public SecureSocketOptions SmtpSsl { get; }
    public bool HasSmtp => SmtpHost != null;

    internal bool UseOAuth2 { get; }
    internal bool UseDeviceCode { get; }
    internal string? TenantId { get; }
    internal string? ClientId { get; }

    public string Username => MailUsername;
    public string AuthMethod => UseOAuth2 ? "OAuth2" : "Password";

    public abstract string Protocol { get; }
    public abstract string Host { get; }
    public abstract int Port { get; }
    public abstract SecureSocketOptions Ssl { get; }
    public abstract bool IsConnected { get; }
    public abstract bool IsAuthenticated { get; }

    protected MailDriveInfoBase(PSDriveInfo driveInfo, string username, string password,
        string? smtpHost, int smtpPort, SecureSocketOptions smtpSsl, string displayRoot,
        bool useOAuth2 = false, string? tenantId = null, string? clientId = null,
        bool useDeviceCode = false)
        : base(driveInfo.Name, driveInfo.Provider, driveInfo.Root,
               driveInfo.Description, driveInfo.Credential, displayRoot)
    {
        MailUsername = username;
        MailPassword = password;
        SmtpHost = smtpHost;
        SmtpPort = smtpPort;
        SmtpSsl = smtpSsl;
        UseOAuth2 = useOAuth2;
        UseDeviceCode = useDeviceCode;
        TenantId = tenantId;
        ClientId = clientId;
    }

    public abstract void Dispose();
}
