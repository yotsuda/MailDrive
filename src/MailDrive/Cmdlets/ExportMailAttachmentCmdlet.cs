using System.Management.Automation;
using MailDrive.Models;
using MailDrive.Provider;
using MimeKit;

namespace MailDrive.Cmdlets;

[Cmdlet(VerbsData.Export, "MailAttachment", SupportsShouldProcess = true)]
[OutputType(typeof(FileInfo))]
public class ExportMailAttachmentCmdlet : PSCmdlet
{
    [Parameter(Mandatory = true, ValueFromPipeline = true, ParameterSetName = "FromMessage")]
    public MailMessageInfo? Message { get; set; }

    [Parameter(Mandatory = true, ValueFromPipeline = true, ParameterSetName = "FromFile")]
    public FileInfo? File { get; set; }

    [Parameter(Position = 0)]
    [Alias("Destination")]
    public string OutputDirectory { get; set; } = ".";

    [Parameter]
    public string? Name { get; set; }

    [Parameter(HelpMessage = "Include inline/embedded images (e.g. CID-referenced images in HTML mail).")]
    public SwitchParameter IncludeEmbedded { get; set; }

    protected override void ProcessRecord()
    {
        MimeMessage mimeMessage;

        if (File != null)
        {
            // Load from local .eml file
            if (!File.Exists)
            {
                WriteError(new ErrorRecord(
                    new FileNotFoundException($"File not found: {File.FullName}"),
                    "FileNotFound", ErrorCategory.ObjectNotFound, File));
                return;
            }
            mimeMessage = MimeMessage.Load(File.FullName);
        }
        else if (Message != null)
        {
            // Fetch from IMAP/POP3
            var fetched = MailHelpers.FetchFullMessage(Message, SessionState);
            if (fetched == null)
            {
                WriteError(new ErrorRecord(
                    new InvalidOperationException("Cannot fetch message."),
                    "FetchFailed", ErrorCategory.ObjectNotFound, Message));
                return;
            }
            mimeMessage = fetched.Value.Message;
        }
        else return;

        // Collect parts: attachments + optionally embedded images
        var parts = new List<MimePart>();
        foreach (var att in mimeMessage.Attachments)
        {
            if (att is MimePart p) parts.Add(p);
        }
        if (IncludeEmbedded)
        {
            CollectEmbeddedImages(mimeMessage.Body, parts);
        }

        if (parts.Count == 0)
        {
            WriteVerbose("No attachments found.");
            return;
        }

        // Resolve output directory
        string outputDir = ResolveOutputDirectory();
        Directory.CreateDirectory(outputDir);

        foreach (var part in parts)
        {
            var fileName = part.FileName
                ?? part.ContentDisposition?.FileName
                ?? part.ContentType.Name
                ?? (part.ContentId != null ? $"{part.ContentId}.{GetExtension(part)}" : null)
                ?? $"embedded.{GetExtension(part)}";

            if (Name != null &&
                !fileName.Equals(Name, StringComparison.OrdinalIgnoreCase))
                continue;

            var outputPath = System.IO.Path.Combine(outputDir, fileName);

            // Avoid overwriting: append (2), (3), etc.
            if (System.IO.File.Exists(outputPath))
            {
                var baseName = System.IO.Path.GetFileNameWithoutExtension(fileName);
                var ext = System.IO.Path.GetExtension(fileName);
                int n = 2;
                while (System.IO.File.Exists(outputPath))
                {
                    outputPath = System.IO.Path.Combine(outputDir, $"{baseName} ({n}){ext}");
                    n++;
                }
            }

            if (!ShouldProcess(outputPath, "Export attachment")) continue;

            using var stream = System.IO.File.Create(outputPath);
            part.Content.DecodeTo(stream);

            WriteObject(new FileInfo(outputPath));
            WriteVerbose($"Exported: {outputPath}");
        }

        if (Name != null && !parts.Any(p =>
            (p.FileName ?? p.ContentDisposition?.FileName ?? p.ContentType.Name ?? "")
                .Equals(Name, StringComparison.OrdinalIgnoreCase)))
        {
            WriteWarning($"Attachment '{Name}' not found.");
        }
    }

    private static void CollectEmbeddedImages(MimeEntity? entity, List<MimePart> parts)
    {
        if (entity == null) return;
        if (entity is Multipart multipart)
        {
            foreach (var child in multipart)
                CollectEmbeddedImages(child, parts);
        }
        else if (entity is MimePart part
            && part.ContentDisposition?.Disposition != ContentDisposition.Attachment
            && part.ContentType.MediaType == "image"
            && !parts.Contains(part))
        {
            parts.Add(part);
        }
    }

    private static string GetExtension(MimePart part)
    {
        return part.ContentType.MediaSubtype?.ToLowerInvariant() switch
        {
            "png" => "png",
            "gif" => "gif",
            "bmp" => "bmp",
            "webp" => "webp",
            "svg+xml" => "svg",
            _ => "jpg",
        };
    }

    private string ResolveOutputDirectory()
    {
        if (System.IO.Path.IsPathRooted(OutputDirectory))
            return OutputDirectory;

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
            var dir = string.IsNullOrEmpty(relative)
                ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
                : System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), relative);
            WriteWarning($"Current location is a mail drive. Saving to: {dir}");
            return dir;
        }

        return GetUnresolvedProviderPathFromPSPath(OutputDirectory);
    }
}
