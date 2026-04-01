using System.Management.Automation;
using MailDrive.Models;
using MailDrive.Provider;
using MailKit.Net.Smtp;
using MimeKit;

namespace MailDrive.Cmdlets;

[Cmdlet("Reply", "Mail", SupportsShouldProcess = true)]
public class ReplyMailCmdlet : PSCmdlet
{
    [Parameter(Mandatory = true, ValueFromPipeline = true)]
    public MailMessageInfo OriginalMessage { get; set; } = null!;

    [Parameter(Mandatory = true, Position = 0)]
    public string Body { get; set; } = "";

    [Parameter]
    public SwitchParameter All { get; set; }

    [Parameter]
    public SwitchParameter Html { get; set; }

    [Parameter]
    public string[]? Attachments { get; set; }

    [Parameter]
    [Alias("Drive")]
    public string? DriveName { get; set; }

    protected override void ProcessRecord()
    {
        var fetched = MailHelpers.FetchFullMessage(OriginalMessage, SessionState);
        if (fetched == null)
        {
            WriteError(new ErrorRecord(
                new InvalidOperationException("Cannot fetch original message."),
                "FetchFailed", ErrorCategory.ObjectNotFound, OriginalMessage));
            return;
        }

        var (driveInfo, original) = fetched.Value;
        var smtpDrive = ResolveSmtpDrive(driveInfo);
        if (smtpDrive == null)
        {
            ThrowTerminatingError(new ErrorRecord(
                new InvalidOperationException("No SMTP configured on the drive."),
                "NoSmtp", ErrorCategory.ObjectNotFound, null));
            return;
        }

        var reply = new MimeMessage();
        reply.From.Add(MailboxAddress.Parse(smtpDrive.MailUsername));

        // Reply to sender (or Reply-To if present)
        if (original.ReplyTo.Count > 0)
            reply.To.AddRange(original.ReplyTo);
        else
            reply.To.AddRange(original.From);

        // Reply All: add To and Cc (excluding self)
        if (All)
        {
            var self = smtpDrive.MailUsername.ToLowerInvariant();
            foreach (var addr in original.To.Mailboxes)
                if (!addr.Address.Equals(self, StringComparison.OrdinalIgnoreCase))
                    reply.To.Add(addr);
            foreach (var addr in original.Cc.Mailboxes)
                if (!addr.Address.Equals(self, StringComparison.OrdinalIgnoreCase))
                    reply.Cc.Add(addr);
        }

        reply.Subject = original.Subject?.StartsWith("Re:", StringComparison.OrdinalIgnoreCase) == true
            ? original.Subject : $"Re: {original.Subject}";

        if (!string.IsNullOrEmpty(original.MessageId))
        {
            reply.InReplyTo = original.MessageId;
            reply.References.Add(original.MessageId);
            foreach (var r in original.References)
                reply.References.Add(r);
        }

        // Quote original
        var originalBody = original.TextBody ?? "";
        var quoted = string.Join("\n", originalBody.Split('\n').Select(l => $"> {l.TrimEnd('\r')}"));
        var fullBody = $"{Body}\n\nOn {original.Date.LocalDateTime:yyyy-MM-dd HH:mm}, {original.From} wrote:\n{quoted}";

        var builder = new BodyBuilder();
        if (Html) builder.HtmlBody = fullBody;
        else builder.TextBody = fullBody;
        if (Attachments != null)
            foreach (var path in Attachments) builder.Attachments.Add(path);
        reply.Body = builder.ToMessageBody();

        var target = string.Join(", ", reply.To);
        if (!ShouldProcess(target, "Reply")) return;

        SendMessage(smtpDrive, reply);
        WriteVerbose($"Replied to {target}");
    }

    private MailDriveInfoBase? ResolveSmtpDrive(MailDriveInfoBase messageDrive)
    {
        if (!string.IsNullOrEmpty(DriveName))
        {
            var d = SessionState.Drive.Get(DriveName);
            return d is MailDriveInfoBase m && m.HasSmtp ? m : null;
        }
        return messageDrive.HasSmtp ? messageDrive : null;
    }

