namespace Bot
{
    public partial class App
    {
        // Instance-field initialization is guaranteed when the WPF App object is constructed.
        // Keep this independent from beforefieldinit/static-field semantics so durable delayed
        // delivery tasks resume even when no settings window is opened after startup.
        private readonly object _virtualGoodsAutoDeliveryBootstrap =
            ChromeNs.AutoDeliveryAppBootstrap.InitializeForApp();
    }
}

namespace Bot.ChromeNs
{
    internal static class AutoDeliveryAppBootstrap
    {
        public static object InitializeForApp()
        {
            AutoDeliveryCoordinator.Initialize();
            AutoDeliveryOrderEventSeed.Initialize();
            return new object();
        }
    }
}
