using BotLib;
using BotLib.Db.Sqlite;
using BotLib.Extensions;
using BotLib.Misc;
using Bot.Common;
using System;
using Bot.Common.Account;
using DbEntity;
using Bot.AssistWindow;
using Bot.Common.Db;

namespace Bot
{
    public class Params
    {
        public const int Version = 90502;
        public static string VersionStr;
        public const string CreateDateStr = "2023.08.18";
        public static string HelpRoot;
        public const int KeepInstalledVersionsCount = 3;
        public const string AppName = "智能辅助";
        public const int MaxAddQaCountForQuestionAndAnswersCiteTableManager = 30000;
        public const int MaxSynableQuestionTimeoutDays = 10;
        public static int BottomPannelAnswerCount;
        public static bool RulePatternMatchStrict;
        private static string _pcGuid;
        private static string _instanceGuid;
        public static readonly DateTime AppStartTime;
        public static bool IsAppClosing;

        public static string SystemInfo
        {
            get
            {
                return string.Format("{0},PcId={1},{2}", new object[]
                {
                    VersionStr,
                    PcId,
                    ComputerInfo.SysInfoForLog,
                    "软件"
                });
            }
        }

        public static string PcId
        {
            get
            {
                if (_pcGuid == null) _pcGuid = ComputerInfo.GetCpuID();
                return _pcGuid;
            }
        }

        public static string InstanceGuid
        {
            get
            {
                if (_instanceGuid == null)
                {
                    string text = PersistentParams.GetParam("InstanceGuid", "");
                    string param = PersistentParams.GetParam("PcId4InstanceGuid", "");
                    if (string.IsNullOrEmpty(text) || param != PcId)
                    {
                        text = StringEx.xGenGuidB64Str();
                        PersistentParams.TrySaveParam("InstanceGuid", text);
                        PersistentParams.TrySaveParam("PcId4InstanceGuid", PcId);
                    }
                    _instanceGuid = text;
                }
                return _instanceGuid;
            }
        }

        public static bool IsAppStartMoreThan10Second
        {
            get { return (DateTime.Now - AppStartTime).TotalSeconds > 10.0; }
        }

        public static bool IsAppStartMoreThan20Second
        {
            get { return (DateTime.Now - AppStartTime).TotalSeconds > 20.0; }
        }

        static Params()
        {
            VersionStr = ShareUtil.ConvertVersionToString(Version);
            HelpRoot = "https://github.com/renchengxiaofeixia";
            AppStartTime = DateTime.Now;
            IsAppClosing = false;
        }

        public static void SetProcessPath(string processName, string processPath)
        {
            PersistentParams.TrySaveParam(GetProcessPathKey(processName), processPath);
        }

        private static string GetProcessPathKey(string processName)
        {
            return "ProcessPath#" + processName;
        }

        public static string GetProcessPath(string processName)
        {
            return PersistentParams.GetParam(GetProcessPathKey(processName), "");
        }

        public class Other
        {
        }

        public class Panel
        {
            public const string RightPanelCompOrderCsvDefault = "工作台";
            public const bool ShortcutIsVisibleDefault = true;
            public const bool GoodsKnowledgeIsVisibleDefault = true;
            public const bool RobotIsVisibleDefault = true;
            public const bool OrderIsVisibleDefault = true;
            public const bool LogisIsVisibleDefault = true;
            public const bool CouponIsVisibleDefault = true;

            public static string GetRightPanelCompOrderCsv(string seller)
            {
                return PersistentParams.GetParam2Key("RightPanelCompOrderCsv", seller, RightPanelCompOrderCsvDefault);
            }

            public static void SetRightPanelCompOrderCsv(string seller, string tabs)
            {
                PersistentParams.TrySaveParam2Key("RightPanelCompOrderCsv", seller, tabs);
            }

            public static bool GetShortcutIsVisible(string seller) { return PersistentParams.GetParam2Key("ShortcutIsVisible", seller, true); }
            public static void SetShortcutIsVisible(string seller, bool visible) { PersistentParams.TrySaveParam2Key("ShortcutIsVisible", seller, visible); }
            public static bool GetGoodsKnowledgeIsVisible(string seller) { return PersistentParams.GetParam2Key("GoodsKnowledgeIsVisible", seller, true); }
            public static void SetGoodsKnowledgeIsVisible(string seller, bool visible) { PersistentParams.TrySaveParam2Key("GoodsKnowledgeIsVisible", seller, visible); }
            public static bool GetRobotIsVisible(string seller) { return PersistentParams.GetParam2Key("RobotIsVisible", seller, true); }
            public static void SetRobotIsVisible(string seller, bool visible) { PersistentParams.TrySaveParam2Key("RobotIsVisible", seller, visible); }
            public static bool GetOrderIsVisible(string seller) { return PersistentParams.GetParam2Key("OrderIsVisible", seller, true); }
            public static void SetOrderIsVisible(string seller, bool visible) { PersistentParams.TrySaveParam2Key("OrderIsVisible", seller, visible); }
            public static bool GetCouponIsVisible(string seller) { return PersistentParams.GetParam2Key("CouponIsVisible", seller, true); }
            public static void SetCouponIsVisible(string seller, bool visible) { PersistentParams.TrySaveParam2Key("CouponIsVisible", seller, visible); }

