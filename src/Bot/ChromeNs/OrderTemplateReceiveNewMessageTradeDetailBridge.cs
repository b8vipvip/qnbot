namespace Bot
{
    /// <summary>
    /// Compatibility bootstrap retained for binary/source stability.
    /// OrderTemplateRequiredFieldsV2 now owns both receiveNewMsg and messageCenterNotify order events.
    /// This retired bridge stays inert so it cannot race the canonical path or hold an automatic
    /// reply while optional order fields are unavailable.
    /// </summary>
    public partial class App
    {
        private static readonly object OrderTemplateReceiveNewMessageTradeDetailBootstrap =
            ChromeNs.OrderTemplateReceiveNewMessageTradeDetailBridge.InitializeForApp();
    }
}

namespace Bot.ChromeNs
{
    internal static class OrderTemplateReceiveNewMessageTradeDetailBridge
    {
        public static object InitializeForApp()
        {
            return new object();
        }
    }
}
