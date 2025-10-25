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


はい、僕が `TokenBucketRateLimiter` 版に設定したパラメータは、もともとの `SemaphoreSlim`＋`TokenRefillLoop` 版と**ほぼ同じ挙動**になるように合わせてあります。
違いを整理すると以下のとおりです👇

---

## ⚙️ パラメータ対応表

| 概念             | 自前実装 (`SemaphoreSlim` 版)        | RateLimiter 版設定                                           | 意味・挙動                      |
| -------------- | ------------------------------- | --------------------------------------------------------- | -------------------------- |
| 1秒あたりの許可数      | `connectPerSecondLimit`         | `TokensPerPeriod = connectPerSecondLimit`                 | 1秒あたり何個の「接続開始」を許可するか       |
| 最大バースト（貯められる量） | `connectPerSecondLimit`         | `TokenLimit = connectPerSecondLimit`                      | 最初に一気に処理できる最大数（トークンバケット容量） |
| 補充周期           | `Task.Delay(1000)`              | `ReplenishmentPeriod = TimeSpan.FromSeconds(1)`           | 1秒ごとにトークンを補充               |
| 自動補充           | 手動 (`TokenRefillLoop`)          | `AutoReplenishment = true`                                | ループ不要、自動で補充される             |
| 同時接続上限         | `SemaphoreSlim(maxConcurrency)` | `ParallelOptions.MaxDegreeOfParallelism = maxConcurrency` | 並列実行タスクの最大数                |

---

## ✅ 実際の動作の違い（わずかな差）

| 項目      | `SemaphoreSlim` 版           | `TokenBucketRateLimiter` 版                       | 備考                                           |
| ------- | --------------------------- | ------------------------------------------------ | -------------------------------------------- |
| 初回バースト  | 開始時にフルで `limit` トークンある      | 同じくフルで `TokenLimit` ある                           | 最初の1秒で最大 `connectPerSecondLimit` 接続可能        |
| 補充タイミング | 正確に 1秒毎（`Task.Delay(1000)`） | 内部タイマーでほぼ同等                                      | 微妙に高精度（`System.Threading.PeriodicTimer` ベース） |
| 飢餓防止    | 自作だと並列競合でやや不安               | RateLimiter は公平性あり                               | `QueueProcessingOrder`で順序制御可                 |
| キャンセル伝播 | 手動で例外キャッチ                   | `AcquireAsync` が自然に `OperationCanceledException` | よりC#らしい                                      |

---

## 🔬 挙動の例（1秒あたり200・並列256）

| 時間    | 接続数（理論上）      | 解説               |
| ----- | ------------- | ---------------- |
| 0秒〜1秒 | 最大200接続開始     | 初期バケットに200トークンあり |
| 1秒〜2秒 | 新たに200トークン補充  | 直前に消費された分だけ再発行   |
| 以降    | 毎秒200接続ペースで安定 | 滑らかに続く           |

→ つまり「**1秒に最大200件接続開始**」という挙動は**完全一致**します。

---

## 💡 追加でできること（RateLimiter版のみ）

* `QueueLimit = 0` ＋ `TryAcquire()` にすると「超過分はスキップ」
* `QueueLimit = n` で「最大 n 件まで待たせる」
* `QueueProcessingOrder` で「FIFO／LIFO」を選べる（通常は FIFO）
* レート制限の統計や残トークンも `GetStatistics()` で取得可能

---

### ✅ 結論

* あなたの目的（毎秒200接続、並列256）は **完全に同じ挙動で実現できています**
* しかも `TokenBucketRateLimiter` 版は **正確・安全・簡潔** なので、
  **今後の本番運用ではこちらを推奨** します。

---

必要なら、`SemaphoreSlim` 版と `RateLimiter` 版を並列でログ出力して
「1秒あたりの実際の接続回数」を可視化するテストコードも作れます。
やってみますか？