    private static void SendMessage(MailDriveInfoBase drive, MimeMessage message)
    {
        using var smtp = new SmtpClient();
        smtp.Connect(drive.SmtpHost!, drive.SmtpPort, drive.SmtpSsl);
        smtp.Authenticate(drive.MailUsername, drive.MailPassword);
        smtp.Send(message);
        smtp.Disconnect(true);
    }
}

[Cmdlet("Forward", "Mail", SupportsShouldProcess = true)]
public class ForwardMailCmdlet : PSCmdlet
{
    [Parameter(Mandatory = true, ValueFromPipeline = true)]
    public MailMessageInfo OriginalMessage { get; set; } = null!;

    [Parameter(Mandatory = true, Position = 0)]
    public string[] To { get; set; } = [];

    [Parameter(Position = 1)]
    public string Body { get; set; } = "";

    [Parameter]
    public SwitchParameter Html { get; set; }

    [Parameter]
    public SwitchParameter IncludeAttachments { get; set; }

    [Parameter]
    [Alias("Drive")]
    public string? DriveName { get; set; }

    protected override void ProcessRecord()
    {
        var fetched = MailHelpers.FetchFullMessage(OriginalMessage, SessionState);
        if (fetched == null)
        {
            WriteError(new ErrorRecord(
                new InvalidOperationException("Cannot fetch original message."),
                "FetchFailed", ErrorCategory.ObjectNotFound, OriginalMessage));
            return;
        }

        var (driveInfo, original) = fetched.Value;
        var smtpDrive = ResolveSmtpDrive(driveInfo);
        if (smtpDrive == null)
        {
            ThrowTerminatingError(new ErrorRecord(
                new InvalidOperationException("No SMTP configured on the drive."),
                "NoSmtp", ErrorCategory.ObjectNotFound, null));
            return;
        }

        var forward = new MimeMessage();
        forward.From.Add(MailboxAddress.Parse(smtpDrive.MailUsername));
        foreach (var to in To) forward.To.Add(MailboxAddress.Parse(to));

        forward.Subject = original.Subject?.StartsWith("Fwd:", StringComparison.OrdinalIgnoreCase) == true
            ? original.Subject : $"Fwd: {original.Subject}";

        var originalBody = original.TextBody ?? "";
        var header = $"---------- Forwarded message ----------\n" +
                     $"From: {original.From}\n" +
                     $"Date: {original.Date.LocalDateTime:yyyy-MM-dd HH:mm}\n" +
                     $"Subject: {original.Subject}\n" +
                     $"To: {original.To}\n\n";
        var fullBody = string.IsNullOrEmpty(Body)
            ? $"{header}{originalBody}"
            : $"{Body}\n\n{header}{originalBody}";

        var builder = new BodyBuilder();
        if (Html) builder.HtmlBody = fullBody;
        else builder.TextBody = fullBody;

        if (IncludeAttachments)
        {
            foreach (var att in original.Attachments)
            {
                if (att is MimePart part)
                {
                    using var stream = part.Content.Open();
                    var ms = new MemoryStream();
                    stream.CopyTo(ms);
                    ms.Position = 0;
                    builder.Attachments.Add(
                        part.FileName ?? "attachment", ms, part.ContentType);
                }
            }
        }

        forward.Body = builder.ToMessageBody();

        var target = string.Join(", ", To);
        if (!ShouldProcess(target, "Forward")) return;

        using var smtp = new SmtpClient();
        smtp.Connect(smtpDrive.SmtpHost!, smtpDrive.SmtpPort, smtpDrive.SmtpSsl);
        smtp.Authenticate(smtpDrive.MailUsername, smtpDrive.MailPassword);
        smtp.Send(forward);
        smtp.Disconnect(true);

        WriteVerbose($"Forwarded to {target}");
    }

    private MailDriveInfoBase? ResolveSmtpDrive(MailDriveInfoBase messageDrive)
    {
        if (!string.IsNullOrEmpty(DriveName))
        {
            var d = SessionState.Drive.Get(DriveName);
            return d is MailDriveInfoBase m && m.HasSmtp ? m : null;
        }
        return messageDrive.HasSmtp ? messageDrive : null;
    }
}
