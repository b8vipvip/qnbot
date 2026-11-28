using Bot.ChromeNs;
using Bot.ShopScope;
using BotLib;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Bot
{
    public partial class App
    {
        private readonly object _knowledgeV2CloudSyncBootstrap =
            Knowledge.KnowledgeV2CloudSyncService.InitializeForApp();
    }
}

namespace Bot.Knowledge
{
    internal static class KnowledgeV2CloudSyncService
    {
        private const string SyncPreferenceScope = "shop-cloud";
        private const string SyncPreferenceEnabledKey = "KnowledgeCloudSyncEnabled";

        internal static bool IsEnabledForShop(ShopContext shop)
        {
            if (shop == null) return false;
            using (ShopSettingsScope.Enter(shop))
            {
                return string.Equals(
                    BotLib.Db.Sqlite.PersistentParams.GetParam2Key(
                        SyncPreferenceEnabledKey, SyncPreferenceScope, "false"),
                    "true",
                    StringComparison.OrdinalIgnoreCase);
            }
        }

        internal static void SetEnabledForShop(ShopContext shop, bool enabled, bool queueSync)
        {
            if (shop == null) return;
            using (ShopSettingsScope.Enter(shop))
            {
                BotLib.Db.Sqlite.PersistentParams.TrySaveParam2Key(
                    SyncPreferenceEnabledKey, SyncPreferenceScope, enabled ? "true" : "false");
            }
            if (enabled && queueSync) QueueSync(shop);
        }

        private const string SettingsScope = "knowledge-v2-cloud";
        private const string RevisionKey = "KnowledgeV2CloudRevision";
        private const string FingerprintKey = "KnowledgeV2CloudFingerprint";
        private const string BaselineKey = "KnowledgeV2CloudRecordBaseline";

        private sealed class State
        {
            public readonly object SyncRoot = new object();
            public int Syncing;
            public int Revision;
            public string Fingerprint = string.Empty;
            public Dictionary<string,string> RecordBaseline = new Dictionary<string,string>(StringComparer.Ordinal);
            public bool BaselinePersisted;
            public string ShopRef = string.Empty;
            public DateTime? LastAttemptUtc;
            public DateTime? LastSuccessUtc;
            public string LastStatus = "not_started";
            public int LastLocalRecordCount;
            public int LastCloudRecordCount;
            public string LastLocalFingerprintPrefix = string.Empty;
            public string LastCloudFingerprintPrefix = string.Empty;
            public bool LastConverged;
            public string FeedbackStatus = "not_started";
        }
        private static readonly ShopScopedPathProvider Paths = new ShopScopedPathProvider();
        private static readonly ShopProfileStore Profiles = new ShopProfileStore(Paths);
        private static readonly ConcurrentDictionary<string, State> States = new ConcurrentDictionary<string, State>(StringComparer.Ordinal);
        private static Timer _timer; private static int _initialized;

        public static object InitializeForApp()
        {
            if (Interlocked.Exchange(ref _initialized,1)==0)
            {
                _timer=new Timer(_=>QueueAll(),null,9000,10000);
                Log.Info("Knowledge V2 云同步服务已启动：按 ShopKey/客户端令牌隔离。");
            }
            return new object();
        }

        private static void QueueAll()
        {
            IList<ShopProfile> profiles;
            try { profiles = Profiles.GetAll(); }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount("Knowledge V2 云同步读取店铺列表失败：" + ex.Message, 10);
                return;
            }

            // A damaged/removed profile or one shop's bad local settings must not abort
            // the timer pass and starve every later ShopKey in the same process.
            foreach (var profile in profiles ?? new List<ShopProfile>())
            {
                if (profile == null) continue;
                try
                {
                    var shop = profile.ToContext();
                    // Recover even when cloud sync is disabled. The durable import intent
                    // must finish before sending a replacement corpus to another device.
                    if (shop != null && shop.HasStableSellerId)
                        using (ShopSettingsScope.Enter(shop))
                            KnowledgeEngineV2Repository.ResumePortableImport(shop.DisplayName);
                    Queue(shop);
                }
                catch (Exception ex)
                {
                    Log.ErrorWithMaxCount("Knowledge V2 云同步跳过异常店铺并继续处理其他店铺："
                        + ex.Message, 10);
                }
            }
        }
        private static void Queue(ShopContext shop)
        {
            if(shop==null || !IsEnabledForShop(shop)
                || !ShopTokenBindingService.ShouldAttemptSync(shop)) return;
            var state=States.GetOrAdd(shop.ShopKey,_=>LoadState(shop));
            if(Interlocked.Exchange(ref state.Syncing,1)!=0) return;
            lock(state.SyncRoot)
            {
                state.LastAttemptUtc=DateTime.UtcNow;
                state.LastStatus="running";
                state.LastConverged=false;
            }
            WriteDiagnosticsSnapshot();
            Task.Run(async()=>
            {
                try
                {
                    await SyncOnce(shop,state);
                }
                catch(Exception ex)
                {
                    lock(state.SyncRoot) state.LastStatus="failed";
                    Log.ErrorWithMaxCount("Knowledge V2 云同步失败："+Safe(ex.Message,260),20);
                }
                finally
                {
                    Interlocked.Exchange(ref state.Syncing,0);
                    WriteDiagnosticsSnapshot();
                }
            });
        }
        internal static void QueueSync(ShopContext shop)
        {
            Queue(shop);
        }

        internal static async Task<bool> SyncNowAsync(ShopContext shop)
        {
            if (shop == null) throw new ArgumentNullException(nameof(shop));
            SetEnabledForShop(shop, true, false);
            var state = States.GetOrAdd(shop.ShopKey, _ => LoadState(shop));
            var acquired = false;
            for (var i = 0; i < 80; i++)
            {
                if (Interlocked.CompareExchange(ref state.Syncing, 1, 0) == 0)
                {
                    acquired = true;
                    break;
                }
                await Task.Delay(125).ConfigureAwait(false);
            }
            if (!acquired)
                throw new InvalidOperationException("本店 Knowledge V2 正在同步，请稍后重试。");

            lock (state.SyncRoot)
            {
                state.LastAttemptUtc = DateTime.UtcNow;
                state.LastStatus = "running";
                state.LastConverged = false;
            }
            WriteDiagnosticsSnapshot();
            try
            {
                await SyncOnce(shop, state).ConfigureAwait(false);
                lock (state.SyncRoot)
                {
                    if (state.LastStatus == "credentials_missing")
                        throw new InvalidOperationException("本店云同步缺少有效服务端地址或令牌。");
                    return state.LastStatus == "synced" && state.LastConverged;
                }
            }
            catch (Exception ex)
            {
                lock (state.SyncRoot) state.LastStatus = "failed";
                Log.ErrorWithMaxCount("Knowledge V2 手动云同步失败: " + Safe(ex.Message, 260), 10);
                throw;
            }
            finally
            {
                Interlocked.Exchange(ref state.Syncing, 0);
                WriteDiagnosticsSnapshot();
            }
        }

