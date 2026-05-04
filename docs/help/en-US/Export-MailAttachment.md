---
external help file: MailDrive.dll-Help.xml
Module Name: MailDrive
online version: https://github.com/yotsuda/MailDrive/blob/main/docs/help/en-US/Export-MailAttachment.md
schema: 2.0.0
---

# Export-MailAttachment

## SYNOPSIS
{{ Fill in the Synopsis }}

## SYNTAX

### FromMessage
```
Export-MailAttachment -Message <MailMessageInfo> [[-OutputDirectory] <String>] [-Name <String>]
 [-IncludeEmbedded] [-ProgressAction <ActionPreference>] [-WhatIf] [-Confirm] [<CommonParameters>]
```

### FromFile
```
Export-MailAttachment -File <FileInfo> [[-OutputDirectory] <String>] [-Name <String>] [-IncludeEmbedded]
 [-ProgressAction <ActionPreference>] [-WhatIf] [-Confirm] [<CommonParameters>]
```

## DESCRIPTION
{{ Fill in the Description }}

## EXAMPLES

### Example 1
```powershell
PS C:\> {{ Add example code here }}
```

{{ Add example description here }}

## PARAMETERS

### -Confirm
Prompts you for confirmation before running the cmdlet.

```yaml
Type: SwitchParameter
Parameter Sets: (All)
Aliases: cf

Required: False
Position: Named
Default value: None
Accept pipeline input: False
Accept wildcard characters: False
```

### -File
{{ Fill File Description }}

```yaml
Type: FileInfo
Parameter Sets: FromFile
Aliases:

Required: True
Position: Named
Default value: None
Accept pipeline input: True (ByValue)
Accept wildcard characters: False
```

### -IncludeEmbedded
Include inline/embedded images (e.g.
CID-referenced images in HTML mail).

```yaml
Type: SwitchParameter
Parameter Sets: (All)
Aliases:

Required: False
Position: Named
Default value: None
Accept pipeline input: False
Accept wildcard characters: False
```

### -Message
{{ Fill Message Description }}

```yaml
Type: MailMessageInfo
Parameter Sets: FromMessage
Aliases:

Required: True
Position: Named
Default value: None
Accept pipeline input: True (ByValue)
Accept wildcard characters: False
```

### -Name
{{ Fill Name Description }}

```yaml
Type: String
Parameter Sets: (All)
Aliases:

Required: False
Position: Named
Default value: None
Accept pipeline input: False
Accept wildcard characters: False
```

### -OutputDirectory
{{ Fill OutputDirectory Description }}

```yaml
Type: String
Parameter Sets: (All)
Aliases: Destination

Required: False
Position: 0
Default value: None
Accept pipeline input: False
Accept wildcard characters: False
```

### -WhatIf
Shows what would happen if the cmdlet runs.
The cmdlet is not run.

```yaml
Type: SwitchParameter
Parameter Sets: (All)
Aliases: wi

Required: False
Position: Named
Default value: None
Accept pipeline input: False
Accept wildcard characters: False
```

### -ProgressAction
{{ Fill ProgressAction Description }}

```yaml
Type: ActionPreference
Parameter Sets: (All)
Aliases: proga

Required: False
Position: Named
Default value: None
Accept pipeline input: False
Accept wildcard characters: False
```

### CommonParameters
This cmdlet supports the common parameters: -Debug, -ErrorAction, -ErrorVariable, -InformationAction, -InformationVariable, -OutVariable, -OutBuffer, -PipelineVariable, -Verbose, -WarningAction, and -WarningVariable. For more information, see [about_CommonParameters](http://go.microsoft.com/fwlink/?LinkID=113216).

## INPUTS

### MailDrive.Models.MailMessageInfo

### System.IO.FileInfo

## OUTPUTS

### System.IO.FileInfo

## NOTES

## RELATED LINKS