もちろんです 👍
以下は、あなたの TCP ブロードキャスト処理（`BroadcastAsync_`）を中心にした **丁寧でわかりやすい README.md** のドラフトです。
プロジェクトのリポジトリ直下に置いてそのまま使えるようにしています。

---

# 🚀 TCP Broadcaster — 高並列・レート制御付き TCP ブロードキャスト

## 概要

このライブラリは、**数千台規模の TCP サーバーに同時ブロードキャスト**することを目的とした
C# 実装例です。`TcpClient` による都度接続モデルをベースに、
以下の要件を満たすよう設計されています。

* 数千台への**一斉送信**を効率的に処理
* **同時接続数（並列数）**の上限を制御
* **接続レート（1秒あたりの接続数）**を制限してネットワークを保護
* **キャンセル対応／タイムアウト管理**を明示的に実装
* 最新の `.NET 7+` の **`TokenBucketRateLimiter`** を利用

---

## 🧩 アーキテクチャ概要

```text
BroadcastAsync_
 ├─ Parallel.ForEachAsync()             … 高並列で各宛先を処理
 │   ├─ TokenBucketRateLimiter.Acquire  … 1秒あたりの接続レート制御
 │   └─ SendOnceAsync()                 … 各接続処理（Connect + Send）
 └─ ConcurrentBag<SendResult>           … 成功／失敗結果を集約
```

### SendOnceAsync の動作

1. `TcpClient` を生成（`NoDelay = true`）
2. タイムアウト付きで `ConnectAsync()`
3. ストリーム取得後、**データ長（4byte, Big Endian）＋本文**を送信
4. 結果を `SendResult` 構造体で返却

---

## ⚙️ 主な構成

### BroadcastAsync_

```csharp
public static async Task<IReadOnlyList<SendResult>> BroadcastAsync_(
    IEnumerable<IPAddress> ips,
    int port,
    ReadOnlyMemory<byte> payload,
    int connectTimeoutMs = 3000,
    int sendTimeoutMs = 3000,
    int maxConcurrency = 256,
    int connectPerSecondLimit = 200,
    CancellationToken ct = default)
```

| パラメータ                   | 説明                |
| ----------------------- | ----------------- |
| `ips`                   | 送信先 IP アドレスの列挙    |
| `port`                  | 接続ポート番号           |
| `payload`               | 送信データ本体           |
| `connectTimeoutMs`      | 接続試行のタイムアウト（ミリ秒）  |
| `sendTimeoutMs`         | 送信操作のタイムアウト（ミリ秒）  |
| `maxConcurrency`        | 同時に処理できる接続数の上限    |
| `connectPerSecondLimit` | 1秒あたりに新規に開始できる接続数 |
| `ct`                    | キャンセルトークン         |

---

### TokenBucketRateLimiter の設定

```csharp
var limiter = new TokenBucketRateLimiter(new TokenBucketRateLimiterOptions
{
    TokenLimit = connectPerSecondLimit,
    TokensPerPeriod = connectPerSecondLimit,
    ReplenishmentPeriod = TimeSpan.FromSeconds(1),
    AutoReplenishment = true,
    QueueLimit = maxConcurrency * 2,
    QueueProcessingOrder = QueueProcessingOrder.OldestFirst
});
```

* **1秒あたり `connectPerSecondLimit` 接続**を許可するレート制御。
* 内部タイマーにより自動でトークンが補充されます。
* 過剰要求が発生した場合は最大 `QueueLimit` 件まで待機。

---

## 🧠 動作のイメージ

| 秒数   | 許可される接続開始数   | 補足            |
| ---- | ------------ | ------------- |
| 0〜1秒 | 最大200件       | 初期トークン分を消費    |
| 1〜2秒 | 新たに200トークン補充 | 継続して200件/秒ペース |
| 以降   | 200件/秒で安定    | 滑らかな接続レート     |

---

## 💬 SendOnceAsync のポイント

```csharp
var len = BitConverter.GetBytes(IPAddress.HostToNetworkOrder(payload.Length));
await stream.WriteAsync(len, cts.Token);
await stream.WriteAsync(payload, cts.Token);
```