        private static async Task SyncOnce(ShopContext shop,State state)
        {
            using(ShopSettingsScope.Enter(shop))
            {
                KnowledgeEngineV2Repository.ResumePortableImport(shop.DisplayName);
                var connection=new ShopControlPlaneConnectionStore(shop,Paths);
                string token,error;
                var url=connection.GetServerUrl();
                if(!connection.TryGetToken(out token,out error)||string.IsNullOrWhiteSpace(url)||string.IsNullOrWhiteSpace(token))
                {
                    lock(state.SyncRoot) state.LastStatus="credentials_missing";
                    return;
                }

                // Statistics sync is independent of knowledge revision conflicts. A failed
                // feedback request does not prevent the knowledge snapshot from syncing.
                try
                {
                    await SyncFeedbackOnce(shop, url, token).ConfigureAwait(false);
                    lock (state.SyncRoot) state.FeedbackStatus = "synced";
                }
                catch (Exception ex)
                {
                    lock (state.SyncRoot) state.FeedbackStatus = "failed";
                    Log.ErrorWithMaxCount("Knowledge V2 反馈同步失败：" + Safe(ex.Message, 180), 10);
                }

                // Immutable revision/rollback history sync has its own durable
                // cursor. A history outage cannot prevent the active corpus sync.
                try { await SyncRevisionHistoryOnce(shop, url, token).ConfigureAwait(false); }
                catch (Exception ex)
                {
                    Log.ErrorWithMaxCount("Knowledge V2修订历史同步失败：" + Safe(ex.Message, 180), 10);
                }

                var local=KnowledgeEngineV2Repository.LoadAll(shop.DisplayName);
                var localFingerprint=Fingerprint(local);
                // Upgrade an older aggregate-only checkpoint only when the local
                // snapshot is provably identical to the acknowledged server state.
                if (state.RecordBaseline.Count == 0 && localFingerprint == state.Fingerprint)
                    state.RecordBaseline = RecordBaseline(local);
                // An acknowledged local snapshot can already contain independently
                // generated duplicate evidence IDs. Repair those deletes before uploading.
                var normalizedLocal = NormalizeEvidenceDeletes(local, DeletedEvidenceKeys(local));
                if (!string.Equals(Fingerprint(normalizedLocal), localFingerprint, StringComparison.Ordinal))
                {
                    Backup(shop, local, state.Revision);
                    if (!KnowledgeEngineV2Repository.TryReplaceAllIfUnchanged(
                        shop.DisplayName, localFingerprint, normalizedLocal))
                    {
                        lock(state.SyncRoot) state.LastStatus = "local_changed_during_delete_reconciliation";
                        return;
                    }
                    local = KnowledgeEngineV2Repository.LoadAll(shop.DisplayName);
                    localFingerprint = Fingerprint(local);
                }
                lock(state.SyncRoot)
                {
                    state.LastLocalRecordCount=local.Count;
                    state.LastLocalFingerprintPrefix=FingerprintPrefix(localFingerprint);
                    state.LastCloudFingerprintPrefix=FingerprintPrefix(state.Fingerprint);
                    state.LastCloudRecordCount=0;
                    state.LastConverged=false;
                }
                var payload=new JObject { ["revision"]=state.Revision };
                if(state.Revision==0 || !string.Equals(localFingerprint,state.Fingerprint,StringComparison.Ordinal))
                    payload["records"]=JArray.FromObject(local);

                ServicePointManager.SecurityProtocol|=SecurityProtocolType.Tls12;
                using(var handler=new HttpClientHandler{UseProxy=true,Proxy=WebRequest.DefaultWebProxy})
                using(var http=new HttpClient(handler))
                {
                    http.Timeout=TimeSpan.FromSeconds(45);
                    using(var response=await ShopRuntimeCredentialAuthority.SendWithUnauthorizedRecoveryAsync(
                        shop,
                        token,
                        async requestToken =>
                        {
                            using(var request=new HttpRequestMessage(HttpMethod.Post,url.TrimEnd('/')+"/api/runtime/v1/bot-web/knowledge-v2-sync"))
                            {
                                request.Headers.Authorization=new AuthenticationHeaderValue("Bearer",requestToken);
                                request.Headers.TryAddWithoutValidation("X-Shop-Key",shop.ShopKey);
                                request.Headers.TryAddWithoutValidation("User-Agent","qianniu-knowledge-v2-cloud/1.0");
                                request.Content=new StringContent(payload.ToString(Formatting.None),Encoding.UTF8,"application/json");
                                return await http.SendAsync(request).ConfigureAwait(false);
                            }
                        }))
                    {
                        var body=await response.Content.ReadAsStringAsync();
                        if(!response.IsSuccessStatusCode) throw new Exception("HTTP "+(int)response.StatusCode+" "+Safe(body,200));
                        var root=JObject.Parse(body);
                        var revisionToken=root["revision"];
                        var recordsToken=root["records"] as JArray;
                        if(revisionToken==null || revisionToken.Type!=JTokenType.Integer || recordsToken==null)
                            throw new InvalidOperationException("Knowledge V2 云端响应缺少有效 revision/records，已保留同步检查点。");
                        var cloudRevision=revisionToken.Value<int>();
                        if(cloudRevision<0)
                            throw new InvalidOperationException("Knowledge V2 云端 revision 无效。");
                        var records=recordsToken.ToObject<List<KnowledgeV2Record>>();
                        if(records==null || records.Any(x=>x==null))
                            throw new InvalidOperationException("Knowledge V2 云端 records 无效。");
                        var cloudFingerprint=Fingerprint(records);
                        var snapshotAcknowledged=string.Equals(cloudFingerprint,localFingerprint,StringComparison.Ordinal);
                        var syncWarning = root["sync_warning"] as JObject;
                        var missingTombstones = syncWarning != null
                            && (string.Equals((string)syncWarning["code"], "missing_tombstones", StringComparison.Ordinal)
                                || string.Equals((string)syncWarning["code"], "cloud_delete_wins", StringComparison.Ordinal));

                        if(missingTombstones && cloudRevision == state.Revision)
                        {
                            // The server refused a Windows full-snapshot replacement because
                            // the snapshot omitted cloud IDs without tombstones or attempted to
                            // resurrect a cloud-deleted record. Reconcile from the authoritative
                            // cloud snapshot and preserve local-only/stale facts as disabled
                            // conflict candidates. Never retry the unsafe snapshot as an overwrite.
                            var latestLocal = KnowledgeEngineV2Repository.LoadAll(shop.DisplayName);
                            var latestFingerprint = Fingerprint(latestLocal);
                            var merged = MergeCloudAndLocal(records, latestLocal, preserveLocalOnly: true, baseline: state.RecordBaseline);
                            var mergedFingerprint = Fingerprint(merged);
                            if(!string.Equals(mergedFingerprint, latestFingerprint, StringComparison.Ordinal))
                            {
                                var backup = Backup(shop, latestLocal, cloudRevision);
                                if(!KnowledgeEngineV2Repository.TryReplaceAllIfUnchanged(
                                    shop.DisplayName, latestFingerprint, merged))
                                {
                                    lock(state.SyncRoot) state.LastStatus = "local_changed_during_tombstone_recovery";
                                    Log.Info("Knowledge V2检测到本地快照缺少云端记录且本地同时变化，本轮不覆盖并等待重试: shopRef="
                                        + MyWebSocketServer.DiagnosticRef("shop", shop.ShopKey));
                                    return;
                                }
                                var conflictCountToken = syncWarning["missing_count"] ?? syncWarning["conflict_count"];
                                var conflictCount = conflictCountToken == null ? 0 : (int)conflictCountToken;
                                Log.Info("Knowledge V2已应用云端删除墓碑优先规则并从权威快照恢复本地记录: shopRef="
                                    + MyWebSocketServer.DiagnosticRef("shop", shop.ShopKey)
                                    + ", tombstone_conflicts=" + conflictCount
                                    + ", cloud_records=" + records.Count
                                    + ", merged_records=" + merged.Count
                                    + ", backup=" + Path.GetFileName(backup));
                            }

                            PersistState(shop, state, cloudRevision, cloudFingerprint, records);
                            var recoveredLocal = KnowledgeEngineV2Repository.LoadAll(shop.DisplayName);
                            var recoveredFingerprint = Fingerprint(recoveredLocal);
                            lock(state.SyncRoot)
                            {
                                state.LastLocalRecordCount = recoveredLocal.Count;
                                state.LastCloudRecordCount = records.Count;
                                state.LastLocalFingerprintPrefix = FingerprintPrefix(recoveredFingerprint);
                                state.LastCloudFingerprintPrefix = FingerprintPrefix(cloudFingerprint);
                                state.LastConverged = string.Equals(recoveredFingerprint, cloudFingerprint, StringComparison.Ordinal);
                                state.LastSuccessUtc = DateTime.UtcNow;
                                state.LastStatus = state.LastConverged ? "synced" : "merge_pending_push";
                            }
                            return;
                        }

                        if(cloudRevision<state.Revision)
                        {
                            // A confirmed token rebind or server-side state reset can
                            // legitimately create a fresh cloud revision namespace while
                            // this shop's local corpus remains intact. Back up the full local
                            // corpus, reset only the local sync checkpoint, then let the next
                            // poll re-negotiate from revision 0. If the server still has data,
                            // runtime_sync returns it and the normal merge path preserves both
                            // local-only facts and cloud facts as disabled conflict candidates.
                            var previousRevision=state.Revision;
                            var rollbackBackup=Backup(shop,local,cloudRevision);
                            PersistState(shop,state,0,string.Empty);
                            lock(state.SyncRoot)
                            {
                                state.LastCloudRecordCount=records==null?0:records.Count;
                                state.LastConverged=false;
                                state.LastStatus="cloud_revision_reset_reconcile";
                            }
                            Log.Info("Knowledge V2 检测到云端 revision 命名空间重置，已备份本地语料并重置同步检查点；下一轮将重新协商而非覆盖: shopRef="
                                +MyWebSocketServer.DiagnosticRef("shop",shop.ShopKey)
                                +", local_revision="+previousRevision+", cloud_revision="+cloudRevision
                                +", backup="+Path.GetFileName(rollbackBackup));
                            return;
                        }
                        // A higher revision can be the acknowledgement of our own upload, not
                        // a competing cloud edit. Do not merge the acknowledged old snapshot
                        // over edits/deletes made locally while the HTTP request was in flight.
                        if(cloudRevision>state.Revision && !snapshotAcknowledged)
                        {
                            lock(state.SyncRoot)
                            {
                                state.LastCloudRecordCount=records.Count;
                                state.LastCloudFingerprintPrefix=FingerprintPrefix(cloudFingerprint);
                            }

                            // Re-read after the network round-trip. A user or learning task may
                            // have edited V2 while the HTTP request was in flight.
                            var latestLocal=KnowledgeEngineV2Repository.LoadAll(shop.DisplayName);
                            var latestFingerprint=Fingerprint(latestLocal);
                            var hasBaseline=!string.IsNullOrWhiteSpace(state.Fingerprint);
                            var localDirty=hasBaseline
                                ? !string.Equals(latestFingerprint,state.Fingerprint,StringComparison.Ordinal)
                                : latestLocal.Count>0;
                            var merged=localDirty
                                ? MergeCloudAndLocal(records,latestLocal,preserveLocalOnly:true,baseline:state.RecordBaseline)
                                : MergeCloudAndLocal(records, Enumerable.Empty<KnowledgeV2Record>(), preserveLocalOnly:true);
                            var mergedFingerprint=Fingerprint(merged);

                            if(!string.Equals(mergedFingerprint,latestFingerprint,StringComparison.Ordinal))
                            {
                                // A complete local snapshot is written before the conditional
                                // replacement. If backup creation fails, no cloud data is applied.
                                var backup=Backup(shop,latestLocal,cloudRevision);
                                if(!KnowledgeEngineV2Repository.TryReplaceAllIfUnchanged(
                                    shop.DisplayName,latestFingerprint,merged))
                                {
                                    lock(state.SyncRoot) state.LastStatus="local_changed_during_sync";
                                    Log.Info("Knowledge V2 云端版本到达时本地知识同时发生变化，本轮未覆盖；将于下一轮重新合并: shopRef="
                                        + MyWebSocketServer.DiagnosticRef("shop",shop.ShopKey));
                                    return;
                                }
                                Log.Info("Knowledge V2 云端版本已安全合并: shopRef="
                                    + MyWebSocketServer.DiagnosticRef("shop",shop.ShopKey)
                                    + ", revision="+cloudRevision+", cloud_records="+records.Count
                                    + ", merged_records="+merged.Count+", backup="+Path.GetFileName(backup));
                            }

                            // Persist the cloud snapshot as the next comparison baseline.
                            // If local-only/conflicting records were retained, the next poll
                            // sends the merged set at this revision instead of discarding it.
                            PersistState(shop,state,cloudRevision,cloudFingerprint,records);
                            var finalLocal=KnowledgeEngineV2Repository.LoadAll(shop.DisplayName);
                            var finalLocalFingerprint=Fingerprint(finalLocal);
                            lock(state.SyncRoot)
                            {
                                state.LastLocalRecordCount=finalLocal.Count;
                                state.LastCloudRecordCount=records.Count;
                                state.LastLocalFingerprintPrefix=FingerprintPrefix(finalLocalFingerprint);
                                state.LastCloudFingerprintPrefix=FingerprintPrefix(cloudFingerprint);
                                state.LastConverged=string.Equals(finalLocalFingerprint,cloudFingerprint,StringComparison.Ordinal);
                                state.LastSuccessUtc=DateTime.UtcNow;
                                state.LastStatus=state.LastConverged?"synced":"merge_pending_push";
                            }
                            return;
                        }

                        // Same-revision responses must not silently acknowledge data the
                        // server did not return. Preserve the old checkpoint on mismatch.
                        if(!snapshotAcknowledged)
                            throw new InvalidOperationException("Knowledge V2 同版本云端内容与请求快照不一致，已保留检查点等待重试。");
                        PersistState(shop,state,cloudRevision,localFingerprint,local);
                        var afterRequestLocal=KnowledgeEngineV2Repository.LoadAll(shop.DisplayName);
                        var afterRequestFingerprint=Fingerprint(afterRequestLocal);
                        lock(state.SyncRoot)
                        {
                            state.LastLocalRecordCount=afterRequestLocal.Count;
                            state.LastCloudRecordCount=local.Count;
                            state.LastLocalFingerprintPrefix=FingerprintPrefix(afterRequestFingerprint);
                            state.LastCloudFingerprintPrefix=FingerprintPrefix(localFingerprint);
                            state.LastConverged=string.Equals(afterRequestFingerprint,localFingerprint,StringComparison.Ordinal);
                            state.LastSuccessUtc=DateTime.UtcNow;
                            state.LastStatus=state.LastConverged?"synced":"local_changed_during_sync";
                        }
                    }
                }
            }
        }

