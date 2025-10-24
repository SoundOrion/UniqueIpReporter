## 🧩 全体概要

この一連のコードは、**TCP 接続を受け付けて、接続してきたクライアントの「IPアドレス」を一意に管理・監視するサーバーアプリケーション**です。
構成は .NET の **`BackgroundService`（ホスト型サービス）** をベースにしたシンプルなマイクロサービス構造になっています。

---

## 📦 主要な構成と役割

### 1️⃣ `Program.cs`

アプリ全体のエントリーポイント。

* .NET の **Generic Host** を使って構成 (`Host.CreateApplicationBuilder`)。
* 設定クラス `UniqueIpOptions` を登録。

  * `Port = 5000`（TCP待受ポート）
  * `EntryTtl = 30分`（IPが30分間アクセスなければ削除）
  * `CleanupInterval = 5分`（クリーンアップ実行間隔）
* シングルトンの `UniqueIpStore` を登録。
* 以下3つのバックグラウンドサービスを登録：

  * `TcpListenerService`：IPを受信してストアに記録
  * `UniqueIpCleanupService`：古いIPの削除
  * `UniqueIpReporterService`：定期的に件数ログを出力
* （オプション）`WebApiService`：HTTP API で状況確認（コメントアウト中）

---

### 2️⃣ `UniqueIpStore.cs`

IPアドレスを管理する**スレッドセーフなストアクラス**。

* 内部構造：`ConcurrentDictionary<IPAddress, DateTime>`
  → IPアドレスとその最終観測時刻（UTC）を保持。
* 主なメソッド：

  * `TryAddOrTouch(IPAddress ip)`：IPを追加または「最終観測時刻」を更新。
  * `CleanupOlderThan(DateTime threshold)`：指定時刻より古いIPを削除。
  * `TrimToMaxCount(int maxCount)`：上限超過時に古い順で削除。
  * `Snapshot()`：現在のIPと時刻を辞書で返す。
  * `Count`：登録中のIP数。

➡️ スレッドセーフな実装のため、複数スレッド（Listener / Cleanup / Reporter）が同時アクセスしても安全です。

---

### 3️⃣ `TcpListenerService.cs`

TCPサーバー機能を担うサービス。

* `_opt.Port` で指定されたポートで `TcpListener` を起動。
* クライアント接続を受け入れ、非同期で処理 (`HandleClientAsync`)。
* クライアントの IP アドレスを `UniqueIpStore` に記録。
* 受信データを UTF-8 テキストとして表示。
* データ受信ごとに最終観測時刻を更新。

```plaintext
[ TcpListener ] Listening on 0.0.0.0:5000 ...
[ 192.168.0.10 ] Hello server!
```

➡️ 実運用では、受信データ処理を任意のロジック（例：コマンド解析やイベント処理）に変更可能。

---

### 4️⃣ `UniqueIpCleanupService.cs`

古いIPエントリを定期的に削除するバックグラウンドタスク。

* 5分ごと（`CleanupInterval`）に実行。
* `EntryTtl`（既定30分）より古いエントリを削除。
* 削除件数をログ出力。

```plaintext
[Cleanup] TTL=00:30:00, Interval=00:05:00
[Cleanup] Removed 3 old IPs. Remain=42
```

➡️ メモリリークを防ぐ「ガーベジコレクタ」のような役割です。

---

### 5️⃣ `UniqueIpReporterService.cs`

一定間隔でIP件数をレポートするサービス。

* 15秒ごとに `UniqueIpStore.Count` を表示。
* 単純な動作確認やモニタリングに有用。

```plaintext
[Reporter] Unique IPs: 12
```



---

### 6️⃣ `WebApiService.cs`（オプション）

HTTP経由で現在のIP一覧や統計を返す最小構成API（コメントアウト中）。

* `/unique-ips`：全IPと最終観測時刻をJSON出力。
* `/stats`：件数だけを返す。

例：

```bash
curl http://localhost:5000/stats
# => { "count": 15 }
```

➡️ Webアプリとして監視UIと連携させることも可能です。

---

## ⚙️ 処理の流れまとめ

1. アプリ起動（`Program.cs`）
   ↓
2. `TcpListenerService` が TCP 接続を受け付け
   ↓
3. 接続元 IP を `UniqueIpStore` に登録／更新
   ↓
4. `UniqueIpReporterService` が定期的に件数を出力
   ↓
5. `UniqueIpCleanupService` が古いエントリを削除
   ↓
6. （必要なら）`WebApiService` がAPI経由で状態提供
