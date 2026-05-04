# MailDrive テスト手順

## 対応サーバー

### IMAP (Basic Auth)

| サービス | ホスト | ポート | 備考 |
|---|---|---|---|
| **Gmail** | `imap.gmail.com` | 993 | アプリパスワードが必要 (2段階認証 ON → アプリパスワード生成) |
| **Yahoo** | `imap.mail.yahoo.com` | 993 | アプリパスワードが必要 |
| **iCloud** | `imap.mail.me.com` | 993 | アプリ用パスワード |
| **Fastmail** | `imap.fastmail.com` | 993 | アプリパスワード |

### POP3 (Basic Auth)

| サービス | ホスト | ポート | 備考 |
|---|---|---|---|
| **Gmail** | `pop.gmail.com` | 995 | アプリパスワード + POP 有効化が必要 |
| **Yahoo** | `pop.mail.yahoo.com` | 995 | アプリパスワードが必要 |

### SMTP

| サービス | ホスト | ポート | 備考 |
|---|---|---|---|
| **Gmail** | `smtp.gmail.com` | 587 | STARTTLS |
| **Yahoo** | `smtp.mail.yahoo.com` | 587 | STARTTLS |
| **Fastmail** | `smtp.fastmail.com` | 587 | STARTTLS |

### Basic Auth が使えない (OAuth2 必須)

| サービス | 備考 |
|---|---|
| **Outlook / Microsoft 365** | 2023年に Basic Auth 廃止。XOAUTH2 が必要。現状の実装では未対応 |

### ローカルテスト用

| 方法 | 備考 |
|---|---|
| **hMailServer** | Windows 用の無料メールサーバー。IMAP/POP3/SMTP すべて対応 |
| **GreenMail** (Docker) | `docker run -p 3993:3993 -p 3995:3995 -p 3587:3587 greenmail/standalone` |
| **Dovecot** (Docker) | 本格的な IMAP/POP3 サーバー |

## ビルド & デプロイ

```powershell
# Release ビルド + モジュールパスにデプロイ
cd C:\MyProj\MailDrive
.\deploy.ps1

# Debug ビルドの場合
.\deploy.ps1 -Configuration Debug
```

## テスト手順 (IMAP — Gmail の場合)

### 1. アプリパスワードの準備

1. Google アカウントで2段階認証を有効化
2. https://myaccount.google.com/apppasswords でアプリパスワードを生成
3. 生成された16文字のパスワードを控える

### 2. モジュールのロードと接続

```powershell
Import-Module MailDrive
New-MailDrive -Host imap.gmail.com -Username 'user@gmail.com' -Password 'xxxx xxxx xxxx xxxx' -SmtpHost smtp.gmail.com
```

### 3. 基本操作

```powershell
# フォルダ一覧
cd Imap:\
ls

# メッセージ一覧
cd INBOX
ls
ls -First 100           # 最新100件

# メッセージ詳細
Get-Item '.\123_sender_Subject.eml'

# メール本文を表示
Get-Content '.\123_sender_Subject.eml'

# .eml として開く
Invoke-Item '.\123_sender_Subject.eml'

# Tab 補完
cd I<Tab>               # INBOX に補完されるか
ls .\1<Tab>             # メッセージ名が補完されるか
```

### 4. フォルダ操作

```powershell
mkdir TestFolder
rmdir TestFolder
```

### 5. メッセージ削除

```powershell
rm '.\123_sender_Subject.eml'
```

### 6. メール送信

```powershell
Send-Mail -To 'someone@example.com' -Subject 'Test' -Body 'Hello from MailDrive!'
Send-Mail -To 'someone@example.com' -Subject 'With attachment' -Body 'See attached.' -Attachments C:\tmp\report.pdf
```

### 7. ドライブ解除

```powershell
Remove-PSDrive Imap
```

## テスト手順 (POP3)

```powershell
# 接続
New-PopDrive -Host pop.gmail.com -Username 'user@gmail.com' -Password 'xxxx xxxx xxxx xxxx'

# メッセージ一覧
cd Pop:\
ls

# メール本文を表示
Get-Content '.\1_sender_Subject.eml'

# 削除 (即コミット — Disconnect + Reconnect)
rm '.\1_sender_Subject.eml'

# ドライブ解除
Remove-PSDrive Pop
```

## テスト手順 (設定ファイル)

```powershell
# 設定ファイルの作成・編集
Open-MailConfig

# 設定ファイルからドライブを一括マウント
Import-MailConfig

# 強制リロード (既存ドライブを再作成)
Import-MailConfig -Force
```

## 確認ポイント (IMAP)

- [ ] `New-MailDrive` で接続が成功する
- [ ] `ls` でフォルダ一覧が表示される (Name, Messages, Unread, Subfolders, Attributes)
- [ ] `cd INBOX` → `ls` でメッセージ一覧が表示される (Uid, Date, From, Subject, Read, Size)
- [ ] Tab 補完でフォルダ名・メッセージ名が補完される
- [ ] `Get-Content` でメール本文が表示される
- [ ] `Invoke-Item` で .eml が既定アプリで開く
- [ ] `mkdir` / `rmdir` でフォルダの作成・削除ができる
- [ ] `rm` でメッセージの削除ができる
- [ ] `Remove-PSDrive` で接続が切断される
- [ ] Gmail のサブフォルダ (`[Gmail]\All Mail` 等) にナビゲートできる

## 確認ポイント (POP3)

- [ ] `New-PopDrive` で接続が成功する
- [ ] `ls` でメッセージ一覧が表示される
- [ ] `Get-Content` でメール本文が表示される
- [ ] `rm` でメッセージの削除ができる (Disconnect + Reconnect)
- [ ] Tab 補完でメッセージ名が補完される

## 確認ポイント (SMTP)

- [ ] `Send-Mail` でメールが送信される
- [ ] `-Attachments` で添付ファイル付き送信ができる
- [ ] `-Html` で HTML メールが送信される
- [ ] `-Path` で使用ドライブを指定できる

## 確認ポイント (設定ファイル)

- [ ] `Open-MailConfig` で設定ファイルが作成され notepad で開く
- [ ] `Import-MailConfig` で IMAP / POP3 ドライブが自動マウントされる
- [ ] モジュールインポート時に `InitializeDefaultDrives` で自動マウントされる
- [ ] `-Force` で既存ドライブが再作成される
