using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;

namespace ZipReplace48
{
    class Program
    {
        static int Main(string[] args)
        {
            // ★環境に合わせて設定
            var sourceZip = @"C:\package.zip";
            var targetDir = @"C:\temp\Work";
            var exeNames = new[] { "a.exe", "b.exe", "c.exe" };

            try
            {
                // --- 条件チェック ---
                if (!Directory.Exists(targetDir))
                {
                    Log("対象フォルダが存在しないため、処理を行いません。");
                    return 0;
                }

                var exePaths = exeNames.Select(n => Path.Combine(targetDir, n)).ToArray();
                if (!exePaths.All(File.Exists))
                {
                    Log("対象フォルダに a.exe, b.exe, c.exe のいずれかが存在しないため、処理を行いません。");
                    return 0;
                }

                if (IsAnyProcessRunning(exePaths))
                {
                    Log("a.exe / b.exe / c.exe のいずれかのプロセスが稼働中のため、処理を行いません。");
                    return 0;
                }

                if (!File.Exists(sourceZip))
                {
                    Log("元ZIP が見つからないため、処理を行いません。");
                    return 1;
                }

                var targetZip = Path.Combine(targetDir, Path.GetFileName(sourceZip));

                // --- 新しさ比較 ---
                var srcZipTimeUtc = File.GetLastWriteTimeUtc(sourceZip);
                DateTime? targetZipTimeUtc = File.Exists(targetZip) ? File.GetLastWriteTimeUtc(targetZip) : (DateTime?)null;

                var exeTimesUtc = exePaths.Where(File.Exists).Select(File.GetLastWriteTimeUtc);
                var baselineUtc = (targetZipTimeUtc.HasValue ? exeTimesUtc.Concat(new[] { targetZipTimeUtc.Value }) : exeTimesUtc)
                                  .DefaultIfEmpty(DateTime.MinValue)
                                  .Max();

                Log($"元ZIP(UTC): {srcZipTimeUtc:O}");
                Log($"対象側基準(UTC): {baselineUtc:O}");

                if (srcZipTimeUtc <= baselineUtc)
                {
                    Log("元ZIPが新しくないため、差し替えは行いません。");
                    return 0;
                }

                // --- 差し替え ---
                ReplaceFolderWithZipSafe(sourceZip, targetDir, Path.GetFileName(sourceZip));

                Log("差し替えが完了しました。");
                return 0;
            }
            catch (Exception ex)
            {
                Log("エラーが発生しました: " + ex);
                return 2;
            }
        }

        // exe のフルパスに紐づけて稼働判定（名前衝突を避ける）
        static bool IsAnyProcessRunning(string[] fullExePaths)
        {
            var byName = fullExePaths
                .Select(p => Path.GetFileNameWithoutExtension(p))
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            foreach (var name in byName)
            {
                Process[] procs = Array.Empty<Process>();
                try { procs = Process.GetProcessesByName(name); } catch { /* 続行 */ }

                foreach (var p in procs)
                {
                    try
                    {
                        // 注意: MainModule 取得は権限やビット数差で失敗し得る
                        var exePath = p.MainModule != null ? p.MainModule.FileName : null;
                        if (exePath == null) continue;

                        foreach (var targetPath in fullExePaths)
                        {
                            if (PathEquals(exePath, targetPath)) return true;
                        }
                    }
                    catch
                    {
                        // 取得不可の場合、名前一致だけで判断（保守的に true 扱いにしてもよい）
                        // return true; // ←厳格にするならこちら
                    }
                    finally
                    {
                        try { p.Dispose(); } catch { }
                    }
                }
            }
            return false;
        }

