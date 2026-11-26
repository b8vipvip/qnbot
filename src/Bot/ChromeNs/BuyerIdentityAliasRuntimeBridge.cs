using Bot.ChatRecord;
using BotLib;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Bot.ChromeNs
{
    /// <summary>
    /// 把真实 receiveNewMsg 中的内部 nick、界面 display 和 targetId 持续写入别名服务。
    /// BuyerIdentityAliasService 和右侧列表合并代码此前已存在，但没有运行时入口，导致代码被编译却不生效。
    /// </summary>
    internal static class BuyerIdentityAliasRuntimeBridge
    {
        private static readonly object Sync = new object();
        private static readonly ConditionalWeakTable<QN, object> Attached =
            new ConditionalWeakTable<QN, object>();
        private static Timer _timer;
        private static int _initialized;

        public static void Initialize()
        {
            if (Interlocked.Exchange(ref _initialized, 1) != 0) return;
            _timer = new Timer(_ => Attach(), null, 0, 750);
            Log.Info("买家内部昵称/显示昵称别名桥接已启动。");
        }

        private static void Attach()
        {
            try
            {
                foreach (var qn in QN.GetRuntimeSafetySnapshot())
                {
                    if (qn == null) continue;
                    try
                    {
                        lock (Sync)
                        {
                            object marker;
                            if (Attached.TryGetValue(qn, out marker)) continue;
                            var messageAttempted = false;
                            var buyerSwitchAttempted = false;
                            var sellerSwitchAttempted = false;
                            try
                            {
                                messageAttempted = true;
                                qn.EvRecieveNewMessage += OnReceiveNewMessage;
                                buyerSwitchAttempted = true;
                                qn.EvBuyerSwitched += OnBuyerSwitched;
                                sellerSwitchAttempted = true;
                                qn.EvSellerSwitched += OnSellerSwitched;
                                Attached.Add(qn, new object());
                            }
                            catch
                            {
                                if (sellerSwitchAttempted) { try { qn.EvSellerSwitched -= OnSellerSwitched; } catch { } }
                                if (buyerSwitchAttempted) { try { qn.EvBuyerSwitched -= OnBuyerSwitched; } catch { } }
                                if (messageAttempted) { try { qn.EvRecieveNewMessage -= OnReceiveNewMessage; } catch { } }
                                throw;
                            }
                        }
                        Log.Info("买家昵称别名桥接已绑定客服实例: seller="
                            + (qn.Seller == null ? string.Empty : qn.Seller.Nick));
                    }
                    catch (Exception ex)
                    {
                        Log.ErrorWithMaxCount("跳过异常千牛会话的买家昵称别名订阅并继续：" + ex.Message, 10);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount("绑定买家昵称别名桥接失败：" + ex.Message, 10);
            }
        }

        private static void OnReceiveNewMessage(object sender, RecieveNewMessageEventArgs e)
        {
            var qn = sender as QN;
            if (qn == null || e == null || string.IsNullOrWhiteSpace(e.Message)) return;
            try
            {
                var seller = qn.Seller == null ? string.Empty : (qn.Seller.Nick ?? string.Empty).Trim();
                var response = JsonConvert.DeserializeObject<ChatResponse>(e.Message);
                foreach (var message in response == null || response.result == null
                    ? new List<QNChatMessage>()
                    : response.result.Where(x => x != null))
                {
                    BuyerIdentityAliasService.ObserveMessage(seller, message);
                }
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount("观察买家昵称别名失败：" + ex.Message, 10);
            }
        }

        private static void OnBuyerSwitched(object sender, BuyerSwitchedEventArgs e)
        {
            ObserveConversation(e == null ? null : e.Seller, e == null ? null : e.Buyer);
        }

        private static void OnSellerSwitched(object sender, SellerSwitchedEventArgs e)
        {
            ObserveConversation(e == null ? null : e.Seller, e == null ? null : e.Buyer);
        }

        private static void ObserveConversation(DbEntity.LocalUser seller, DbEntity.Conversation buyer)
        {
            if (seller == null || buyer == null) return;
            BuyerIdentityAliasService.Observe(
                seller.Nick,
                buyer.Nick,
                buyer.Display,
                string.Empty);
        }
    }
}
