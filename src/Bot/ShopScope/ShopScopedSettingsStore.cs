using Bot.ChromeNs;
using Newtonsoft.Json;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Bot.ShopScope
{
    internal sealed class ShopScopedSettingsStore
    {
        private const string Schema = "qnbot.shop-settings";
        private static readonly string LegacySchema = "qianniu" + "-ai-bot.shop-settings";
        private const int SchemaVersion = 1;
        private const string Algorithm = "DPAPI-CurrentUser";
        private static readonly ConcurrentDictionary<string, object> Locks =
            new ConcurrentDictionary<string, object>(StringComparer.Ordinal);

        private readonly ShopContext _shop;
        private readonly IShopScopedPathProvider _paths;
        private readonly string _path;

        public ShopScopedSettingsStore(ShopContext shop, IShopScopedPathProvider paths)
        {
            if (shop == null) throw new ArgumentNullException(nameof(shop));
            if (paths == null) throw new ArgumentNullException(nameof(paths));
            _shop = shop;
            _paths = paths;
            _path = _paths.GetConfigPath(_shop, "settings.json");
        }

        public bool TryGetString(string key, out string value)
        {
            key = RequireKey(key);
            lock (GetLock())
            {
                var document = Load();
                return document.Values.TryGetValue(key, out value);
            }
        }

        public void SetString(string key, string value)
        {
            key = RequireKey(key);
            lock (GetLock())
            {
                var document = Load();
                document.Values[key] = value ?? string.Empty;
                document.UpdatedAtUtc = DateTime.UtcNow;
                Save(document);
            }
        }

        public bool Remove(string key)
        {
            key = RequireKey(key);
            lock (GetLock())
            {
                var document = Load();
                if (!document.Values.Remove(key)) return false;
                document.UpdatedAtUtc = DateTime.UtcNow;
                Save(document);
                return true;
            }
        }

        public IDictionary<string, string> ExportValues()
        {
            lock (GetLock())
            {
                return new Dictionary<string, string>(Load().Values, StringComparer.Ordinal);
            }
        }

        public void ReplaceValues(IDictionary<string, string> values)
        {
            lock (GetLock())
            {
                var document = NewDocument();
                document.Values = NormalizeValues(values);
                document.UpdatedAtUtc = DateTime.UtcNow;
                Save(document);
            }
        }

        public void MergeValues(IDictionary<string, string> values, bool overwrite)
        {
            if (values == null) return;
            lock (GetLock())
            {
                var document = Load();
                foreach (var pair in NormalizeValues(values))
                {
                    if (overwrite || !document.Values.ContainsKey(pair.Key))
                        document.Values[pair.Key] = pair.Value;
                }
                document.UpdatedAtUtc = DateTime.UtcNow;
                Save(document);
            }
        }

        public string SettingsPath
        {
            get { return _path; }
        }

        private object GetLock()
        {
            return Locks.GetOrAdd(_shop.ShopKey, _ => new object());
        }

        private SettingsDocument Load()
        {
            if (!File.Exists(_path)) return NewDocument();
            try
            {
                var json = File.ReadAllText(_path, Encoding.UTF8);
                var document = JsonConvert.DeserializeObject<SettingsDocument>(json);
                Validate(document);
                document.Values = DecryptValues(document.ProtectedValues);
                if (document.ValueCount != document.Values.Count)
                    throw new InvalidDataException("Shop settings value count does not match protected payload.");
                return document;
            }
            catch (InvalidDataException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new InvalidDataException("Cannot read protected shop settings: " + ex.Message, ex);
            }
        }

        private SettingsDocument NewDocument()
        {
            return new SettingsDocument
            {
                Schema = Schema,
                SchemaVersion = SchemaVersion,
                ShopKey = _shop.ShopKey,
                Algorithm = Algorithm,
                UpdatedAtUtc = DateTime.UtcNow,
                ProtectedValues = string.Empty,
                ValueCount = 0,
                Values = new Dictionary<string, string>(StringComparer.Ordinal)
            };
        }

        private void Save(SettingsDocument document)
        {
            document.Schema = Schema;
            document.SchemaVersion = SchemaVersion;
            document.ShopKey = _shop.ShopKey;
            document.Algorithm = Algorithm;
            document.Values = document.Values ?? new Dictionary<string, string>(StringComparer.Ordinal);
            document.ValueCount = document.Values.Count;
            document.ProtectedValues = EncryptValues(document.Values);
            AtomicWrite(_path, JsonConvert.SerializeObject(document, Formatting.Indented));
        }

        private string EncryptValues(Dictionary<string, string> values)
        {
            var json = JsonConvert.SerializeObject(
                values ?? new Dictionary<string, string>(StringComparer.Ordinal),
                Formatting.None);
            var plain = Encoding.UTF8.GetBytes(json);
            byte[] protectedBytes = null;
            try
            {
                protectedBytes = ProtectedData.Protect(
                    plain,
                    Entropy(),
                    DataProtectionScope.CurrentUser);
                return Convert.ToBase64String(protectedBytes);
            }
            finally
            {
                Array.Clear(plain, 0, plain.Length);
                if (protectedBytes != null) Array.Clear(protectedBytes, 0, protectedBytes.Length);
            }
        }

        private Dictionary<string, string> DecryptValues(string protectedValues)
        {
            if (string.IsNullOrWhiteSpace(protectedValues))
                return new Dictionary<string, string>(StringComparer.Ordinal);

            byte[] protectedBytes = null;
            byte[] plain = null;
            try
            {
                protectedBytes = Convert.FromBase64String(protectedValues);
                plain = ProtectedData.Unprotect(
                    protectedBytes,
                    Entropy(),
                    DataProtectionScope.CurrentUser);
                var json = Encoding.UTF8.GetString(plain);
                var values = JsonConvert.DeserializeObject<Dictionary<string, string>>(json)
                    ?? new Dictionary<string, string>(StringComparer.Ordinal);
                return NormalizeValues(values);
            }
            finally
            {
                if (protectedBytes != null) Array.Clear(protectedBytes, 0, protectedBytes.Length);
                if (plain != null) Array.Clear(plain, 0, plain.Length);
            }
        }

        private byte[] Entropy()
        {
            return Encoding.UTF8.GetBytes("qianniu" + "-ai-bot|shop-settings|" + _shop.ShopKey);
        }

        private void Validate(SettingsDocument document)
        {
            if (document == null
                || (!string.Equals(document.Schema, Schema, StringComparison.Ordinal)
                    && !string.Equals(document.Schema, LegacySchema, StringComparison.Ordinal))
                || document.SchemaVersion != SchemaVersion
                || !string.Equals(document.ShopKey, _shop.ShopKey, StringComparison.Ordinal)
                || !string.Equals(document.Algorithm, Algorithm, StringComparison.Ordinal)
                || document.ValueCount < 0)
            {
                throw new InvalidDataException("Shop settings file has an invalid schema or ShopKey.");
            }
        }

        private static Dictionary<string, string> NormalizeValues(IDictionary<string, string> values)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            if (values == null) return result;
            foreach (var pair in values)
            {
                var key = RequireKey(pair.Key);
                result[key] = pair.Value ?? string.Empty;
            }
            return result;
        }

        private static void AtomicWrite(string path, string content)
        {
            string error;
            if (!CrossProcessAtomicStateFile.WriteAllTextAtomic(
                path, content, 3, 50, out error, path + ".bak"))
                throw new IOException("店铺配置原子保存失败：" + error);
        }

        private static string RequireKey(string key)
        {
            key = (key ?? string.Empty).Trim();
            if (key.Length == 0 || key.Length > 160)
                throw new ArgumentException("A valid settings key is required.", nameof(key));
            return key;
        }

        private sealed class SettingsDocument
        {
            [JsonProperty("schema")]
            public string Schema { get; set; }

            [JsonProperty("schema_version")]
            public int SchemaVersion { get; set; }

            [JsonProperty("shop_key")]
            public string ShopKey { get; set; }

            [JsonProperty("algorithm")]
            public string Algorithm { get; set; }

            [JsonProperty("protected_values")]
            public string ProtectedValues { get; set; }

            [JsonProperty("value_count")]
            public int ValueCount { get; set; }

            [JsonProperty("updated_at_utc")]
            public DateTime UpdatedAtUtc { get; set; }

            [JsonIgnore]
            public Dictionary<string, string> Values { get; set; }
        }
    }
}