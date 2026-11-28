using Bot.ShopScope;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Bot.Knowledge
{
    // Private, machine-user-bound resume data. The diagnostic ZIP never
    // contains buyer nicknames, conversation IDs or the encrypted checkpoint.
    // This file is written atomically after each fully archived contact.
    internal sealed class BuyerHistoryScanCheckpoint
    {
        public int SchemaVersion { get; set; }
        public string Id { get; set; }
        public string CreatedAt { get; set; }
        public string UpdatedAt { get; set; }
        public string LastRunId { get; set; }
        public string State { get; set; }
        public bool ScanAll { get; set; }
        public int MaxContacts { get; set; }
        public DateTime? StartTime { get; set; }
        public DateTime? EndTime { get; set; }
        public List<string> Contacts { get; set; }
        public List<string> Completed { get; set; }
        public int ArchivedMessages { get; set; }
        public bool LearnKnowledge { get; set; }
        public int ObservedTurns { get; set; }
        public int AnalyzedHumanPairs { get; set; }
        public int ModelReferencedPairs { get; set; }
        public int PublishedKnowledge { get; set; }
        public int CandidateKnowledge { get; set; }
        public int DuplicateKnowledge { get; set; }
    }

    internal sealed class BuyerHistoryScanResumeInfo
    {
        public string Id { get; set; }
        public string UpdatedAt { get; set; }
        public int Total { get; set; }
        public int Completed { get; set; }
        public bool LearnKnowledge { get; set; }
        public int ArchivedMessages { get; set; }
        public int ObservedTurns { get; set; }
        public int AnalyzedHumanPairs { get; set; }
        public int ModelReferencedPairs { get; set; }
        public int PublishedKnowledge { get; set; }
        public int CandidateKnowledge { get; set; }
        public int DuplicateKnowledge { get; set; }
        public bool ScanAll { get; set; }
        public int MaxContacts { get; set; }
        public DateTime? StartTime { get; set; }
        public DateTime? EndTime { get; set; }
        public string State { get; set; }
    }

    internal static class BuyerHistoryScanCheckpointStore
    {
        private static readonly Regex IdPattern = new Regex(
            @"^\d{8}T\d{6}Z-[0-9a-f]{10}$", RegexOptions.Compiled);
        private static readonly object Sync = new object();
        private const int Schema = 4;

        private static string Root(string seller)
        {
            var shop = ShopContextLocator.ResolveCurrentOrBySellerNick((seller ?? "").Trim());
            if (shop == null || string.IsNullOrWhiteSpace(shop.ShopKey))
                throw new InvalidOperationException("当前店铺 ShopKey 不可用，不能恢复历史扫描。");
            return Path.Combine(new ShopScopedPathProvider().GetLogRoot(shop),
                "buyer-history-resume-private");
        }

        private static byte[] Entropy(string seller)
        {
            var shop = ShopContextLocator.ResolveCurrentOrBySellerNick((seller ?? "").Trim());
            if (shop == null || string.IsNullOrWhiteSpace(shop.ShopKey))
                throw new InvalidOperationException("无法绑定扫描任务的店铺身份。");
            return Encoding.UTF8.GetBytes("qnbot:history-scan:resume:v1:" + shop.ShopKey);
        }

        private static string FileName(string seller, string id)
        {
            if (string.IsNullOrWhiteSpace(id) || !IdPattern.IsMatch(id))
                throw new InvalidOperationException("断点任务ID无效。");
            return Path.Combine(Root(seller), id + ".checkpoint");
        }

        private static BuyerHistoryScanCheckpoint Decode(string seller, string path)
        {
            var bytes = ProtectedData.Unprotect(File.ReadAllBytes(path),
                Entropy(seller), DataProtectionScope.CurrentUser);
            var value = JsonConvert.DeserializeObject<BuyerHistoryScanCheckpoint>(
                Encoding.UTF8.GetString(bytes));
            if (value == null
                || value.Id != Path.GetFileNameWithoutExtension(path)
                || value.Contacts == null || value.Completed == null)
                throw new InvalidDataException("断点记录格式无效。");

            // Schema 1 could mark a buyer complete after the old 200-page cap or
            // 120-round contact scroll silently stopped. Schema 2 could silently
            // accept malformed pagination markers or lose pending contacts. Force a fresh contact
            // discovery and full history rescan when upgrading that checkpoint.
            var requiresFullRescan = value.SchemaVersion == 1 || value.SchemaVersion == 2;
            if (requiresFullRescan)
            {
                value.SchemaVersion = Schema;
                value.Contacts = new List<string>();
                value.Completed = new List<string>();
                value.ArchivedMessages = 0;
                value.State = "interrupted";
            }
            else if (value.SchemaVersion == 3)
            {
                // Version 3 was archive-only. Never use its completed roster as
                // proof that AI learning completed.
                value.SchemaVersion = Schema;
                value.LearnKnowledge = false;
                Save(seller, value);
            }
            else if (value.SchemaVersion != Schema)
            {
                throw new InvalidDataException("断点记录版本不受支持。");
            }

            value.Contacts = value.Contacts.Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.Ordinal).ToList();
            value.Completed = value.Completed.Where(x => value.Contacts.Contains(x))
                .Distinct(StringComparer.Ordinal).ToList();
            if (requiresFullRescan) Save(seller, value);
            return value;
        }

        internal static BuyerHistoryScanResumeInfo Latest(string seller, bool? learnKnowledge = false)
        {
            var root = Root(seller);
            if (!Directory.Exists(root)) return null;
            foreach (var path in Directory.GetFiles(root, "*.checkpoint")
                .OrderByDescending(x => x, StringComparer.Ordinal))
            {
                // Do not mask damaged latest resume data by silently picking an
                // older task; the operator must be informed.
                var value = Decode(seller, path);
                if (learnKnowledge.HasValue && value.LearnKnowledge != learnKnowledge.Value) continue;
                return new BuyerHistoryScanResumeInfo
                {
                    Id = value.Id, UpdatedAt = value.UpdatedAt,
                    Total = value.Contacts.Count, Completed = value.Completed.Count,
                    LearnKnowledge = value.LearnKnowledge, ArchivedMessages = value.ArchivedMessages,
                    ObservedTurns = value.ObservedTurns, AnalyzedHumanPairs = value.AnalyzedHumanPairs,
                    ModelReferencedPairs = value.ModelReferencedPairs, PublishedKnowledge = value.PublishedKnowledge,
                    CandidateKnowledge = value.CandidateKnowledge, DuplicateKnowledge = value.DuplicateKnowledge,
                    ScanAll = value.ScanAll, MaxContacts = value.MaxContacts,
                    StartTime = value.StartTime, EndTime = value.EndTime,
                    State = value.State
                };
            }
            return null;
        }

        internal static BuyerHistoryScanCheckpoint Load(string seller, string id,
            ChatHistoryScanOptions options)
        {
            lock (Sync)
            {
                var path = FileName(seller, id);
                if (!File.Exists(path))
                    throw new FileNotFoundException("未找到本店的断点记录。");
                var value = Decode(seller, path);
                if (value.LearnKnowledge != options.LearnKnowledge || value.ScanAll != options.ScanAll
                    || value.MaxContacts != options.MaxContacts
                    || value.StartTime != options.StartTime
                    || value.EndTime != options.EndTime)
                    throw new InvalidOperationException(
                        "断点任务的扫描范围与当前参数不一致，请改用重新扫描。");
                return value;
            }
        }

        internal static BuyerHistoryScanCheckpoint Create(string seller,
            string id, ChatHistoryScanOptions options)
        {
            var now = DateTime.UtcNow.ToString("o");
            var value = new BuyerHistoryScanCheckpoint
            {
                SchemaVersion = Schema, Id = id, CreatedAt = now, UpdatedAt = now,
                LastRunId = id, State = "running", ScanAll = options.ScanAll,
                LearnKnowledge = options.LearnKnowledge,
                MaxContacts = options.MaxContacts, StartTime = options.StartTime,
                EndTime = options.EndTime, Contacts = new List<string>(),
                Completed = new List<string>()
            };
            Save(seller, value);
            return value;
        }

        internal static void Save(string seller, BuyerHistoryScanCheckpoint value)
        {
            if (value == null) throw new ArgumentNullException("value");
            lock (Sync)
            {
                var path = FileName(seller, value.Id);
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                value.UpdatedAt = DateTime.UtcNow.ToString("o");
                var clear = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(value));
                var protectedBytes = ProtectedData.Protect(clear, Entropy(seller),
                    DataProtectionScope.CurrentUser);
                Array.Clear(clear, 0, clear.Length);
                var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    using (var fs = new FileStream(temp, FileMode.CreateNew,
                        FileAccess.Write, FileShare.None))
                    {
                        fs.Write(protectedBytes, 0, protectedBytes.Length);
                        fs.Flush(true);
                    }
                    if (File.Exists(path))
                        File.Replace(temp, path, null);
                    else
                        File.Move(temp, path);
                }
                finally
                {
                    if (File.Exists(temp)) File.Delete(temp);
                }
            }
        }
    }
}