        private static async Task SyncRevisionHistoryOnce(ShopContext shop, string url, string token)
        {
            var scope = url.TrimEnd('/') + "|" + shop.ShopKey;
            using (var handler = new HttpClientHandler { UseProxy = true, Proxy = WebRequest.DefaultWebProxy })
            using (var http = new HttpClient(handler))
            {
                http.Timeout = TimeSpan.FromSeconds(15);
                for (var i = 0; i < 4; i++)
                {
                    var payload = KnowledgeEngineV2Repository.BuildRevisionSyncRequest(shop.DisplayName, scope);
                    payload["device_id"] = BotWebConsoleSyncService.LocalKnowledgeDeviceId;
                    using (var response = await ShopRuntimeCredentialAuthority.SendWithUnauthorizedRecoveryAsync(
                        shop, token, async requestToken =>
                        {
                            using (var request = new HttpRequestMessage(HttpMethod.Post, url.TrimEnd('/') + "/api/runtime/v1/bot-web/knowledge-v2-revision-sync"))
                            {
                                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", requestToken);
                                request.Headers.TryAddWithoutValidation("X-Shop-Key", shop.ShopKey);
                                request.Content = new StringContent(payload.ToString(Formatting.None), Encoding.UTF8, "application/json");
                                return await http.SendAsync(request).ConfigureAwait(false);
                            }
                        }))
                    {
                        if (!response.IsSuccessStatusCode)
                            throw new InvalidOperationException("修订同步 HTTP " + (int)response.StatusCode);
                        var body = JObject.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
                        if (!KnowledgeEngineV2Repository.ApplyRevisionSyncResponse(shop.DisplayName, scope, payload, body)) break;
                    }
                }
            }
        }

