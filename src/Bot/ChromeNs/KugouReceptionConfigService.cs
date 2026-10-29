using Bot.ShopScope;
using BotLib;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.IO;
using System.Linq;
using System.Text;

namespace Bot.ChromeNs
{
    internal sealed class KugouReceptionConfig
    {
        public bool EnableAutoGuidance { get; set; }
        public bool EnablePhoneWait { get; set; }
        public string TclGuideUrl { get; set; }

        public KugouReceptionConfig()
        {
            EnableAutoGuidance = true;
            EnablePhoneWait = true;
            TclGuideUrl = KugouReceptionPolicy.TClGuideUrl;
        }
    }

    // An intentionally small, explicit policy surface. Merchants can edit guidance
    // and rollback, but cannot grant the bot recharge/refund/OTP permissions via JSON.
    internal static class KugouReceptionConfigService
    {
        private const string Schema = "qnbot.kugou.reception-rules.v1";

        private static string PathForSeller(string seller)
        {
            var shop = ShopContextLocator.ResolveRuntimeBySellerNick(seller);
            if (shop == null) throw new InvalidOperationException("无法确认店铺，禁止读写跨店铺流程规则");
            using (ShopSettingsScope.Enter(shop))
            {
                var folder = Path.GetDirectoryName(BusinessPolicyProfileService.GetUserPath());
                if (string.IsNullOrWhiteSpace(folder)) throw new InvalidOperationException("店铺配置目录为空");
                return Path.Combine(folder, "kugou-reception-rules.json");
            }
        }

        private static JObject DefaultDocument()
        {
            return new JObject
            {
                ["schema"] = Schema,
                ["version"] = 1,
                ["enable_auto_guidance"] = true,
                ["enable_phone_wait"] = true,
                ["tcl_guide_url"] = KugouReceptionPolicy.TClGuideUrl,
                ["manual_review_required"] = true,
                ["automatic_recharge_enabled"] = false,
                ["notes"] = "用户端真实权益必须准确披露。高风险知识需人工审核；订单已付款/设备已核验/账号已绑定/充值到账不可互相替代。"
            };
        }

        private static JObject Validate(string raw)
        {
            var obj = JObject.Parse(raw ?? "");
            if (Convert.ToString(obj["schema"]) != Schema)
                throw new InvalidOperationException("流程规则 schema 不匹配");
            if (Convert.ToInt32(obj["version"] ?? 0) != 1)
                throw new InvalidOperationException("不支持的流程规则版本");
            if (obj["manual_review_required"] == null
                || obj.Value<bool>("manual_review_required") != true)
                throw new InvalidOperationException("不能关闭业务知识人工审核");
            if (obj.Value<bool?>("automatic_recharge_enabled") != false)
                throw new InvalidOperationException("不能通过流程JSON授权自动充值");
            var rawUrl = Convert.ToString(obj["tcl_guide_url"]);
            Uri uri;
            if (!Uri.TryCreate(rawUrl, UriKind.Absolute, out uri)
                || uri.Scheme != Uri.UriSchemeHttps
                || !string.Equals(uri.Host, "howhub.k2n.cn", StringComparison.OrdinalIgnoreCase)
                || !uri.AbsolutePath.StartsWith("/guide/", StringComparison.Ordinal))
                throw new InvalidOperationException("教程必须是已审核的 howhub.k2n.cn HTTPS 教程路径");
            if (obj["enable_phone_wait"] == null || obj["enable_auto_guidance"] == null)
                throw new InvalidOperationException("缺少启用字段");
            if (obj["enable_phone_wait"].Type != JTokenType.Boolean
                || obj["enable_auto_guidance"].Type != JTokenType.Boolean)
                throw new InvalidOperationException("启用字段必须为布尔值");
            var allowed = new[] { "schema", "version", "enable_auto_guidance", "enable_phone_wait",
                "tcl_guide_url", "manual_review_required", "automatic_recharge_enabled", "notes" };
            var unknown = obj.Properties().FirstOrDefault(p => !allowed.Contains(p.Name, StringComparer.Ordinal));
            if (unknown != null) throw new InvalidOperationException("不支持的配置字段：" + unknown.Name);
            return obj;
        }

        public static string GetJson(string seller)
        {
            var path = PathForSeller(seller);
            string error;
            var raw = CrossProcessAtomicStateFile.ReadAllTextShared(path, 2, 30, out error);
            if (!string.IsNullOrWhiteSpace(error))
                throw new IOException("读取流程配置失败：" + error);
            return (string.IsNullOrWhiteSpace(raw) ? DefaultDocument() : Validate(raw))
                .ToString(Formatting.Indented);
        }

        public static KugouReceptionConfig GetConfig(string seller)
        {
            try
            {
                var obj = Validate(GetJson(seller));
                return new KugouReceptionConfig
                {
                    EnableAutoGuidance = obj.Value<bool>("enable_auto_guidance"),
                    EnablePhoneWait = obj.Value<bool>("enable_phone_wait"),
                    TclGuideUrl = Convert.ToString(obj["tcl_guide_url"])
                };
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount("流程配置无效，禁用自动设备引导：" + ex.Message, 10);
                return new KugouReceptionConfig
                {
                    EnableAutoGuidance = false,
                    EnablePhoneWait = true,
                    TclGuideUrl = KugouReceptionPolicy.TClGuideUrl
                };
            }
        }

        public static string SaveJson(string seller, string raw)
        {
            if (!KugouReceptionPolicy.Applies(seller))
                throw new InvalidOperationException("当前店铺未匹配到酷狗业务商品，不允许写入专属规则");
            var validated = Validate(raw).ToString(Formatting.Indented);
            var path = PathForSeller(seller);
            using (var lease = CrossProcessAtomicStateFile.Acquire(path, "KugouReceptionRules", 3000))
            {
                if (lease == null || !lease.Acquired) throw new IOException("配置锁未获取，未修改任何规则");
                var directory = Path.GetDirectoryName(path);
                if (!Directory.Exists(directory)) Directory.CreateDirectory(directory);
                if (File.Exists(path))
                {
                    var backup = path + ".backup." + DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff");
                    File.Copy(path, backup, false);
                }
                string error;
                if (!CrossProcessAtomicStateFile.WriteAllTextAtomic(path, validated, 3, 50, out error))
                    throw new IOException("流程规则原子保存失败：" + error);
            }
            return "流程规则已验证并保存，已保留旧版本备份。";
        }

        public static string RestoreLatestBackup(string seller)
        {
            var path = PathForSeller(seller);
            using (var lease = CrossProcessAtomicStateFile.Acquire(path, "KugouReceptionRules", 3000))
            {
                if (lease == null || !lease.Acquired) throw new IOException("恢复锁未获取");
                var directory = Path.GetDirectoryName(path);
                if (!Directory.Exists(directory)) throw new FileNotFoundException("尚无历史备份");
                var backup = Directory.GetFiles(directory, Path.GetFileName(path) + ".backup.*")
                    .OrderByDescending(x => x, StringComparer.Ordinal).FirstOrDefault();
                if (backup == null) throw new FileNotFoundException("尚无可恢复的流程规则备份");
                var original = Validate(File.ReadAllText(backup, Encoding.UTF8)).ToString(Formatting.Indented);
                if (File.Exists(path))
                    File.Copy(path, path + ".backup." + DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff"), false);
                string error;
                if (!CrossProcessAtomicStateFile.WriteAllTextAtomic(path, original, 3, 50, out error))
                    throw new IOException("恢复历史备份失败：" + error);
            }
            return "已恢复上一份有效流程规则备份。";
        }
    }
}
