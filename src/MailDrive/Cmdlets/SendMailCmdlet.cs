using System.Management.Automation;
using MailDrive.Models;
using MailDrive.Provider;
using MailKit.Net.Smtp;
using MimeKit;

namespace MailDrive.Cmdlets;

[Cmdlet(VerbsCommunications.Send, "Mail", SupportsShouldProcess = true)]
public class SendMailCmdlet : PSCmdlet
{
    [Parameter(Mandatory = true, Position = 0)]
    public string[] To { get; set; } = [];

    [Parameter]
    public string[]? Cc { get; set; }

    [Parameter]
    public string[]? Bcc { get; set; }

    [Parameter(Mandatory = true, Position = 1)]
    public string Subject { get; set; } = "";

    [Parameter(Mandatory = true, Position = 2, ValueFromPipeline = true)]
    public string Body { get; set; } = "";

    [Parameter]
    public SwitchParameter Html { get; set; }

    [Parameter]
    public string[]? Attachments { get; set; }

    [Parameter]
    public string? From { get; set; }

    [Parameter(HelpMessage = "Name of the PSDrive to use for SMTP settings.")]
    [Alias("Drive")]
    public string? Path { get; set; }

    protected override void ProcessRecord()
    {
        var drive = FindSmtpDrive();
        if (drive == null)
        {
            ThrowTerminatingError(new ErrorRecord(
                new InvalidOperationException(
                    "No drive with SMTP configured. Set SmtpHost in your drive or config."),
                "NoSmtpDrive", ErrorCategory.ObjectNotFound, null));
            return;
        }

        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse(From ?? drive.MailUsername));
        foreach (var to in To) message.To.Add(MailboxAddress.Parse(to));
        if (Cc != null) foreach (var cc in Cc) message.Cc.Add(MailboxAddress.Parse(cc));
        if (Bcc != null) foreach (var bcc in Bcc) message.Bcc.Add(MailboxAddress.Parse(bcc));
        message.Subject = Subject;

        var builder = new BodyBuilder();
        if (Html) builder.HtmlBody = Body;
        else builder.TextBody = Body;
        if (Attachments != null)
            foreach (var path in Attachments)
                builder.Attachments.Add(path);
        message.Body = builder.ToMessageBody();

        var target = string.Join(", ", To);
        if (!ShouldProcess(target, "Send mail")) return;

        using var smtp = new SmtpClient();
        smtp.Connect(drive.SmtpHost!, drive.SmtpPort, drive.SmtpSsl);
        if (drive.UseOAuth2)
        {
            var token = Provider.OAuth2Helper.AcquireToken(drive.MailUsername, drive.TenantId, drive.ClientId);
            smtp.Authenticate(new MailKit.Security.SaslMechanismOAuth2(drive.MailUsername, token));
        }
        else
        {
            smtp.Authenticate(drive.MailUsername, drive.MailPassword);
        }
        smtp.Send(message);
        smtp.Disconnect(true);

        // Notify IMAP server and invalidate cache so new messages appear in dir
        if (drive is ImapDriveInfo imap)
        {
            imap.Client.NoOp();
            imap.InvalidateAll();
        }

        WriteVerbose($"Sent to {target}");
    }

    private MailDriveInfoBase? FindSmtpDrive()
    {
        var resolved = MailHelpers.ResolveDriveFromPath(Path ?? ".", SessionState);
        if (resolved is MailDriveInfoBase m && m.HasSmtp) return m;
        return null;
    }
}
