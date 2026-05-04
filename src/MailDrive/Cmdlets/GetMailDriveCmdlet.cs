using System.Management.Automation;
using MailDrive.Provider;

namespace MailDrive.Cmdlets;

[Cmdlet(VerbsCommon.Get, "MailDrive")]
[OutputType(typeof(MailDriveInfo))]
public class GetMailDriveCmdlet : PSCmdlet
{
    [Parameter(Position = 0, ValueFromPipeline = true, ValueFromPipelineByPropertyName = true)]
    [SupportsWildcards]
    public string[]? Name { get; set; }

    protected override void ProcessRecord()
    {
        var drives = SessionState.Drive.GetAll()
            .OfType<MailDriveInfoBase>();

        if (Name != null && Name.Length > 0)
        {
            var patterns = Name.Select(n => new WildcardPattern(n, WildcardOptions.IgnoreCase)).ToArray();
            drives = drives.Where(d => patterns.Any(p => p.IsMatch(d.Name)));
        }

        foreach (var drive in drives)
        {
            WriteObject(MailDriveInfo.From(drive));
        }
    }
}

public sealed class MailDriveInfo
{
    public string Name { get; init; } = "";
    public string Protocol { get; init; } = "";
    public string Username { get; init; } = "";
    public string Host { get; init; } = "";
    public int Port { get; init; }
    public string Ssl { get; init; } = "";
    public string AuthMethod { get; init; } = "";
    public string? SmtpHost { get; init; }
    public int SmtpPort { get; init; }
    public string? SmtpSsl { get; init; }
    public bool HasSmtp { get; init; }
    public bool IsConnected { get; init; }
    public bool IsAuthenticated { get; init; }
    public string? Description { get; init; }

    public static MailDriveInfo From(MailDriveInfoBase d) => new()
    {
        Name = d.Name,
        Protocol = d.Protocol,
        Username = d.Username,
        Host = d.Host,
        Port = d.Port,
        Ssl = d.Ssl.ToString(),
        AuthMethod = d.AuthMethod,
        SmtpHost = d.SmtpHost,
        SmtpPort = d.SmtpPort,
        SmtpSsl = d.HasSmtp ? d.SmtpSsl.ToString() : null,
        HasSmtp = d.HasSmtp,
        IsConnected = d.IsConnected,
        IsAuthenticated = d.IsAuthenticated,
        Description = d.Description,
    };
}
