using BotLib;
using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace Bot.ChromeNs
{
    /// <summary>
    /// Small crash-safe state-file primitive shared by runtime ledgers that must survive more than
    /// one Bot process. The named mutex covers the caller's whole read/modify/write transaction;
    /// writes always go to a unique sibling file, Flush(true), then atomic replace. The previous
    /// valid file is never deleted before the replacement succeeds.
    /// </summary>
    internal static class CrossProcessAtomicStateFile
    {
        internal sealed class Lease : IDisposable
        {
            private Mutex _mutex;
            public bool Acquired { get; private set; }

            internal Lease(Mutex mutex, bool acquired)
            {
                _mutex = mutex;
                Acquired = acquired;
            }

            public void Dispose()
            {
                if (_mutex == null) return;
                if (Acquired)
                {
                    try { _mutex.ReleaseMutex(); }
                    catch { }
                    Acquired = false;
                }
                try { _mutex.Dispose(); }
                catch { }
                _mutex = null;
            }
        }

        internal static Lease Acquire(string path, string scope, int waitMilliseconds)
        {
            Mutex mutex = null;
            var acquired = false;
            try
            {
                mutex = new Mutex(false, BuildMutexName(path, scope));
                try
                {
                    acquired = mutex.WaitOne(Math.Max(250, waitMilliseconds));
                }
                catch (AbandonedMutexException)
                {
                    acquired = true;
                    Log.Info("检测到跨进程状态锁的旧持有进程已退出，已安全接管: scope=" + scope);
                }
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount("获取跨进程状态锁失败: scope=" + scope + ", error=" + ex.Message, 10);
            }
            return new Lease(mutex, acquired);
        }

        internal static string ReadAllTextShared(
            string path,
            int retryCount,
            int retryDelayMilliseconds,
            out string error)
        {
            error = string.Empty;
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return string.Empty;
            Exception last = null;
            for (var attempt = 1; attempt <= Math.Max(1, retryCount); attempt++)
            {
                try
                {
                    using (var stream = new FileStream(
                        path,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete))
                    using (var reader = new StreamReader(stream, Encoding.UTF8, true))
                    {
                        return reader.ReadToEnd();
                    }
                }
                catch (IOException ex)
                {
                    last = ex;
                    if (attempt < retryCount) Thread.Sleep(Math.Max(10, retryDelayMilliseconds) * attempt);
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                    return string.Empty;
                }
            }
            error = last == null ? "unknown read failure" : last.Message;
            return string.Empty;
        }

        internal static bool WriteAllTextAtomic(
            string path,
            string content,
            int retryCount,
            int retryDelayMilliseconds,
            out string error,
            string backupPath = null)
        {
            error = string.Empty;
            var directory = Path.GetDirectoryName(path);
            try
            {
                if (!Directory.Exists(directory)) Directory.CreateDirectory(directory);
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }

            var temp = path + "." + Process.GetCurrentProcess().Id + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                var bytes = new UTF8Encoding(false).GetBytes(content ?? string.Empty);
                using (var stream = new FileStream(
                    temp,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    8192,
                    FileOptions.WriteThrough))
                {
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush(true);
                }

                Exception last = null;
                for (var attempt = 1; attempt <= Math.Max(1, retryCount); attempt++)
                {
                    try
                    {
                        if (File.Exists(path)) File.Replace(temp, path, backupPath, true);
                        else File.Move(temp, path);
                        return true;
                    }
                    catch (IOException ex)
                    {
                        last = ex;
                        if (attempt < retryCount) Thread.Sleep(Math.Max(10, retryDelayMilliseconds) * attempt);
                    }
                }
                error = last == null ? "unknown atomic replace failure" : last.Message;
                return false;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
            finally
            {
                try { if (File.Exists(temp)) File.Delete(temp); }
                catch { }
            }
        }

        private static string BuildMutexName(string path, string scope)
        {
            var normalized = string.Empty;
            try { normalized = Path.GetFullPath(path ?? string.Empty).Trim().ToLowerInvariant(); }
            catch { normalized = (path ?? string.Empty).Trim().ToLowerInvariant(); }
            using (var sha = SHA256.Create())
            {
                var bytes = Encoding.UTF8.GetBytes((scope ?? "state") + "|" + normalized);
                var hash = BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", string.Empty).ToLowerInvariant();
                return @"Local\QianniuAiBot.State." + hash.Substring(0, Math.Min(32, hash.Length));
            }
        }
    }
}
