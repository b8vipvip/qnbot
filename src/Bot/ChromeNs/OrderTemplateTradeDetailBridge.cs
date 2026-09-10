using System.Threading.Tasks;

namespace Bot
{
    /// <summary>
    /// Compatibility bootstrap retained for binary/source stability.
    /// OrderTemplateRequiredFieldsV2 is the only runtime owner of order-template field enrichment.
    /// Keeping this bootstrap inert prevents the retired bridge from racing the canonical path or
    /// delaying automatic replies while optional order fields are unavailable.
    /// </summary>
    public partial class App
    {
        private static readonly object OrderTemplateTradeDetailBootstrap =
            ChromeNs.OrderTemplateTradeDetailBridge.InitializeForApp();
    }
}

namespace Bot.ChromeNs
{
    internal static class OrderTemplateTradeDetailBridge
    {
        public static object InitializeForApp()
        {
            return new object();
        }
    }

    public partial class QN
    {
        internal Task ProcessOrderTemplateTradeDetailPlanAsync(OrderPlacedReplyPlan plan)
        {
            return ProcessOrderPlacedReplyAsync(plan);
        }
    }
}
