using System.Management.Automation;
using MailDrive.Models;
using MailDrive.Provider;
using MailKit.Security;

namespace MailDrive.Cmdlets;

[Cmdlet(VerbsCommon.New, "PopDrive")]
public class NewPopDriveCmdlet : PSCmdlet
{
    [Parameter(Position = 0)]
    [ValidateNotNullOrEmpty]
    public string Name { get; set; } = "Pop";

    [Parameter(Mandatory = true, Position = 1)]
    [ValidateNotNullOrEmpty]
    public new string Host { get; set; } = "";

    [Parameter]
    public int Port { get; set; } = 995;

    [Parameter]
    [ValidateSet("Auto", "SslOnConnect", "StartTls", "None")]
    public string Ssl { get; set; } = "Auto";

    [Parameter(Mandatory = true)]
    [ValidateNotNullOrEmpty]
    public string Username { get; set; } = "";

    [Parameter(Mandatory = true)]
    [ValidateNotNullOrEmpty]
    public string Password { get; set; } = "";

    [Parameter]
    public string Description { get; set; } = "";

    [Parameter]
    public string? SmtpHost { get; set; }

    [Parameter]
    public int SmtpPort { get; set; } = 587;

    [Parameter]
    [ValidateSet("Auto", "SslOnConnect", "StartTls", "None")]
    public string SmtpSsl { get; set; } = "Auto";

    protected override void ProcessRecord()
    {
        var provider = SessionState.Provider.GetOne("Pop");
        var sslOption = MailConfig.ParseSsl(Ssl);
        var smtpSslOption = MailConfig.ParseSsl(SmtpSsl);

        var desc = string.IsNullOrEmpty(Description)
            ? $"{Username}@{Host}:{Port}" : Description;

        var driveParams = new PSDriveInfo(Name, provider, Name + @":\", desc, null);
        var driveInfo = new PopDriveInfo(driveParams, Host, Port, sslOption,
            Username, Password, SmtpHost, SmtpPort, smtpSslOption);

        try
        {
            _ = driveInfo.Client;
            WriteVerbose($"Connected to {Host}:{Port} as {Username}");
        }
        catch (Exception ex)
        {
            driveInfo.Dispose();
            ThrowTerminatingError(new ErrorRecord(ex, "PopConnectionFailed",
                ErrorCategory.ConnectionError, Host));
            return;
        }

        SessionState.Drive.New(driveInfo, "global");
        WriteObject(driveInfo);
    }
}
