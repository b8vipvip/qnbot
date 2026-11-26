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
        private const string SettingsScope = "knowledge-v2-cloud";
        private const string RevisionKey = "KnowledgeV2CloudRevision";
        private const string FingerprintKey = "KnowledgeV2CloudFingerprint";

        private sealed class State
        {
            public readonly object SyncRoot = new object();
            public int Syncing;
            public int Revision;
            public string Fingerprint = string.Empty;
            public string ShopRef = string.Empty;
            public DateTime? LastAttemptUtc;
            public DateTime? LastSuccessUtc;
            public string LastStatus = "not_started";
            public int LastLocalRecordCount;
            public int LastCloudRecordCount;
            public string LastLocalFingerprintPrefix = string.Empty;
            public string LastCloudFingerprintPrefix = string.Empty;
            public bool LastConverged;
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
            IList<ShopProfile> profiles; try { profiles=Profiles.GetAll(); } catch { return; }
            foreach(var p in profiles) if(p!=null) Queue(p.ToContext());
        }
        private static void Queue(ShopContext shop)
        {
            if(shop==null || !KnowledgeCloudSyncService.IsEnabledForShop(shop)
                || !ShopTokenBindingService.ShouldAttemptSync(shop)) return;
            var state=States.GetOrAdd(shop.ShopKey,_=>LoadState(shop));
            if(Interlocked.Exchange(ref state.Syncing,1)!=0) return;
            lock(state.SyncRoot)
            {
                state.LastAttemptUtc=DateTime.UtcNow;
                state.LastStatus="running";
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
        private static async Task SyncOnce(ShopContext shop,State state)
        {
            using(ShopSettingsScope.Enter(shop))
            {
                var connection=new ShopControlPlaneConnectionStore(shop,Paths);
                string token,error;
                var url=connection.GetServerUrl();
                if(!connection.TryGetToken(out token,out error)||string.IsNullOrWhiteSpace(url)||string.IsNullOrWhiteSpace(token))
                {
                    lock(state.SyncRoot) state.LastStatus="credentials_missing";
                    return;
                }

                var local=KnowledgeEngineV2Repository.LoadAll(shop.DisplayName);
                var localFingerprint=Fingerprint(local);
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
                        var cloudRevision=root.Value<int?>("revision")??state.Revision;
                        var records=root["records"]==null?null:root["records"].ToObject<List<KnowledgeV2Record>>();

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
                        if(cloudRevision>state.Revision)
                        {
                            if(records==null)
                                throw new InvalidOperationException("Knowledge V2 云端版本已更新但未返回 records，已保留本地数据并等待重试。");
                            var cloudFingerprint=Fingerprint(records);
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
                                ? MergeCloudAndLocal(records,latestLocal,preserveLocalOnly:!hasBaseline)
                                : records;
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
                            PersistState(shop,state,cloudRevision,cloudFingerprint);
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

                        PersistState(shop,state,cloudRevision,localFingerprint);
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
                return new State
                {
                    Revision=Math.Max(0,revision),
                    Fingerprint=fingerprint??string.Empty,
                    ShopRef=MyWebSocketServer.RuntimeAcceptanceDiagnosticRef("shop",shop.ShopKey),
                    LastCloudFingerprintPrefix=FingerprintPrefix(fingerprint??string.Empty),
                    LastStatus="loaded"
                };
            }
        }

        private static void PersistState(ShopContext shop,State state,int targetRevision,string targetFingerprint)
        {
            if(state==null) throw new ArgumentNullException(nameof(state));
            var revision=Math.Max(0,targetRevision).ToString();
            var fingerprint=targetFingerprint??string.Empty;
            using(ShopSettingsScope.Enter(shop))
            {
                var store=new ShopScopedSettingsStore(shop,Paths);
                store.MergeValues(new Dictionary<string,string>(StringComparer.Ordinal)
                {
                    { RevisionKey, revision },
                    { FingerprintKey, fingerprint }
                }, true);
                string storedRevision,storedFingerprint;
                if(!store.TryGetString(RevisionKey,out storedRevision)
                    || !store.TryGetString(FingerprintKey,out storedFingerprint)
                    || !string.Equals(storedRevision,revision,StringComparison.Ordinal)
                    || !string.Equals(storedFingerprint,fingerprint,StringComparison.Ordinal))
                    throw new InvalidOperationException("Knowledge V2 云同步 revision/hash 持久化失败；已保留上次成功检查点。");
            }

            // Do not advance the in-memory checkpoint until both persisted values have
            // been read back successfully. A transient disk/DPAPI error must cause the
            // next poll to retry from the previous revision instead of skipping changes.
            lock(state.SyncRoot)
            {
                state.Revision=Math.Max(0,targetRevision);
                state.Fingerprint=targetFingerprint??string.Empty;
            }
        }

        private static List<KnowledgeV2Record> MergeCloudAndLocal(
            IEnumerable<KnowledgeV2Record> cloudRecords,
            IEnumerable<KnowledgeV2Record> localRecords,
            bool preserveLocalOnly)
        {
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
                var evidenceId = AutoPublishedEvidenceKey(record);
                KnowledgeV2Record existingEvidence;
                if (evidenceId.Length > 0 && recordsByHumanEvidence.TryGetValue(evidenceId, out existingEvidence))
                {
                    if (SameRecordContent(existingEvidence, record))
                        MergeRecordMetadata(existingEvidence, record);
                    else
                        AddCandidate(merged, mergedIds, record, record.Id);
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
                var evidenceId = AutoPublishedEvidenceKey(local);
                KnowledgeV2Record evidenceRecord;
                if (evidenceId.Length > 0 && recordsByHumanEvidence.TryGetValue(evidenceId, out evidenceRecord))
                {
                    if (SameRecordContent(evidenceRecord, local))
                        MergeRecordMetadata(evidenceRecord, local);
                    else
                        AddCandidate(merged, mergedIds, local, local.Id);
                    continue;
                }

                KnowledgeV2Record cloud;
                var id = (local.Id ?? string.Empty).Trim();
                if (id.Length > 0 && cloudById.TryGetValue(id, out cloud))
                {
                    if (SameRecordContent(cloud, local))
                    {
                        MergeRecordMetadata(cloud, local);
                        continue;
                    }
                    AddCandidate(merged, mergedIds, local, id);
                    continue;
                }

                if (preserveLocalOnly)
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

            var sourceFingerprint=Fingerprint(new[]{source});
            candidate.Id="cloud-conflict-"+sourceFingerprint.Substring(0,24);
            candidate.Title="云同步冲突待审核："+Safe(source.Title,180);
            candidate.SourceType="cloud_sync_conflict_candidate";
            candidate.SourceId=originalId??string.Empty;
            candidate.Enabled=false;
            candidate.Status="candidate";
            candidate.Authority=Math.Min(candidate.Authority,0.85);
            candidate.Confidence=Math.Min(candidate.Confidence,0.85);
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