        private static async Task SyncFeedbackOnce(ShopContext shop, string url, string token)
        {
            var scope = url.TrimEnd('/').ToLowerInvariant() + "|" + shop.ShopKey;
            using (var handler = new HttpClientHandler { UseProxy = true, Proxy = WebRequest.DefaultWebProxy })
            using (var http = new HttpClient(handler))
            {
                http.Timeout = TimeSpan.FromSeconds(15);
                // Bound each timer pass; durable cursor/acks resume subsequent pages.
                for (var i = 0; i < 4; i++)
                {
                    var payload = KnowledgeEngineV2FeedbackService.BuildCloudFeedbackRequest(shop.DisplayName, scope);
                    payload["device_id"] = BotWebConsoleSyncService.LocalKnowledgeDeviceId;
                    using (var response = await ShopRuntimeCredentialAuthority.SendWithUnauthorizedRecoveryAsync(
                        shop, token, async requestToken =>
                        {
                            using (var request = new HttpRequestMessage(HttpMethod.Post, url.TrimEnd('/') + "/api/runtime/v1/bot-web/knowledge-v2-feedback-sync"))
                            {
                                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", requestToken);
                                request.Headers.TryAddWithoutValidation("X-Shop-Key", shop.ShopKey);
                                request.Content = new StringContent(payload.ToString(Formatting.None), Encoding.UTF8, "application/json");
                                return await http.SendAsync(request).ConfigureAwait(false);
                            }
                        }))
                    {
                        if (!response.IsSuccessStatusCode)
                            throw new InvalidOperationException("反馈同步 HTTP " + (int)response.StatusCode);
                        var body = JObject.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
                        if (!KnowledgeEngineV2FeedbackService.ApplyCloudFeedbackResponse(shop.DisplayName, scope, payload, body)) break;
                    }
                }
            }
        }

        private static State LoadState(ShopContext shop)
        {
            using(ShopSettingsScope.Enter(shop))
            {
                var store=new ShopScopedSettingsStore(shop,Paths);
                string revisionText;
                int revision;
                if(!store.TryGetString(RevisionKey,out revisionText)
                    || !int.TryParse(revisionText,out revision))
                    revision=0;
                string fingerprint;
                store.TryGetString(FingerprintKey,out fingerprint);
                string baselineJson;
                store.TryGetString(BaselineKey,out baselineJson);
                var baseline = new Dictionary<string,string>(StringComparer.Ordinal);
                var baselinePersisted = false;
                if (!string.IsNullOrWhiteSpace(baselineJson))
                {
                    try
                    {
                        var stored = JObject.Parse(baselineJson);
                        if ((string)stored["fingerprint"] == fingerprint && (int?)stored["revision"] == revision
                            && stored["records"] is JObject)
                        {
                            baseline = stored["records"].ToObject<Dictionary<string,string>>();
                            baselinePersisted = baseline != null && baseline.All(x => !string.IsNullOrWhiteSpace(x.Key)
                                && x.Value != null && x.Value.Length == 64);
                            if (!baselinePersisted) baseline = new Dictionary<string,string>(StringComparer.Ordinal);
                        }
                    }
                    catch (JsonException)
                    {
                        // Missing/invalid baseline must never authorize an overwrite.
                        baseline.Clear();
                    }
                }
                return new State
                {
                    Revision=Math.Max(0,revision),
                    Fingerprint=fingerprint??string.Empty,
                    RecordBaseline=baseline,
                    BaselinePersisted=baselinePersisted,
                    ShopRef=MyWebSocketServer.RuntimeAcceptanceDiagnosticRef("shop",shop.ShopKey),
                    LastCloudFingerprintPrefix=FingerprintPrefix(fingerprint??string.Empty),
                    LastStatus="loaded"
                };
            }
        }

