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

    protected MailDriveInfoBase(PSDriveInfo driveInfo, string username, string password,
        string? smtpHost, int smtpPort, SecureSocketOptions smtpSsl, string displayRoot)
        : base(driveInfo.Name, driveInfo.Provider, driveInfo.Root,
               driveInfo.Description, driveInfo.Credential, displayRoot)
    {
        MailUsername = username;
        MailPassword = password;
        SmtpHost = smtpHost;
        SmtpPort = smtpPort;
        SmtpSsl = smtpSsl;
    }

    public abstract void Dispose();
}
