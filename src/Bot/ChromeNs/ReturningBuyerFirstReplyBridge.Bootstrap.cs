using BotLib;
using System;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Bot
{
    public partial class App
    {
        private readonly object _returningBuyerFirstReplyBridge =
            ChromeNs.ReturningBuyerFirstReplyBridge.InitializeForApp();
    }
}

namespace Bot.ChromeNs
{
    internal static partial class ReturningBuyerFirstReplyBridge
    {
        internal const int ReturningBuyerIdleMinutes = 10;
        internal const int ExistingSessionResetMinutes = 30;
        private static readonly ConditionalWeakTable<QN, object> Qns = new ConditionalWeakTable<QN, object>();
        private static readonly object QnAttachSync = new object();
        private static readonly ConcurrentDictionary<string, DateTime> Reservations = new ConcurrentDictionary<string, DateTime>(StringComparer.Ordinal);
        private static Timer _timer;
        private static int _initialized;
        private static int _running;

        public static object InitializeForApp()
        {
            if (Interlocked.Exchange(ref _initialized, 1) == 0)
            {
                _timer = new Timer(_ => Tick(), null, 350, 700);
                Log.Info("回访买家首条回复已启用：超过10分钟无互动后再次来询，重新满足首次回复。");
            }
            return new object();
        }

        private static void Tick()
        {
            if (Interlocked.Exchange(ref _running, 1) != 0) return;
            try
            {
                foreach (var qn in QN.GetRuntimeSafetySnapshot())
                {
                    if (qn == null) continue;
                    try
                    {
                        lock (QnAttachSync)
                        {
                            object marker;
                            if (Qns.TryGetValue(qn, out marker)) continue;
                            var subscriptionAttempted = false;
                            try
                            {
                                subscriptionAttempted = true;
                                qn.EvRecieveNewMessage += OnMessage;
                                Qns.Add(qn, new object());
                            }
                            catch
                            {
                                if (subscriptionAttempted) { try { qn.EvRecieveNewMessage -= OnMessage; } catch { } }
                                throw;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.ErrorWithMaxCount("跳过异常千牛会话的回访首答订阅并继续：" + ex.Message, 10);
                    }
                }
                var cutoff = DateTime.Now.AddMinutes(-ReturningBuyerIdleMinutes);
                foreach (var x in Reservations)
                {
                    if (x.Value >= cutoff) continue;
                    DateTime ignored;
                    Reservations.TryRemove(x.Key, out ignored);
                }
            }
            catch (Exception ex) { Log.ErrorWithMaxCount("回访首答检查失败：" + ex.Message, 10); }
            finally { Interlocked.Exchange(ref _running, 0); }
        }
    }
}
