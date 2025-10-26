using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Configuration;

namespace ZipReplace48
{
    class Program
    {
        // 終了コード
        // 0: 何もしなかった（正常）
        // 10: 差し替えを実施（正常）
        // 1: 入力不足・前提条件NG
        // 2: 例外発生
        const int ExitNoWork = 0;
        const int ExitReplaced = 10;
        const int ExitMissing = 1;
        const int ExitError = 2;

        static int Main(string[] args)
        {
            //// ★環境に合わせて設定
            //var sourceZip = @"C:\package.zip";
            //var targetDir = @"C:\temp\Work";
            //var exeNames = new[] { "a.exe", "b.exe", "c.exe" };

            //// App.config から設定を取得
            //var sourceZip = ConfigurationManager.AppSettings["SourceZip"];
            //var targetDir = ConfigurationManager.AppSettings["TargetDir"];
            //var exeNames = (ConfigurationManager.AppSettings["ExeNames"] ?? "")
            //                  .Split(',')
            //                  .Select(x => x.Trim())
            //                  .Where(x => !string.IsNullOrEmpty(x))
            //                  .ToArray();

            //if (string.IsNullOrEmpty(sourceZip) || string.IsNullOrEmpty(targetDir) || exeNames.Length == 0)
            //{
            //    Log("App.config の設定が不足しています。");
            //    return ExitMissing;
            //}

            // --- 任意の設定ファイルを指定 ---
            var configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "myapp.config");

            if (!File.Exists(configPath))
            {
                Console.WriteLine($"設定ファイルが見つかりません: {configPath}");
                return 1;
            }

            var fileMap = new ExeConfigurationFileMap { ExeConfigFilename = configPath };
            var config = ConfigurationManager.OpenMappedExeConfiguration(fileMap, ConfigurationUserLevel.None);

            // --- 設定値を取得 ---
            var sourceZip = config.AppSettings.Settings["SourceZip"]?.Value;
            var targetDir = config.AppSettings.Settings["TargetDir"]?.Value;
            var exeNamesRaw = config.AppSettings.Settings["ExeNames"]?.Value;

            var exeNames = (exeNamesRaw ?? "")
                .Split(',')
                .Select(x => x.Trim())
                .Where(x => !string.IsNullOrEmpty(x))
                .ToArray();

            if (string.IsNullOrEmpty(sourceZip) || string.IsNullOrEmpty(targetDir) || exeNames.Length == 0)
            {
                Console.WriteLine("設定値が不足しています。");
                return 1;
            }

            // 多重起動防止（対象ディレクトリ単位で排他）
            string mutexName = @"Global\ZipReplace48_" + targetDir.Replace('\\', '_').Replace(':', '_');
            using (var mutex = new Mutex(false, mutexName))
            {
                if (!mutex.WaitOne(0))
                {
                    Log("別のインスタンスが実行中のため中断します。");
                    return ExitNoWork;
                }
            }

            try
            {
                // --- 条件チェック ---
                if (!Directory.Exists(targetDir))
                {
                    Log("対象フォルダが存在しないため、処理を行いません。");
                    return ExitNoWork;
                }

                var exePaths = exeNames.Select(n => Path.Combine(targetDir, n)).ToArray();
                if (!exePaths.All(File.Exists))
                {
                    Log("対象フォルダに a.exe, b.exe, c.exe のいずれかが存在しないため、処理を行いません。");
                    return ExitNoWork;
                }

                if (IsAnyProcessRunning(exePaths))
                {
                    Log("a.exe / b.exe / c.exe のいずれかのプロセスが稼働中のため、処理を行いません。");
                    return ExitNoWork;
                }

                if (!File.Exists(sourceZip))
                {
                    Log("元ZIP が見つからないため、処理を行いません。");
                    return ExitMissing;
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
                    return ExitNoWork;
                }

                // クリティカル区間直前で再チェック（Race対策）
                if (IsAnyProcessRunning(exePaths))
                {
                    Log("チェック後にプロセスが稼働開始したため、中断します。");
                    return ExitNoWork;
                }

                // --- 差し替え ---
                ReplaceFolderWithZipSafe(sourceZip, targetDir, Path.GetFileName(sourceZip), exePaths);

                Log("差し替えが完了しました。");
                return ExitReplaced;
            }
            catch (Exception ex)
            {
                Log("エラーが発生しました: " + ex);
                return ExitError;
            }
        }

