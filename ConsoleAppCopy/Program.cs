using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace ZipReplace48
{
    class Program
    {
        static void Main(string[] args)
        {
            // ★環境に合わせて設定
            // C:\ にある「とあるzipフォルダ」（=zipファイル）
            var sourceZip = @"C:\package.zip";

            // C:\temp にある「とあるフォルダ」
            // 中に「とあるzipフォルダ（=zipファイル）」と a.exe,b.exe,c.exe がある想定
            var targetDir = @"C:\temp\Work";

            // 必須の実行ファイル名
            var exeNames = new[] { "a.exe", "b.exe", "c.exe" };

            try
            {
                // --- 条件チェック ---

                // (1) 対象フォルダがない → 何もしない
                if (!Directory.Exists(targetDir))
                {
                    Log("対象フォルダが存在しないため、処理を行いません。");
                    return;
                }

                // (2) a.exe, b.exe, c.exe が全て存在しない（1つでも欠ける）→ 何もしない
                var exePaths = exeNames.Select(n => Path.Combine(targetDir, n)).ToArray();
                if (!exePaths.All(File.Exists))
                {
                    Log("対象フォルダに a.exe, b.exe, c.exe のいずれかが存在しないため、処理を行いません。");
                    return;
                }

                // (3) a.exe, b.exe, c.exe のいずれかのプロセスが稼働中 → 何もしない
                if (IsAnyProcessRunning(exeNames))
                {
                    Log("a.exe / b.exe / c.exe のいずれかのプロセスが稼働中のため、処理を行いません。");
                    return;
                }

                // 比較対象：対象フォルダ内の「とあるzip」（同名を想定）
                if (!File.Exists(sourceZip))
                {
                    Log("元ZIP が見つからないため、処理を行いません。");
                    return;
                }
                var targetZip = Path.Combine(targetDir, Path.GetFileName(sourceZip));

                //var srcZipTime = File.GetLastWriteTimeUtc(sourceZip);
                //var tgtZipTime = File.GetLastWriteTimeUtc(targetZip);

                //Log($"元ZIP: {srcZipTime:O}");
                //Log($"対象ZIP: {tgtZipTime:O}");

                //if (srcZipTime <= tgtZipTime)
                //{
                //    Log("元ZIPが新しくないため、差し替えを行いません。");
                //    return;
                //}

                // --- 新しさ比較 ---
                var srcZipTimeUtc = File.GetLastWriteTimeUtc(sourceZip);

                DateTime? targetZipTimeUtc = File.Exists(targetZip)
                    ? (DateTime?)File.GetLastWriteTimeUtc(targetZip)
                    : null;

                var exeTimesUtc = exePaths.Where(File.Exists).Select(File.GetLastWriteTimeUtc);
                var baselineUtc = (targetZipTimeUtc.HasValue ? exeTimesUtc.Concat(new[] { targetZipTimeUtc.Value }) : exeTimesUtc)
                                  .DefaultIfEmpty(DateTime.MinValue)
                                  .Max();

                Log($"元ZIP(UTC): {srcZipTimeUtc:O}");
                Log($"対象側基準(UTC): {baselineUtc:O}");

                if (srcZipTimeUtc <= baselineUtc)
                {
                    Log("元ZIPが新しくないため、差し替えは行いません。");
                    return;
                }

                // --- 差し替え ---
                ReplaceFolderWithZip(sourceZip, targetDir, Path.GetFileName(sourceZip));

                Log("差し替えが完了しました。");
            }
            catch (Exception ex)
            {
                Log("エラーが発生しました: " + ex);
            }
        }

        // a.exe, b.exe, c.exe のどれかが稼働中なら true
        static bool IsAnyProcessRunning(string[] exeNames)
        {
            var names = exeNames
                .Select(n => Path.GetFileNameWithoutExtension(n))
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Select(n => n.ToLowerInvariant())
                .Distinct()
                .ToArray();

            foreach (var name in names)
            {
                Process[] procs = null;
                try
                {
                    procs = Process.GetProcessesByName(name);
                }
                catch
                {
                    // 取得失敗は無視して続行
                }
                if (procs != null && procs.Length > 0) return true;
            }
            return false;
        }

        static void ReplaceFolderWithZip(string sourceZip, string targetDir, string zipFileName)
        {
            // 一時展開先（安全に差し替えるため）
            var tempExtract = Path.Combine(Path.GetTempPath(), "ZipReplace_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempExtract);

            try
            {
                // 事前展開
                ZipFile.ExtractToDirectory(sourceZip, tempExtract);

                // 対象フォルダをクリア（フォルダ自体は残す）
                ClearDirectory(targetDir);

                // 元ZIPを対象フォルダ直下へコピー（同名で上書き）
                var destZipPath = Path.Combine(targetDir, zipFileName);
                File.Copy(sourceZip, destZipPath, true);

                // 展開物をコピー
                CopyAll(new DirectoryInfo(tempExtract), new DirectoryInfo(targetDir));
            }
            finally
            {
                // 掃除
                TryDeleteDirectory(tempExtract);
            }
        }

        static void ClearDirectory(string dir)
        {
            // ファイル削除
            foreach (var file in Directory.GetFiles(dir, "*", SearchOption.TopDirectoryOnly))
            {
                try
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                    File.Delete(file);
                }
                catch { /* 必要に応じてログ */ }
            }
            // サブフォルダ削除
            foreach (var sub in Directory.GetDirectories(dir, "*", SearchOption.TopDirectoryOnly))
            {
                TryDeleteDirectory(sub);
            }
        }

        static void TryDeleteDirectory(string dir)
        {
            try
            {
                if (Directory.Exists(dir))
                {
                    foreach (var file in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
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

        static void CopyAll(DirectoryInfo source, DirectoryInfo target)
        {
            // 先にディレクトリを作成
            foreach (var dir in source.GetDirectories("*", SearchOption.AllDirectories))
            {
                var rel = dir.FullName.Substring(source.FullName.TrimEnd('\\').Length).TrimStart('\\');
                Directory.CreateDirectory(Path.Combine(target.FullName, rel));
            }
            // ファイルをコピー
            foreach (var file in source.GetFiles("*", SearchOption.AllDirectories))
            {
                var rel = file.FullName.Substring(source.FullName.TrimEnd('\\').Length).TrimStart('\\');
                var dest = Path.Combine(target.FullName, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(dest) ?? target.FullName);
                file.CopyTo(dest, true);
            }
        }

        static void Log(string message) => Console.WriteLine("[{0:yyyy-MM-dd HH:mm:ss}] {1}", DateTime.Now, message);
    }
}

// Program.cs
using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;

class Program
{
    static void Main(string[] args)
    {
        // ★ここを環境に合わせて設定してください
        var sourceZip = @"C:\package.zip";   // C:\ にある「とあるzip」
        var targetDir = @"C:\temp\Work";     // C:\temp にある「とあるフォルダ」
        var exeNames = new[] { "a.exe", "b.exe", "c.exe" };

        try
        {
            // 1) 条件チェック --------------------------------------------------

            // (a) 対象フォルダがない → 何もしない
            if (!Directory.Exists(targetDir))
            {
                Log("対象フォルダが存在しないため、処理を行いません。");
                return;
            }

            // (b) a.exe, b.exe, c.exe が全て存在しない → 何もしない（※1つでも欠けていたら何もしない）
            var exePaths = exeNames.Select(name => Path.Combine(targetDir, name)).ToArray();
            if (!exePaths.All(File.Exists))
            {
                Log("対象フォルダに a.exe, b.exe, c.exe のいずれかが存在しないため、処理を行いません。");
                return;
            }

            // (c) a.exe, b.exe, c.exe のプロセスが1つでも生存 → 何もしない
            if (IsAnyProcessRunning(exeNames))
            {
                Log("a.exe / b.exe / c.exe のいずれかのプロセスが稼働中のため、処理を行いません。");
                return;
            }

            // (d) 比較用：対象フォルダ内のZIP（同名を想定。無ければスキップ可能）
            var targetZip = Path.Combine(targetDir, Path.GetFileName(sourceZip));

            // 2) 新しさ比較ロジック ------------------------------------------
            // 元ZIP（sourceZip）が「対象フォルダ側の基準時刻」より新しければ差し替え
            // 基準時刻：対象フォルダ内ZIPの最終更新時刻と a,b,c の最終更新時刻の最大
            if (!File.Exists(sourceZip))
            {
                Log("元ZIP が見つからないため、処理を行いません。");
                return;
            }

            var srcZipTime = File.GetLastWriteTimeUtc(sourceZip);

            DateTime? targetZipTime = File.Exists(targetZip)
                ? File.GetLastWriteTimeUtc(targetZip)
                : (DateTime?)null;

            var exeTimes = exePaths
                .Where(File.Exists)
                .Select(p => File.GetLastWriteTimeUtc(p));

            // 対象側の基準時刻（ZIPがあればZIPも含めて最大時刻）
            var targetBaselineTime = (targetZipTime.HasValue ? exeTimes.Append(targetZipTime.Value) : exeTimes)
                                     .DefaultIfEmpty(DateTime.MinValue)
                                     .Max();

            Log($"元ZIP : {sourceZip}  最終更新(UTC)={srcZipTime:O}");
            Log($"対象側基準(UTC)   : {targetBaselineTime:O}");

            if (srcZipTime <= targetBaselineTime)
            {
                Log("元ZIPが新しくないため、差し替えは行いません。");
                return;
            }

            // 3) 差し替え実行 --------------------------------------------------
            // - 対象フォルダ配下を一旦クリア
            // - 元ZIPを対象フォルダ直下へコピー
            // - ZIPを展開
            ReplaceFolderWithZip(sourceZip, targetDir, Path.GetFileName(sourceZip));

            Log("差し替えが完了しました。");
        }
        catch (Exception ex)
        {
            Log("エラーが発生しました: " + ex);
        }
    }

    // a.exe, b.exe, c.exe のどれかが稼働中なら true
    static bool IsAnyProcessRunning(string[] exeNames)
    {
        // プロセス名は拡張子なし（例: "a"）
        var names = exeNames
            .Select(n => Path.GetFileNameWithoutExtension(n))
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => n.ToLowerInvariant())
            .Distinct()
            .ToArray();

        foreach (var name in names)
        {
            // 同名のプロセスが1つでもあれば稼働中
            var procs = Process.GetProcessesByName(name);
            if (procs?.Length > 0) return true;
        }
        return false;
    }

    static void ReplaceFolderWithZip(string sourceZip, string targetDir, string zipFileName)
    {
        // 一時展開ディレクトリ（より安全な差し替えのため）
        var tempExtract = Path.Combine(Path.GetTempPath(), "ZipReplace_" + Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(tempExtract);

        try
        {
            // 事前にZIPを一時展開
            ZipFile.ExtractToDirectory(sourceZip, tempExtract);

            // 対象フォルダをクリア（フォルダ自体は残す）
            ClearDirectory(targetDir);

            // 元ZIPを対象フォルダ直下へコピー
            var destZipPath = Path.Combine(targetDir, zipFileName);
            File.Copy(sourceZip, destZipPath, overwrite: true);

            // 展開物を対象フォルダへ移動（上書き）
            CopyAll(new DirectoryInfo(tempExtract), new DirectoryInfo(targetDir));
        }
        finally
        {
            // 一時ディレクトリは掃除
            TryDeleteDirectory(tempExtract);
        }
    }

    static void ClearDirectory(string dir)
    {
        // ファイル削除
        foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.TopDirectoryOnly))
        {
            File.SetAttributes(file, FileAttributes.Normal);
            File.Delete(file);
        }
        // サブディレクトリ削除
        foreach (var sub in Directory.EnumerateDirectories(dir, "*", SearchOption.TopDirectoryOnly))
        {
            TryDeleteDirectory(sub);
        }
    }

    static void TryDeleteDirectory(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
            {
                // 読取専用属性などを解除してから削除
                foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                {
                    try { File.SetAttributes(file, FileAttributes.Normal); } catch { /* ignore */ }
                }
                Directory.Delete(dir, recursive: true);
            }
        }
        catch
        {
            // 掃除失敗は致命ではないので黙殺
        }
    }

    static void CopyAll(DirectoryInfo source, DirectoryInfo target)
    {
        foreach (var dir in source.GetDirectories("*", SearchOption.AllDirectories))
        {
            var relPath = dir.FullName.Substring(source.FullName.Length).TrimStart(Path.DirectorySeparatorChar);
            var targetSub = Path.Combine(target.FullName, relPath);
            Directory.CreateDirectory(targetSub);
        }
        foreach (var file in source.GetFiles("*", SearchOption.AllDirectories))
        {
            var relPath = file.FullName.Substring(source.FullName.Length).TrimStart(Path.DirectorySeparatorChar);
            var dest = Path.Combine(target.FullName, relPath);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            file.CopyTo(dest, overwrite: true);
        }
    }

    static void Log(string message) => Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}");
}
