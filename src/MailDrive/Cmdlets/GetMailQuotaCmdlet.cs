using System.Management.Automation;
using MailDrive.Provider;
using MailKit;

namespace MailDrive.Cmdlets;

[Cmdlet(VerbsCommon.Get, "MailQuota")]
[OutputType(typeof(PSObject))]
public class GetMailQuotaCmdlet : PSCmdlet
{
    [Parameter(Position = 0)]
    [Alias("Drive")]
    public string? DriveName { get; set; }

    protected override void ProcessRecord()
    {
        var drive = FindImapDrive();
        if (drive == null)
        {
            ThrowTerminatingError(new ErrorRecord(
                new InvalidOperationException("No IMAP drive found. Quota requires an IMAP connection."),
                "NoImapDrive", ErrorCategory.ObjectNotFound, null));
            return;
        }

        try
        {
            var inbox = drive.Client.Inbox;
            if (!inbox.IsOpen) inbox.Open(FolderAccess.ReadOnly);

            try
            {
                var quota = drive.Client.Inbox.GetQuota();
                var result = new PSObject();

                if (quota.StorageLimit.HasValue)
                {
                    result.Properties.Add(new PSNoteProperty("StorageUsed", quota.CurrentStorageSize ?? 0));
                    result.Properties.Add(new PSNoteProperty("StorageLimit", quota.StorageLimit.Value));
                    result.Properties.Add(new PSNoteProperty("StorageUsedMB",
                        Math.Round((quota.CurrentStorageSize ?? 0) / 1024.0, 1)));
                    result.Properties.Add(new PSNoteProperty("StorageLimitMB",
                        Math.Round(quota.StorageLimit.Value / 1024.0, 1)));
                }

                if (quota.MessageLimit.HasValue)
                {
                    result.Properties.Add(new PSNoteProperty("MessageCount", quota.CurrentMessageCount ?? 0));
                    result.Properties.Add(new PSNoteProperty("MessageLimit", quota.MessageLimit.Value));
                }

                WriteObject(result);
            }
            finally
            {
                try { if (inbox.IsOpen) inbox.Close(false); } catch { }
            }
        }
        catch (NotSupportedException)
        {
            WriteWarning("This IMAP server does not support quota.");
        }
    }

    private ImapDriveInfo? FindImapDrive()
    {
        if (!string.IsNullOrEmpty(DriveName))
        {
            var d = SessionState.Drive.Get(DriveName);
            return d as ImapDriveInfo;
        }

        try
        {
            if (SessionState.Path.CurrentLocation.Drive is ImapDriveInfo current)
                return current;
        }
        catch { }

        try
        {
            foreach (var d in SessionState.Drive.GetAllForProvider("Imap"))
                if (d is ImapDriveInfo imap) return imap;
        }
        catch { }

        return null;
    }
}