        // exe のフルパスに紐づけて稼働判定（名前衝突を避ける）
        // アクセスできないケースは保守的に「稼働中とみなす」
        static bool IsAnyProcessRunning(string[] fullExePaths)
        {
            var targets = fullExePaths
                .Select(p => new { Name = Path.GetFileNameWithoutExtension(p), Path = NormalizeFullPath(p) })
                .Where(x => !string.IsNullOrWhiteSpace(x.Name))
                .GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase);

            foreach (var g in targets)
            {
                Process[] procs = Array.Empty<Process>();
                try { procs = Process.GetProcessesByName(g.Key); } catch { }

                foreach (var p in procs)
                {
                    try
                    {
                        var exePath = p.MainModule?.FileName;
                        if (exePath == null) return true; // 情報が取れない場合は保守的にtrue
                        var exeNorm = NormalizeFullPath(exePath);
                        foreach (var t in g)
                            if (PathEquals(exeNorm, t.Path)) return true;
                    }
                    catch
                    {
                        return true; // アクセス失敗時も保守的にtrue
                    }
                    finally
                    {
                        try { p.Dispose(); } catch { }
                    }
                }
            }
            return false;
        }

        static string NormalizeFullPath(string path)
        {
            var full = Path.GetFullPath(path);
            // 末尾セパレータは削る（比較一貫性のため）
            return full.TrimEnd(Path.DirectorySeparatorChar);
        }

        static bool PathEquals(string a, string b) =>
            string.Equals(NormalizeFullPath(a), NormalizeFullPath(b), StringComparison.OrdinalIgnoreCase);

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

                // ステージング → 対象へコピー（Move ではなく Copy による上書き）
                CopyAll(new DirectoryInfo(tempStage), new DirectoryInfo(targetDir));
            }
            finally
            {
                TryDeleteDirectory(tempBase);
            }
        }

        // Zip Slip / Zip Bomb 防止つき展開
        static void SafeExtractZip(string zipPath, string extractDir)
        {
            var basePath = Path.GetFullPath(extractDir);
            if (!basePath.EndsWith(Path.DirectorySeparatorChar.ToString()))
                basePath += Path.DirectorySeparatorChar; // 末尾セパレータを保証

            const long MaxTotal = 2L * 1024 * 1024 * 1024; // 2 GB
            const long MaxEntry = 512L * 1024 * 1024;      // 512 MB
            long total = 0;

            using (var zip = ZipFile.OpenRead(zipPath))
            {
                foreach (var entry in zip.Entries)
                {
                    // ZIPは'/'区切りなので正規化
                    var entryName = entry.FullName.Replace('/', Path.DirectorySeparatorChar);

                    // 空エントリはスキップ
                    if (string.IsNullOrEmpty(entryName))
                        continue;

                    // 絶対パス／ドライブ直指定は禁止
                    if (Path.IsPathRooted(entryName))
                        throw new InvalidDataException("無効なZIPエントリ（絶対パス）: " + entry.FullName);

                    var combined = Path.GetFullPath(Path.Combine(basePath, entryName));

                    // ベース配下に収まっているか（末尾セパレータ保護付き）
                    if (!combined.StartsWith(basePath, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("無効なZIPエントリ（パストラバーサルの可能性）: " + entry.FullName);

                    // ディレクトリエントリ？
                    if (entry.FullName.EndsWith("/", StringComparison.Ordinal))
                    {
                        Directory.CreateDirectory(combined);
                        continue;
                    }

                    // Zip Bomb 対策（サイズ上限チェック）
                    var len = entry.Length; // 圧縮前サイズ（展開後サイズの見込み）
                    if (len < 0 || len > MaxEntry)
                        throw new InvalidDataException("ZIP内のファイルが大きすぎます: " + entry.FullName);
                    total += len;
                    if (total > MaxTotal)
                        throw new InvalidDataException("ZIPの総サイズが許容値を超えています。");

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
            // ファイルをコピー
            foreach (var file in source.EnumerateFiles("*", SearchOption.AllDirectories))
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

        // ログはUTCで統一（O=ISO8601）
        static void Log(string message) =>
            Console.WriteLine("[{0:O} UTC] {1}", DateTime.UtcNow, message);
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
