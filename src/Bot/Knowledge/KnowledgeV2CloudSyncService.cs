using Bot.ShopScope;
using BotLib;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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
        private sealed class State { public int Syncing; public int Revision; public string Fingerprint = string.Empty; }
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
            if(shop==null || !KnowledgeCloudSyncService.IsEnabledForShop(shop)) return;
            var state=States.GetOrAdd(shop.ShopKey,_=>new State());
            if(Interlocked.Exchange(ref state.Syncing,1)!=0) return;
            Task.Run(async()=>{try{await SyncOnce(shop,state);}catch(Exception ex){Log.ErrorWithMaxCount("Knowledge V2 云同步失败："+Safe(ex.Message,260),20);}finally{Interlocked.Exchange(ref state.Syncing,0);}});
        }
        private static async Task SyncOnce(ShopContext shop,State state)
        {
            using(ShopSettingsScope.Enter(shop))
            {
                var connection=new ShopControlPlaneConnectionStore(shop,Paths);
                string token,error; var url=connection.GetServerUrl();
                if(!connection.TryGetToken(out token,out error)||string.IsNullOrWhiteSpace(url)||string.IsNullOrWhiteSpace(token)) return;
                var local=KnowledgeEngineV2Repository.LoadAll(shop.DisplayName);
                var fingerprint=Fingerprint(local);
                var payload=new JObject { ["revision"]=state.Revision };
                if(state.Revision==0 || !string.Equals(fingerprint,state.Fingerprint,StringComparison.Ordinal))
                    payload["records"]=JArray.FromObject(local);
                ServicePointManager.SecurityProtocol|=SecurityProtocolType.Tls12;
                using(var handler=new HttpClientHandler{UseProxy=true,Proxy=WebRequest.DefaultWebProxy})
                using(var http=new HttpClient(handler))
                using(var request=new HttpRequestMessage(HttpMethod.Post,url.TrimEnd('/')+"/api/runtime/v1/bot-web/knowledge-v2-sync"))
                {
                    http.Timeout=TimeSpan.FromSeconds(45);
                    request.Headers.Authorization=new AuthenticationHeaderValue("Bearer",token);
                    request.Headers.TryAddWithoutValidation("X-Shop-Key",shop.ShopKey);
                    request.Headers.TryAddWithoutValidation("User-Agent","qianniu-knowledge-v2-cloud/1.0");
                    request.Content=new StringContent(payload.ToString(Formatting.None),Encoding.UTF8,"application/json");
                    using(var response=await http.SendAsync(request))
                    {
                        var body=await response.Content.ReadAsStringAsync();
                        if(!response.IsSuccessStatusCode) throw new Exception("HTTP "+(int)response.StatusCode+" "+Safe(body,200));
                        var root=JObject.Parse(body); var cloudRevision=root.Value<int?>("revision")??state.Revision;
                        var records=root["records"]==null?null:root["records"].ToObject<List<KnowledgeV2Record>>();
                        if(cloudRevision>state.Revision && records!=null)
                        {
                            // ReplaceAll is the existing structural V2 write path: it updates the
                            // shop-scoped DB, compatibility mirror and runtime snapshot together.
                            KnowledgeEngineV2Repository.ReplaceAll(shop.DisplayName,records);
                            local=records; fingerprint=Fingerprint(local);
                            Log.Info("Knowledge V2 已应用云端版本: shop="+shop.ShopKey+", revision="+cloudRevision+", records="+records.Count);
                        }
                        state.Revision=cloudRevision; state.Fingerprint=fingerprint;
                    }
                }
            }
        }
        private static string Fingerprint(IEnumerable<KnowledgeV2Record> records)
        {
            return JsonConvert.SerializeObject((records??Enumerable.Empty<KnowledgeV2Record>()).Select(x=>new { x.Id,x.UpdatedAt,x.Enabled,x.Status,x.Title,x.Answer }));
        }
        private static string Safe(string value,int max){value=(value??string.Empty).Replace("\r"," ").Replace("\n"," ").Trim();return value.Length<=max?value:value.Substring(0,max)+"...";}
    }
}
