using Bot.ShopScope;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Bot.Knowledge
{
    // On-disk scan evidence is scoped to the active ShopKey, never the buyer nick.
    // Names and raw message keys are NEVER recorded, including in diagnostic packages.
    internal sealed class BuyerHistoryScanEvidence
    {
        public string RunId { get; set; }
        public string StartedAt { get; set; }
        public string FinishedAt { get; set; }
        public string Status { get; set; }
        public string Phase { get; set; }
        public int ContactCount { get; set; }
        public int CompletedContacts { get; set; }
        public int FailedContacts { get; set; }
        public int ReadMessages { get; set; }
        public int InterruptedContacts { get; set; }
        public int RetryAttempts { get; set; }
        public string PackagePath { get; set; }
    }

    internal sealed class BuyerHistoryScanEvidenceSession : IDisposable
    {
        private readonly object _sync = new object();
        private readonly string _root;
        private readonly string _sessionFolder;
        private readonly string _eventsFile;
        private readonly string _messagesFile;
        private readonly string _manifestFile;
        private readonly BuyerHistoryScanEvidence _record;
        private bool _finished;
        internal BuyerHistoryScanEvidence Record { get { return _record; } }
        internal string ExpectedPackagePath
        {
            get { return Path.Combine(_sessionFolder, _record.RunId + ".zip"); }
        }

        internal BuyerHistoryScanEvidenceSession(string seller)
        {
            var shop = ShopContextLocator.ResolveBySellerNick((seller ?? "").Trim());
            if (shop == null) throw new InvalidOperationException("店铺未绑定，无法保存扫描记录。");
            _root = Path.Combine(new ShopScopedPathProvider().GetLogRoot(shop), "buyer-history-scans");
            Directory.CreateDirectory(_root);
            var id = DateTime.UtcNow.ToString("yyyyMMddTHHmmssZ") + "-" + Guid.NewGuid().ToString("N").Substring(0, 10);
            _sessionFolder = Path.Combine(_root, id);
            Directory.CreateDirectory(_sessionFolder);
            _eventsFile = Path.Combine(_sessionFolder, "events.jsonl");
            _messagesFile = Path.Combine(_sessionFolder, "messages-redacted.jsonl");
            _manifestFile = Path.Combine(_sessionFolder, "manifest.json");
            _record = new BuyerHistoryScanEvidence
            {
                RunId = id, StartedAt = DateTime.UtcNow.ToString("o"),
                Status = "running", Phase = "starting"
            };
            File.WriteAllText(_eventsFile, "", Encoding.UTF8);
            File.WriteAllText(_messagesFile, "", Encoding.UTF8);
            SaveManifest();
            Event("start", "scan_started");
        }

        private void SaveManifest()
        {
            File.WriteAllText(_manifestFile,
                JsonConvert.SerializeObject(_record, Formatting.Indented), Encoding.UTF8);
        }

        internal void Event(string name, string phase)
        {
            lock (_sync)
            {
                _record.Phase = phase ?? "";
                File.AppendAllText(_eventsFile,
                    JsonConvert.SerializeObject(new {
                        utc = DateTime.UtcNow.ToString("o"),
                        type = name ?? "", phase = phase ?? "",
                        scanned = _record.CompletedContacts, failed = _record.FailedContacts,
                        read = _record.ReadMessages, interruptions = _record.InterruptedContacts,
                        retries = _record.RetryAttempts
                    }) + Environment.NewLine, Encoding.UTF8);
                SaveManifest();
            }
        }

        internal string ContactHash(string buyer)
        {
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(
                    _record.RunId + ":" + (buyer ?? "")))).Replace("-", "").Substring(0, 16);
        }

        internal void SetContactCount(int count)
        {
            _record.ContactCount = count;
            Event("discovery", "contact_list_ready");
        }

        internal void Retry(string reason)
        {
            _record.RetryAttempts++;
            if (reason == "switched") _record.InterruptedContacts++;
            Event("retry", reason);
        }

        internal void Contact(string buyer, bool success, int count, string phase)
        {
            lock (_sync)
            {
                if (success) _record.CompletedContacts++;
                else _record.FailedContacts++;
                _record.ReadMessages += count;
                File.AppendAllText(_eventsFile, JsonConvert.SerializeObject(new
                {
                    utc = DateTime.UtcNow.ToString("o"), type = "contact",
                    contact = ContactHash(buyer), status = success ? "completed" : "failed",
                    messages = count, reason = phase
                }) + Environment.NewLine, Encoding.UTF8);
                SaveManifest();
            }
        }

        internal void Messages(string buyer, IEnumerable<Tuple<string, DateTime, string>> turns)
        {
            var pseudonym = ContactHash(buyer);
            var lines = new StringBuilder();
            foreach (var turn in turns)
            {
                var text = BuyerMessageArchiveService.Redact(turn.Item1);
                if (!string.IsNullOrWhiteSpace(buyer))
                    text = text.Replace(buyer, "[买家]");
                // Contact/account identity never appears in the data; each
                // incoming event is keyed only by a per-scan salted digest.
                using (var sha = SHA256.Create())
                {
                    var fingerprint = BitConverter.ToString(sha.ComputeHash(
                        Encoding.UTF8.GetBytes(_record.RunId + "|" + (turn.Item3 ?? ""))))
                        .Replace("-", "").Substring(0, 24);
                    lines.AppendLine(JsonConvert.SerializeObject(new
                    {
                        contact = pseudonym,
                        utc_or_local_time = turn.Item2 == DateTime.MinValue ? "" : turn.Item2.ToString("o"),
                        event_hash = fingerprint, text = text
                    }));
                }
            }
            lock (_sync) File.AppendAllText(_messagesFile, lines.ToString(), Encoding.UTF8);
        }

        internal void Complete(string status, string phase)
        {
            lock (_sync)
            {
                if (_finished) return;
                _finished = true;
                _record.Status = status;
                _record.FinishedAt = DateTime.UtcNow.ToString("o");
                Event("finish", phase);
                var path = Path.Combine(_sessionFolder, _record.RunId + ".zip");
                // Package may be emitted for completed, partial, cancelled and
                // error sessions. The source JSONL files survive ZIP failure.
                using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                using (var zip = new ZipArchive(file, ZipArchiveMode.Create, false))
                {
                    foreach (var source in new[] { _manifestFile, _eventsFile, _messagesFile })
                    {
                        var entry = zip.CreateEntry(Path.GetFileName(source), CompressionLevel.Optimal);
                        using (var input = File.OpenRead(source))
                        using (var output = entry.Open()) input.CopyTo(output);
                    }
                }
                _record.PackagePath = path;
                SaveManifest();
            }
        }

        public void Dispose()
        {
            if (!_finished)
            {
                try { Complete("interrupted", "disposed_without_terminal_state"); }
                catch { /* Source JSONL evidence remains on disk. */ }
            }
        }

        internal static IList<BuyerHistoryScanEvidence> List(string seller)
        {
            var shop = ShopContextLocator.ResolveBySellerNick((seller ?? "").Trim());
            if (shop == null) return new List<BuyerHistoryScanEvidence>();
            var root = Path.Combine(new ShopScopedPathProvider().GetLogRoot(shop), "buyer-history-scans");
            if (!Directory.Exists(root)) return new List<BuyerHistoryScanEvidence>();
            var list = new List<BuyerHistoryScanEvidence>();
            foreach (var dir in Directory.GetDirectories(root).OrderByDescending(x => x).Take(100))
            {
                try
                {
                    var manifest = Path.Combine(dir, "manifest.json");
                    if (!File.Exists(manifest)) continue;
                    var item = JsonConvert.DeserializeObject<BuyerHistoryScanEvidence>(File.ReadAllText(manifest));
                    if (item != null && item.RunId == Path.GetFileName(dir))
                    {
                        var zip = Path.Combine(dir, item.RunId + ".zip");
                        item.PackagePath = File.Exists(zip) ? zip : "";
                        list.Add(item);
                    }
                }
                catch { /* One corrupted run never hides the others. */ }
            }
            return list;
        }
    }
}
