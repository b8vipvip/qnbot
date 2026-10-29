using Bot.ShopScope;
using BotLib;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Bot.ChromeNs
{
    /// <summary>
    /// Privacy-minimized evidence ledger for workflow diagnostics. This has no send,
    /// payment, refund, account or fulfillment authority; the existing canonical
    /// buyer lifecycle still owns all outgoing message decisions.
    /// </summary>
    internal static class ReceptionEvidenceLedger
    {
        private sealed class EvidenceRow
        {
            public string Id { get; set; }
            public string BuyerKey { get; set; }
            public string ProductKey { get; set; }
            public string SkuKey { get; set; }
            public string SessionKey { get; set; }
            public string SourceMessageKey { get; set; }
            public string EvidenceDigest { get; set; }
            public string Kind { get; set; }
            public string Observation { get; set; }
            public string Stage { get; set; }
            public string Authority { get; set; }
            public double Confidence { get; set; }
            public long ObservedUtcTicks { get; set; }
            public int SchemaVersion { get; set; }
        }

        private const int SchemaVersion = 1;
        private const int MaxRows = 500;
        private const int KeepDays = 7;

        private static string Digest(string value)
        {
            using (var hash = SHA256.Create())
                return BitConverter.ToString(hash.ComputeHash(
                    Encoding.UTF8.GetBytes(value ?? string.Empty))).Replace("-", "").ToLowerInvariant();
        }

        private static string PathForSeller(string seller)
        {
            var shop = ShopContextLocator.ResolveRuntimeBySellerNick(seller);
            if (shop == null) throw new InvalidOperationException("未能确认店铺范围，拒绝证据写入");
            using (ShopSettingsScope.Enter(shop))
            {
                var parent = Path.GetDirectoryName(BusinessPolicyProfileService.GetUserPath());
                if (string.IsNullOrWhiteSpace(parent)) throw new InvalidOperationException("无店铺隔离目录");
                return Path.Combine(parent, "reception-evidence-ledger.json");
            }
        }

        /// <summary>
        /// Captures an observation, not an operational permission. Free-text transcript,
        /// raw OCR, image URLs, product titles, phone numbers and order IDs never persist.
        /// </summary>
        private static void Append(
            string seller, string buyer, string productId, string sku, string sessionId,
            string messageId, string evidenceText, string kind, string observation,
            string stage, string authority, double confidence)
        {
            if (string.IsNullOrWhiteSpace(seller) || string.IsNullOrWhiteSpace(buyer)) return;
            try
            {
                var file = PathForSeller(seller);
                using (var lease = CrossProcessAtomicStateFile.Acquire(file, "ReceptionEvidenceLedger", 3000))
                {
                    if (lease == null || !lease.Acquired) throw new IOException("跨进程证据锁未取得");
                    string error;
                    var raw = CrossProcessAtomicStateFile.ReadAllTextShared(file, 3, 30, out error);
                    if (!string.IsNullOrWhiteSpace(error)) throw new IOException(error);
                    List<EvidenceRow> records;
                    if (string.IsNullOrWhiteSpace(raw)) records = new List<EvidenceRow>();
                    else
                    {
                        // Corrupted persisted data is an error, not permission to erase history.
                        records = JsonConvert.DeserializeObject<List<EvidenceRow>>(raw);
                        if (records == null) throw new InvalidDataException("证据存储内容结构无效");
                    }
                    var buyerKey = Digest(seller + "\n" + buyer);
                    var sourceKey = Digest(seller + "\n" + buyer + "\n" + (messageId ?? ""));
                    var sourceHash = Digest(evidenceText ?? "");
                    var id = Digest(buyerKey + "|" + (sessionId ?? "") + "|" + kind
                        + "|" + sourceKey + "|" + sourceHash);
                    if (records.Any(x => x != null && x.Id == id)) return;
                    records.Add(new EvidenceRow
                    {
                        Id = id, BuyerKey = buyerKey,
                        ProductKey = Digest(productId ?? ""),
                        SkuKey = Digest(sku ?? ""),
                        SessionKey = Digest(sessionId ?? ""),
                        SourceMessageKey = sourceKey, EvidenceDigest = sourceHash,
                        Kind = kind, Observation = observation,
                        Stage = stage, Authority = authority,
                        Confidence = Math.Max(0, Math.Min(1, confidence)),
                        SchemaVersion = SchemaVersion, ObservedUtcTicks = DateTime.UtcNow.Ticks
                    });
                    var cutoff = DateTime.UtcNow.AddDays(-KeepDays).Ticks;
                    records = records.Where(x => x != null && x.ObservedUtcTicks >= cutoff)
                        .OrderByDescending(x => x.ObservedUtcTicks).Take(MaxRows).ToList();
                    if (!CrossProcessAtomicStateFile.WriteAllTextAtomic(
                        file, JsonConvert.SerializeObject(records, Formatting.Indented), 3, 40, out error))
                        throw new IOException(error);
                }
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount("接待证据记录失败，原主流程继续且不视为授权：" + ex.Message, 10);
            }
        }

        internal static void RecordDeliveredEvidenceRequest(
            string seller, string buyer, string requestText)
        {
            // Called only AFTER authoritative transport confirms delivery.
            // This records an expected image, not payment confirmation.
            Append(seller, buyer, "", "", "", "", requestText, "outbound_instruction",
                "awaiting_image", "awaiting_evidence", "delivered_seller_message", 1.0);
        }

        internal static void RecordVisionObservation(
            string seller, string buyer, string messageKey, string visualSummary,
            double confidence, bool withdrawn)
        {
            if (string.IsNullOrWhiteSpace(messageKey)) return;
            Append(seller, buyer, "", "", "", messageKey, visualSummary, "image",
                withdrawn ? "withdrawn_image_seen" : "image_analyzed",
                "pending_evidence_review", "vision_observation_only", confidence);
        }

        internal static void RecordImageFollowUp(
            string seller, string buyer, string imageMessageKey, string followUpText)
        {
            if (string.IsNullOrWhiteSpace(imageMessageKey)) return;
            // Hashes retain the causal image relation without storing the buyer's words.
            Append(seller, buyer, "", "", "", imageMessageKey, followUpText,
                "image_followup", "linked_to_prior_image", "pending_evidence_review",
                "buyer_referential_text_only", 0);
        }

        internal static string GetAuditJson(string seller)
        {
            try
            {
                var file = PathForSeller(seller);
                string error;
                var raw = CrossProcessAtomicStateFile.ReadAllTextShared(file, 2, 30, out error);
                if (!string.IsNullOrWhiteSpace(error)) throw new IOException(error);
                return string.IsNullOrWhiteSpace(raw) ? "[]" : raw;
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount("读取接待证据审计失败：" + ex.Message, 10);
                return "[]";
            }
        }
    }
}