        private static void PersistState(ShopContext shop,State state,int targetRevision,string targetFingerprint,
            IEnumerable<KnowledgeV2Record> baselineRecords = null)
        {
            if(state==null) throw new ArgumentNullException(nameof(state));
            var revision=Math.Max(0,targetRevision).ToString();
            var fingerprint=targetFingerprint??string.Empty;
            // Steady-state polling should not serialize thousands of record hashes
            // or rewrite shop settings every ten seconds.
            lock(state.SyncRoot)
                if (state.BaselinePersisted && state.Revision==Math.Max(0,targetRevision)
                    && string.Equals(state.Fingerprint,fingerprint,StringComparison.Ordinal)) return;
            var baseline=RecordBaseline(baselineRecords);
            var baselineJson=new JObject { ["revision"]=Math.Max(0,targetRevision), ["fingerprint"]=fingerprint,
                ["records"]=JObject.FromObject(baseline) }.ToString(Formatting.None);
            using(ShopSettingsScope.Enter(shop))
            {
                var store=new ShopScopedSettingsStore(shop,Paths);
                store.MergeValues(new Dictionary<string,string>(StringComparer.Ordinal)
                {
                    { RevisionKey, revision },
                    { FingerprintKey, fingerprint },
                    { BaselineKey, baselineJson }
                }, true);
                string storedRevision,storedFingerprint,storedBaseline;
                if(!store.TryGetString(RevisionKey,out storedRevision)
                    || !store.TryGetString(FingerprintKey,out storedFingerprint)
                    || !string.Equals(storedRevision,revision,StringComparison.Ordinal)
                    || !string.Equals(storedFingerprint,fingerprint,StringComparison.Ordinal)
                    || !store.TryGetString(BaselineKey,out storedBaseline)
                    || !string.Equals(storedBaseline,baselineJson,StringComparison.Ordinal))
                    throw new InvalidOperationException("Knowledge V2 云同步 revision/hash 持久化失败；已保留上次成功检查点。");
            }

            // Do not advance the in-memory checkpoint until both persisted values have
            // been read back successfully. A transient disk/DPAPI error must cause the
            // next poll to retry from the previous revision instead of skipping changes.
            lock(state.SyncRoot)
            {
                state.Revision=Math.Max(0,targetRevision);
                state.Fingerprint=targetFingerprint??string.Empty;
                state.RecordBaseline=baseline;
                state.BaselinePersisted=true;
            }
        }

        private static string RecordContentFingerprint(KnowledgeV2Record source)
        {
            var identity=CloneRecord(source);
            identity.UseCount=identity.AcceptedCount=identity.CorrectionCount=identity.WithdrawCount=0;
            identity.CreatedAt=identity.UpdatedAt=identity.LastVerifiedAt=DateTime.MinValue;
            return Fingerprint(new[]{identity});
        }

        private static Dictionary<string,string> RecordBaseline(IEnumerable<KnowledgeV2Record> records)
        {
            return (records??Enumerable.Empty<KnowledgeV2Record>()).Where(x=>x!=null&&!string.IsNullOrWhiteSpace(x.Id))
                .GroupBy(x=>x.Id,StringComparer.Ordinal)
                .ToDictionary(g=>g.Key,g=>RecordContentFingerprint(g.First()),StringComparer.Ordinal);
        }

