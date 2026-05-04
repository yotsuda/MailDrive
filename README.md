# MailDrive

PowerShell NavigationCmdletProvider for IMAP / POP3 / SMTP.
Navigate your mailbox like a filesystem using `cd`, `dir`, `cat`, `mv`, `del` and other familiar commands.

## Features

- **IMAP / POP3 / SMTP** via [MailKit](https://github.com/jstreet/MailKit)
- **Folder operations** — create, rename, delete, subscribe/unsubscribe
- **Message operations** — read, move, copy, delete (Trash), flag, search
- **Send / Reply / Forward** with attachments
- **Gmail native search** — `X-GM-RAW` powered server-side search (`has:attachment`, `from:`, etc.)
- **OAuth2** — Device Code Flow for Microsoft 365 / Azure AD
- **Bulk export** — `.eml` backup with incremental skip, attachment extraction from local `.eml` files
- **Unified view** — folders (blue) and messages in the same table
- **Auto-reconnect** — transparent retry on connection drop

## Requirements

- PowerShell 7.4+
- .NET 9.0

## Quick Start

```powershell
Import-Module MailDrive
Open-MailConfig          # Opens config in notepad — add your mail account
Import-MailConfig        # Mount drives from config

cd Gmail:\INBOX
dir                      # List messages
dir -Search "from:boss"  # Gmail native search
cat .\1_sender_subject.eml  # Read message body
```

## Configuration

Config file location: `Get-MailConfigPath`

```json
{
    "PSDrives": [
        {
            "Name": "Gmail",
            "Protocol": "IMAP",
            "Host": "imap.gmail.com",
            "Port": 993,
            "Ssl": "SslOnConnect",
            "Username": "you@gmail.com",
            "Password": "xxxx xxxx xxxx xxxx",
            "SmtpHost": "smtp.gmail.com",
            "SmtpPort": 465,
            "SmtpSsl": "SslOnConnect"
        }
    ]
}
```

For Gmail, generate an [App Password](https://myaccount.google.com/apppasswords) (requires 2-Step Verification).

## Usage Examples

### Navigation

```powershell
cd Gmail:\INBOX          # Enter INBOX
dir                      # List folders and messages
dir -First 50            # Show latest 50 messages
dir -Force               # Refresh cache
cd ..                    # Go up
```

### Read and Search

```powershell
cat .\1_sender_subject.eml              # Read message body
dir -Search "has:attachment"             # Gmail: server-side search
dir -Search "from:alice subject:meeting" # Multiple conditions
```

### Send

```powershell
Send-Mail -To user@example.com -Subject "Hello" -Body "Hi there"
Send-Mail -To user@example.com -Subject "Report" -Body "See attached" -Attachments .\report.pdf
```

### Reply and Forward

```powershell
Get-Item .\1_sender_subject.eml | Submit-MailReply "Thanks!"
Get-Item .\1_sender_subject.eml | Submit-MailForward -To colleague@example.com
```

### Folder Operations

```powershell
mkdir Gmail:\Archive                     # Create folder
mv .\1_sender_subject.eml Gmail:\Archive # Move message
del .\2_sender_subject.eml              # Move to Trash
del .\2_sender_subject.eml -Force       # Permanent delete
Rename-Item Gmail:\OldName Gmail:\NewName
```

### Export

```powershell
# Bulk backup (skips existing files by default)
dir Gmail:\INBOX | Export-MailMessage C:\backup\Gmail

# With folder structure preserved
dir Gmail:\ -Recurse | Export-MailMessage C:\backup\Gmail

# Extract attachments from server
Get-Item .\1_sender_subject.eml | Export-MailAttachment C:\temp\

# Extract attachments from local .eml (including embedded images)
Get-Item C:\backup\message.eml | Export-MailAttachment C:\temp\ -IncludeEmbedded
```

### Flags

```powershell
Set-ItemProperty .\1_sender_subject.eml -Name IsFlagged -Value $true
Clear-ItemProperty .\1_sender_subject.eml -Name IsFlagged
```

## Cmdlets

| Cmdlet | Description |
|---|---|
| `New-MailDrive` | Create an IMAP drive |
| `New-PopDrive` | Create a POP3 drive |
| `Import-MailConfig` | Mount drives from config file |
| `Open-MailConfig` | Open config in editor |
| `Get-MailConfigPath` | Show config file path |
| `Send-Mail` | Send a message via SMTP |
| `Submit-MailReply` | Reply to a message |
| `Submit-MailForward` | Forward a message |
| `Export-MailMessage` | Export messages as .eml files |
| `Export-MailAttachment` | Extract attachments |
| `New-MailDraft` | Save a draft (IMAP) |
| `Get-MailQuota` | Query IMAP quota |

## Mail MCP Server

MailDrive works as an email MCP server when combined with [PowerShell.MCP](https://github.com/yotsuda/PowerShell.MCP).

```json
{
    "mcpServers": {
        "pwsh": {
            "command": "pwsh",
            "args": ["-NoProfile", "-Command", "Import-Module PowerShell.MCP; Start-MCPServer"]
        }
    }
}
```

Once connected, AI agents can read, search, send, and manage email through standard PowerShell commands:

```
# Agent reads inbox
invoke_expression: "dir Gmail:\\INBOX"

# Agent reads a specific message
invoke_expression: "cat 'Gmail:\\INBOX\\28_Google_Security.eml'"

# Agent searches for invoices
invoke_expression: "dir Gmail:\\INBOX -Search 'has:attachment from:Microsoft'"

# Agent sends email
invoke_expression: "Send-Mail -To user@example.com -Subject 'Report' -Body 'Done'"

# Agent exports attachments
invoke_expression: "Get-Item 'Gmail:\\INBOX\\23_Microsoft_Invoice.eml' | Export-MailAttachment C:\\temp"
```

No dedicated mail MCP server needed — the PowerShell provider acts as the interface.

## Testing with GreenMail

See [GREENMAIL.md](GREENMAIL.md) for local testing with GreenMail.

## Build

```powershell
.\build.ps1
Import-Module MailDrive -Force
```

## Help

Per-cmdlet documentation lives under `docs/help/<locale>/`. English (`en-US/`)
is the only locale shipped today; the layout supports additional locales.

| Action | Command |
|---|---|
| View help offline | `Get-Help <cmdlet> -Full` |
| Open online version | `Get-Help <cmdlet> -Online` |
| Regenerate from cmdlet metadata | `.\docs\build-help.ps1` |

The build script uses [PlatyPS](https://github.com/PowerShell/platyPS): it
refreshes the markdown skeletons (preserving any prose you've written),
patches per-cmdlet online URLs, and compiles MAML into
`<module>/<locale>/MailDrive.dll-Help.xml` so `Get-Help` works without a
network round trip. To bootstrap a new locale, run with
`-Locales en-US, ja-JP` etc. — each new locale starts from scratch and is
yours to translate.

## License

MIT
