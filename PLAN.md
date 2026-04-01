# MailDrive 未実装機能 実装計画

## 概要

MailDrive の未実装機能をすべて実装する。優先度順にフェーズ分けし、依存関係を考慮した順序で進める。

---

## Phase 1: 添付ファイルのダウンロード ✅ 完了

**目的:** メッセージから添付ファイルをローカルに保存できるようにする。

### 1-1. `Save-Attachment` Cmdlet 新規作成

- パラメータ:
  - `-Message <MailMessageInfo>` (Mandatory, ValueFromPipeline)
  - `-OutputDirectory <string>` (デフォルト: カレントディレクトリ)
  - `-Name <string>` (特定の添付ファイル名を指定、省略時は全添付)
  - `-DriveName <string>`
- IMAP: UID でフルメッセージ取得 → MimePart を列挙 → ファイル書き出し
- POP3: インデックスでメッセージ取得 → 同様に書き出し
- 出力: 保存した `FileInfo` オブジェクトをパイプラインに返す

### 1-2. POP3 の HasAttachments 対応

- `PopDriveProvider.GetChildItems` でメッセージ取得時、ヘッダだけでなく `BodyStructure` も取得
- `HasAttachments` を正しく設定する

### 変更ファイル

- `Cmdlets/SaveAttachmentCmdlet.cs` (新規)
- `Provider/PopDriveProvider.cs`
- `Provider/PopDriveInfo.cs`
- `MailDrive.psd1` (CmdletsToExport に追加)

---

## Phase 2: フォルダ操作の拡充 ✅ 完了

### 2-1. フォルダリネーム (`Rename-Item`)

- `ImapDriveProvider` に `RenameItem` メソッドを実装
- MailKit: `folder.Rename(newParent, newName)` を使用
- キャッシュ無効化

### 2-2. ゴミ箱への移動 (`Remove-Item` の改善)

- `Remove-Item` のデフォルト動作を「Trash フォルダへ Move」に変更
- `-Force` 指定時のみ即 Expunge (現行動作)
- Trash フォルダの検出: `SpecialFolder.Trash` または名前ベースのフォールバック
- POP3 は変更なし（即削除のまま）

### 変更ファイル

- `Provider/ImapDriveProvider.cs` (RenameItem 追加、RemoveItem 改修)
- `Provider/ImapDriveInfo.cs` (Trash フォルダ検出メソッド追加)

---

## Phase 3: フラグ管理の拡充 ✅ 完了

### 3-1. `SetProperty` の拡張

- 対応プロパティを追加:
  - `IsFlagged` (bool) → `MessageFlags.Flagged`
  - `IsAnswered` (bool) → `MessageFlags.Answered`
  - `IsDraft` (bool) → `MessageFlags.Draft`
- 既存の `IsRead` → `MessageFlags.Seen` はそのまま

### 3-2. `ClearProperty` の実装

- 指定プロパティのフラグをすべてクリア（Remove flags）

### 3-3. MailMessageInfo モデル拡張

- `IsFlagged`, `IsAnswered`, `IsDraft` プロパティ追加
- `GetChildItems` のフェッチ時に `MessageSummaryItems.Flags` から設定（既存）
- `Format.ps1xml` に Flagged 列を追加

### 変更ファイル

- `Provider/ImapDriveProvider.cs` (SetProperty 拡張、ClearProperty 実装)
- `Models/MailEntities.cs`
- `module/MailDrive.Format.ps1xml`

---

## Phase 4: 下書き保存 ✅ 完了

### 4-1. `Save-Draft` Cmdlet 新規作成

- パラメータ:
  - `-To <string[]>`
  - `-Subject <string>` (Mandatory)
  - `-Body <string>` (Mandatory, ValueFromPipeline)
  - `-Cc <string[]>`, `-Bcc <string[]>`
  - `-Html` (Switch)
  - `-Attachments <string[]>`
  - `-DriveName <string>`
- MimeMessage を構築 → Drafts フォルダに `Append` (MessageFlags.Draft 付き)
- Drafts フォルダ検出: `SpecialFolder.Drafts` またはフォールバック

### 4-2. ContentWriter の実装 (オプション)

- `Set-Content` で既存の下書きを上書き保存する
- 実装: 旧メッセージ削除 → 新メッセージ Append
- スコープを下書きフォルダ内のメッセージに限定

### 変更ファイル