        internal static List<KnowledgeV2Record> MergeCloudAndLocal(
            IEnumerable<KnowledgeV2Record> cloudRecords,
            IEnumerable<KnowledgeV2Record> localRecords,
            bool preserveLocalOnly,
            IDictionary<string,string> baseline = null)
        {
            // Determine the delete set across both snapshots before iterating either.
            // Keep a tombstone for every affected record ID: the server rejects snapshots
            // that silently omit an existing cloud ID, even when another ID has the same evidence.
            var cloudInput = (cloudRecords ?? Enumerable.Empty<KnowledgeV2Record>()).Where(x => x != null).ToList();
            var localInput = (localRecords ?? Enumerable.Empty<KnowledgeV2Record>()).Where(x => x != null).ToList();
            var deletedEvidence = DeletedEvidenceKeys(cloudInput.Concat(localInput));
            cloudRecords = NormalizeEvidenceDeletes(cloudInput, deletedEvidence);
            localRecords = NormalizeEvidenceDeletes(localInput, deletedEvidence);

            var merged = new List<KnowledgeV2Record>();
            var cloudById = new Dictionary<string, KnowledgeV2Record>(StringComparer.Ordinal);
            var recordsByHumanEvidence = new Dictionary<string, KnowledgeV2Record>(StringComparer.Ordinal);
            var mergedIds = new HashSet<string>(StringComparer.Ordinal);

            // A verified FAQ source ID is a deterministic hash of shop + session +
            // original buyer message ID + original human reply ID. Two devices can
            // independently synthesize the same evidence into different random record
            // IDs. Keep one production record for that evidence; conflicting AI outputs
            // are preserved as disabled review candidates, never active duplicates.
            foreach (var raw in cloudRecords ?? Enumerable.Empty<KnowledgeV2Record>())
            {
                if (raw == null) continue;
                var record = CloneRecord(raw);
                // Never collapse tombstones with different IDs.
                var evidenceId = IsDeleteTombstone(record) ? string.Empty : AutoPublishedEvidenceKey(record);
                KnowledgeV2Record existingEvidence;
                if (evidenceId.Length > 0 && recordsByHumanEvidence.TryGetValue(evidenceId, out existingEvidence))
                {
                    if (SameRecordContent(existingEvidence, record))
                        MergeRecordMetadata(existingEvidence, record);
                    // The server requires every previously committed ID to remain present.
                    // Do not tombstone this duplicate: an evidence-key tombstone would delete
                    // the canonical active record too. Retain a disabled candidate at its ID.
                    var duplicate = CloneRecord(record);
                    duplicate.SourceType = "cloud_sync_conflict_candidate";
                    duplicate.SourceId = record.Id;
                    duplicate.Enabled = false;
                    duplicate.Status = "candidate";
                    AddUnique(merged, mergedIds, duplicate);
                    cloudById[duplicate.Id] = duplicate;
                    continue;
                }

                AddUnique(merged, mergedIds, record);
                var id = (record.Id ?? string.Empty).Trim();
                if (id.Length > 0 && !cloudById.ContainsKey(id)) cloudById[id] = record;
                if (evidenceId.Length > 0) recordsByHumanEvidence[evidenceId] = record;
            }

            foreach (var local in localRecords ?? Enumerable.Empty<KnowledgeV2Record>())
            {
                if (local == null) continue;
                var evidenceId = IsDeleteTombstone(local) ? string.Empty : AutoPublishedEvidenceKey(local);
                var id = (local.Id ?? string.Empty).Trim();
                KnowledgeV2Record cloud;
                if (id.Length > 0 && cloudById.TryGetValue(id, out cloud))
                {
                    var localDeleted = IsDeleteTombstone(local);
                    var cloudDeleted = IsDeleteTombstone(cloud);
                    if (localDeleted && !cloudDeleted)
                    {
                        // Explicit user deletion is a delete-wins tombstone. Preserve a
                        // concurrent remote edit as a disabled review candidate, but never
                        // leave it active or resurrect it on the deleting device.
                        AddCandidate(merged, mergedIds, cloud, id);
                        var tombstone = CloneRecord(local);
                        var index = merged.IndexOf(cloud);
                        if (index >= 0) merged[index] = tombstone;
                        cloudById[id] = tombstone;
                        var cloudEvidenceId = AutoPublishedEvidenceKey(cloud);
                        if (cloudEvidenceId.Length > 0) recordsByHumanEvidence[cloudEvidenceId] = tombstone;
                        if (evidenceId.Length > 0) recordsByHumanEvidence[evidenceId] = tombstone;
                        continue;
                    }
                    if (cloudDeleted && !localDeleted)
                    {
                        // A cloud delete is also authoritative. Keep the stale local
                        // version only as a disabled candidate, never as a live answer.
                        AddCandidate(merged, mergedIds, local, id);
                        continue;
                    }
                    if (SameRecordContent(cloud, local))
                    {
                        MergeRecordMetadata(cloud, local);
                        continue;
                    }
                    string prior;
                    if (baseline != null && baseline.TryGetValue(id, out prior))
                    {
                        var localUnchanged=string.Equals(RecordContentFingerprint(local),prior,StringComparison.Ordinal);
                        var cloudUnchanged=string.Equals(RecordContentFingerprint(cloud),prior,StringComparison.Ordinal);
                        if (localUnchanged && !cloudUnchanged)
                        {
                            // Independent cloud edit: retain the cloud record, no false
                            // conflict candidate for the old local revision.
                            continue;
                        }
                        if (cloudUnchanged && !localUnchanged)
                        {
                            // Independent local edit: preserve its original stable ID,
                            // even when other records changed remotely at the same time.
                            var replacement=CloneRecord(local);
                            var index=merged.IndexOf(cloud);
                            if (index>=0) merged[index]=replacement;
                            cloudById[id]=replacement;
                            var oldEvidence=AutoPublishedEvidenceKey(cloud);
                            KnowledgeV2Record mapped;
                            if (oldEvidence.Length>0 && recordsByHumanEvidence.TryGetValue(oldEvidence,out mapped)
                                && ReferenceEquals(mapped,cloud)) recordsByHumanEvidence.Remove(oldEvidence);
                            var newEvidence=AutoPublishedEvidenceKey(replacement);
                            if (newEvidence.Length>0) recordsByHumanEvidence[newEvidence]=replacement;
                            continue;
                        }
                    }
                    // Both devices changed the same stable ID relative to a known
                    // baseline. Do not silently make the cloud arrival winner an
                    // authoritative fact when the actual answers/scopes disagree.
                    if (baseline != null && baseline.ContainsKey(id) && cloud.Enabled && local.Enabled
                        && !SameBusinessClaims(cloud, local))
                    {
                        AddCandidate(merged, mergedIds, local, id);
                        cloud.Enabled = false;
                        cloud.Status = "candidate";
                        cloud.Type = "learning_candidate";
                        // Preserve the stable cloud record ID and original evidence
                        // provenance. Both variants remain available for resolution.
                        continue;
                    }
                    // Concurrent demotion and edit must never leave the edited
                    // answer active against an explicit stop on another device.
                    if (!local.Enabled && cloud.Enabled)
                    {
                        AddCandidate(merged, mergedIds, cloud, id);
                        var inactive=CloneRecord(local);
                        var index=merged.IndexOf(cloud);
                        if (index>=0) merged[index]=inactive;
                        cloudById[id]=inactive;
                        var oldEvidence=AutoPublishedEvidenceKey(cloud);
                        if (oldEvidence.Length>0) recordsByHumanEvidence[oldEvidence]=inactive;
                        continue;
                    }
                    AddCandidate(merged, mergedIds, local, id);
                    continue;
                }

                KnowledgeV2Record evidenceRecord;
                if (evidenceId.Length > 0 && recordsByHumanEvidence.TryGetValue(evidenceId, out evidenceRecord))
                {
                    if (IsDeleteTombstone(local) && !IsDeleteTombstone(evidenceRecord))
                    {
                        // Devices can independently create different random IDs for the
                        // same verified human evidence. A delete on one device must also
                        // win that evidence-key conflict; otherwise the other device's
                        // active ID survives and resurrects the deleted answer.
                        AddCandidate(merged, mergedIds, evidenceRecord, evidenceRecord.Id);
                        var tombstone = CloneRecord(local);
                        var evidenceIndex = merged.IndexOf(evidenceRecord);
                        if (evidenceIndex >= 0)
                        {
                            mergedIds.Remove((evidenceRecord.Id ?? string.Empty).Trim());
                            var tombstoneId = (tombstone.Id ?? string.Empty).Trim();
                            if (tombstoneId.Length > 0) mergedIds.Add(tombstoneId);
                            merged[evidenceIndex] = tombstone;
                        }
                        else
                        {
                            AddUnique(merged, mergedIds, tombstone);
                        }
                        cloudById.Remove((evidenceRecord.Id ?? string.Empty).Trim());
                        var deletedId = (tombstone.Id ?? string.Empty).Trim();
                        if (deletedId.Length > 0) cloudById[deletedId] = tombstone;
                        recordsByHumanEvidence[evidenceId] = tombstone;
                    }
                    else if (IsDeleteTombstone(evidenceRecord) && !IsDeleteTombstone(local))
                    {
                        // The cloud tombstone wins across independently generated IDs too.
                        AddCandidate(merged, mergedIds, local, local.Id);
                    }
                    else
                    {
                        if (SameRecordContent(evidenceRecord, local))
                            MergeRecordMetadata(evidenceRecord, local);
                        // This independently generated ID may already be referenced
                        // by feedback, revisions or another device. Retain it as an
                        // explicitly disabled candidate rather than dropping its identity.
                        var duplicate=CloneRecord(local);
                        duplicate.SourceType="cloud_sync_conflict_candidate";
                        duplicate.SourceId=local.Id;
                        duplicate.Enabled=false;
                        duplicate.Status="candidate";
                        duplicate.Type="learning_candidate";
                        AddUnique(merged,mergedIds,duplicate);
                    }
                    continue;
                }

                if (preserveLocalOnly || IsDeleteTombstone(local))
                {
                    var copy = CloneRecord(local);
                    AddUnique(merged, mergedIds, copy);
                    if (evidenceId.Length > 0) recordsByHumanEvidence[evidenceId] = copy;
                }
                else
                {
                    AddCandidate(merged, mergedIds, local, id);
                }
            }
            return merged;
        }

