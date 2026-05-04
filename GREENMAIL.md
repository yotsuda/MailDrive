# GreenMail - ローカルテスト用メールサーバー

## 概要

[GreenMail](https://greenmail-mail-test.github.io/greenmail/) は Java 製のテスト用メールサーバー。
SMTP / IMAP / POP3 をローカルで起動でき、MailDrive の動作確認に使える。

## 前提条件

- Java 11 以上がインストール済みであること (`java -version` で確認)

## JAR ファイルの場所

```
C:\MyProj\MailDrive\scratch\greenmail-standalone.jar
```

未ダウンロードの場合:
```powershell
Invoke-WebRequest -Uri "https://repo1.maven.org/maven2/com/icegreen/greenmail-standalone/2.1.3/greenmail-standalone-2.1.3.jar" -OutFile "C:\MyProj\MailDrive\scratch\greenmail-standalone.jar"
```

## 起動方法

```powershell
java -Dgreenmail.setup.test.all "-Dgreenmail.users=test:test@localhost" -jar C:\MyProj\MailDrive\scratch\greenmail-standalone.jar
```

- `test.all` : SMTP, IMAP, POP3 を全て起動
- `test:test@localhost` : フォーマットは `user:password@domain`（ユーザー名 `test`、パスワード `test`、ドメイン `localhost`）
- IMAP/SMTP 認証は `test` / `test`、メールアドレスは `test@localhost`

## ポート番号

| プロトコル | ポート |
|-----------|--------|
| SMTP      | 3025   |
| IMAP      | 3143   |
| POP3      | 3110   |

## MailDrive での接続

### 1. ビルド & デプロイ

```powershell
cd C:\MyProj\MailDrive
dotnet build
.\deploy.ps1
Import-Module MailDrive -Force
```

### 2. IMAP ドライブ作成

```powershell
New-MailDrive -Name GM -Host localhost -Port 3143 -Ssl None -Username test -Password test -SmtpHost localhost -SmtpPort 3025 -SmtpSsl None
```

### 3. POP3 ドライブ作成

```powershell
New-PopDrive -Name GP -Host localhost -Port 3110 -Ssl None -Username test -Password test -SmtpHost localhost -SmtpPort 3025 -SmtpSsl None
```

## テスト操作例

### テストメール送信

```powershell
Send-Mail -To test@localhost -Subject "Hello" -Body "Test message" -Path GM:
```

### メール一覧表示

```powershell
cd GM:\INBOX
dir
```

### メール本文を読む

```powershell
Get-Content "GM:\INBOX\1_test_Hello.eml"
```

### メッセージのフラグ操作

```powershell
# フラグ付与
Set-ItemProperty "GM:\INBOX\1_test_Hello.eml" -Name IsFlagged -Value $true

# フラグ解除
Clear-ItemProperty "GM:\INBOX\1_test_Hello.eml" -Name IsFlagged
```

### フォルダ操作

```powershell
# フォルダ作成
New-Item GM:\Archive -ItemType Directory

# メッセージ移動
Move-Item "GM:\INBOX\1_test_Hello.eml" GM:\Archive

# フォルダ名変更
Rename-Item GM:\Archive GM:\OldMail
```

### 添付ファイルのエクスポート

```powershell
Get-Item "GM:\INBOX\1_test_Hello.eml" | Export-MailAttachment -Destination C:\temp
```

### 下書き作成

```powershell
New-MailDraft -To someone@example.com -Subject "Draft" -Body "WIP" -Path GM:
```

### メッセージ削除

```powershell
Remove-Item "GM:\INBOX\1_test_Hello.eml"        # Trash へ移動
Remove-Item "GM:\INBOX\1_test_Hello.eml" -Force  # 完全削除
```

## 注意事項

- GreenMail は**メモリ上**で動作するため、プロセスを停止するとメールは全て消える
- SSL なし (`-Ssl None`) で接続すること (ローカルテストのため)
- POP3 では既読/未読・フラグなどの状態管理は利用不可 (プロトコル制限)
- GreenMail の SMTP は From アドレスのドメインチェックをしないため、テスト用途に最適
