using System;
using System.Collections.Generic;
using System.Linq;

namespace Bot.Knowledge
{
    internal static partial class KnowledgeEngineV2Service
    {
        /// <summary>
        /// Applies one record to an already-built snapshot without re-reading or re-parsing
        /// the whole knowledge repository. Enabled records are indexed; disabled records remain
        /// in the snapshot as non-indexed management state so edits do not create a cold-query gap.
        /// A new immutable-style snapshot is assembled under the per-shop build lock and then
        /// swapped atomically, so concurrent buyer queries keep seeing a complete snapshot.
        /// </summary>
        internal static void ApplyRecordUpdate(string seller, KnowledgeV2Record record)
        {
            if (record == null || string.IsNullOrWhiteSpace(record.Id))
            {
                Invalidate(seller);
                return;
            }
            var shop = ResolveShop(seller);
            if (shop == null) return;

            RefreshCommittedRecord(seller, shop.ShopKey, record.Id);
        }

        private static void RefreshCommittedRecord(string seller, string shopKey, string id)
        {
            lock (BuildLocks.GetOrAdd(shopKey, _ => new object()))
            {
                Snapshot current;
                if (!Snapshots.TryGetValue(shopKey, out current) || current == null)
                {
                    Warm(seller);
                    return;
                }
                // Never publish the caller's possibly stale post-commit payload.
                // Read SQLite while holding the same lock used by cold/bulk builds.
                var record = KnowledgeEngineV2Repository.LoadCommittedRecord(seller, id);
                if (record != null) record = KnowledgeV2AuthorityPolicy.NormalizeForRead(record);
                var next = CloneSnapshot(current);
                var index = next.Records.FindIndex(x => x != null
                    && string.Equals(x.Id, id, StringComparison.Ordinal));
                var indexable = record != null && record.Enabled
                    && !string.Equals(record.Status, "disabled", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(record.Status, "deleted", StringComparison.OrdinalIgnoreCase);
                if (index >= 0)
                {
                    var previous = next.Records[index];
                    RemoveRecordFromIndexes(next, previous, index);
                    var replacement = Clone(record) ?? Clone(previous);
                    if (record == null)
                    {
                        replacement.Enabled = false;
                        replacement.Status = "deleted";
                    }
                    next.Records[index] = replacement;
                }
                else if (indexable)
                {
                    index = next.Records.Count;
                    next.Records.Add(Clone(record));
                }

                if (indexable && index >= 0)
                    AddRecordToIndexes(next, next.Records[index], index);

                next.BuiltAt = DateTime.Now;
                Snapshots[shopKey] = next;
            }
        }

        /// <summary>
        /// Removes one record from all runtime indexes without shifting snapshot record positions.
        /// A disabled tombstone is kept in the in-memory list so every existing index id remains stable.
        /// </summary>
        internal static void ApplyRecordDelete(string seller, string id)
        {
            if (string.IsNullOrWhiteSpace(id)) return;
            var shop = ResolveShop(seller);
            if (shop == null) return;

            RefreshCommittedRecord(seller, shop.ShopKey, id);
        }

        /// <summary>
        /// Replaces the complete runtime snapshot in one atomic swap. Bulk import/restore therefore
        /// keeps serving the previous snapshot until the new one is fully built instead of exposing
        /// an empty/cold interval to buyer traffic.
        /// </summary>
        internal static void ReplaceSnapshot(string seller, IEnumerable<KnowledgeV2Record> records)
        {
            var shop = ResolveShop(seller);
            if (shop == null) return;
            lock (BuildLocks.GetOrAdd(shop.ShopKey, _ => new object()))
            {
                // A bulk/cloud callback can arrive after a newer local edit.
                // Rebuild from the committed repository, never the old caller list.
                var enabled = KnowledgeEngineV2Repository.LoadAll(seller)
                    .Where(x => x != null)
                    .Select(KnowledgeV2AuthorityPolicy.NormalizeForRead)
                    .Where(x => x != null && x.Enabled
                        && !string.Equals(x.Status, "disabled", StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(x.Status, "deleted", StringComparison.OrdinalIgnoreCase))
                    .ToList();
                Snapshots[shop.ShopKey] = BuildSnapshot(shop.ShopKey, enabled);
            }
        }

        private static Snapshot CloneSnapshot(Snapshot source)
        {
            return new Snapshot
            {
                ShopKey = source.ShopKey,
                BuiltAt = source.BuiltAt,
                Records = source.Records.Select(Clone).ToList(),
                Exact = CloneIndex(source.Exact),
                Intent = CloneIndex(source.Intent),
                Predicate = CloneIndex(source.Predicate),
                Entity = CloneIndex(source.Entity),
                Ngram = CloneIndex(source.Ngram)
            };
        }

        private static Dictionary<string, HashSet<int>> CloneIndex(Dictionary<string, HashSet<int>> source)
        {
            var result = NewIndex();
            foreach (var pair in source ?? NewIndex())
                result[pair.Key] = new HashSet<int>(pair.Value ?? new HashSet<int>());
            return result;
        }

        private static void AddRecordToIndexes(Snapshot snapshot, KnowledgeV2Record record, int id)
        {
            if (snapshot == null || record == null || !record.Enabled) return;
            Add(snapshot.Intent, record.Intent, id);
            Add(snapshot.Predicate, record.Predicate, id);
            foreach (var entity in RecordEntities(record))
                Add(snapshot.Entity, KnowledgeEngineV2Semantics.Compact(entity), id);
            foreach (var alias in (record.Aliases ?? new List<string>()).Concat(new[] { record.Title }))
            {
                var exact = KnowledgeEngineV2Semantics.Compact(alias);
                if (exact.Length >= 2) Add(snapshot.Exact, exact, id);
                foreach (var gram in KnowledgeEngineV2Semantics.RecallNgrams(exact, 2, 24))
                    Add(snapshot.Ngram, gram, id);
            }
        }

        private static void RemoveRecordFromIndexes(Snapshot snapshot, KnowledgeV2Record record, int id)
        {
            if (snapshot == null || record == null) return;
            Remove(snapshot.Intent, record.Intent, id);
            Remove(snapshot.Predicate, record.Predicate, id);
            foreach (var entity in RecordEntities(record))
                Remove(snapshot.Entity, KnowledgeEngineV2Semantics.Compact(entity), id);
            foreach (var alias in (record.Aliases ?? new List<string>()).Concat(new[] { record.Title }))
            {
                var exact = KnowledgeEngineV2Semantics.Compact(alias);
                if (exact.Length >= 2) Remove(snapshot.Exact, exact, id);
                foreach (var gram in KnowledgeEngineV2Semantics.RecallNgrams(exact, 2, 24))
                    Remove(snapshot.Ngram, gram, id);
            }
        }

        private static void Remove(Dictionary<string, HashSet<int>> index, string key, int id)
        {
            key = (key ?? string.Empty).Trim();
            if (key.Length == 0 || key == "general") return;
            HashSet<int> values;
            if (!index.TryGetValue(key, out values) || values == null) return;
            values.Remove(id);
            if (values.Count == 0) index.Remove(key);
        }
    }
}
