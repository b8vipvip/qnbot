using BotLib.Db.Sqlite;
using System;
using System.Configuration;

namespace Bot.ShopScope
{
    internal sealed class ShopControlPlaneConnectionStore
    {
        private const string LegacyScope = "ai-control-plane";
        private const string UrlKey = "ControlPlaneUrl";
        private const string UrlOverrideKey = "ControlPlaneUrlOverride";
        private const string LegacyTokenKey = "ControlPlaneClientToken";
        private const string DefaultUrlSettingKey = "BotControlPlaneDefaultUrl";
        private const string ServerUrlEnvironmentKey = "QIANNIU_BOT_SERVER_URL";
        private const string BuiltInDefaultServerUrl = "https://qn.numbot.cn";
        private const string ObsoleteBuiltInHost = "botserver.mv3.cn";
        private const string LegacyBuiltInHost = "aboter.mv3.cn";
        private const string CurrentBuiltInHost = "qn.numbot.cn";

        private readonly ShopContext _shop;
        private readonly ShopTokenStore _tokens;
        private readonly ShopScopedSettingsStore _settings;

        public ShopControlPlaneConnectionStore(ShopContext shop, IShopScopedPathProvider paths)
        {
            if (shop == null) throw new ArgumentNullException(nameof(shop));
            if (paths == null) throw new ArgumentNullException(nameof(paths));
            _shop = shop;
            _tokens = new ShopTokenStore(shop, paths);
            _settings = new ShopScopedSettingsStore(shop, paths);
        }

        public string GetServerUrl()
        {
            // The API Control Plane is a program/deployment endpoint, not a shop secret.
            // Every shop on one Bot installation uses the same endpoint; only the token
            // remains ShopKey-scoped. Preserve existing installs by migrating the first
            // historical per-shop URL into the old global slot once.
            var global = GetProgramServerUrl();
            if (!string.IsNullOrWhiteSpace(global)) return global;

            string scoped;
            if (_settings.TryGetString(UrlKey, out scoped))
            {
                scoped = NormalizeUrl(scoped);
                if (!string.IsNullOrWhiteSpace(scoped))
                {
                    SaveProgramServerUrl(scoped);
                    return scoped;
                }
            }

            return GetBuiltInServerUrl();
        }

        // Compatibility entry point for older callers. New UI no longer exposes an
        // editable per-shop server URL; writes here intentionally become program-global.
        public void SetServerUrl(string serverUrl)
        {
            SaveProgramServerUrl(serverUrl);
        }

        public bool HasShopServerUrl
        {
            get { return !string.IsNullOrWhiteSpace(GetServerUrl()); }
        }

        public bool TryGetToken(out string token, out string error)
        {
            return _tokens.TryLoad(out token, out error);
        }

        public void SaveToken(string token)
        {
            _tokens.Save(token);
            ShopTokenBindingService.ResetFailure(_shop);
        }

        public void ClearToken()
        {
            _tokens.Clear();
        }

        public bool HasToken
        {
            get { return _tokens.Exists; }
        }

        public string TokenFingerprint
        {
            get { return _tokens.GetFingerprint(); }
        }

        public string TokenPath
        {
            get { return _tokens.TokenPath; }
        }

        public static string GetLegacyGlobalToken()
        {
            return (PersistentParams.GetParam2Key(LegacyTokenKey, LegacyScope, string.Empty) ?? string.Empty).Trim();
        }

        public static string GetLegacyGlobalServerUrl()
        {
            var configured = GetProgramServerUrl();
            return !string.IsNullOrWhiteSpace(configured)
                ? configured
                : GetBuiltInServerUrl();
        }

        public static string GetProgramServerUrl()
        {
            // An explicit tray-menu choice is a per-install operator override and must
            // take precedence over inherited machine/user environment variables.
            var uiOverride = NormalizeUrl(
                PersistentParams.GetParam2Key(UrlOverrideKey, LegacyScope, string.Empty));
            if (!string.IsNullOrWhiteSpace(uiOverride)) return uiOverride;

            var environment = NormalizeUrl(Environment.GetEnvironmentVariable(ServerUrlEnvironmentKey));
            if (!string.IsNullOrWhiteSpace(environment)) return environment;

            // Keep the previous global setting as a migration fallback for older installs.
            return NormalizeUrl(PersistentParams.GetParam2Key(UrlKey, LegacyScope, string.Empty));
        }

        public static void SaveProgramServerUrl(string serverUrl)
        {
            string normalized;
            string error;
            if (!TryNormalizeServerUrl(serverUrl, out normalized, out error))
                throw new ArgumentException(error, nameof(serverUrl));

            PersistentParams.TrySaveParam2Key(UrlOverrideKey, LegacyScope, normalized);
        }

        public static string GetBuiltInServerUrl()
        {
            try
            {
                var configured = NormalizeUrl(ConfigurationManager.AppSettings[DefaultUrlSettingKey]);
                if (!string.IsNullOrWhiteSpace(configured)) return configured;
            }
            catch { }
            return NormalizeUrl(BuiltInDefaultServerUrl);
        }

        public static bool TryNormalizeServerUrl(string serverUrl, out string normalized, out string error)
        {
            normalized = NormalizeUrl(serverUrl);
            error = string.Empty;
            if (string.IsNullOrWhiteSpace(normalized))
            {
                error = "请输入服务端域名或完整地址。";
                return false;
            }

            Uri parsed;
            if (!Uri.TryCreate(normalized, UriKind.Absolute, out parsed))
            {
                error = "服务端地址格式无效，请输入域名或 http(s):// 开头的完整地址。";
                return false;
            }

            if (!string.Equals(parsed.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(parsed.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                error = "服务端地址仅支持 HTTP 或 HTTPS。";
                return false;
            }

            if (string.IsNullOrWhiteSpace(parsed.Host) || !string.IsNullOrEmpty(parsed.UserInfo))
            {
                error = "服务端地址必须包含有效主机名，且不能包含用户名或密码。";
                return false;
            }

            if (!string.IsNullOrEmpty(parsed.Query) || !string.IsNullOrEmpty(parsed.Fragment))
            {
                error = "服务端地址不能包含查询参数或片段。";
                return false;
            }

            normalized = parsed.GetLeftPart(UriPartial.Authority) + parsed.AbsolutePath.TrimEnd('/');
            if (normalized.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
                normalized = normalized.Substring(0, normalized.Length - 3).TrimEnd('/');
            return true;
        }

        public static string NormalizeUrl(string serverUrl)
        {
            var value = (serverUrl ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;

            // A bare domain is accepted by the tray editor and defaults to HTTPS.
            if (value.IndexOf("://", StringComparison.Ordinal) < 0)
                value = "https://" + value;

            value = value.TrimEnd('/');
            if (value.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
                value = value.Substring(0, value.Length - 3).TrimEnd('/');

            Uri parsed;
            if (Uri.TryCreate(value, UriKind.Absolute, out parsed)
                && (string.Equals(parsed.Host, ObsoleteBuiltInHost, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(parsed.Host, LegacyBuiltInHost, StringComparison.OrdinalIgnoreCase)))
            {
                try
                {
                    var builder = new UriBuilder(parsed)
                    {
                        Host = CurrentBuiltInHost,
                        Scheme = Uri.UriSchemeHttps,
                        Port = -1
                    };
                    value = builder.Uri.AbsoluteUri.TrimEnd('/');
                }
                catch { }
            }

            return value.TrimEnd('/');
        }
    }
}
