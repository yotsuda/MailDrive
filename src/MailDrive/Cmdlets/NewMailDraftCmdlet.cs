using System.Management.Automation;
using MailDrive.Provider;
using MailKit;
using MimeKit;

namespace MailDrive.Cmdlets;

[Cmdlet(VerbsCommon.New, "MailDraft", SupportsShouldProcess = true)]
public class NewMailDraftCmdlet : PSCmdlet
{
    [Parameter(Mandatory = true, Position = 0)]
    public string Subject { get; set; } = "";

    [Parameter(Mandatory = true, Position = 1, ValueFromPipeline = true)]
    public string Body { get; set; } = "";

    [Parameter]
    public string[]? To { get; set; }

    [Parameter]
    public string[]? Cc { get; set; }

    [Parameter]
    public string[]? Bcc { get; set; }

    [Parameter]
    public SwitchParameter Html { get; set; }

    [Parameter]
    public string[]? Attachments { get; set; }

    [Parameter]
    [Alias("Drive")]
    public string? Path { get; set; }

    protected override void ProcessRecord()
    {
        var drive = FindImapDrive();
        if (drive == null)
        {
            ThrowTerminatingError(new ErrorRecord(
                new InvalidOperationException("No IMAP drive found. Drafts require an IMAP connection."),
                "NoImapDrive", ErrorCategory.ObjectNotFound, null));
            return;
        }

        var draftsFolder = GetDraftsFolder(drive);
        if (draftsFolder == null)
        {
            ThrowTerminatingError(new ErrorRecord(
                new InvalidOperationException("Cannot find Drafts folder on this IMAP server."),
                "NoDraftsFolder", ErrorCategory.ObjectNotFound, null));
            return;
        }

        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse(drive.MailUsername));
        if (To != null) foreach (var to in To) message.To.Add(MailboxAddress.Parse(to));
        if (Cc != null) foreach (var cc in Cc) message.Cc.Add(MailboxAddress.Parse(cc));
        if (Bcc != null) foreach (var bcc in Bcc) message.Bcc.Add(MailboxAddress.Parse(bcc));
        message.Subject = Subject;

        var builder = new BodyBuilder();
        if (Html) builder.HtmlBody = Body;
        else builder.TextBody = Body;
        if (Attachments != null)
            foreach (var path in Attachments) builder.Attachments.Add(path);
        message.Body = builder.ToMessageBody();

        if (!ShouldProcess(Subject, "Save draft")) return;

        draftsFolder.Open(FolderAccess.ReadWrite);
        try
        {
            draftsFolder.Append(message, MessageFlags.Draft | MessageFlags.Seen);
        }
        finally
        {
            try { if (draftsFolder.IsOpen) draftsFolder.Close(false); } catch { }
        }

        var draftsPath = draftsFolder.FullName.Replace(drive.DirectorySeparator, '/');
        drive.InvalidateMessages(draftsPath);

        WriteVerbose($"Draft saved: {Subject}");
    }

    private IMailFolder? GetDraftsFolder(ImapDriveInfo drive)
    {
        try
        {
            var drafts = drive.Client.GetFolder(SpecialFolder.Drafts);
            if (drafts != null && drafts.Exists) return drafts;
        }
        catch { }

        var root = drive.Client.GetFolder(drive.Client.PersonalNamespaces[0]);
        foreach (var sub in root.GetSubfolders(false))
        {
            var name = sub.Name.ToLowerInvariant();
            if (name is "drafts" or "[gmail]/drafts")
                return sub;
            if (sub.Attributes.HasFlag(FolderAttributes.Drafts))
                return sub;
        }

        return null;
    }

    private ImapDriveInfo? FindImapDrive()
    {
        return MailHelpers.ResolveDriveFromPath(Path ?? ".", SessionState) as ImapDriveInfo;
    }
}
