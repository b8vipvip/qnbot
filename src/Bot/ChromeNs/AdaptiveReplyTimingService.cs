using BotLib;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace Bot.ChromeNs
{
    internal enum AdaptiveDelayKind
    {
        Complete,
        Fragment,
        Greeting,
        Media,
        DenseBurst
    }

    internal sealed class AdaptiveReplyTimingProfile
    {
        public string Key { get; set; }
        public List<int> RecentIntervalsMs { get; set; }
        public int TotalSamples { get; set; }
        public string UpdatedAt { get; set; }

        public AdaptiveReplyTimingProfile()
        {
            RecentIntervalsMs = new List<int>();
            UpdatedAt = string.Empty;
        }
    }

    internal sealed class AdaptiveReplyTimingSnapshot
    {
        public int SampleCount { get; set; }
        public int RecommendedDelayMs { get; set; }
        public int MedianIntervalMs { get; set; }
        public int P75IntervalMs { get; set; }
    }

    internal static class AdaptiveReplyTimingService
    {
        private sealed class TimingFile
        {
            public int Version { get; set; }
            public List<AdaptiveReplyTimingProfile> Profiles { get; set; }

            public TimingFile()
            {
                Version = 1;
                Profiles = new List<AdaptiveReplyTimingProfile>();
            }
        }

        private static readonly object Sync = new object();
        private static TimingFile _cache;
        private static bool _dirty;
        private static int _pendingUpdates;
        private static Timer _flushTimer;

        public static void RecordInterval(
            string shopKey,
            string seller,
            string buyer,
            DateTime previousReceivedAt,
            DateTime currentReceivedAt)
        {
            shopKey = (shopKey ?? string.Empty).Trim();
            if (shopKey.Length == 0) return;
            if (previousReceivedAt == DateTime.MinValue || currentReceivedAt == DateTime.MinValue) return;
            var interval = (int)Math.Round((currentReceivedAt - previousReceivedAt).TotalMilliseconds);
            // 排除同一事件重复回放、网络补抓造成的极短间隔，以及已经属于新一轮咨询的长间隔。
            if (interval < 120 || interval > 4500) return;

            lock (Sync)
            {
                EnsureLoaded();
                var key = BuildKey(shopKey, seller, buyer);
                var profile = _cache.Profiles.FirstOrDefault(x => x != null
                    && string.Equals(x.Key, key, StringComparison.Ordinal));
                if (profile == null)
                {
                    profile = new AdaptiveReplyTimingProfile { Key = key };
                    _cache.Profiles.Add(profile);
                }
                if (profile.RecentIntervalsMs == null) profile.RecentIntervalsMs = new List<int>();
                profile.RecentIntervalsMs.Add(interval);
                if (profile.RecentIntervalsMs.Count > 24)
                {
                    profile.RecentIntervalsMs.RemoveRange(0, profile.RecentIntervalsMs.Count - 24);
                }
                profile.TotalSamples++;
                profile.UpdatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                _dirty = true;
                _pendingUpdates++;
                EnsureFlushTimer();
                CleanupProfiles();

                if (profile.TotalSamples == 3 || profile.TotalSamples % 10 == 0)
                {
                    var snapshot = BuildSnapshot(profile);
                    Log.Info("已学习买家连续输入节奏: seller=" + Safe(seller, 50)
                        + ", buyer=" + Safe(buyer, 50)
                        + ", samples=" + snapshot.SampleCount
                        + ", medianMs=" + snapshot.MedianIntervalMs
                        + ", p75Ms=" + snapshot.P75IntervalMs
                        + ", recommendedMs=" + snapshot.RecommendedDelayMs);
                }

                if (_pendingUpdates >= 20) SaveInternal();
            }
        }

        public static int AdjustDelay(
            string shopKey,
            string seller,
            string buyer,
            int baselineMs,
            AdaptiveDelayKind kind)
        {
            shopKey = (shopKey ?? string.Empty).Trim();
            baselineMs = Math.Max(80, Math.Min(1800, baselineMs));
            if (shopKey.Length == 0) return baselineMs;
            AdaptiveReplyTimingSnapshot snapshot;
            lock (Sync)
            {
                EnsureLoaded();
                var key = BuildKey(shopKey, seller, buyer);
                var profile = _cache.Profiles.FirstOrDefault(x => x != null
                    && string.Equals(x.Key, key, StringComparison.Ordinal));
                if (profile == null || profile.RecentIntervalsMs == null || profile.RecentIntervalsMs.Count < 3)
                {
                    return baselineMs;
                }
                snapshot = BuildSnapshot(profile);
            }

            var learned = snapshot.RecommendedDelayMs;
            int adjusted;
            if (kind == AdaptiveDelayKind.Fragment)
            {
                adjusted = snapshot.SampleCount >= 8
                    ? Weighted(baselineMs, learned, 0.66)
                    : Math.Max(baselineMs, learned);
                adjusted = Clamp(adjusted, 650, 1550);
            }
            else if (kind == AdaptiveDelayKind.Greeting)
            {
                adjusted = Weighted(baselineMs, learned, 0.58);
                adjusted = Clamp(adjusted, 700, 1450);
            }
            else if (kind == AdaptiveDelayKind.Media)
            {
                adjusted = Weighted(baselineMs, learned, 0.58);
                adjusted = Clamp(adjusted, 600, 1400);
            }
            else if (kind == AdaptiveDelayKind.DenseBurst)
            {
                adjusted = Weighted(baselineMs, learned, 0.45);
                adjusted = Clamp(adjusted, 300, 720);
            }
            else
            {
                adjusted = Weighted(baselineMs, learned, 0.45);
                adjusted = Clamp(adjusted, 300, 950);
            }
            return adjusted;
        }

        public static AdaptiveReplyTimingSnapshot GetSnapshot(string shopKey, string seller, string buyer)
        {
            shopKey = (shopKey ?? string.Empty).Trim();
            if (shopKey.Length == 0) return new AdaptiveReplyTimingSnapshot();
            lock (Sync)
            {
                EnsureLoaded();
                var key = BuildKey(shopKey, seller, buyer);
                var profile = _cache.Profiles.FirstOrDefault(x => x != null
                    && string.Equals(x.Key, key, StringComparison.Ordinal));
                return profile == null
                    ? new AdaptiveReplyTimingSnapshot()
                    : BuildSnapshot(profile);
            }
        }

        public static void Flush()
        {
            lock (Sync)
            {
                EnsureLoaded();
                SaveInternal();
            }
        }

        private static AdaptiveReplyTimingSnapshot BuildSnapshot(AdaptiveReplyTimingProfile profile)
        {
            var values = (profile == null || profile.RecentIntervalsMs == null
                    ? new List<int>()
                    : profile.RecentIntervalsMs)
                .Where(x => x >= 120 && x <= 4500)
                .OrderBy(x => x)
                .ToList();
            if (values.Count == 0) return new AdaptiveReplyTimingSnapshot();
            var median = Percentile(values, 0.50);
            var p75 = Percentile(values, 0.75);
            // 等到略高于买家的第75百分位输入间隔，能覆盖多数连续补充，又不会按极端慢输入无限等待。
            var recommended = Clamp(p75 + 180, 350, 1600);
            return new AdaptiveReplyTimingSnapshot
            {
                SampleCount = Math.Max(values.Count, profile.TotalSamples),
                MedianIntervalMs = median,
                P75IntervalMs = p75,
                RecommendedDelayMs = recommended
            };
        }

        private static int Percentile(IList<int> sorted, double percentile)
        {
            if (sorted == null || sorted.Count == 0) return 0;
            if (sorted.Count == 1) return sorted[0];
            var position = (sorted.Count - 1) * percentile;
            var lower = (int)Math.Floor(position);
            var upper = (int)Math.Ceiling(position);
            if (lower == upper) return sorted[lower];
            var fraction = position - lower;
            return (int)Math.Round(sorted[lower] + (sorted[upper] - sorted[lower]) * fraction);
        }

        private static int Weighted(int baseline, int learned, double baselineWeight)
        {
            return (int)Math.Round(baseline * baselineWeight + learned * (1.0 - baselineWeight));
        }

        private static void EnsureLoaded()
        {
            if (_cache != null) return;
            try
            {
                var path = GetPath();
                if (!File.Exists(path))
                {
                    _cache = new TimingFile();
                    return;
                }
                var json = File.ReadAllText(path, Encoding.UTF8);
                _cache = JsonConvert.DeserializeObject<TimingFile>(json) ?? new TimingFile();
                if (_cache.Profiles == null) _cache.Profiles = new List<AdaptiveReplyTimingProfile>();
            }
            catch (Exception ex)
            {
                Log.Info("读取买家输入节奏配置失败，使用空配置：" + ex.Message);
                _cache = new TimingFile();
            }
        }

        private static void EnsureFlushTimer()
        {
            if (_flushTimer != null) return;
            _flushTimer = new Timer(_ =>
            {
                try { Flush(); } catch { }
            }, null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
        }

        private static void SaveInternal()
        {
            if (!_dirty || _cache == null) return;
            try
            {
                var path = GetPath();
                var directory = Path.GetDirectoryName(path);
                if (!Directory.Exists(directory)) Directory.CreateDirectory(directory);
                var temp = path + ".tmp";
                File.WriteAllText(temp, JsonConvert.SerializeObject(_cache, Formatting.Indented), new UTF8Encoding(false));
                if (File.Exists(path)) File.Delete(path);
                File.Move(temp, path);
                _dirty = false;
                _pendingUpdates = 0;
            }
            catch (Exception ex)
            {
                Log.Info("保存买家输入节奏配置失败：" + ex.Message);
            }
        }

        private static void CleanupProfiles()
        {
            if (_cache == null || _cache.Profiles == null) return;
            var cutoff = DateTime.Now.AddDays(-30);
            _cache.Profiles = _cache.Profiles
                .Where(x => x != null && ParseDate(x.UpdatedAt) >= cutoff)
                .OrderByDescending(x => ParseDate(x.UpdatedAt))
                .Take(2000)
                .ToList();
        }

        private static DateTime ParseDate(string value)
        {
            DateTime parsed;
            return DateTime.TryParse(value, out parsed) ? parsed : DateTime.Now;
        }

        private static string GetPath()
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "QianniuAiBot",
                "data",
                "adaptive-reply-timing.json");
        }

        private static string BuildKey(string shopKey, string seller, string buyer)
        {
            // Legacy unscoped profiles are intentionally not reused: the same seller/buyer
            // nicknames may exist in different shops and must never share learned timing.
            return (shopKey ?? string.Empty).Trim()
                + "|" + (seller ?? string.Empty).Trim().ToLowerInvariant()
                + "|" + (buyer ?? string.Empty).Trim().ToLowerInvariant();
        }

        private static int Clamp(int value, int min, int max)
        {
            return Math.Max(min, Math.Min(max, value));
        }

        private static string Safe(string value, int max)
        {
            value = (value ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim();
            return value.Length <= max ? value : value.Substring(0, max) + "...";
        }
    }
}