- `Cmdlets/SaveDraftCmdlet.cs` (新規)
- `Provider/ImapDriveProvider.cs` (ContentWriter 実装)
- `Provider/ImapDriveInfo.cs` (Drafts フォルダ検出)
- `MailDrive.psd1`

---

## Phase 5: OAuth2 / XOAUTH2 対応 ✅ 完了

**目的:** Microsoft 365 / Outlook.com への接続を可能にする。

### 5-1. OAuth2 フロー実装

- `Microsoft.Identity.Client` (MSAL) を NuGet 依存に追加
- Device Code Flow を使用（CLI 向け、ブラウザ認証）
- トークン取得 → `SaslMechanismOAuth2` で IMAP/SMTP 認証
- スコープ: `https://outlook.office365.com/IMAP.AccessAsUser.All`, `SMTP.Send`

### 5-2. トークンキャッシュ

- MSAL 標準のトークンキャッシュ（ファイルベース）を利用
- リフレッシュトークンによる自動更新

### 5-3. MailConfig の拡張

- `AuthMethod` フィールド追加: `"Password"` (デフォルト) / `"OAuth2"`
- `TenantId`, `ClientId` フィールド追加
- OAuth2 用のデフォルト ClientId を組み込み（パブリッククライアント）

### 5-4. Cmdlet の拡張

- `New-ImapDrive` / `New-PopDrive` に `-AuthMethod`, `-TenantId`, `-ClientId` パラメータ追加
- OAuth2 選択時は `-Password` を不要にし、Device Code Flow を起動

### 変更ファイル

- `MailDrive.csproj` (MSAL パッケージ追加)
- `Provider/MailDriveInfoBase.cs` (OAuth2 認証ロジック)
- `Provider/ImapDriveInfo.cs`
- `Provider/PopDriveInfo.cs`
- `Models/MailConfig.cs`
- `Cmdlets/NewImapDriveCmdlet.cs`
- `Cmdlets/NewPopDriveCmdlet.cs`
- `deploy.ps1` (MSAL DLL コピー追加)

---

## Phase 6: その他の機能 ✅ 完了

### 6-1. フォルダ購読管理

- `Subscribe-MailFolder` / `Unsubscribe-MailFolder` Cmdlet
- または `SetProperty` で `IsSubscribed` プロパティとして公開
- MailKit: `folder.Subscribe()` / `folder.Unsubscribe()`

### 6-2. クォータ確認

- `Get-MailQuota` Cmdlet
- MailKit: `client.GetQuota(folder)` → 使用量 / 上限を表示
- 出力: カスタムオブジェクト `{ StorageUsed, StorageLimit, MessageCount, MessageLimit }`

### 6-3. IMAP SEARCH の拡張（フラグ検索）

- 既存の Search クエリに追加:
  - `flagged` → `SearchQuery.Flagged`
  - `answered` → `SearchQuery.Answered`
  - `draft` → `SearchQuery.Draft`
  - `has:attachment` → `SearchQuery.HasAttachments` (MailKit 非標準の場合は BodyStructure フィルタ)

### 変更ファイル

- `Cmdlets/MailFolderCmdlets.cs` (新規: Subscribe/Unsubscribe/Quota)
- `Provider/ImapDriveProvider.cs` (Search 拡張)
- `MailDrive.psd1`

---

## 実装順序の根拠

| Phase | 依存 | 理由 |
|-------|------|------|
| 1. 添付ファイル | なし | 最も実用的。他の機能に依存しない |
| 2. フォルダ操作 | なし | Provider の基本機能の補完 |
| 3. フラグ管理 | なし | モデル拡張が Phase 4 の前提 |
| 4. 下書き | Phase 3 | Draft フラグが必要 |
| 5. OAuth2 | なし | 独立だが影響範囲が大きく後半に配置 |
| 6. その他 | Phase 3 | フラグ検索は Phase 3 のモデル拡張が前提 |

---

## テスト方針

- 各 Phase 完了時に `TESTING.md` へテスト項目を追加
- Gmail (IMAP/POP3) + Fastmail で検証
- Phase 5 完了後に Microsoft 365 で OAuth2 検証
- 既存テスト項目のリグレッション確認

## 新規 Cmdlet 一覧 (最終)

| Cmdlet | Phase |
|--------|-------|
| `Save-Attachment` | 1 |
| `Save-Draft` | 4 |
| `Subscribe-MailFolder` | 6 |
| `Unsubscribe-MailFolder` | 6 |
| `Get-MailQuota` | 6 |