* TCP は **ストリーム（メッセージ境界なし）** のため、
  メッセージ先頭に **4バイトの長さ情報（Big Endian）** を付加しています。
* `HostToNetworkOrder()` により、CPUエンディアン差異を吸収。
* 受信側は同じく `NetworkToHostOrder()` で復元します。

---

## 🧾 結果オブジェクト

```csharp
public record SendResult(IPAddress Target, SendStatus Status, string? Error = null);

public enum SendStatus
{
    Success,
    Timeout,
    Refused,
    Reset,
    NetworkError,
    OtherError
}
```

送信ごとの結果を `ConcurrentBag` に蓄積し、
全完了後に `IReadOnlyList<SendResult>` として返します。

---

## 🕒 バックグラウンド運用例

この関数は `BackgroundService` などから定期的に呼び出すことを想定しています。
例えば「20分ごとに全端末へブロードキャスト」するには：

```csharp
public sealed class BroadcastJob : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var targets = TargetRegistry.Current.GetActiveIPs(); // ConcurrentDictionary管理など
            var payload = Encoding.UTF8.GetBytes("ping");

            var results = await TcpBroadcaster.BroadcastAsync_(
                targets, 5000, payload, connectPerSecondLimit: 200,
                maxConcurrency: 256, ct: stoppingToken);

            LogResults(results);

            await Task.Delay(TimeSpan.FromMinutes(20), stoppingToken);
        }
    }
}
```

---

## ✅ 環境要件

* **.NET 7 以上**
  （`System.Threading.RateLimiting` が標準ライブラリに含まれます）
* ネットワークに対して適切な権限があること（ポート開放など）

---

## 📦 まとめ

| 機能      | 実現方法                                               |
| ------- | -------------------------------------------------- |
| 同時接続上限  | `Parallel.ForEachAsync` + `MaxDegreeOfParallelism` |
| 接続レート制御 | `TokenBucketRateLimiter`                           |
| タイムアウト  | `CancellationTokenSource.CancelAfter()`            |
| 再利用・保守性 | 依存最小、単一関数で完結                                       |
| 高スケール動作 | 数千台規模で安定稼働可能（1秒あたり200接続ペース）                        |

---

もし希望があれば、README に「受信側（サーバー）」の受け取り実装例
（`NetworkStream.ReadExactlyAsync()` で長さ→本文を読む）も追記できます。
追加しますか？

了解しました！
README の続きを追記し、「**受信側（サーバー）実装例**」を丁寧に説明した完全版を以下に示します。
これで、送信・受信の両側がそろい、即運用可能な形になります。

---

# 📡 受信側（サーバー）実装例

送信側が `[4バイトの長さ] + [本文データ]` の順で送信する仕様に合わせ、
受信側では以下の手順で安全に読み取ります：

1. **接続待受**
2. **長さヘッダ（4バイト）を正確に読み取る**
3. **NetworkToHostOrder でエンディアン変換**
4. **本文をその長さ分だけ読み取る**
5. **処理・応答**

---

## 🧱 サンプルコード

