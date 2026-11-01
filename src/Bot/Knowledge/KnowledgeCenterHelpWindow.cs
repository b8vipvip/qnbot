using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace Bot.Knowledge
{
    /// <summary>
    /// Read-only, in-client help for the current Knowledge Center V2 implementation.
    /// Keep the operational facts here aligned with docs/KNOWLEDGE_CENTER_V2_USER_HELP.md.
    /// </summary>
    internal sealed class KnowledgeCenterHelpWindow : Window
    {
        private static readonly Brush Accent = new SolidColorBrush(Color.FromRgb(37, 99, 235));
        private static readonly Brush Heading = new SolidColorBrush(Color.FromRgb(31, 41, 55));
        private static readonly Brush Muted = new SolidColorBrush(Color.FromRgb(91, 103, 122));
        private static readonly Brush Panel = new SolidColorBrush(Color.FromRgb(248, 250, 253));
        private static readonly Brush Border = new SolidColorBrush(Color.FromRgb(220, 226, 234));

        private readonly string _seller;

        private KnowledgeCenterHelpWindow(string seller)
        {
            _seller = (seller ?? string.Empty).Trim();
            Title = "知识库使用帮助";
            Width = 1120;
            Height = 780;
            MinWidth = 900;
            MinHeight = 640;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Content = BuildLayout();
            PreviewKeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (e.Key != Key.Escape) return;
                e.Handled = true;
                Close();
            };
        }

        internal static void MyShow(Window owner, string seller)
        {
            var effectiveSeller = (seller ?? string.Empty).Trim();
            if (effectiveSeller.Length == 0)
            {
                try { effectiveSeller = KnowledgeCenterV2Context.ResolveSeller(owner); }
                catch { effectiveSeller = string.Empty; }
            }

            var window = new KnowledgeCenterHelpWindow(effectiveSeller);
            if (owner != null) window.Owner = owner;
            window.Show();
        }

        private UIElement BuildLayout()
        {
            var root = new Grid { Background = Brushes.White };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var header = BuildHeader();
            Grid.SetRow(header, 0);
            root.Children.Add(header);

            var tabs = new TabControl { Margin = new Thickness(14, 10, 14, 10) };
            tabs.Items.Add(Tab("快速配置", BuildQuickStartDocument()));
            tabs.Items.Add(Tab("字段说明", BuildFieldReferenceDocument()));
            tabs.Items.Add(Tab("工作原理", BuildRuntimeDocument()));
            tabs.Items.Add(Tab("学习与治理", BuildGovernanceDocument()));
            tabs.Items.Add(Tab("备份与排查", BuildSafetyDocument()));
            Grid.SetRow(tabs, 1);
            root.Children.Add(tabs);

            var footer = new DockPanel
            {
                Margin = new Thickness(14, 0, 14, 12),
                LastChildFill = true
            };
            var close = new Button
            {
                Content = "关闭",
                Width = 82,
                Height = 30,
                IsCancel = true,
                Margin = new Thickness(12, 0, 0, 0)
            };
            close.Click += delegate { Close(); };
            DockPanel.SetDock(close, Dock.Right);
            footer.Children.Add(close);
            footer.Children.Add(new TextBlock
            {
                Text = "本帮助只说明和读取当前功能，不会修改知识、阈值或店铺设置。按 Esc 可关闭。",
                Foreground = Muted,
                VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.Wrap
            });
            Grid.SetRow(footer, 2);
            root.Children.Add(footer);
            return root;
        }

        private UIElement BuildHeader()
        {
            var border = new Border
            {
                Background = Panel,
                BorderBrush = Border,
                BorderThickness = new Thickness(0, 0, 0, 1),
                Padding = new Thickness(22, 16, 22, 14)
            };
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var title = new StackPanel();
            title.Children.Add(new TextBlock
            {
                Text = "知识库使用帮助",
                FontSize = 24,
                FontWeight = FontWeights.SemiBold,
                Foreground = Heading
            });
            title.Children.Add(new TextBlock
            {
                Text = "Knowledge Center V2 的配置方法、结构化字段、回复决策、学习治理和数据保护",
                Foreground = Muted,
                Margin = new Thickness(0, 5, 0, 0),
                TextWrapping = TextWrapping.Wrap
            });
            grid.Children.Add(title);

            var scope = new Border
            {
                Background = Brushes.White,
                BorderBrush = Border,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(12, 8, 12, 8),
                VerticalAlignment = VerticalAlignment.Center
            };
            scope.Child = new TextBlock
            {
                Text = "当前店铺：" + (_seller.Length == 0 ? "未识别，请从对应店铺设置页重新打开" : _seller),
                Foreground = _seller.Length == 0 ? Brushes.DarkOrange : Heading,
                FontWeight = FontWeights.SemiBold
            };
            Grid.SetColumn(scope, 1);
            grid.Children.Add(scope);
            border.Child = grid;
            return border;
        }

        private static TabItem Tab(string header, FlowDocument document)
        {
            return new TabItem
            {
                Header = header,
                Content = new FlowDocumentScrollViewer
                {
                    Document = document,
                    IsToolBarVisible = false,
                    IsSelectionEnabled = true,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
                }
            };
        }

        private FlowDocument BuildQuickStartDocument()
        {
            var document = NewDocument();
            document.Blocks.Add(TitleBlock("推荐的首次配置顺序"));
            document.Blocks.Add(Callout(
                "店铺隔离是第一原则",
                "知识、V2 引擎设置、反馈、修订和治理历史都按 ShopKey 独立保存。先确认右上角店铺正确；未识别店铺时不要继续导入或编辑。"));
            document.Blocks.Add(Numbered(
                "确认店铺与备份：在“设置 → 店铺与连接 → 店铺绑定”核对当前店铺。首次调整前进入知识中心“导入导出”，先点“导出V2完整包”。云同步是可选项，也只同步当前 ShopKey。",
                "选择回复模式：在“设置 → 回复与通知 → 自动回复规则 → 回复模式”选择 AI优先 或 本地优先。AI优先把本地知识作为上下文交给 AI 生成最终答案；本地优先才允许高置信知识零 AI 直答，未达到条件仍回退兼容 AI 链路。",
                "录入结构化知识：在“知识 / 商品知识 / 流程”页点击“新增知识”。至少认真填写标题、Intent、Subject、Predicate、Aliases、标准答案、风险等级、可信度、权威度，并保持 Enabled=true、Status=active。",
                "先以 Shadow 验证：进入“设置”，启用 Knowledge Engine V2，运行模式先选 shadow；建议先保留默认本地直答匹配阈值 0.82、最低知识可信度 0.68，保存后点“立即预热索引”。",
                "在测试台验证：用真实但脱敏的典型问法、同义问法、上下文短句和容易混淆的问题逐条测试，查看 Intent / Subject / Predicate / Top Matches / 拒绝原因；同时处理“冲突”和“学习”页的待办。",
                "确认后再切 Production：只有“本地优先 + V2已启用 + production + active知识 + 无冲突 + 非高风险 + 分数达标”时才可能本地直答。切换后继续观察顶部“质量 / 修订 / 治理”。"));

            document.Blocks.Add(HeadingBlock("最小可用知识示例"));
            document.Blocks.Add(LabelBlock("标题", "电视会员是什么"));
            document.Blocks.Add(LabelBlock("类型", "presale（售前事实）或 product_knowledge（明确绑定商品时）"));
            document.Blocks.Add(LabelBlock("Intent", "capability"));
            document.Blocks.Add(LabelBlock("Subject", "酷狗音乐/电视/会员"));
            document.Blocks.Add(LabelBlock("Predicate", "membership_type"));
            document.Blocks.Add(LabelBlock("Entities", "酷狗音乐、电视、会员；建议每行一个"));
            document.Blocks.Add(LabelBlock("Aliases", "这个是电视会员吗 / TV版会员吗；把买家真实常用问法分行填写"));
            document.Blocks.Add(LabelBlock("标准答案", "只写已经确认的业务事实，不编造价格、库存、到账、物流或售后承诺"));
            document.Blocks.Add(LabelBlock("状态", "RiskLevel=normal，Enabled=true，Status=active"));

            document.Blocks.Add(HeadingBlock("上线前检查清单"));
            document.Blocks.Add(Bullets(
                "同一 Subject + Predicate 下没有相互矛盾的 active 答案。",
                "学习候选已经人工批准或明确驳回，没有让 candidate 直接进入生产。",
                "退款、赔偿、密码、验证码、银行卡、法律等内容标为 high；高风险知识不会本地直答。",
                "测试台的常见问法能命中预期知识，近似问题不会误命中。",
                "已导出当前店铺 V2 完整包；生产切换只针对当前店铺。"));
            return document;
        }

        private static FlowDocument BuildFieldReferenceDocument()
        {
            var document = NewDocument();
            document.Blocks.Add(TitleBlock("结构化字段怎么填"));
            document.Blocks.Add(BodyBlock(
                "当前 V2 索引主要使用标题、Aliases、Intent、Predicate 和 Entities 召回与排序。Subject + Predicate 组成事实键，用于判断真正的事实冲突。列表字段支持逗号、中文逗号、分号、竖线或换行分隔，建议一行一项。"));

            document.Blocks.Add(HeadingBlock("核心识别字段"));
            document.Blocks.Add(LabelBlock("标题", "这条知识解决的标准问题。写成买家会问的完整问题，不要只写“会员”或“售后”。"));
            document.Blocks.Add(LabelBlock("Intent", "买家的目的。常用值：general、capability、purchase、price、time、how_to、troubleshoot、requirements、after_sale。"));
            document.Blocks.Add(LabelBlock("Subject", "业务对象，例如“酷狗音乐/电视/会员”。同一业务对象应保持相同写法，避免被拆成多个事实池。"));
            document.Blocks.Add(LabelBlock("Predicate", "正在询问的属性。常用值：membership_type、purchase_channel、price、device_support、feature_support、activation_method、account_binding、refund_policy、time、troubleshooting、requirements。"));
            document.Blocks.Add(LabelBlock("Entities", "用于实体召回的品牌、平台、设备、会员、功能等关键词。商品 ID 需要参与问法检索时，也应保留对应实体/问法。"));
            document.Blocks.Add(LabelBlock("Aliases", "同义问法和常见口语；精确或近似精确 Alias 是本地直答的重要条件。不要把答案、无关关键词或不同业务问题混在同一条里。"));

            document.Blocks.Add(HeadingBlock("答案与适用范围"));
            document.Blocks.Add(LabelBlock("标准答案", "本地直答实际发送的答案来源。应完整、明确、可直接给买家，并只包含已经确认的事实。"));
            document.Blocks.Add(LabelBlock("简短答案", "可选的结构化摘要；当前本地直答仍以“标准答案”为准。"));
            document.Blocks.Add(LabelBlock("适用条件", "记录该答案成立的前提，例如指定版本、渠道或商品。"));
            document.Blocks.Add(LabelBlock("排除条件", "记录不应使用该答案的场景，例如旧版、其他渠道或不适用商品。"));
            document.Blocks.Add(LabelBlock("必要上下文", "记录回答前需要确认的信息。当前字段不会绕过上层订单、上下文和高风险安全门控。"));
            document.Blocks.Add(LabelBlock("绑定商品ID", "用于商品知识分类、统计和完整包迁移；填写精确商品 ID，每行一个。"));

            document.Blocks.Add(HeadingBlock("类型、风险和状态"));
            document.Blocks.Add(LabelBlock("business_fact", "普通业务事实。"));
            document.Blocks.Add(LabelBlock("procedure", "操作、绑定、充值、激活等步骤。"));
            document.Blocks.Add(LabelBlock("presale / product_knowledge", "售前能力、购买、价格或明确商品知识。"));
            document.Blocks.Add(LabelBlock("order_rule / after_sale", "订单规则或售后规则。"));
            document.Blocks.Add(LabelBlock("safety_rule", "退款、账号、安全、法律等边界事实。"));
            document.Blocks.Add(LabelBlock("fixed_reply / temporary", "固定话术或临时知识；临时内容应定期复核。"));
            document.Blocks.Add(LabelBlock("learning_candidate", "自动学习或人工纠正形成的候选；必须在“学习”页批准后才可进入生产。"));
            document.Blocks.Add(LabelBlock("风险等级", "normal 或 high。high 以及命中高风险语义的内容不会走本地直答。"));
            document.Blocks.Add(LabelBlock("可信度 / 权威度", "均为 0~1。综合可信度由知识可信度、权威度和真实反馈小幅修正；不要为追求直答率随意全部填 1。"));
            document.Blocks.Add(LabelBlock("Enabled / Status", "生产知识通常为 Enabled=true、Status=active；candidate 不直答，disabled 不进入运行时索引。"));

            document.Blocks.Add(HeadingBlock("为什么 Subject 与 Predicate 必须拆开"));
            document.Blocks.Add(BodyBlock(
                "“电视会员是什么”和“电视会员在哪里买”可以拥有同一 Subject，但 Predicate 分别是 membership_type 与 purchase_channel，因此不是冲突。只有相同 Subject + Predicate 的不同答案才进入冲突检查。"));
            return document;
        }

        private static FlowDocument BuildRuntimeDocument()
        {
            var document = NewDocument();
            document.Blocks.Add(TitleBlock("知识库如何参与回复"));
            document.Blocks.Add(HeadingBlock("1. 入口条件"));
            document.Blocks.Add(BodyBlock(
                "买家文本进入回复协调器后，只有当前店铺选择“本地优先”且 Knowledge Engine V2 已启用，V2 才尝试本地直答。AI优先模式继续把知识作为兼容链路的上下文，由 AI 生成最终答案。图片/视觉消息及不可回复消息继续走既有链路。"));

            document.Blocks.Add(HeadingBlock("2. 消息理解与 Working Memory"));
            document.Blocks.Add(BodyBlock(
                "系统从当前消息解析 Intent、Subject、Predicate 和 Entities。当前明确消息优先；只有“这个呢”“能用吗”“怎么弄”等缺少完整主体的短句，才会使用最近 45 分钟的买家级 Working Memory 补全缺失对象。新消息已明确新主体时，旧上下文不能覆盖它。"));

            document.Blocks.Add(HeadingBlock("3. 本地召回与排序"));
            document.Blocks.Add(BodyBlock(
                "每个店铺有独立的内存 Snapshot，包含 Exact、Intent、Predicate、Entity 和中文 2-gram 索引。系统先限制候选规模，再按 Predicate、Entity、Intent、Alias、知识可信度和真实反馈修正排序，不会在每条买家消息上全表扫描知识。"));

            document.Blocks.Add(HeadingBlock("4. 本地直答必须同时通过的门"));
            document.Blocks.Add(Bullets(
                "回复模式为“本地优先”，V2 已启用且运行模式为 production。",
                "知识为 Enabled=true、Status=active，且不是 learning_candidate。",
                "同一事实键没有有效答案冲突，问题和答案不属于高风险。",
                "结构化匹配分达到“本地直答匹配阈值”（默认 0.82）。",
                "综合知识可信度达到“最低知识可信度”（默认 0.68）。",
                "Predicate 足够明确或 Alias 高度匹配，并且第一、第二候选分差足够。"));
            document.Blocks.Add(Callout(
                "阈值不是唯一条件",
                "把阈值调低并不能绕过冲突、高风险、候选状态、Alias/Predicate 明确度或候选分差检查。遇到未直答请先看“测试台”的拒绝原因。"));

            document.Blocks.Add(HeadingBlock("5. 失败时安全回退"));
            document.Blocks.Add(BodyBlock(
                "索引未预热、没有候选、分数不足、Shadow 模式、冲突、高风险或其他门控未通过时，本轮不会阻塞买家消息，也不会勉强发送；系统继续走现有 Smart Reply / AI 兼容链路。冷索引会在后台预热。"));

            document.Blocks.Add(HeadingBlock("6. 发送仍走原安全链路"));
            document.Blocks.Add(BodyBlock(
                "本地答案仍必须经过自动回复开关、任务有效性、并发相关性、目标买家确认、去重、Bot消息后缀、SendTextWithRetryAsync、发送回显和失败诊断。知识库不会绕过现有发送安全边界。"));

            document.Blocks.Add(HeadingBlock("7. 索引更新"));
            document.Blocks.Add(BodyBlock(
                "普通新增/编辑通过原子增量更新当前店铺 Snapshot；批量替换、删除/停用和重新迁移等结构性操作会重建或替换 Snapshot。并发查询只会看到更新前或更新后的完整快照。"));
            return document;
        }

        private static FlowDocument BuildGovernanceDocument()
        {
            var document = NewDocument();
            document.Blocks.Add(TitleBlock("学习、质量、修订与治理"));
            document.Blocks.Add(Callout(
                "共同安全原则",
                "自动学习只产生候选或质量证据；没有人工批准，不会自动覆盖、停用、删除或回滚生产知识。"));

            document.Blocks.Add(HeadingBlock("学习页"));
            document.Blocks.Add(BodyBlock(
                "接待结束复盘、人工客服最终回复和其他学习来源会生成 Type=learning_candidate、Status=candidate 的记录。候选不会参与本地直答，也会在旧知识兼容镜像中保持禁用。选中并点击“批准入库”后，才转为正式 active 知识。"));

            document.Blocks.Add(HeadingBlock("质量入口"));
            document.Blocks.Add(BodyBlock(
                "真实发送成功才记录 sent；买家明确认可或人工采用相同答案记录 accepted；明确否定后人工给出不同答案记录 correction；撤回记录 withdrawal；发送失败记录 send_failed，但不惩罚知识正确性。质量页展示健康、观察、低质量和未使用状态，当前不会自动禁用知识。"));

            document.Blocks.Add(HeadingBlock("修订入口"));
            document.Blocks.Add(BodyBlock(
                "修订候选来自最近 120 天的真实人工纠正聚类，不由 AI 编造事实。普通候选至少需要 2 次纠正、2 个不同买家和足够一致度；高风险知识至少 3 次纠正、3 个不同买家。应用和驳回都由人工触发；如果原答案已被后来人工修改，候选会变为 stale 并拒绝覆盖。"));

            document.Blocks.Add(HeadingBlock("治理队列与修订效果"));
            document.Blocks.Add(BodyBlock(
                "治理集中显示低质量、冲突、待复核修订、验证过期、长期未使用和建议回滚等问题。扫描本身只读。“确认仍有效”只刷新验证时间；停用、应用修订和回滚都要求人工确认。"));
            document.Blocks.Add(LabelBlock("回滚建议门槛", "修订后至少 3 次有效发送、至少 2 次负向证据、负向率至少 25%，并且比修订前恶化至少 15 个百分点。满足条件也只提示，不自动回滚。"));

            document.Blocks.Add(HeadingBlock("治理设置（当前店铺独立）"));
            document.Blocks.Add(LabelBlock("普通知识验证过期", "默认 180 天，允许 30–730 天。"));
            document.Blocks.Add(LabelBlock("高风险知识验证过期", "默认 60 天，允许 7–365 天，且不能大于普通阈值。"));
            document.Blocks.Add(LabelBlock("长期未使用提醒", "默认 120 天，允许 30–730 天。"));
            document.Blocks.Add(BodyBlock(
                "这些天数只决定何时进入人工治理队列。保存后会重新扫描并追加“治理历史”，不会自动修改生产知识。治理历史是追加式审计，答案只记录 SHA-256 指纹，不复制完整答案。"));
            return document;
        }

        private static FlowDocument BuildSafetyDocument()
        {
            var document = NewDocument();
            document.Blocks.Add(TitleBlock("备份、同步、数据位置与常见排查"));
            document.Blocks.Add(HeadingBlock("店铺级数据"));
            document.Blocks.Add(Bullets(
                "knowledge-center-v2.db：正式结构化知识与 V2 元数据。",
                "knowledge-feedback-v2.db：真实发送、确认、纠正、撤回和发送失败事件。",
                "knowledge-revision-v2.db：修订候选、原答案、建议答案和人工处理状态。",
                "knowledge-governance-v2.db：追加式治理动作历史。",
                "引擎和治理阈值保存为当前 ShopKey 的受保护设置；店铺显示名不作为目录或授权键。"));
            document.Blocks.Add(BodyBlock(
                "程序升级不会用安装包覆盖 data、params.db、店铺配置、账号信息或上述知识数据库。云同步启用后也只同步当前 ShopKey；写入云端版本前保留本店备份。"));

            document.Blocks.Add(HeadingBlock("导入导出怎么选"));
            document.Blocks.Add(LabelBlock("导出V2完整包", "包含全部结构化知识、V2 设置和 Schema Version；推荐在批量操作或调整生产设置前使用。"));
            document.Blocks.Add(LabelBlock("导入V2完整包", "会替换当前店铺的 V2 结构化知识和包内设置；客户端会先自动创建 JSON 备份，并要求 Yes/No 确认。"));
            document.Blocks.Add(LabelBlock("仅导出结构化知识", "只导出记录，不包含完整 V2 设置，适合审阅或转换。"));
            document.Blocks.Add(LabelBlock("从旧知识重新迁移", "会清空并重建当前店铺 V2 数据；必须先导出完整包，只在确认需要重新迁移时使用。"));

            document.Blocks.Add(HeadingBlock("旧版知识库预览"));
            document.Blocks.Add(BodyBlock(
                "“设置 → 知识库”（直接进入 Knowledge Center V2）→ 顶部“旧版知识库预览”只读取当前 ShopKey 已保存的旧版问答快照，便于核对分类、问题、答案、关键词、来源和时间。它不挂载旧版管理界面，不提供新增、编辑、删除、导入导出或 AI 优化，也不会启用旧版检索、匹配或自动回复。多店铺环境不会猜测旧全局数据归属；列表为空且仍有旧数据时，请先在“店铺绑定”确认归属。"));

            document.Blocks.Add(HeadingBlock("为什么没有本地直答"));
            document.Blocks.Add(Numbered(
                "先确认当前店铺正确，自动回复已开启，回复模式是“本地优先”。",
                "确认 V2 已启用且运行模式是 production；Shadow 只计算、不发送。",
                "确认知识 Enabled=true、Status=active，不是 learning_candidate，也不是 high 风险。",
                "打开“冲突”检查同一 Subject + Predicate 是否存在不同答案。",
                "在“测试台”输入同一问法，查看 Top Matches、匹配分、综合可信度和具体拒绝原因。",
                "补充准确 Alias、Entities、Intent、Subject 和 Predicate；不要第一步就降低全店阈值。"));

            document.Blocks.Add(HeadingBlock("编辑或导入后没有更新"));
            document.Blocks.Add(Bullets(
                "先点知识中心顶部“刷新”，再到“设置”点“立即预热索引”。",
                "确认编辑的是当前店铺，不要只按相似的店铺显示名判断。",
                "候选知识需要在“学习”页批准；disabled 记录保留用于管理但不进入运行时索引。",
                "云同步问题请回到“设置 → 店铺与连接 → 店铺绑定”检查 Token、ShopKey 和本店云同步开关。"));

            document.Blocks.Add(HeadingBlock("阈值怎么调"));
            document.Blocks.Add(BodyBlock(
                "默认值是直答匹配 0.82、最低可信度 0.68。误答时优先补结构化字段、拆分混合事实、解决冲突或提高阈值；漏答时先补 Alias / Entities 并查看测试台原因。每次只改一个变量，用相同测试问题复测，稳定后再进入 production。"));
            return document;
        }

        private static FlowDocument NewDocument()
        {
            return new FlowDocument
            {
                PagePadding = new Thickness(24, 20, 28, 28),
                FontFamily = new FontFamily("Microsoft YaHei UI"),
                FontSize = 14,
                Foreground = Heading,
                ColumnWidth = double.PositiveInfinity,
                LineHeight = 22
            };
        }

        private static Paragraph TitleBlock(string text)
        {
            return new Paragraph(new Run(text))
            {
                FontSize = 23,
                FontWeight = FontWeights.SemiBold,
                Foreground = Heading,
                Margin = new Thickness(0, 0, 0, 14)
            };
        }

        private static Paragraph HeadingBlock(string text)
        {
            return new Paragraph(new Run(text))
            {
                FontSize = 17,
                FontWeight = FontWeights.SemiBold,
                Foreground = Heading,
                Margin = new Thickness(0, 17, 0, 7),
                KeepWithNext = true
            };
        }

        private static Paragraph BodyBlock(string text)
        {
            return new Paragraph(new Run(text))
            {
                Margin = new Thickness(0, 0, 0, 9)
            };
        }

        private static Paragraph LabelBlock(string label, string text)
        {
            var paragraph = new Paragraph { Margin = new Thickness(0, 0, 0, 7) };
            paragraph.Inlines.Add(new Bold(new Run(label + "：")) { Foreground = Accent });
            paragraph.Inlines.Add(new Run(text));
            return paragraph;
        }

        private static Section Callout(string title, string text)
        {
            var section = new Section
            {
                Background = Panel,
                BorderBrush = Accent,
                BorderThickness = new Thickness(4, 0, 0, 0),
                Padding = new Thickness(13, 10, 13, 10),
                Margin = new Thickness(0, 2, 0, 12)
            };
            section.Blocks.Add(LabelBlock(title, text));
            return section;
        }

        private static System.Windows.Documents.List Bullets(params string[] items)
        {
            return DocumentList(TextMarkerStyle.Disc, items);
        }

        private static System.Windows.Documents.List Numbered(params string[] items)
        {
            return DocumentList(TextMarkerStyle.Decimal, items);
        }

        private static System.Windows.Documents.List DocumentList(TextMarkerStyle style, params string[] items)
        {
            var list = new System.Windows.Documents.List
            {
                MarkerStyle = style,
                MarkerOffset = 18,
                Padding = new Thickness(4, 0, 0, 0),
                Margin = new Thickness(0, 0, 0, 10)
            };
            foreach (var item in items ?? new string[0])
            {
                list.ListItems.Add(new ListItem(new Paragraph(new Run(item))
                {
                    Margin = new Thickness(0, 0, 0, 6)
                }));
            }
            return list;
        }
    }
}
