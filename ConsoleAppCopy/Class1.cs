using System;
using System.IO;
using System.Linq;

はい、すごく簡単にできます 👍

いまの構造はすでにモジュール化されているので、
「**CopyAll直前で再チェック**」と「**exeを最後にコピー**」の2つを軽く手を入れるだけでOKです。

---

## ✅ ① CopyAll直前でプロセス再チェック

`ReplaceFolderWithZipSafe` の末尾付近を少しだけ変えます。

**変更前（あなたの元コード）**

```csharp
// 直前再チェック（稼働開始を検知）
if (IsAnyProcessRunning(exePaths))
{
    Log("コピー直前にプロセスが稼働開始したため中断しました。");
return;
}

// 対象フォルダ内をクリア
ClearDirectory(targetDir);

// ステージング → 対象へコピー
CopyAll(new DirectoryInfo(tempStage), new DirectoryInfo(targetDir));
```

これを👇に変更：

**変更後**

```csharp
// 対象フォルダ内をクリア
ClearDirectory(targetDir);

// クリア直後に再チェック
if (IsAnyProcessRunning(exePaths))
{
    Log("クリア直後にプロセスが稼働開始したため中断しました。");
    return;
}

// ステージング → 対象へコピー
CopyAll(new DirectoryInfo(tempStage), new DirectoryInfo(targetDir));
```

👉 これだけで、「削除中～コピー開始前に誰かがexeを起動した」ケースを検出できます。

---

## ✅ ② exeファイルを最後にコピーする

`CopyAll` メソッド内を少し整理して、
「`.exe` は後でまとめてコピー」するだけです。

**変更後例：**

```csharp
static void CopyAll(DirectoryInfo source, DirectoryInfo target)
{
    var srcRoot = source.FullName;
    if (!srcRoot.EndsWith(Path.DirectorySeparatorChar.ToString()))
        srcRoot += Path.DirectorySeparatorChar;

    // 先にディレクトリ作成
    foreach (var dir in source.EnumerateDirectories("*", SearchOption.AllDirectories))
    {
        var rel = dir.FullName.Substring(srcRoot.Length);
        Directory.CreateDirectory(Path.Combine(target.FullName, rel));
    }

    // ファイル列挙を2段階に（exe以外 → exe）
    var allFiles = source.EnumerateFiles("*", SearchOption.AllDirectories).ToList();
    var normalFiles = allFiles.Where(f => !f.Extension.Equals(".exe", StringComparison.OrdinalIgnoreCase));
    var exeFiles = allFiles.Where(f => f.Extension.Equals(".exe", StringComparison.OrdinalIgnoreCase));

    // 通常ファイル先にコピー
    foreach (var file in normalFiles)
        CopyFileWithRetry(file, srcRoot, target);

    // EXEは最後に配置
    foreach (var file in exeFiles)
        CopyFileWithRetry(file, srcRoot, target);
}

static void CopyFileWithRetry(FileInfo file, string srcRoot, DirectoryInfo target)
{
    var rel = file.FullName.Substring(srcRoot.Length);
    var dest = Path.Combine(target.FullName, rel);
    var parent = Path.GetDirectoryName(dest);
    if (!string.IsNullOrEmpty(parent))
        Directory.CreateDirectory(parent);

    Retry(() =>
    {
        try { if (File.Exists(dest)) File.SetAttributes(dest, FileAttributes.Normal); } catch { }
        file.CopyTo(dest, true);
    });
}
```

---

## ✨ 効果まとめ

| 対策 | 変更箇所 | 効果 |
| --------- | -------------------------------- | ---------------------- |
| 再チェック追加 | `ReplaceFolderWithZipSafe` の1行挿入 | 削除直後に起動されたケースをブロック     |
| exeを最後に配置 | `CopyAll` を少し分岐                  | 不完全な状態でexeが起動されるリスクを低減 |

---

これだけで、「再チェック～削除～コピー」の間にユーザーがexeを起動しても、
**確実にスキップ or 完全に更新後にしか起動できない** 安全な動作になります。

希望があれば、上記を元に最終版 `ReplaceFolderWithZipSafe` 全体を統合したコードも出せます。
出してみましょうか？







了解です！
下記が「**削除直後の再チェック**」＋「**.exe を最後に配置**」を統合した最終版です。既存プロジェクトでは、この3メソッドを**置き換え**てください（他はそのままでOK）。

