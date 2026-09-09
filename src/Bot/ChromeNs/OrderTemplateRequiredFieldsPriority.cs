using BotLib;
using System;
using System.Linq;
using System.Threading;

namespace Bot
{
    /// <summary>
    /// Keep the V2 required-order-field handler ahead of legacy order consumers. Several Qianniu
    /// pages emit the same messageCenterNotify frame at almost the same time; if an older consumer
    /// runs first it can reserve/render the sparse card before V2 has a chance to query the exact
    /// trade. The App bootstrap is retained for compatibility, while every QN instance below also
    /// starts the guard so legacy startup ordering can never silently skip it.
    /// </summary>
    public partial class App
    {
        private static readonly object OrderTemplateRequiredFieldsPriorityBootstrap =
            ChromeNs.OrderTemplateRequiredFieldsPriority.InitializeForApp();
    }
}

namespace Bot.ChromeNs
{
    internal static class OrderTemplateRequiredFieldsPriority
    {
        private static Timer _timer;
        private static int _initialized;

        public static object InitializeForApp()
        {
            if (Interlocked.Exchange(ref _initialized, 1) == 0)
            {
                _timer = new Timer(_ => ReorderAll(), null, 1, 100);
                Log.Info("订单模板字段补全优先级守卫已启动：V2 必填字段补全固定先于旧订单消费者；每个QN实例均强制启用。");
            }
            return new object();
        }

        private static void ReorderAll()
        {
            try
            {
                foreach (var qn in QN.GetRuntimeSafetySnapshot())
                {
                    if (qn == null) continue;
                    qn.EnsureRequiredOrderFieldsNotifyHandlerFirst();
                }
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount("调整订单字段补全事件优先级失败：" + ex.Message, 10);
            }
        }
    }

    public partial class QN
    {
        private readonly object _orderRequiredFieldsPriorityInstanceBootstrap =
            OrderTemplateRequiredFieldsPriority.InitializeForApp();

        private readonly object _orderRequiredFieldsEventOrderSync = new object();
        private bool _orderRequiredFieldsHandlerPriorityLogged;

        internal void EnsureRequiredOrderFieldsNotifyHandlerFirst()
        {
            lock (_orderRequiredFieldsEventOrderSync)
            {
                var chain = EvMessageNotity;
                if (chain == null) return;
                var handlers = chain.GetInvocationList();
                if (handlers.Length < 1) return;

                var required = handlers.FirstOrDefault(d =>
                    d != null
                    && d.Method != null
                    && d.Method.DeclaringType == typeof(OrderTemplateRequiredFieldsV2)
                    && string.Equals(d.Method.Name, "OnMessageNotify", StringComparison.Ordinal));
                if (required == null) return;

                if (!ReferenceEquals(handlers[0], required))
                {
                    var ordered = handlers
                        .Where(d => d != null && !ReferenceEquals(d, required))
                        .Prepend(required)
                        .ToArray();

                    EvMessageNotity = null;
                    foreach (var handler in ordered)
                    {
                        EvMessageNotity += (EventHandler<MessageNotifyEventArgs>)handler;
                    }
                    handlers = ordered;
                }

                if (!_orderRequiredFieldsHandlerPriorityLogged)
                {
                    _orderRequiredFieldsHandlerPriorityLogged = true;
                    Log.Info("订单模板字段 V2 已提升为 messageCenterNotify 第一消费者: seller="
                        + (Seller == null ? string.Empty : Seller.Nick)
                        + ", handlers=" + handlers.Length
                        + ", instanceBootstrap=true");
                }
            }
        }
    }
}