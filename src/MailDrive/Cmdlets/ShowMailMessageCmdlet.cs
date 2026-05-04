using System.Diagnostics;
using System.Management.Automation;
using MailDrive.Models;
using MailDrive.Provider;

namespace MailDrive.Cmdlets;

/// <summary>
/// Render a mail message in the OS-default .eml viewer.
/// Fetches the full RFC 822 to a temp file and Invoke-Item-style launches it.
/// MarkdownPointer (when registered for .eml) renders the HTML body in a
/// CSP-protected WebView2; otherwise the user's Outlook / Thunderbird / etc.
/// handles the file. Returns the path with -PassThru so AI agents can hand
/// it off to mdp_show_markdown directly.
/// </summary>
[Cmdlet(VerbsCommon.Show, "MailMessage")]
[OutputType(typeof(FileInfo))]
public class ShowMailMessageCmdlet : PSCmdlet
{
    [Parameter(Mandatory = true, ValueFromPipeline = true, Position = 0)]
    public MailMessageInfo InputObject { get; set; } = null!;

    [Parameter(HelpMessage = "Return the path of the written .eml without launching a viewer.")]
    public SwitchParameter PassThru { get; set; }

    [Parameter(HelpMessage = "Directory for the .eml. Defaults to a temp folder.")]
    public string? OutputDirectory { get; set; }

    protected override void ProcessRecord()
    {
        var fetched = MailHelpers.FetchFullMessage(InputObject, SessionState);
        if (fetched == null)
        {
            WriteError(new ErrorRecord(
                new InvalidOperationException($"Cannot fetch message: {InputObject.FileName}"),
                "FetchFailed", ErrorCategory.ObjectNotFound, InputObject));
            return;
        }

        var dir = OutputDirectory ?? Path.Combine(Path.GetTempPath(), "MailDrive-show");
        Directory.CreateDirectory(dir);
        var outPath = Path.Combine(dir, SanitizeFileName(InputObject.FileName));

        fetched.Value.Message.WriteTo(outPath);
        var fi = new FileInfo(outPath);

        if (PassThru)
        {
            WriteObject(fi);
            return;
        }

        // Hand off to whatever is registered for .eml (typically the user's
        // mail client; if MarkdownPointer is registered, you get HTML render
        // with CSP protection and external resources blocked).
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = outPath,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            WriteWarning($"Could not launch a viewer for .eml: {ex.Message}. Path written to {outPath}.");
            WriteObject(fi);
        }
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = string.Concat(name.Select(c => invalid.Contains(c) ? '_' : c));
        return clean.Length > 200 ? clean[..200] : clean;
    }
}
