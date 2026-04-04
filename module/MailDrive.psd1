@{
    RootModule           = 'MailDrive.dll'
    ModuleVersion        = '0.2.0'
    GUID                 = 'f3a1b2c4-5d6e-7f89-0abc-def123456789'
    Author               = 'Yoshifumi Tsuda'
    Description          = 'PowerShell NavigationCmdletProvider for IMAP/POP3 mailbox navigation and SMTP sending'

    PowerShellVersion    = '7.4'
    CompatiblePSEditions = @('Core')

    CmdletsToExport      = @(
        'New-ImapDrive'
        'New-PopDrive'
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

    PrivateData = @{
        PSData = @{
            Tags = @('IMAP', 'POP3', 'SMTP', 'Email', 'Provider', 'PSDrive', 'MailKit')
        }
    }
}
