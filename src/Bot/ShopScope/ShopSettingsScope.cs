using Bot.ChromeNs;
using System;
using System.Threading;

namespace Bot.ShopScope
{
    /// <summary>
    /// Short-lived explicit operation scope. Settings windows and the buyer-burst dispatcher enter
    /// it with a resolved ShopContext; it is never populated from QN.CurQN inside asynchronous work.
    /// </summary>
    internal static class ShopSettingsScope
    {
        private static readonly AsyncLocal<ShopContext> CurrentValue = new AsyncLocal<ShopContext>();

        public static ShopContext Current
        {
            get { return CurrentValue.Value; }
        }

        public static IDisposable Enter(ShopContext shop)
        {
            return Enter(shop, true);
        }

        public static IDisposable Enter(ShopContext shop, bool bindRuntime)
        {
            if (shop == null) throw new ArgumentNullException(nameof(shop));
            var previous = CurrentValue.Value;
            CurrentValue.Value = shop;
            if (bindRuntime)
            {
                try
                {
                    // Keep the legacy RPA/UI compatibility layer aligned with the same ShopKey
                    // before any scoped UI or message operation can reach a native-window action.
                    MultiShopRuntimeSessionCoordinator.EnsureShopBinding(shop);
                }
                catch
                {
                    // Shop scoping must remain available even when Qianniu is temporarily offline.
                }
            }
            return new Scope(previous, shop);
        }

        private sealed class Scope : IDisposable
        {
            private readonly ShopContext _previous;
            private readonly ShopContext _entered;
            private int _disposed;

            public Scope(ShopContext previous, ShopContext entered)
            {
                _previous = previous;
                _entered = entered;
            }

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
                if (ReferenceEquals(CurrentValue.Value, _entered)
                    || (CurrentValue.Value != null && CurrentValue.Value.Equals(_entered)))
                {
                    CurrentValue.Value = _previous;
                }
            }
        }
    }
}