            public static bool GetPanelOptionVisible(string seller, string tabName)
            {
                if (tabName == "话术") return GetShortcutIsVisible(seller);
                if (tabName == "订单") return GetOrderIsVisible(seller);
                // "工作台" is the persisted/default name for the Bot tab. Older
                // settings also used "机器人"; both must share the same visibility flag.
                if (tabName == "工作台" || tabName == "机器人"
                    || tabName == "Bot" || tabName == "Robot" || tabName == "状态")
                    return GetRobotIsVisible(seller);
                if (tabName == "商品") return GetGoodsKnowledgeIsVisible(seller);
                if (tabName == "优惠券") return GetCouponIsVisible(seller);
                return false;
            }
        }

        public class Robot
        {
            private const string RuntimeScope = "shop-runtime";
            private static bool _processCanUseRobot;

            public const int AutoModeBringForegroundIntervalSecond = 5;
            public const int AutoModeCloseUnAnsweredBuyerIntervalSecond = 10;
            public const bool RuleIncludeExceptDefault = false;
            public const int AutoModeReplyDelaySecDefault = 0;
            public const int SendModeReplyDelaySecDefault = 0;
            public const bool QuoteModeSendAnswerWhenFullMatchDefault = false;
            public const double AutoModeAnswerMiniScore = 0.5;
            public const double QuoteOrSendModeAnswerMiniScore = 0.5;
            public const bool CancelAutoOnResetDefault = true;
            public const string AutoModeNoAnswerTipDefault = "亲,目前是机器人值班.这个问题机器人无法回答,等人工客服回来后再回复您.";

            static Robot()
            {
                _processCanUseRobot = true;
            }

            public enum OperationEnum
            {
                None,
                Auto,
                Send,
                Quote
            }

            public static bool CanUseRobot
            {
                get
                {
                    if (ShopScope.ShopSettingsScope.Current == null) return _processCanUseRobot;
                    return PersistentParams.GetParam2Key("BotEnabled", RuntimeScope, _processCanUseRobot);
                }
                set
                {
                    if (ShopScope.ShopSettingsScope.Current == null)
                    {
                        _processCanUseRobot = value;
                        return;
                    }
                    PersistentParams.TrySaveParam2Key("BotEnabled", RuntimeScope, value);
                }
            }

            public static bool CanUseRobotReal
            {
                get { return CanUseRobot; }
            }

            public static string GetBaseUrl() { return PersistentParams.GetParam2Key("BaseUrl", "ai", string.Empty); }
            public static void SetBaseUrl(string baseUrl) { PersistentParams.TrySaveParam2Key("BaseUrl", "ai", baseUrl); }
            public static string GetApiKey() { return PersistentParams.GetParam2Key("ApiKey", "ai", string.Empty); }
            public static void SetApiKey(string apiKey) { PersistentParams.TrySaveParam2Key("ApiKey", "ai", apiKey); }
            public static string GetModelName() { return PersistentParams.GetParam2Key("ModelName", "ai", string.Empty); }
            public static void SetModelName(string modelName) { PersistentParams.TrySaveParam2Key("ModelName", "ai", modelName); }
            public static string GetSystemPrompt() { return PersistentParams.GetParam2Key("SystemPrompt", "ai", string.Empty); }
            public static void SetSystemPrompt(string systemPrompt) { PersistentParams.TrySaveParam2Key("SystemPrompt", "ai", systemPrompt); }
            public static OperationEnum GetOperation() { return PersistentParams.GetParam2Key("Robot.Operation", "ai", OperationEnum.None); }
            public static void SetOperation(string nick, OperationEnum operation) { PersistentParams.TrySaveParam2Key("Robot.Operation", "ai", operation); }

            public static bool GetIsAutoReply()
            {
                if (ShopScope.ShopSettingsScope.Current == null)
                    return PersistentParams.GetParam<bool>("IsAutoReply", true);
                return PersistentParams.GetParam2Key("IsAutoReply", RuntimeScope, true);
            }

            public static void SetIsAutoReply(bool isAutoReply)
            {
                if (ShopScope.ShopSettingsScope.Current == null)
                {
                    PersistentParams.TrySaveParam<bool>("IsAutoReply", isAutoReply);
                    return;
                }
                PersistentParams.TrySaveParam2Key("IsAutoReply", RuntimeScope, isAutoReply);
            }

            public static string GetAutoModeNoAnswerTip(string nick)
            {
                if (ShopScope.ShopSettingsScope.Current == null)
                    return PersistentParams.GetParam2Key("Robot.AutoModeNoAnswerTip", nick, AutoModeNoAnswerTipDefault);
                return PersistentParams.GetParam2Key("Robot.AutoModeNoAnswerTip", RuntimeScope, AutoModeNoAnswerTipDefault);
            }

            public static void SetAutoModeNoAnswerTip(string nick, string autoModeNoAnswerTip)
            {
                if (ShopScope.ShopSettingsScope.Current == null)
                {
                    PersistentParams.TrySaveParam2Key("Robot.AutoModeNoAnswerTip", nick, autoModeNoAnswerTip);
                    return;
                }
                PersistentParams.TrySaveParam2Key("Robot.AutoModeNoAnswerTip", RuntimeScope, autoModeNoAnswerTip);
            }
        }
    }
}