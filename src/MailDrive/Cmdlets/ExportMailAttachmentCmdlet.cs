using System.Management.Automation;
using MailDrive.Models;
using MailDrive.Provider;
using MimeKit;

namespace MailDrive.Cmdlets;

[Cmdlet(VerbsData.Export, "MailAttachment", SupportsShouldProcess = true)]
[OutputType(typeof(System.IO.FileInfo))]
public class ExportMailAttachmentCmdlet : PSCmdlet
{
    [Parameter(Mandatory = true, ValueFromPipeline = true)]
    public MailMessageInfo Message { get; set; } = null!;

    [Parameter(Position = 0)]
    [Alias("Destination")]
    public string OutputDirectory { get; set; } = ".";

    [Parameter]
    public string? Name { get; set; }

    [Parameter]
    [Alias("Drive")]
    public string? DriveName { get; set; }

    protected override void ProcessRecord()
    {
        var fetched = MailHelpers.FetchFullMessage(Message, SessionState);
        if (fetched == null)
        {
            WriteError(new ErrorRecord(
                new InvalidOperationException("Cannot fetch message."),
                "FetchFailed", ErrorCategory.ObjectNotFound, Message));
            return;
        }

        var (_, mimeMessage) = fetched.Value;

        var attachments = mimeMessage.Attachments.ToList();
        if (attachments.Count == 0)
        {
            WriteVerbose("No attachments found.");
            return;
        }

        // Resolve to FileSystem path — avoid resolving against IMAP/POP provider
        string outputDir;
        if (Path.IsPathRooted(OutputDirectory))
        {
            outputDir = OutputDirectory;
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
                outputDir = string.IsNullOrEmpty(relative)
                    ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
                    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), relative);
                WriteWarning($"Current location is a mail drive. Saving to: {outputDir}");
            }
            else
            {
                outputDir = GetUnresolvedProviderPathFromPSPath(OutputDirectory);
            }
        }
        Directory.CreateDirectory(outputDir);

        foreach (var att in attachments)
        {
            var fileName = att.ContentDisposition?.FileName
                ?? att.ContentType.Name
                ?? "attachment";

            if (Name != null &&
                !fileName.Equals(Name, StringComparison.OrdinalIgnoreCase))
                continue;

            var outputPath = Path.Combine(outputDir, fileName);

            // Avoid overwriting: append (2), (3), etc.
            if (File.Exists(outputPath))
            {
                var baseName = Path.GetFileNameWithoutExtension(fileName);
                var ext = Path.GetExtension(fileName);
                int n = 2;
                while (File.Exists(outputPath))
                {
                    outputPath = Path.Combine(outputDir, $"{baseName} ({n}){ext}");
                    n++;
                }
            }

            if (!ShouldProcess(outputPath, "Export attachment")) continue;

            if (att is MimePart part)
            {
                using var stream = File.Create(outputPath);
                part.Content.DecodeTo(stream);
            }

            WriteObject(new System.IO.FileInfo(outputPath));
            WriteVerbose($"Exported: {outputPath}");
        }

        if (Name != null && !attachments.Any(a =>
            (a.ContentDisposition?.FileName ?? a.ContentType.Name ?? "")
                .Equals(Name, StringComparison.OrdinalIgnoreCase)))
        {
            WriteWarning($"Attachment '{Name}' not found in message.");
        }
    }
}
