using System.Management.Automation;
using MailDrive.Models;
using MailDrive.Provider;

namespace MailDrive.Cmdlets;

[Cmdlet(VerbsData.Import, "MailConfig")]
[OutputType(typeof(MailDriveInfoBase))]
public class ImportMailConfigCmdlet : PSCmdlet
{
    [Parameter(Position = 0)]
    public string? Path { get; set; }

    [Parameter]
    public SwitchParameter Force { get; set; }

    protected override void ProcessRecord()
    {
        var configPath = Path ?? MailConfig.DefaultPath;
        if (!File.Exists(configPath))
        {
            WriteWarning($"Config not found: {configPath}");
            WriteWarning("Run Edit-MailConfig to create one.");
            return;
        }

        // Skip if config unchanged since InitializeDefaultDrives (unless -Force)
        if (!Force && MailConfig.ConfigLastWriteTimeUtc != null)
        {
            var currentWriteTime = File.GetLastWriteTimeUtc(configPath);
            if (currentWriteTime == MailConfig.ConfigLastWriteTimeUtc)
            {
                WriteVerbose("Config unchanged since module load. Use -Force to reload.");
                return;
            }
        }

        // Move away from mail drive before removing/recreating drives
        try
        {
            var providerName = SessionState.Path.CurrentLocation.Provider.Name;
            if (providerName is "Imap" or "Pop")
                SessionState.Path.SetLocation("C:");
        }
        catch { }

        var config = MailConfig.Load(configPath);
        if (config?.PSDrives == null || config.PSDrives.Count == 0)
        {
            WriteWarning("No PSDrives configured.");
            return;
        }

        // Update timestamp after successful load
        MailConfig.ConfigLastWriteTimeUtc = File.GetLastWriteTimeUtc(configPath);

        foreach (var settings in config.PSDrives)
        {
            settings.CascadeFrom(config);
            if (string.IsNullOrEmpty(settings.Name) || string.IsNullOrEmpty(settings.Host))
            {
                WriteWarning("Skipping drive with missing Name or Host.");
                continue;
            }

            // Skip existing (unless -Force)
            try
            {
                SessionState.Drive.Get(settings.Name);
                if (!Force)
                {
                    WriteVerbose($"Drive '{settings.Name}' already exists. Use -Force to recreate.");
                    continue;
                }
                var existing = SessionState.Drive.Get(settings.Name);
                if (existing is IDisposable d) d.Dispose();
                SessionState.Drive.Remove(settings.Name, true, "global");
            }
            catch (System.Management.Automation.DriveNotFoundException) { }

            var protocol = settings.Protocol ?? "IMAP";
            var ssl = MailConfig.ParseSsl(settings.Ssl);
            var smtpSsl = MailConfig.ParseSsl(settings.SmtpSsl);
            var smtpPort = settings.SmtpPort ?? 587;

            MailDriveInfoBase driveInfo;
            if (string.Equals(protocol, "POP3", StringComparison.OrdinalIgnoreCase))
            {
                var port = settings.Port ?? 995;
                var desc = string.IsNullOrEmpty(settings.Description)
                    ? $"{settings.Username}@{settings.Host}:{port}" : settings.Description;
                var provider = SessionState.Provider.GetOne("Pop");
                var dp = new PSDriveInfo(settings.Name, provider, settings.Name + @":\", desc, null);
                driveInfo = new PopDriveInfo(dp, settings.Host, port, ssl,
                    settings.Username ?? "", settings.Password ?? "",
                    settings.SmtpHost, smtpPort, smtpSsl);
            }
            else
            {
                var port = settings.Port ?? 993;
                var desc = string.IsNullOrEmpty(settings.Description)
                    ? $"{settings.Username}@{settings.Host}:{port}" : settings.Description;
                var provider = SessionState.Provider.GetOne("Imap");
                var dp = new PSDriveInfo(settings.Name, provider, settings.Name + @":\", desc, null);
                driveInfo = new ImapDriveInfo(dp, settings.Host, port, ssl,
                    settings.Username ?? "", settings.Password ?? "",
                    settings.SmtpHost, smtpPort, smtpSsl);
            }

            SessionState.Drive.New(driveInfo, "global");
            WriteObject(driveInfo);
        }
    }
}

[Cmdlet(VerbsCommon.Get, "MailConfigPath")]
[OutputType(typeof(string))]
public class GetMailConfigPathCmdlet : PSCmdlet
{
    protected override void ProcessRecord()
    {
        WriteObject(MailConfig.DefaultPath);
    }
}

[Cmdlet(VerbsCommon.Open, "MailConfig")]
[OutputType(typeof(string))]
public class EditMailConfigCmdlet : PSCmdlet
{
    [Parameter]
    public SwitchParameter UseDefaultEditor { get; set; }

    protected override void ProcessRecord()
    {
        var configPath = MailConfig.DefaultPath;

        if (!File.Exists(configPath))
        {
            MailConfig.EnsureDefaultConfig(configPath);
            WriteVerbose($"Created: {configPath}");
        }

        System.Diagnostics.ProcessStartInfo psi;
        if (UseDefaultEditor)
            psi = new(configPath) { UseShellExecute = true };
        else
            psi = new("notepad.exe", configPath);

        System.Diagnostics.Process.Start(psi);
        WriteObject(configPath);
    }
}