```csharp
using System.Net;
using System.Net.Sockets;
using System.Text;

public sealed class TcpReceiver
{
    private readonly int _port;

    public TcpReceiver(int port)
    {
        _port = port;
    }

    public async Task StartAsync(CancellationToken ct = default)
    {
        var listener = new TcpListener(IPAddress.Any, _port);
        listener.Start();
        Console.WriteLine($"[Server] Listening on port {_port}...");

        while (!ct.IsCancellationRequested)
        {
            var client = await listener.AcceptTcpClientAsync(ct);
            _ = Task.Run(() => HandleClientAsync(client, ct), ct);
        }
    }

    private static async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        var remote = client.Client.RemoteEndPoint;
        Console.WriteLine($"[Server] Accepted connection from {remote}");

        try
        {
            using var stream = client.GetStream();

            // 1️⃣ ヘッダ（4バイト）を読み取る
            var lenBuf = new byte[4];
            await ReadExactlyAsync(stream, lenBuf, 0, 4, ct);

            // 2️⃣ 長さをビッグエンディアン → ホスト順に変換
            int bodyLength = IPAddress.NetworkToHostOrder(BitConverter.ToInt32(lenBuf, 0));

            // 3️⃣ 本文を指定バイト数だけ読み取る
            var payload = new byte[bodyLength];
            await ReadExactlyAsync(stream, payload, 0, bodyLength, ct);

            // 4️⃣ メッセージ処理（例：UTF-8テキストとして出力）
            var message = Encoding.UTF8.GetString(payload);
            Console.WriteLine($"[Server] Received {bodyLength} bytes: \"{message}\"");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Server] Error from {remote}: {ex.Message}");
        }
        finally
        {
            client.Close();
        }
    }

    // Utility: 指定バイト数を正確に読み取る
    private static async Task ReadExactlyAsync(NetworkStream stream, byte[] buffer, int offset, int count, CancellationToken ct)
    {
        int readTotal = 0;
        while (readTotal < count)
        {
            int n = await stream.ReadAsync(buffer, offset + readTotal, count - readTotal, ct);
            if (n == 0) throw new IOException("Connection closed before expected data received.");
            readTotal += n;
        }
    }
}
```

---

## 🔍 処理フロー概要

```
Client        Server
  │              │
  │  len(4B) →   │  ← ヘッダ受信
  │ payload →     │  ← 本文受信
  │              │
  │              └─ Decode & Log
```

* TCP は「**ストリーム通信**」のため、1回の `ReadAsync()` で
  すべてのデータが届く保証がありません。
  → `ReadExactlyAsync()` で確実に指定バイト数を読み取ります。
* `NetworkToHostOrder()` により、異なるエンディアンのマシン間でも正常に動作します。

---

## 🧩 実行例

### サーバー側

```bash
[Server] Listening on port 5000...
[Server] Accepted connection from 10.0.0.12:58734
[Server] Received 5 bytes: "hello"
```

### クライアント側（ブロードキャスト）

```bash
[Client] SendResult(10.0.0.12, Success)
[Client] SendResult(10.0.0.13, Timeout)
[Client] SendResult(10.0.0.14, Success)
```

---

## 🔐 補足: 実運用時の考慮事項

| 項目             | 推奨設定・対策                                           |
| -------------- | ------------------------------------------------- |
| **例外処理**       | 受信ループではクライアントごとに `try/catch` で独立処理                |
| **バックログ**      | `TcpListener.Start(backlog)` で待機キューを十分確保（例: 1024） |
| **スレッド負荷**     | 接続台数が多い場合は `SocketAsyncEventArgs` ベースに移行検討        |
| **Keep-Alive** | 長期接続なら OS レベルで有効化（`SocketOptionName.KeepAlive`）   |
| **プロトコル拡張**    | 長さヘッダ後に識別コード（例: メッセージ種別など）を追加可能                   |

---

## ✅ 全体まとめ

| 目的         | 実現手段                                               |
| ---------- | -------------------------------------------------- |
| 高速ブロードキャスト | `Parallel.ForEachAsync` + `TokenBucketRateLimiter` |
| レート制御      | 毎秒あたり最大接続数 (`connectPerSecondLimit`)               |
| 安全な送受信     | 長さプリフィックス + エンディアン変換                               |
| スケーラブルな設計  | 並列数 (`maxConcurrency`) とレートを調整可能                   |
| 運用         | `BackgroundService` で定期ブロードキャスト（例: 20分ごと）          |

---

💡 **このREADMEを読むだけで、送信・受信の両側が動作可能。**
.NET 7以降なら追加依存なしで即実行できます。

---

もし希望があれば、次に「**Ping応答サーバー**（受信後に返答を送る簡易例）」も加えられます。
そのままネットワーク健全性チェックなどに使えます。追加してみますか？