        private static HashSet<string> DeletedEvidenceKeys(IEnumerable<KnowledgeV2Record> records)
        {
            return new HashSet<string>((records ?? Enumerable.Empty<KnowledgeV2Record>())
                .Where(IsDeleteTombstone).Select(AutoPublishedEvidenceKey)
                .Where(x => x.Length > 0), StringComparer.Ordinal);
        }

        private static List<KnowledgeV2Record> NormalizeEvidenceDeletes(
            IEnumerable<KnowledgeV2Record> records, HashSet<string> deletedEvidence)
        {
            return (records ?? Enumerable.Empty<KnowledgeV2Record>())
                .Where(x => x != null).Select(record =>
                {
                    if (IsDeleteTombstone(record)
                        || !deletedEvidence.Contains(AutoPublishedEvidenceKey(record)))
                        return record;
                    var tombstone = CloneRecord(record);
                    tombstone.Enabled = false;
                    tombstone.Status = "deleted";
                    // Do not invent a timestamp or change the original ID/evidence key.
                    return tombstone;
                }).ToList();
        }

        private static bool IsDeleteTombstone(KnowledgeV2Record record)
        {
            return record != null && !record.Enabled
                && string.Equals(record.Status ?? string.Empty, "deleted", StringComparison.OrdinalIgnoreCase);
        }

        private static string AutoPublishedEvidenceKey(KnowledgeV2Record record)
        {
            if (record == null
                || !KnowledgeV2AuthorityPolicy.IsAutoPublishedHumanEvidenceSource(record.SourceType))
                return string.Empty;
            return (record.SourceId ?? string.Empty).Trim();
        }

        private static void AddCandidate(List<KnowledgeV2Record> merged,HashSet<string> ids,
            KnowledgeV2Record source,string originalId)
        {
            var candidate=CloneRecord(source);
            if(string.Equals(candidate.SourceType,"cloud_sync_conflict_candidate",StringComparison.OrdinalIgnoreCase)
                && string.Equals(candidate.Status,"candidate",StringComparison.OrdinalIgnoreCase)
                && !candidate.Enabled)
            {
                AddUnique(merged,ids,candidate);
                return;
            }

            // Retry metadata and usage counters are not a different business answer.
            // Stabilize the candidate identity across repeated syncs of the same content.
            var identity = CloneRecord(source);
            identity.UseCount = identity.AcceptedCount = identity.CorrectionCount = identity.WithdrawCount = 0;
            identity.CreatedAt = identity.UpdatedAt = identity.LastVerifiedAt = DateTime.MinValue;
            var sourceFingerprint=Fingerprint(new[]{identity});
            candidate.Id="cloud-conflict-"+sourceFingerprint.Substring(0,24);
            candidate.Title="云同步冲突待审核："+Safe(source.Title,180);
            candidate.SourceType="cloud_sync_conflict_candidate";
            candidate.SourceId=originalId??string.Empty;
            candidate.Enabled=false;
            candidate.Status="candidate";
            candidate.Authority=Math.Min(candidate.Authority,0.85);
            candidate.Confidence=Math.Min(candidate.Confidence,0.85);
            var existing = merged.FirstOrDefault(x => string.Equals(x.Id, candidate.Id, StringComparison.Ordinal));
            if (existing != null)
            {
                // A deleted conflict candidate must never reappear on another retry.
                if (!IsDeleteTombstone(existing) && SameRecordContent(existing, candidate))
                    MergeRecordMetadata(existing, candidate);
                return;
            }
            AddUnique(merged,ids,candidate);
        }

        private static void AddUnique(List<KnowledgeV2Record> records,HashSet<string> ids,KnowledgeV2Record item)
        {
            if(item==null) return;
            var id=(item.Id??string.Empty).Trim();
            if(id.Length>0 && !ids.Add(id)) return;
            records.Add(item);
        }

        private static bool SameList(IEnumerable<string> left,IEnumerable<string> right)
        {
            return (left??Enumerable.Empty<string>()).SequenceEqual(
                right??Enumerable.Empty<string>(),StringComparer.Ordinal);
        }

        private static bool SameBusinessClaims(KnowledgeV2Record left, KnowledgeV2Record right)
        {
            return string.Equals(left.Title, right.Title, StringComparison.Ordinal)
                && string.Equals(left.Answer, right.Answer, StringComparison.Ordinal)
                && string.Equals(left.ShortAnswer, right.ShortAnswer, StringComparison.Ordinal)
                && string.Equals(left.Subject, right.Subject, StringComparison.Ordinal)
                && string.Equals(left.Predicate, right.Predicate, StringComparison.Ordinal)
                && string.Equals(left.Intent, right.Intent, StringComparison.Ordinal)
                && string.Equals(left.RiskLevel, right.RiskLevel, StringComparison.OrdinalIgnoreCase)
                && SameList(left.ProductIds, right.ProductIds)
                && SameList(left.Entities, right.Entities)
                && SameList(left.Aliases, right.Aliases)
                && SameList(left.Conditions, right.Conditions)
                && SameList(left.RequiredContext, right.RequiredContext)
                && SameList(left.Exclusions, right.Exclusions);
        }

