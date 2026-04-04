using System.Management.Automation;
using MailDrive.Models;
using MailDrive.Provider;

namespace MailDrive.Cmdlets;

[Cmdlet(VerbsData.Export, "MailMessage", SupportsShouldProcess = true)]
[OutputType(typeof(FileInfo))]
public class ExportMailMessageCmdlet : PSCmdlet
{
    [Parameter(Mandatory = true, ValueFromPipeline = true)]
    public MailItemBase InputObject { get; set; } = null!;

    [Parameter(Position = 0)]
    [Alias("Destination")]
    public string OutputDirectory { get; set; } = ".";

    [Parameter]
    public SwitchParameter Force { get; set; }

    [Parameter(HelpMessage = "Delete message from server after download (POP3 only).")]
    public SwitchParameter DeleteFromServer { get; set; }

    private string _resolvedDir = null!;
    private int _exported;
    private int _skipped;

    protected override void BeginProcessing()
    {
        // Resolve output directory once
        if (Path.IsPathRooted(OutputDirectory))
        {
            _resolvedDir = OutputDirectory;
        }
        else
        {
            bool isMailDrive = false;
            try
            {
                var providerName = SessionState.Path.CurrentLocation.Provider.Name;
                isMailDrive = providerName is "Imap" or "Pop";
            }
            catch { }

            if (isMailDrive)
            {
                var relative = OutputDirectory.TrimStart('.', '/', '\\');
                _resolvedDir = string.IsNullOrEmpty(relative)
                    ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
                    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), relative);
                WriteWarning($"Current location is a mail drive. Saving to: {_resolvedDir}");
            }
            else
            {
                _resolvedDir = GetUnresolvedProviderPathFromPSPath(OutputDirectory);
            }
        }
        Directory.CreateDirectory(_resolvedDir);
    }

    protected override void ProcessRecord()
    {
        // Create local directory for IMAP folders
        if (InputObject is MailFolderInfo folder)
        {
            var folderDir = Path.Combine(_resolvedDir, folder.Name);
            Directory.CreateDirectory(folderDir);
            WriteVerbose($"Created directory: {folderDir}");
            return;
        }

        if (InputObject is not MailMessageInfo message) return;

        // Resolve subfolder from message's Directory (e.g. "GM:\INBOX" → "INBOX")
        var msgDir = _resolvedDir;
        if (!string.IsNullOrEmpty(message.Directory))
        {
            var colonIdx = message.Directory.IndexOf(':');
            if (colonIdx >= 0)
            {
                var relative = message.Directory[(colonIdx + 1)..].Replace('/', '\\').Trim('\\');
                if (!string.IsNullOrEmpty(relative))
                {
                    msgDir = Path.Combine(_resolvedDir, relative);
                    Directory.CreateDirectory(msgDir);
                }
            }
        }
        var outputPath = Path.Combine(msgDir, message.FileName);

        // Skip existing (default) unless -Force
        if (File.Exists(outputPath) && !Force)
        {
            _skipped++;
            WriteVerbose($"Skipped (exists): {message.FileName}");
            return;
        }

        if (!ShouldProcess(outputPath, "Export message")) return;

        var fetched = MailHelpers.FetchFullMessage(message, SessionState);
        if (fetched == null)
        {
            WriteError(new ErrorRecord(
                new InvalidOperationException($"Cannot fetch message: {message.FileName}"),
                "FetchFailed", ErrorCategory.ObjectNotFound, message));
            return;
        }

        var (drive, mimeMessage) = fetched.Value;

        mimeMessage.WriteTo(outputPath);
        _exported++;
        WriteObject(new FileInfo(outputPath));

        if (DeleteFromServer)
        {
            if (drive is PopDriveInfo pop)
            {
                try
                {
                    pop.Client.DeleteMessage((int)(message.Uid - 1));
                    WriteVerbose($"Deleted from server: {message.FileName}");
                }
                catch (Exception ex)
                {
                    WriteError(new ErrorRecord(ex, "DeleteFailed",
                        ErrorCategory.WriteError, message));
                }
            }
            else
            {
                WriteWarning("-DeleteFromServer is only supported for POP3 drives.");
            }
        }
    }

    protected override void EndProcessing()
    {
        if (_skipped > 0)
            WriteVerbose($"{_skipped} message(s) skipped (already exist). Use -Force to overwrite.");
        WriteVerbose($"{_exported} message(s) exported to {_resolvedDir}");
    }
}
