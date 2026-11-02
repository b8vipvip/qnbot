using Bot.ShopScope;
using BotLib;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Bot.Knowledge
{
    internal sealed class BuyerHistoryScanMergeResult
    {
        public int ScansRead { get; set; }
        public int ScansFailed { get; set; }
        public int RowsRead { get; set; }
        public int InvalidRows { get; set; }
        public int DuplicateInScans { get; set; }
        public int AlreadyArchived { get; set; }
        public int NewlyArchived { get; set; }
    }

    internal sealed class BuyerHistoryScannedMessage
    {
        public string Text { get; set; }
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Explicitly replays saved, redacted history evidence into buyer-questions-v2.
    /// Unlike per-run event_hash, a text/timestamp fingerprint survives repeated
    /// scans (the run-specific pseudonym and event hash do not).
    /// A matching text and timestamp cannot reliably distinguish different buyers,
    /// because diagnostic packages intentionally omit raw buyer identity.
    /// </summary>
    internal static class BuyerHistoryScanMessageMergeService
    {
        private const string DataFile = "messages-redacted.jsonl";
        private const int MaximumLineLength = 200000;

        internal static string Fingerprint(string text, DateTime time)
        {
            var normalized = BuyerMessageArchiveService.Redact(text);
            using (var sha = SHA256.Create())
            {
                var bytes = Encoding.UTF8.GetBytes(time.Ticks.ToString(CultureInfo.InvariantCulture)
                    + "|" + normalized);
                return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "");
            }
        }

        internal static BuyerHistoryScanMergeResult MergeAll(string seller)
        {
            if (ChatHistoryScanService.IsBuyerScanActive(seller))
                throw new InvalidOperationException("历史消息扫描仍在运行，请先完成或取消扫描再更新消息。");
            var shop = ShopContextLocator.ResolveBySellerNick((seller ?? "").Trim());
            if (shop == null)
                throw new InvalidOperationException("未找到当前店铺的 ShopKey，无法导入扫描数据。");
            var result = new BuyerHistoryScanMergeResult();
            var root = Path.Combine(new ShopScopedPathProvider().GetLogRoot(shop),
                "buyer-history-scans");
            if (!Directory.Exists(root)) return result;

            var unique = new Dictionary<string, BuyerHistoryScannedMessage>(StringComparer.Ordinal);
            foreach (var directory in Directory.GetDirectories(root).OrderBy(x => x, StringComparer.Ordinal))
            {
                var runId = Path.GetFileName(directory);
                if (string.IsNullOrWhiteSpace(runId)) continue;
                var manifestPath = Path.Combine(directory, "manifest.json");
                if (!File.Exists(manifestPath)) continue;
                try
                {
                    // Only a run whose manifest agrees with its directory can
                    // be read. Never follow packagePath found in JSON metadata.
                    var manifest = JsonConvert.DeserializeObject<BuyerHistoryScanEvidence>(
                        File.ReadAllText(manifestPath, Encoding.UTF8));
                    if (manifest == null || manifest.RunId != runId)
                        throw new InvalidDataException("scan_manifest_identity_mismatch");
                    var sourcePath = Path.Combine(directory, DataFile);
                    if (File.Exists(sourcePath))
                    {
                        foreach (var line in File.ReadLines(sourcePath, Encoding.UTF8))
                            AddLine(line, unique, result);
                    }
                    else
                    {
                        var packagePath = Path.Combine(directory, runId + ".zip");
                        if (!File.Exists(packagePath))
                            throw new FileNotFoundException("missing_scan_message_evidence");
                        using (var file = File.OpenRead(packagePath))
                        using (var zip = new ZipArchive(file, ZipArchiveMode.Read))
                        {
                            var entry = zip.GetEntry(DataFile);
                            if (entry == null)
                                throw new InvalidDataException("missing_redacted_messages_entry");
                            using (var reader = new StreamReader(entry.Open(), Encoding.UTF8))
                            {
                                string line;
                                while ((line = reader.ReadLine()) != null)
                                    AddLine(line, unique, result);
                            }
                        }
                    }
                    result.ScansRead++;
                }
                catch (Exception ex)
                {
                    // Partial results stay usable; do not expose any raw message.
                    result.ScansFailed++;
                    Log.Info("[BuyerQuestions] scan_merge_source_error type=" + ex.GetType().Name);
                }
            }

            if (unique.Count == 0)
            {
                Log.Info("[BuyerQuestions] scan_merge_complete scans=" + result.ScansRead
                    + ";failed=" + result.ScansFailed + ";rows=" + result.RowsRead
                    + ";unique=0;added=0");
                return result;
            }
            // The importer is serialized with live-message writes by the
            // archive's shop-specific SQLite state lock.
            BuyerMessageArchiveService.ImportScannedMessages(
                seller, unique.Values.ToList(), result);
            Log.Info("[BuyerQuestions] scan_merge_complete scans=" + result.ScansRead
                + ";failed=" + result.ScansFailed + ";rows=" + result.RowsRead
                + ";invalid=" + result.InvalidRows + ";duplicateInScans="
                + result.DuplicateInScans + ";alreadyArchived=" + result.AlreadyArchived
                + ";added=" + result.NewlyArchived);
            return result;
        }

        private static void AddLine(string line,
            Dictionary<string, BuyerHistoryScannedMessage> unique,
            BuyerHistoryScanMergeResult result)
        {
            if (string.IsNullOrWhiteSpace(line)) return;
            result.RowsRead++;
            if (line.Length > MaximumLineLength)
            {
                result.InvalidRows++;
                return;
            }
            try
            {
                var record = JObject.Parse(line);
                var text = BuyerMessageArchiveService.Redact((string)record["text"]);
                var stamp = (string)record["utc_or_local_time"];
                DateTime time;
                if (text.Length == 0 || !DateTime.TryParse(stamp, CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out time) || time == DateTime.MinValue)
                {
                    result.InvalidRows++;
                    return;
                }
                var fingerprint = Fingerprint(text, time);
                if (!unique.ContainsKey(fingerprint))
                    unique.Add(fingerprint, new BuyerHistoryScannedMessage
                    {
                        Text = text, Timestamp = time
                    });
                else
                    result.DuplicateInScans++;
            }
            catch (Exception)
            {
                result.InvalidRows++;
            }
        }
    }
}
