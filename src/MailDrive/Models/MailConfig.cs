using System.Reflection;
using System.Text.Json;
using MailKit.Security;

namespace MailDrive.Models;

public class MailSettings
{
    public string? Name { get; set; }
    public string? Protocol { get; set; }
    public string? Host { get; set; }
    public int? Port { get; set; }
    public string? Ssl { get; set; }
    public string? Username { get; set; }
    public string? Password { get; set; }
    public string? Description { get; set; }
    public string? SmtpHost { get; set; }
    public int? SmtpPort { get; set; }
    public string? SmtpSsl { get; set; }

    /// <summary>"Password" (default), "OAuth2" (PKCE), or "DeviceCode"</summary>
    public string? AuthMethod { get; set; }
    public string? TenantId { get; set; }
    public string? ClientId { get; set; }

    internal void CascadeFrom(MailConfig global)
    {
        Port ??= global.Port;
        Ssl ??= global.Ssl;
        SmtpHost ??= global.SmtpHost;
        SmtpPort ??= global.SmtpPort;
        SmtpSsl ??= global.SmtpSsl;
        AuthMethod ??= global.AuthMethod;
        TenantId ??= global.TenantId;
        ClientId ??= global.ClientId;
    }

    internal bool IsOAuth2 =>
        string.Equals(AuthMethod, "OAuth2", StringComparison.OrdinalIgnoreCase)
        || string.Equals(AuthMethod, "DeviceCode", StringComparison.OrdinalIgnoreCase);

    internal bool IsDeviceCode =>
        string.Equals(AuthMethod, "DeviceCode", StringComparison.OrdinalIgnoreCase);
}

public class MailConfig : MailSettings
{
    public List<MailSettings>? PSDrives { get; set; }

    public static string DefaultPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "PowerShell", "Modules", "MailDrive", "MailDriveConfig.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>Last write time recorded when config was loaded by InitializeDefaultDrives.</summary>
    internal static DateTime? ConfigLastWriteTimeUtc { get; set; }

    public static MailConfig? Load(string path)
    {
        if (!File.Exists(path)) return null;
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<MailConfig>(json, JsonOptions);
    }

    public static SecureSocketOptions ParseSsl(string? ssl) => ssl switch
    {
        "SslOnConnect" => SecureSocketOptions.SslOnConnect,
        "StartTls" => SecureSocketOptions.StartTls,
        "None" => SecureSocketOptions.None,
        _ => SecureSocketOptions.Auto,
    };

    public static void EnsureDefaultConfig(string path)
    {
        var dir = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);
        var asm = Assembly.GetExecutingAssembly();
        using var stream = asm.GetManifestResourceStream("MailDrive.Resources.MailDriveConfig.json")!;
        using var fs = File.Create(path);
        stream.CopyTo(fs);
    }
}