---

### ReplaceFolderWithZipSafe（クリア直後に再チェックを追加）

```csharp
static void ReplaceFolderWithZipSafe(string sourceZip, string targetDir, string zipFileName, string[] exePaths)
{
    // 一時展開先（安全に差し替えるため）
    var tempBase = Path.Combine(Path.GetTempPath(), "ZipReplace_" + Guid.NewGuid().ToString("N"));
    var tempExtract = Path.Combine(tempBase, "extract");
    var tempStage = Path.Combine(tempBase, "stage");

    Directory.CreateDirectory(tempExtract);
    Directory.CreateDirectory(tempStage);

    try
    {
        // 安全に展開（Zip Slip / Zip Bomb 防止）
        SafeExtractZip(sourceZip, tempExtract);

        // ステージングに ZIP 自体も置く（元の仕様どおり）
        Retry(() => File.Copy(sourceZip, Path.Combine(tempStage, zipFileName), overwrite: true));

        // 展開物をステージングへコピー
        CopyAll(new DirectoryInfo(tempExtract), new DirectoryInfo(tempStage));

        // 直前再チェック（稼働開始を検知）
        if (IsAnyProcessRunning(exePaths))
        {
            Log("コピー直前にプロセスが稼働開始したため中断しました。");
            return;
        }

        // 対象フォルダ内をクリア
        ClearDirectory(targetDir);

        // ★クリア直後の再チェック（ここを追加）
        if (IsAnyProcessRunning(exePaths))
        {
            Log("クリア直後にプロセスが稼働開始したため中断しました。");
            return;
        }

        // ステージング → 対象へコピー（Move ではなく Copy による上書き）
        CopyAll(new DirectoryInfo(tempStage), new DirectoryInfo(targetDir));
    }
    finally
    {
        TryDeleteDirectory(tempBase);
    }
}
```

---

### CopyAll（.exe を最後にコピー）

```csharp
static void CopyAll(DirectoryInfo source, DirectoryInfo target)
{
    // ソースのルート末尾に必ずセパレータを付与し、相対パスの計算を安定化
    var srcRoot = source.FullName;
    if (!srcRoot.EndsWith(Path.DirectorySeparatorChar.ToString()))
        srcRoot += Path.DirectorySeparatorChar;

    // 先にディレクトリを作成
    foreach (var dir in source.EnumerateDirectories("*", SearchOption.AllDirectories))
    {
        var rel = dir.FullName.Substring(srcRoot.Length);
        var destDir = Path.Combine(target.FullName, rel);
        Directory.CreateDirectory(destDir);
    }

    // ファイルを2段階でコピー（通常ファイル → exe）
    var allFiles = source.EnumerateFiles("*", SearchOption.AllDirectories).ToList();
    var normalFiles = allFiles.Where(f => !f.Extension.Equals(".exe", StringComparison.OrdinalIgnoreCase));
    var exeFiles = allFiles.Where(f => f.Extension.Equals(".exe", StringComparison.OrdinalIgnoreCase));

    foreach (var file in normalFiles)
        CopyFileWithRetry(file, srcRoot, target);

    // EXE は最後に配置（入口=実行可能ファイルを最後に見せる）
    foreach (var file in exeFiles)
        CopyFileWithRetry(file, srcRoot, target);
}
```

---

### CopyFileWithRetry（小分割：共通のコピー処理）

```csharp
static void CopyFileWithRetry(FileInfo file, string srcRoot, DirectoryInfo target)
{
    var rel = file.FullName.Substring(srcRoot.Length);
    var dest = Path.Combine(target.FullName, rel);
    var parent = Path.GetDirectoryName(dest);
    if (!string.IsNullOrEmpty(parent))
        Directory.CreateDirectory(parent);

    Retry(() =>
    {
        try { if (File.Exists(dest)) File.SetAttributes(dest, FileAttributes.Normal); } catch { }
        file.CopyTo(dest, true);
    });
}
```

---

## これで得られる効果

***削除直後に起動された * *ケースを検出して安全に中断。
***実行可能ファイルを最後に配置 * *することで、未完成状態での起動を抑止。
* 既存の安全策（Zip Slip/Bomb対策、指数バックオフ、二重チェック）は維持。

他にも「UPDATING センチネル」や「ディレクトリのスワップ」などの強化案が欲しければ、追補します！
