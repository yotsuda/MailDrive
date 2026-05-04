@{
    RootModule           = 'MailDrive.dll'
    ModuleVersion        = '0.3.0'
    GUID                 = 'f3a1b2c4-5d6e-7f89-0abc-def123456789'
    Author               = 'Yoshifumi Tsuda'
    Copyright            = '(c) 2026 Yoshifumi Tsuda. All rights reserved.'
    Description          = 'PowerShell NavigationCmdletProvider for IMAP/POP3 mailbox navigation and SMTP sending. Navigate your mailbox like a filesystem (cd, dir, cat, mv, del). Supports OAuth2 for Microsoft 365.'

    PowerShellVersion    = '7.4'
    CompatiblePSEditions = @('Core')

    CmdletsToExport      = @(
        'New-MailDrive'
        'New-PopDrive'
        'Get-MailDrive'
        'Import-MailConfig'
        'Open-MailConfig'
        'Send-Mail'
        'Submit-MailReply'
        'Submit-MailForward'
        'Export-MailAttachment'
        'Export-MailMessage'
        'New-MailDraft'
        'Get-MailQuota'
        'Get-MailConfigPath'
    )

    FunctionsToExport    = @()
    AliasesToExport      = @()

    FormatsToProcess     = @('MailDrive.Format.ps1xml')

    HelpInfoURI          = 'https://github.com/yotsuda/MailDrive/blob/main/docs/help/'

    PrivateData = @{
        PSData = @{
            Tags         = @('IMAP', 'POP3', 'SMTP', 'Email', 'Provider', 'PSDrive', 'MailKit', 'OAuth2', 'Gmail', 'Outlook')
            LicenseUri   = 'https://github.com/yotsuda/MailDrive/blob/main/LICENSE'
            ProjectUri   = 'https://github.com/yotsuda/MailDrive'
            ReleaseNotes = @'
0.3.0 -- first PowerShell Gallery release
- IMAP / POP3 navigation as a PSDrive (cd, dir, cat, mv, del)
- SMTP send / reply / forward with attachments
- OAuth2 (Device Code Flow) support for Microsoft 365
- Gmail native search via X-GM-RAW
- Bulk export to .eml with incremental skip
- Attachment extraction (server + local .eml, with embedded images)
- Folder operations: create, rename, delete, subscribe
- Flag management: IsRead, IsFlagged, IsAnswered, IsDraft
- Drafts via New-MailDraft
- Quota query via Get-MailQuota
- Connection inspection via Get-MailDrive (shows Username / Host / Port / Ssl / Auth / SMTP / connection state)
- Conversation threads as virtual sub-folder ("dir Gmail:\INBOX\Threads" lists thread containers; descend to see messages grouped by Gmail X-GM-THRID)
- MailMessageInfo now exposes MessageId / InReplyTo / References / ThreadId for cross-thread reasoning
- HasAttachments now correctly reflects MIME structure (was always False before)
- Config-file-based drive auto-mount (Import-MailConfig)

Renames since pre-release:
- New-ImapDrive  ->  New-MailDrive  (IMAP is the primary protocol)
- New-PopDrive remains as-is for POP3

Performance:
- Cold "dir" on root listing ~47x faster on Gmail-like accounts
  (uses LIST \HasChildren attribute instead of N+1 GetSubfolders calls)

Bug fixes:
- "dir -First N" now honors the limit on cache hits
'@
        }
    }
}