        private static bool SameRecordContent(KnowledgeV2Record left,KnowledgeV2Record right)
        {
            if(left==null||right==null) return false;
            return string.Equals(left.Title??string.Empty,right.Title??string.Empty,StringComparison.Ordinal)
                && string.Equals(left.Answer??string.Empty,right.Answer??string.Empty,StringComparison.Ordinal)
                && string.Equals(left.ShortAnswer??string.Empty,right.ShortAnswer??string.Empty,StringComparison.Ordinal)
                && string.Equals(left.Subject??string.Empty,right.Subject??string.Empty,StringComparison.Ordinal)
                && string.Equals(left.Predicate??string.Empty,right.Predicate??string.Empty,StringComparison.Ordinal)
                && SameList(left.Entities,right.Entities)
                && SameList(left.Aliases,right.Aliases)
                && SameList(left.Conditions,right.Conditions)
                && SameList(left.Exclusions,right.Exclusions)
                && SameList(left.RequiredContext,right.RequiredContext)
                && SameList(left.ProductIds,right.ProductIds)
                && string.Equals(left.RiskLevel??string.Empty,right.RiskLevel??string.Empty,StringComparison.OrdinalIgnoreCase)
                && string.Equals(left.SourceType??string.Empty,right.SourceType??string.Empty,StringComparison.OrdinalIgnoreCase)
                && string.Equals(left.SourceId??string.Empty,right.SourceId??string.Empty,StringComparison.Ordinal)
                && left.Authority.Equals(right.Authority)
                && left.Confidence.Equals(right.Confidence)
                && left.Enabled==right.Enabled
                && string.Equals(left.Status??string.Empty,right.Status??string.Empty,StringComparison.OrdinalIgnoreCase)
                && string.Equals(left.Type??string.Empty,right.Type??string.Empty,StringComparison.OrdinalIgnoreCase)
                && string.Equals(left.Intent??string.Empty,right.Intent??string.Empty,StringComparison.OrdinalIgnoreCase);
        }

        private static void MergeRecordMetadata(KnowledgeV2Record target,KnowledgeV2Record source)
        {
            if(target==null||source==null) return;
            target.UseCount=Math.Max(Math.Max(0,target.UseCount),Math.Max(0,source.UseCount));
            target.AcceptedCount=Math.Max(Math.Max(0,target.AcceptedCount),Math.Max(0,source.AcceptedCount));
            target.CorrectionCount=Math.Max(Math.Max(0,target.CorrectionCount),Math.Max(0,source.CorrectionCount));
            target.WithdrawCount=Math.Max(Math.Max(0,target.WithdrawCount),Math.Max(0,source.WithdrawCount));
            if(target.CreatedAt==DateTime.MinValue
                || (source.CreatedAt!=DateTime.MinValue && source.CreatedAt<target.CreatedAt))
                target.CreatedAt=source.CreatedAt;
            if(source.UpdatedAt>target.UpdatedAt) target.UpdatedAt=source.UpdatedAt;
            if(source.LastVerifiedAt>target.LastVerifiedAt) target.LastVerifiedAt=source.LastVerifiedAt;
        }

        private static KnowledgeV2Record CloneRecord(KnowledgeV2Record record)
        {
            return JsonConvert.DeserializeObject<KnowledgeV2Record>(
                JsonConvert.SerializeObject(record))??new KnowledgeV2Record();
        }

        private static string Backup(ShopContext shop,IEnumerable<KnowledgeV2Record> records,int cloudRevision)
        {
            var directory=Paths.GetBackupRoot(shop);
            Directory.CreateDirectory(directory);
            var path=Path.Combine(directory,"knowledge-v2-cloud-before-apply-"
                +DateTime.Now.ToString("yyyyMMdd-HHmmssfff")+"-"+Guid.NewGuid().ToString("N").Substring(0,6)+".json");
            var snapshot=new JObject
            {
                ["shop_ref"]=MyWebSocketServer.DiagnosticRef("shop",shop.ShopKey),
                ["cloud_revision"]=cloudRevision,
                ["created_at_utc"]=DateTime.UtcNow.ToString("o"),
                ["records"]=JArray.FromObject((records??Enumerable.Empty<KnowledgeV2Record>()).ToList())
            };
            File.WriteAllText(path,snapshot.ToString(Formatting.Indented),new UTF8Encoding(false));
            return path;
        }

        private static string FingerprintPrefix(string fingerprint)
        {
            fingerprint=fingerprint??string.Empty;
            return fingerprint.Length<=16?fingerprint:fingerprint.Substring(0,16);
        }

        private static readonly object DiagnosticsSync=new object();
        private static void WriteDiagnosticsSnapshot()
        {
            try
            {
                lock(DiagnosticsSync)
                {
                    var shops=new JArray();
                    foreach(var state in States.Values)
                    {
                        if(state==null) continue;
                        lock(state.SyncRoot)
                        {
                            shops.Add(new JObject
                            {
                                ["shopRef"]=state.ShopRef??string.Empty,
                                ["cloudRevision"]=state.Revision,
                                 ["feedbackStatus"]=state.FeedbackStatus,
                                ["localRecordCount"]=state.LastLocalRecordCount,
                                ["cloudRecordCount"]=state.LastCloudRecordCount,
                                ["localFingerprintPrefix"]=state.LastLocalFingerprintPrefix??string.Empty,
                                ["cloudFingerprintPrefix"]=state.LastCloudFingerprintPrefix??string.Empty,
                                ["converged"]=state.LastConverged,
                                ["syncing"]=Volatile.Read(ref state.Syncing)!=0,
                                ["lastAttemptUtc"]=state.LastAttemptUtc.HasValue?state.LastAttemptUtc.Value.ToString("o"):null,
                                ["lastSuccessUtc"]=state.LastSuccessUtc.HasValue?state.LastSuccessUtc.Value.ToString("o"):null,
                                ["lastStatus"]=state.LastStatus??string.Empty
                            });
                        }
                    }

                    int processId;
                    long processStartTicks;
                    using(var process=Process.GetCurrentProcess())
                    {
                        processId=process.Id;
                        processStartTicks=process.StartTime.ToUniversalTime().Ticks;
                    }
                    var snapshot=new JObject
                    {
                        ["schemaVersion"]=1,
                        ["collectedAtUtc"]=DateTime.UtcNow.ToString("o"),
                        ["processId"]=processId,
                        ["processStartTimeUtcTicks"]=processStartTicks,
                        ["shops"]=shops
                    };
                    var directory=Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "QianniuAiBot","diagnostics");
                    Directory.CreateDirectory(directory);
                    var path=Path.Combine(directory,"knowledge-v2-sync-snapshot.json");
                    var temp=path+"."+Guid.NewGuid().ToString("N")+".tmp";
                    try
                    {
                        File.WriteAllText(temp,snapshot.ToString(Formatting.None),new UTF8Encoding(false));
                        if(File.Exists(path)) File.Replace(temp,path,null);
                        else File.Move(temp,path);
                    }
                    finally
                    {
                        if(File.Exists(temp)) File.Delete(temp);
                    }
                }
            }
            catch(Exception ex)
            {
                Log.ErrorWithMaxCount("Knowledge V2 只读同步诊断快照写入失败："+Safe(ex.Message,180),5);
            }
        }

        private static string Fingerprint(IEnumerable<KnowledgeV2Record> records)
        {
            return KnowledgeEngineV2Repository.ComputeCloudSyncFingerprint(records);
        }
        private static string Safe(string value,int max){value=(value??string.Empty).Replace("\r"," ").Replace("\n"," ").Trim();return value.Length<=max?value:value.Substring(0,max)+"...";}
    }
}
