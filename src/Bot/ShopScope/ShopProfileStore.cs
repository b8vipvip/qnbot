using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace Bot.ShopScope
{
    internal sealed class ShopProfileStore
    {
        private const string RegistrySchema = "qnbot.shop-registry";
        private static readonly string LegacyRegistrySchema = "qianniu" + "-ai-bot.shop-registry";
        private const int CurrentSchemaVersion = 1;
        // LastSeenAtUtc is presence metadata, not a per-second heartbeat.
        // Coalesce unchanged profiles: the old GetOrCreate path wrote *both*
        // profile.json and shops.json on every sync timer tick.
        private static readonly TimeSpan LastSeenWriteInterval = TimeSpan.FromMinutes(1);
        private static readonly object RegistrySync = new object();
        private static long _registryWriteCount;

        internal static long RegistryWriteCount
        {
            get { return Interlocked.Read(ref _registryWriteCount); }
        }
        private readonly IShopScopedPathProvider _paths;

        public ShopProfileStore(IShopScopedPathProvider paths)
        {
            if (paths == null) throw new ArgumentNullException(nameof(paths));
            _paths = paths;
        }

        public ShopProfile GetOrCreate(ShopContext shop)
        {
            if (shop == null) throw new ArgumentNullException(nameof(shop));

            lock (RegistrySync)
            {
                var registry = LoadRegistry();
                var existing = registry.Shops.FirstOrDefault(x => SameIdentity(x, shop));
                var now = DateTime.UtcNow;
                var changed = false;

                if (existing == null)
                {
                    var collision = registry.Shops.FirstOrDefault(x =>
                        string.Equals(x.ShopKey, shop.ShopKey, StringComparison.Ordinal));
                    if (collision != null)
                    {
                        throw new InvalidDataException(
                            "ShopKey collision detected for different seller identities: " + shop.ShopKey);
                    }

                    existing = new ShopProfile
                    {
                        SchemaVersion = CurrentSchemaVersion,
                        ShopKey = shop.ShopKey,
                        Platform = shop.Platform,
                        SellerId = shop.SellerId,
                        DisplayName = shop.DisplayName,
                        HasStableSellerId = shop.HasStableSellerId,
                        CreatedAtUtc = now,
                        LastSeenAtUtc = now
                    };
                    registry.Shops.Add(existing);
                    changed = true;
                }
                else
                {
                    if (!string.Equals(existing.ShopKey, shop.ShopKey, StringComparison.Ordinal))
                    {
                        throw new InvalidDataException(
                            "The same seller identity is already registered with another ShopKey.");
                    }
                    if (!string.Equals(existing.DisplayName, shop.DisplayName, StringComparison.Ordinal)
                        || (shop.HasStableSellerId && !existing.HasStableSellerId)
                        || existing.LastSeenAtUtc == DateTime.MinValue
                        || now < existing.LastSeenAtUtc
                        || now - existing.LastSeenAtUtc >= LastSeenWriteInterval)
                    {
                        changed = true;
                    }
                    existing.DisplayName = shop.DisplayName;
                    existing.HasStableSellerId = existing.HasStableSellerId || shop.HasStableSellerId;
                    if (changed) existing.LastSeenAtUtc = now;
                }

                if (changed)
                {
                    registry.Shops = registry.Shops
                        .OrderBy(x => x.ShopKey, StringComparer.Ordinal)
                        .ToList();
                    WriteProfile(existing);
                    SaveRegistry(registry);
                    Interlocked.Increment(ref _registryWriteCount);
                }
                return Clone(existing);
            }
        }

        public ShopProfile Find(string platform, string sellerId)
        {
            platform = (platform ?? string.Empty).Trim().ToLowerInvariant();
            sellerId = (sellerId ?? string.Empty).Trim();
            lock (RegistrySync)
            {
                var profile = LoadRegistry().Shops.FirstOrDefault(x =>
                    string.Equals(x.Platform, platform, StringComparison.Ordinal)
                    && string.Equals(x.SellerId, sellerId, StringComparison.Ordinal));
                return profile == null ? null : Clone(profile);
            }
        }

        public IList<ShopProfile> GetAll()
        {
            lock (RegistrySync)
            {
                return LoadRegistry().Shops.Select(Clone).ToList();
            }
        }

        private ShopRegistryDocument LoadRegistry()
        {
            if (!File.Exists(_paths.RegistryPath)) return NewRegistry();
            try
            {
                var json = File.ReadAllText(_paths.RegistryPath, Encoding.UTF8);
                var registry = JsonConvert.DeserializeObject<ShopRegistryDocument>(json);
                ValidateRegistry(registry);
                return registry;
            }
            catch (InvalidDataException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new InvalidDataException("Cannot read shop registry: " + ex.Message, ex);
            }
        }

        private static void ValidateRegistry(ShopRegistryDocument registry)
        {
            if (registry == null || registry.Shops == null)
                throw new InvalidDataException("Shop registry is empty or invalid.");
            if (!string.Equals(registry.Schema, RegistrySchema, StringComparison.Ordinal)
                && !string.Equals(registry.Schema, LegacyRegistrySchema, StringComparison.Ordinal))
                throw new InvalidDataException("Unsupported shop registry schema.");
            if (registry.SchemaVersion != CurrentSchemaVersion)
                throw new InvalidDataException("Unsupported shop registry schema version.");

            var shopKeys = new HashSet<string>(StringComparer.Ordinal);
            var identities = new HashSet<string>(StringComparer.Ordinal);
            foreach (var profile in registry.Shops)
            {
                if (profile == null
                    || string.IsNullOrWhiteSpace(profile.ShopKey)
                    || string.IsNullOrWhiteSpace(profile.Platform)
                    || string.IsNullOrWhiteSpace(profile.SellerId))
                {
                    throw new InvalidDataException("Shop registry contains an incomplete profile.");
                }
                if (!shopKeys.Add(profile.ShopKey))
                    throw new InvalidDataException("Shop registry contains a duplicate ShopKey.");
                if (!identities.Add(profile.Platform + "\n" + profile.SellerId))
                    throw new InvalidDataException("Shop registry contains a duplicate seller identity.");
            }
        }

        private void SaveRegistry(ShopRegistryDocument registry)
        {
            registry.Schema = RegistrySchema;
            AtomicWrite(
                _paths.RegistryPath,
                JsonConvert.SerializeObject(registry, Formatting.Indented));
        }

        private void WriteProfile(ShopProfile profile)
        {
            AtomicWrite(
                _paths.GetProfilePath(profile.ToContext()),
                JsonConvert.SerializeObject(profile, Formatting.Indented));
        }

        private static void AtomicWrite(string path, string content)
        {
            var directory = Path.GetDirectoryName(path);
            if (string.IsNullOrWhiteSpace(directory)) throw new InvalidOperationException("Target directory is missing.");
            Directory.CreateDirectory(directory);

            var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
            File.WriteAllText(temp, content ?? string.Empty, new UTF8Encoding(false));
            try
            {
                if (File.Exists(path))
                {
                    var backup = path + ".bak";
                    try
                    {
                        File.Replace(temp, path, backup, true);
                        return;
                    }
                    catch (PlatformNotSupportedException)
                    {
                    }
                    catch (IOException)
                    {
                    }
                    File.Copy(temp, path, true);
                    File.Delete(temp);
                    return;
                }
                File.Move(temp, path);
            }
            finally
            {
                if (File.Exists(temp)) File.Delete(temp);
            }
        }

        private static bool SameIdentity(ShopProfile profile, ShopContext shop)
        {
            return profile != null
                && string.Equals(profile.Platform, shop.Platform, StringComparison.Ordinal)
                && string.Equals(profile.SellerId, shop.SellerId, StringComparison.Ordinal);
        }

        private static ShopRegistryDocument NewRegistry()
        {
            return new ShopRegistryDocument
            {
                Schema = RegistrySchema,
                SchemaVersion = CurrentSchemaVersion,
                Shops = new List<ShopProfile>()
            };
        }

        private static ShopProfile Clone(ShopProfile source)
        {
            return new ShopProfile
            {
                SchemaVersion = source.SchemaVersion,
                ShopKey = source.ShopKey,
                Platform = source.Platform,
                SellerId = source.SellerId,
                DisplayName = source.DisplayName,
                HasStableSellerId = source.HasStableSellerId,
                CreatedAtUtc = source.CreatedAtUtc,
                LastSeenAtUtc = source.LastSeenAtUtc
            };
        }

        private sealed class ShopRegistryDocument
        {
            [JsonProperty("schema")]
            public string Schema { get; set; }

            [JsonProperty("schema_version")]
            public int SchemaVersion { get; set; }

            [JsonProperty("shops")]
            public List<ShopProfile> Shops { get; set; }
        }
    }
}
