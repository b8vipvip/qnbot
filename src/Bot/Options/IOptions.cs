namespace Bot.Options
{
    public interface IOptions
    {
        void Save(string seller);
        void RestoreDefault();
        void NavHelp();
        OptionEnum OptionType { get; }
        void InitUI(string seller);
    }

    public enum OptionEnum
    {
        Unknown,
        InputSuggestion,
        ChaDang,
        GoodsKnowledge,
        Shortcut,
        Coupon,
        Panel,
        BuyerNote,
        Other,
        SuperAccount,
        TopKey,
        Memo,
        HeDui,
        ChaJian,
        Logis,
        ShopDataShare,
        HotKey,
        Robot,
        ShopBinding,
        RemindPay,
        InviteOrder,
        DataManagement,
        AboutUpdate,
        AutoReplyRules,
        Notifications,
        MessagePolicy,
        Diagnostics,
        Compliance,
        AutoDelivery,
        FeatureSettings
    }
}
