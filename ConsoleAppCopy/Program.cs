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