        static bool PathEquals(string a, string b)
        {
            // 正規化して比較（末尾の \ は無視、大小無視）
            var na = Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar);
            var nb = Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar);
            return string.Equals(na, nb, StringComparison.OrdinalIgnoreCase);
        }

        static void ReplaceFolderWithZipSafe(string sourceZip, string targetDir, string zipFileName)
        {
            // 一時展開先（安全に差し替えるため）
            var tempBase = Path.Combine(Path.GetTempPath(), "ZipReplace_" + Guid.NewGuid().ToString("N"));
            var tempExtract = Path.Combine(tempBase, "extract");
            var tempStage = Path.Combine(tempBase, "stage");

            Directory.CreateDirectory(tempExtract);
            Directory.CreateDirectory(tempStage);

            try
            {
                // 安全に展開（Zip Slip 防止）
                SafeExtractZip(sourceZip, tempExtract);

                // ステージングに ZIP 自体も置く（元の仕様どおり）
                Retry(() => File.Copy(sourceZip, Path.Combine(tempStage, zipFileName), overwrite: true));

                // 展開物をステージングへコピー
                CopyAll(new DirectoryInfo(tempExtract), new DirectoryInfo(tempStage));

                // 対象フォルダ内をクリア
                ClearDirectory(targetDir);

                // ステージング → 対象へコピー（Move ではなく Copy による上書き）
                CopyAll(new DirectoryInfo(tempStage), new DirectoryInfo(targetDir));
            }
            finally
            {
                TryDeleteDirectory(tempBase);
            }
        }

        // Zip Slip 防止: 各エントリの出力先が extractDir 配下に収まるか検証して展開
        static void SafeExtractZip(string zipPath, string extractDir)
        {
            using (var zip = ZipFile.OpenRead(zipPath))
            {
                foreach (var entry in zip.Entries)
                {
                    var combined = Path.GetFullPath(Path.Combine(extractDir, entry.FullName));

                    // ディレクトリ外を指すパスは拒否
                    if (!combined.StartsWith(Path.GetFullPath(extractDir), StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("無効なZIPエントリ（パストラバーサルの可能性）: " + entry.FullName);

                    // ディレクトリエントリ
                    if (entry.FullName.EndsWith("/", StringComparison.Ordinal) || entry.FullName.EndsWith("\\", StringComparison.Ordinal))
                    {
                        Directory.CreateDirectory(combined);
                        continue;
                    }

                    var dirName = Path.GetDirectoryName(combined);
                    if (!string.IsNullOrEmpty(dirName))
                    {
                        Directory.CreateDirectory(dirName);
                    }
                    Retry(() => entry.ExtractToFile(combined, overwrite: true));
                }
            }
        }

        static void ClearDirectory(string dir)
        {
            // ファイル削除
            foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.TopDirectoryOnly))
            {
                Retry(() =>
                {
                    try { File.SetAttributes(file, FileAttributes.Normal); } catch { }
                    File.Delete(file);
                });
            }
            // サブフォルダ削除
            foreach (var sub in Directory.EnumerateDirectories(dir, "*", SearchOption.TopDirectoryOnly))
            {
                Retry(() => TryDeleteDirectory(sub));
            }
        }

        static void CopyAll(DirectoryInfo source, DirectoryInfo target)
        {
            // 先にディレクトリを作成
            foreach (var dir in source.EnumerateDirectories("*", SearchOption.AllDirectories))
            {
                var rel = dir.FullName.Substring(source.FullName.TrimEnd('\\').Length).TrimStart('\\');
                Directory.CreateDirectory(Path.Combine(target.FullName, rel));
            }
            // ファイルをコピー
            foreach (var file in source.EnumerateFiles("*", SearchOption.AllDirectories))
            {
                var rel = file.FullName.Substring(source.FullName.TrimEnd('\\').Length).TrimStart('\\');
                var dest = Path.Combine(target.FullName, rel);
                var parent = Path.GetDirectoryName(dest);
                Directory.CreateDirectory(string.IsNullOrEmpty(parent) ? target.FullName : parent);
                Retry(() =>
                {
                    try { File.SetAttributes(dest, FileAttributes.Normal); } catch { }
                    file.CopyTo(dest, true);
                });
            }
        }

        // 小さなバックオフ付きリトライ（共有違反などの軽微な失敗向け）
        static void Retry(Action action, int attempts = 5, int initialDelayMs = 80)
        {
            var delay = initialDelayMs;
            for (int i = 1; ; i++)
            {
                try
                {
                    action();
                    return;
                }
                catch (Exception) when (i < attempts)
                {
                    Thread.Sleep(delay);
                    delay *= 2;
                }
            }
        }

        static void TryDeleteDirectory(string dir)
        {
            try
            {
                if (Directory.Exists(dir))
                {
                    foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                    {
                        try { File.SetAttributes(file, FileAttributes.Normal); } catch { }
                    }
                    Directory.Delete(dir, true);
                }
            }
            catch
            {
                // 掃除失敗は致命でないので黙殺
            }
        }

        static void Log(string message) =>
            Console.WriteLine("[{0:yyyy-MM-dd HH:mm:ss}] {1}", DateTime.Now, message);
    }
}



//var psi = new ProcessStartInfo
//{
//    FileName = "cmd.exe",
//    Arguments = "/c \"\"C:\\path\\to\\ZipReplace48.exe\" >NUL 2>&1\"",
//    UseShellExecute = false,     // 必須：コンソール/シェル非使用
//    CreateNoWindow = true,       // 必須：ウィンドウ抑止
//    WindowStyle = ProcessWindowStyle.Hidden,
//    WorkingDirectory = @"C:\temp\Work"
//};
//using (var p = Process.Start(psi))
//{
//    p.WaitForExit();
//    var exitCode = p.ExitCode;
//}

//`> NUL 2 > &1` は、Windowsの **コマンドプロンプトで出力を捨てるためのリダイレクト構文** です。
//つまり、**`cmd.exe /c` で呼び出したときに一切表示も残さない**ための指定です。

//---

//### 🔧 分解して説明すると

//| 部分    | 意味                                               |
//| ----- | ------------------------------------------------ |
//| `>`   | 標準出力（普通の `Console.WriteLine` など）をどこかにリダイレクトする演算子 |
//| `NUL` | どこにも出力しない特殊デバイス（UNIXで言う `/dev/null` と同じ）         |
//| `2>`  | 標準エラー（例外や `Console.Error.WriteLine`）をどこかにリダイレクト  |
//| `&1`  | 「標準出力(1) と同じところへ送れ」という指定                         |

//したがって：

//```bat
//> NUL 2>&1
//```

//=
//✅ 標準出力を NUL に捨てる
//✅ 標準エラーも標準出力（つまり NUL）と同じ場所に捨てる
//→ 結果的に **何も表示もログも残らない完全サイレント実行** になります。

//---

//### 🧩 具体例

//```bat
//C:\path\to\ZipReplace48.exe >NUL 2>&1
//```

//これを実行すると：

//* `Console.WriteLine` の出力 → 消える
//* 例外時の `Console.Error` 出力 → 消える
//* コマンドプロンプト上にもウィンドウにも一切出ない

//---

//### 💡 つまりこう覚えると簡単

//> `>NUL 2>&1` = 「全部どこにも出さないで実行して」

//---

//サービスから `cmd.exe /c` で呼ぶ場合にこれを付けておけば、
//**画面にもログにも出ず、完全なバックグラウンド処理**になります。
