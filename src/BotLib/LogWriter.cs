using BotLib.Misc;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace BotLib
{
    public class LogWriter
    {
        public LogWriter(string fn, bool saveLogByDay, int maxByte)
        {
            _start = DateTime.Now;
            _file = new LoopSaveFile(fn, maxByte, saveLogByDay);
            WriteLine(string.Format("\r\n============  日志启动({0})  ============", DateTime.Now.ToString()));
        }

        public string FileName
        {
            get { return _file == null ? string.Empty : _file.FileName; }
        }

        public void Flush()
        {
            if (_file != null) _file.Flush();
        }

        public void WriteEnvironmentString(string env)
        {
            _environmentStr = env;
            WriteLine(string.Format("{0}：程序版本={1}\r\n-------------------------------------\r\n", DateTime.Now.ToString(), env));
        }

        public void Close(string reason)
        {
            WriteLine(string.Format("日志关闭({1})：原因={3},持续时间={0},托管内存占用={4}MB\r\n程序版本={2}\r\n===============================\r\n", new object[]
            {
                (DateTime.Now - _start).TotalSeconds,
                DateTime.Now.ToString(),
                _environmentStr,
                reason,
                ((double)GC.GetTotalMemory(false) / Math.Pow(2.0, 20.0)).ToString("0.0")
            }));
            _file.Close();
        }

        public void Clear()
        {
            _file.Clear();
        }

        public void Write(string text, string tag, bool writeStackTrace = false, string stackTrace = null)
        {
            bool limitSameStringWriteCount = LimitSameStringWriteCount;
            if (limitSameStringWriteCount)
            {
                int cnt = UpdateWriteCount(text);
                if (cnt > 0 && cnt < 20 && cnt % 10 == 0)
                {
                    text = string.Concat(new object[]
                    {
                        "第",
                        cnt,
                        "次发生该写入,超出20次将不再提示",
                        Environment.NewLine,
                        text
                    });
                }
                if (cnt > 20)
                {
                    return;
                }
            }
            text = string.Concat(tag,"(",DateTime.Now.ToString(),"):",text, Environment.NewLine);
            if (writeStackTrace)
            {
                if (string.IsNullOrEmpty(stackTrace))
                {
                    stackTrace = GetStackTrace(4);
                }
                text = text + stackTrace + Environment.NewLine;
            }
            _file.WriteLine(text);
        }

        public static string GetStackTrace(int skipFrames = 1)
        {
            var stackTrace = new StackTrace(skipFrames);
            var builder = new StringBuilder();
            foreach (var stackFrame in stackTrace.GetFrames())
            {
                string fullName = stackFrame.GetMethod().ReflectedType.FullName;
                builder.AppendLine(string.Format("{0}:   {1}", fullName, stackFrame.GetMethod().ToString()));
            }
            return builder.ToString();
        }

        public void Error(string text)
        {
            Write(text, "ERROR");
        }

        public void Info(string text)
        {
            Write(text, "Info");
        }

        public void Debug(string text)
        {
            Write(text, "Debug");
        }

        public void Exception(string msg)
        {
            Write(msg, "Exception");
        }

        private int UpdateWriteCount(string text)
        {
            // Field logs can carry a different session ID on every reconnect.
            // A lifetime dictionary of whole log strings retains those IDs
            // indefinitely, even though the underlying log segments rotate.
            text = text ?? string.Empty;
            if (text.Length > 8192) return 0; // Do not retain oversized payloads.
            lock (_writeCountSync)
            {
                int count;
                if (_wcache.TryGetValue(text, out count))
                {
                    if (count < 21) count++;
                    _wcache[text] = count;
                    return count;
                }

                while (_wcache.Count >= MaxWriteCountKeys && _writeCountOrder.Count > 0)
                {
                    _wcache.Remove(_writeCountOrder.Dequeue());
                }
                _wcache[text] = 0;
                _writeCountOrder.Enqueue(text);
                return 0;
            }
        }

        public void Assert(string msg)
        {
            Write(msg, "Assert", false, null);
        }

        public void Show()
        {
            try
            {
                if (File.Exists(_file.FileName))
                {
                    Process.Start(_file.FileName);
                }
            }
            catch
            {
            }
        }

        public void TimeElapse(string title, DateTime t0)
        {
            Info(title + ",ms=" + (DateTime.Now - t0).TotalMilliseconds);
        }

        public void WriteLine(string msg)
        {
            _file.WriteLine(msg);
        }

        public void StackTrace()
        {
            Write("", "StackTrace", true, null);
        }

        public void CopyTo(string dest)
        {
            Close("复制日志");
            if (File.Exists(dest))
            {
                File.Delete(dest);
            }
            File.Copy(_file.FileName, dest);
        }

        private LoopSaveFile _file;

        private string _environmentStr = "未命名版本";

        private DateTime _start;

        public bool LimitSameStringWriteCount = true;

        private const int MaxWriteCountKeys = 2048;
        private readonly object _writeCountSync = new object();
        private readonly Queue<string> _writeCountOrder = new Queue<string>();
        private readonly Dictionary<string, int> _wcache = new Dictionary<string, int>(StringComparer.Ordinal);

        private class LoopSaveFile
        {
            private const int DefaultSegmentBytes = 1024 * 1024;
            private const int OversizedEntryChunkChars = 400 * 1024;
            private static readonly TimeSpan LogRetention = TimeSpan.FromHours(24);
            private static readonly TimeSpan MaintenanceInterval = TimeSpan.FromMinutes(1);

            public string FileName { get; set; }
            private bool _saveLogByDay;
            private NoReEnterTimer _timer;
            private int _limitFileSize;
            private DateTime _lastMaintenanceUtc = DateTime.MinValue;
            private ConcurrentQueue<string> _cache = new ConcurrentQueue<string>();
            private readonly Encoding _encoding = Encoding.GetEncoding("gb2312");

            public LoopSaveFile(string fn, int maxFileByte, bool saveLogByDay)
            {
                FileName = fn;
                // Runtime log files are strict 1024 KiB segments. A normal process restart keeps
                // appending to the same active file; only size rollover may archive it here.
                _limitFileSize = maxFileByte > 0
                    ? Math.Min(maxFileByte, DefaultSegmentBytes)
                    : DefaultSegmentBytes;
                _saveLogByDay = saveLogByDay;
                EnsureDirectory();
                MaintainLogFiles(true);
                _timer = new NoReEnterTimer(WriteLoop, 1000, 0);
            }

            ~LoopSaveFile()
            {
                Close();
            }

            private void EnsureDirectory()
            {
                try
                {
                    var directory = Path.GetDirectoryName(FileName);
                    if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
                }
                catch
                {
                }
            }

            private void WriteLoop()
            {
                try
                {
                    MaintainLogFiles(false);
                    if (_cache.Count <= 0) return;

                    var batch = new List<string>();
                    var currentBytes = CurrentFileLength();
                    string value;
                    while (_cache.TryDequeue(out value))
                    {
                        foreach (var part in SplitOversizedEntry(value ?? string.Empty))
                        {
                            var entryBytes = _encoding.GetByteCount(part + Environment.NewLine);
                            if (currentBytes > 0 && currentBytes + entryBytes > _limitFileSize)
                            {
                                WriteBatch(batch);
                                batch.Clear();
                                RotateCurrentFile();
                                currentBytes = 0;
                            }

                            batch.Add(part);
                            currentBytes += entryBytes;

                            if (currentBytes >= _limitFileSize)
                            {
                                WriteBatch(batch);
                                batch.Clear();
                                RotateCurrentFile();
                                currentBytes = 0;
                            }
                        }
                    }

                    WriteBatch(batch);
                }
                catch
                {
                    // Logging must never crash the Bot. A later timer pass will continue with new data.
                }
            }

            private IEnumerable<string> SplitOversizedEntry(string value)
            {
                if (_encoding.GetByteCount(value + Environment.NewLine) <= _limitFileSize)
                {
                    yield return value;
                    yield break;
                }

                var offset = 0;
                while (offset < value.Length)
                {
                    var take = Math.Min(OversizedEntryChunkChars, value.Length - offset);
                    var chunk = value.Substring(offset, take);
                    while (take > 256 && _encoding.GetByteCount(chunk + Environment.NewLine) > _limitFileSize)
                    {
                        take /= 2;
                        chunk = value.Substring(offset, take);
                    }
                    yield return chunk;
                    offset += take;
                }
            }

            private void WriteBatch(ICollection<string> batch)
            {
                if (batch == null || batch.Count == 0) return;
                using (StreamWriter streamWriter = OpenStream(true))
                {
                    foreach (var value in batch)
                    {
                        streamWriter.WriteLine(value);
                    }
                }
            }

            public string GetFileNameFromDate(DateTime date)
            {
                FileInfo fileInfo = new FileInfo(FileName);
                int length = FileName.LastIndexOf(fileInfo.Extension);
                return FileName.Substring(0, length) + date.ToString("yyyy-MM-dd") + fileInfo.Extension;
            }

            private StreamWriter OpenStream(bool append)
            {
                EnsureDirectory();
                FileStream stream = new FileStream(FileName, append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
                return new StreamWriter(stream, _encoding);
            }

            private long CurrentFileLength()
            {
                try
                {
                    return File.Exists(FileName) ? new FileInfo(FileName).Length : 0L;
                }
                catch
                {
                    return 0L;
                }
            }

            private void MaintainLogFiles(bool force)
            {
                var nowUtc = DateTime.UtcNow;
                if (!force && nowUtc - _lastMaintenanceUtc < MaintenanceInterval) return;
                _lastMaintenanceUtc = nowUtc;

                try
                {
                    if (File.Exists(FileName))
                    {
                        var current = new FileInfo(FileName);
                        if (current.Length >= _limitFileSize)
                        {
                            RotateCurrentFile();
                        }
                    }
                }
                catch
                {
                }

                // Retention only deletes archived segments. It must never roll the active file
                // merely because that file is older than 24 hours.
                DeleteExpiredSegments(nowUtc.Subtract(LogRetention));
            }

            private string BuildSegmentFileName(DateTime stampUtc, int sequence)
            {
                var directory = Path.GetDirectoryName(FileName) ?? string.Empty;
                var stem = Path.GetFileNameWithoutExtension(FileName);
                var extension = Path.GetExtension(FileName);
                var name = stem + "." + stampUtc.ToLocalTime().ToString("yyyyMMdd-HHmmss-fff")
                    + "." + sequence.ToString("D3") + extension;
                return Path.Combine(directory, name);
            }

            private void RotateCurrentFile()
            {
                try
                {
                    if (!File.Exists(FileName)) return;
                    var info = new FileInfo(FileName);
                    if (info.Length <= 0) return;

                    var stamp = info.LastWriteTimeUtc == DateTime.MinValue ? DateTime.UtcNow : info.LastWriteTimeUtc;
                    for (var sequence = 0; sequence < 10000; sequence++)
                    {
                        var destination = BuildSegmentFileName(stamp, sequence);
                        if (File.Exists(destination)) continue;
                        File.Move(FileName, destination);
                        return;
                    }
                }
                catch
                {
                }
            }

            private void DeleteExpiredSegments(DateTime cutoffUtc)
            {
                try
                {
                    var directory = Path.GetDirectoryName(FileName);
                    if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return;
                    var stem = Path.GetFileNameWithoutExtension(FileName);
                    var extension = Path.GetExtension(FileName);
                    var pattern = stem + ".*" + extension;
                    foreach (var path in Directory.GetFiles(directory, pattern))
                    {
                        try
                        {
                            if (string.Equals(path, FileName, StringComparison.OrdinalIgnoreCase)) continue;
                            var info = new FileInfo(path);
                            if (info.LastWriteTimeUtc < cutoffUtc) info.Delete();
                        }
                        catch
                        {
                        }
                    }
                }
                catch
                {
                }
            }

            public void Clear()
            {
                try
                {
                    File.Delete(FileName);
                    using (OpenStream(false))
                    {
                    }
                }
                catch
                {
                }
            }

            public void WriteLine(string text)
            {
                _cache.Enqueue(text);
            }

            public void Flush()
            {
                WriteLoop();
            }

            public void Close()
            {
                Flush();
            }
        }
    }
}