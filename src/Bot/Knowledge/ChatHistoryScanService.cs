using Bot.Automation.ChatDeskNs;
using Bot.ChatRecord;
using Bot.ChromeNs;
using BotLib;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.UIA3;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Bot.Knowledge
{
    public sealed class ChatHistoryScanOptions
    {
        public bool ScanAll { get; set; }
        public DateTime? StartTime { get; set; }
        public DateTime? EndTime { get; set; }
        public int MaxContacts { get; set; }

        public ChatHistoryScanOptions()
        {
            ScanAll = true;
            MaxContacts = 1000;
        }
    }

    public sealed class ChatHistoryScanProgress
    {
        public string Phase { get; set; }
        public int ContactIndex { get; set; }
        public int ContactCount { get; set; }
        public string Buyer { get; set; }
        public int MessageCount { get; set; }
        public int PairCount { get; set; }
        public int TextChars { get; set; }

        public override string ToString()
        {
            var sb = new StringBuilder();
            sb.AppendLine(string.IsNullOrWhiteSpace(Phase) ? "正在处理..." : Phase);
            if (ContactCount > 0)
            {
                sb.AppendLine("联系人：" + ContactIndex + "/" + ContactCount + "  " + (Buyer ?? string.Empty));
            }
            sb.AppendLine("已保留有效消息：" + MessageCount);
            sb.AppendLine("已整理问答轮次：" + PairCount);
            sb.Append("已整理字符：" + TextChars.ToString("N0"));
            return sb.ToString();
        }
    }

    public sealed class ChatHistoryScanResult
    {
        public bool MessageManagerOpened { get; set; }
        public int ChatBuyerListContactCount { get; set; }
        public int MessageManagerContactCount { get; set; }
        public int ApiContactCount { get; set; }
        public int ContactCount { get; set; }
        public int ScannedContacts { get; set; }
        public int FailedContacts { get; set; }
        public int MessageCount { get; set; }
        public int PairCount { get; set; }
        public int TextChars { get; set; }
        public KnowledgeImportResult ImportResult { get; set; }
        public string Diagnostics { get; set; }
    }

    public sealed class ChatHistoryScanService
    {
        private const int PageSize = 100;
        private const int MaxHistoryPages = 200;
        private static readonly string[] NickFields =
        {
            "nick", "buyerNick", "targetNick", "targetNickName", "userNick",
            "contactNick", "displayName", "display", "name"
        };

        private sealed class HistoryTurn
        {
            public string Role;
            public string Text;
            public DateTime Time;
            public string MessageKey;
        }

        private sealed class ContactHistory
        {
            public string Buyer;
            public List<HistoryTurn> Turns = new List<HistoryTurn>();
        }


        // Message-management backfill: enumerate shop contacts and fetch raw history
        // using the established scanner, but do NOT send any history to AI, write V2
        // knowledge or persist the buyer identity. Each row retains only sanitized text.
        public async Task<ChatHistoryScanResult> ScanBuyerMessagesAsync(
            string seller, ChatHistoryScanOptions options,
            Action<ChatHistoryScanProgress> progress, CancellationToken token)
        {
            options = options ?? new ChatHistoryScanOptions();
            ValidateOptions(options);
            var qn = QN.FindExistingBySellerNick((seller ?? string.Empty).Trim());
            if (qn == null || qn.CDP == null || qn.Seller == null
                || !string.Equals((qn.Seller.Nick ?? string.Empty).Trim(), seller,
                    StringComparison.Ordinal))
                throw new InvalidOperationException("无法绑定当前店铺千牛会话，请先登录该店铺并打开聊天。");

            var diagnostics = new StringBuilder();
            var originalBuyer = qn.Buyer == null ? string.Empty : (qn.Buyer.Nick ?? string.Empty);
            Report(progress, "正在搜集本店全部可访问的买家联系人...", 0, 0,
                string.Empty, 0, 0, 0);
            var contacts = new HashSet<string>(StringComparer.Ordinal);
            AddContact(contacts, originalBuyer, seller);
            var visible = await ReadVisibleChatBuyerListContactsAsync(diagnostics, token);
            foreach (var name in visible) AddContact(contacts, name, seller);
            var managerOpened = false;
            if (contacts.Count <= 1)
            {
                managerOpened = await TryOpenMessageManagerAsync(diagnostics, token);
                if (managerOpened)
                    foreach (var name in await ReadVisibleMessageManagerContactsAsync(diagnostics, token))
                        AddContact(contacts, name, seller);
            }
            foreach (var name in await ReadContactsFromApisAsync(qn, diagnostics, token))
                AddContact(contacts, name, seller);
            var names = contacts.Where(IsPlausibleBuyerNick)
                .Take(Math.Max(1, Math.Min(5000, options.MaxContacts))).ToList();
            if (names.Count == 0)
                throw new InvalidOperationException("千牛没有提供可扫描的买家联系人。");

            var received = 0;
            var completed = 0;
            var failed = 0;
            try
            {
                for (var i = 0; i < names.Count; i++)
                {
                    token.ThrowIfCancellationRequested();
                    // Do not emit buyer names into progress, runtime diagnostics or archive.
                    Report(progress, "扫描历史买家消息（仅归档，不调用AI）",
                        i + 1, names.Count, string.Empty, received, 0, 0);
                    try
                    {
                        var ccode = await ResolveConversationCodeAsync(qn, names[i], token);
                        if (string.IsNullOrWhiteSpace(ccode)) { failed++; continue; }
                        var turns = await ReadHistoryAsync(qn, seller, names[i], ccode, options, token);
                        foreach (var turn in turns.Where(x => x.Role == "buyer"))
                        {
                            token.ThrowIfCancellationRequested();
                            BuyerMessageArchiveService.Observe(seller, turn.Text, turn.MessageKey, turn.Time);
                            received++;
                        }
                        completed++;
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        failed++;
                        Log.Info("[BuyerQuestions] history_scan_contact_failed type=" + ex.GetType().Name);
                    }
                }
            }
            finally
            {
                if (!string.IsNullOrWhiteSpace(originalBuyer))
                    try { qn.OpenChat(originalBuyer); } catch { }
            }
            Report(progress, "买家消息扫描完成", names.Count, names.Count,
                string.Empty, received, 0, 0);
            Log.Info("[BuyerQuestions] history_scan_complete contacts=" + completed
                + ";failed=" + failed + ";events=" + received);
            return new ChatHistoryScanResult
            {
                MessageManagerOpened = managerOpened,
                ContactCount = names.Count,
                ScannedContacts = completed,
                FailedContacts = failed,
                MessageCount = received,
                Diagnostics = "完成联系人 " + completed + "；读取失败 " + failed
                    + "；买家消息事件 " + received + "。仅归档脱敏文本，不记录身份。"
            };
        }

        public async Task<ChatHistoryScanResult> ScanAndImportAsync(
            ChatHistoryScanOptions options,
            Action<ChatHistoryScanProgress> progress,
            CancellationToken token)
        {
            options = options ?? new ChatHistoryScanOptions();
            ValidateOptions(options);

            var qn = QN.CurQN;
            if (qn == null || qn.CDP == null || qn.Seller == null || string.IsNullOrWhiteSpace(qn.Seller.Nick))
            {
                throw new Exception("未识别到可用的千牛客服会话，请先打开任意买家聊天窗口。");
            }

            var diagnostics = new StringBuilder();
            var originalBuyer = qn.Buyer == null ? string.Empty : (qn.Buyer.Nick ?? string.Empty);

            Report(progress, "正在读取千牛左侧“全部买家”列表...", 0, 0, string.Empty, 0, 0, 0);
            var chatBuyerListContacts = await ReadVisibleChatBuyerListContactsAsync(diagnostics, token);

            var contacts = new HashSet<string>(StringComparer.Ordinal);
            AddContact(contacts, originalBuyer, qn.Seller.Nick);
            foreach (var name in chatBuyerListContacts)
            {
                AddContact(contacts, name, qn.Seller.Nick);
            }

            var managerOpened = false;
            var messageManagerContacts = new List<string>();
            if (contacts.Count <= 1)
            {
                Report(progress, "左侧买家列表未完整读取，正在尝试千牛消息管理器...", 0, 0, string.Empty, 0, 0, 0);
                managerOpened = await TryOpenMessageManagerAsync(diagnostics, token);
                if (managerOpened)
                {
                    messageManagerContacts = await ReadVisibleMessageManagerContactsAsync(diagnostics, token);
                    foreach (var name in messageManagerContacts)
                    {
                        AddContact(contacts, name, qn.Seller.Nick);
                    }
                }
            }
            else
            {
                diagnostics.AppendLine("已从聊天界面左侧“全部买家”列表读取联系人，无需打开独立消息管理器。");
            }

            Report(progress, "正在通过千牛接口补充联系人...", 0, 0, string.Empty, 0, 0, 0);
            var apiContacts = await ReadContactsFromApisAsync(qn, diagnostics, token);
            foreach (var name in apiContacts)
            {
                AddContact(contacts, name, qn.Seller.Nick);
            }

            diagnostics.AppendLine(string.Format(
                "联系人来源：全部买家列表 {0}，消息管理器 {1}，接口 {2}，合并去重后 {3}。",
                chatBuyerListContacts.Count,
                messageManagerContacts.Count,
                apiContacts.Count,
                contacts.Count));

            var orderedContacts = contacts
                .Where(IsPlausibleBuyerNick)
                .Take(Math.Max(1, Math.Min(5000, options.MaxContacts)))
                .ToList();

            if (orderedContacts.Count < 1)
            {
                throw new Exception("没有读取到可扫描的最近联系人。消息管理器已尝试打开，请确认千牛已登录且“最近联系”中存在买家。");
            }

            var histories = new List<ContactHistory>();
            var totalMessages = 0;
            var failedContacts = 0;

            try
            {
                for (var i = 0; i < orderedContacts.Count; i++)
                {
                    token.ThrowIfCancellationRequested();
                    var buyer = orderedContacts[i];
                    Report(progress, "正在读取聊天记录...", i + 1, orderedContacts.Count, buyer, totalMessages, CountPairs(histories), TextChars(histories));

                    try
                    {
                        var ccode = await ResolveConversationCodeAsync(qn, buyer, token);
                        if (string.IsNullOrWhiteSpace(ccode))
                        {
                            failedContacts++;
                            diagnostics.AppendLine("未取得会话编号：" + buyer);
                            continue;
                        }

                        var turns = await ReadHistoryAsync(qn, qn.Seller.Nick, buyer, ccode, options, token);
                        if (turns.Count < 1) continue;
                        histories.Add(new ContactHistory { Buyer = buyer, Turns = turns });
                        totalMessages += turns.Count;
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        failedContacts++;
                        diagnostics.AppendLine("读取失败 " + buyer + "：" + Safe(ex.Message));
                        Log.Info("扫描历史聊天失败: buyer=" + buyer + ", error=" + ex.Message);
                    }
                }
            }
            finally
            {
                if (!string.IsNullOrWhiteSpace(originalBuyer))
                {
                    try { qn.OpenChat(originalBuyer); } catch { }
                }
            }

            token.ThrowIfCancellationRequested();
            var transcript = BuildTranscript(histories, options);
            var pairCount = CountTranscriptPairs(transcript);
            if (string.IsNullOrWhiteSpace(transcript) || pairCount < 1)
            {
                throw new Exception("已读取聊天记录，但没有找到“买家提问后客服已回答”的有效问答轮次。");
            }

            Report(progress, "聊天记录整理完成，正在分批调用 AI 生成知识库...", orderedContacts.Count, orderedContacts.Count, string.Empty, totalMessages, pairCount, transcript.Length);

            var importData = new ClipboardKnowledgeData { Text = transcript };
            var ai = new KnowledgeAiService();
            var importResult = await ai.ImportAsync(
                importData,
                BotFeatureStore.GetSmartImportTimeoutSeconds(),
                token,
                () => token.IsCancellationRequested ? SmartImportCancelSource.UserCancel : SmartImportCancelSource.None,
                text => Report(progress, text, orderedContacts.Count, orderedContacts.Count, string.Empty, totalMessages, pairCount, transcript.Length));

            MarkImportedSource(importResult, "历史聊天扫描");
            Report(progress, "历史聊天扫描完成。", orderedContacts.Count, orderedContacts.Count, string.Empty, totalMessages, pairCount, transcript.Length);

            return new ChatHistoryScanResult
            {
                MessageManagerOpened = managerOpened,
                ChatBuyerListContactCount = chatBuyerListContacts.Count,
                MessageManagerContactCount = messageManagerContacts.Count,
                ApiContactCount = apiContacts.Count,
                ContactCount = orderedContacts.Count,
                ScannedContacts = histories.Count,
                FailedContacts = failedContacts,
                MessageCount = totalMessages,
                PairCount = pairCount,
                TextChars = transcript.Length,
                ImportResult = importResult,
                Diagnostics = diagnostics.ToString().Trim()
            };
        }

        private static void ValidateOptions(ChatHistoryScanOptions options)
        {
            if (options.ScanAll) return;
            if (!options.StartTime.HasValue || !options.EndTime.HasValue)
            {
                throw new Exception("按时间段扫描时，请同时设置开始日期和结束日期。");
            }
            if (options.StartTime.Value.Date > options.EndTime.Value.Date)
            {
                throw new Exception("开始日期不能晚于结束日期。");
            }
            options.StartTime = options.StartTime.Value.Date;
            options.EndTime = options.EndTime.Value.Date.AddDays(1).AddTicks(-1);
        }

        private static void Report(
            Action<ChatHistoryScanProgress> progress,
            string phase,
            int index,
            int count,
            string buyer,
            int messages,
            int pairs,
            int chars)
        {
            if (progress == null) return;
            progress(new ChatHistoryScanProgress
            {
                Phase = phase,
                ContactIndex = index,
                ContactCount = count,
                Buyer = buyer,
                MessageCount = messages,
                PairCount = pairs,
                TextChars = chars
            });
        }

        private static async Task<bool> TryOpenMessageManagerAsync(StringBuilder diagnostics, CancellationToken token)
        {
            return await Task.Run(() =>
            {
                try
                {
                    token.ThrowIfCancellationRequested();
                    if (Desk.Inst == null) return false;
                    var app = FlaUI.Core.Application.Attach(Desk.Inst.ProcessId);
                    using (var automation = new UIA3Automation())
                    {
                        var existing = FindMessageManagerWindow(app, automation);
                        if (existing != null)
                        {
                            TryBringToFront(existing);
                            return true;
                        }

                        var windows = app.GetAllTopLevelWindows(automation);
                        foreach (var window in windows)
                        {
                            token.ThrowIfCancellationRequested();
                            var settings = window.FindAllDescendants()
                                .FirstOrDefault(x => IsName(x, "设置"));
                            if (settings == null) continue;
                            ClickElement(settings);
                            Thread.Sleep(250);

                            var menuItem = app.GetAllTopLevelWindows(automation)
                                .SelectMany(x => x.FindAllDescendants())
                                .FirstOrDefault(x => IsName(x, "消息管理器"));
                            if (menuItem == null) continue;
                            ClickElement(menuItem);

                            for (var wait = 0; wait < 30; wait++)
                            {
                                token.ThrowIfCancellationRequested();
                                Thread.Sleep(200);
                                var opened = FindMessageManagerWindow(app, automation);
                                if (opened != null)
                                {
                                    TryBringToFront(opened);
                                    return true;
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    diagnostics.AppendLine("自动打开消息管理器失败：" + Safe(ex.Message));
                }
                return false;
            }, token);
        }

        private static Window FindMessageManagerWindow(FlaUI.Core.Application app, UIA3Automation automation)
        {
            try
            {
                return app.GetAllTopLevelWindows(automation)
                    .FirstOrDefault(x => ((x.Name ?? string.Empty) + " " + (x.Title ?? string.Empty))
                        .IndexOf("消息管理器", StringComparison.Ordinal) >= 0);
            }
            catch
            {
                return null;
            }
        }

        private static void TryBringToFront(Window window)
        {
            try { window.SetForeground(); } catch { }
        }

        private static bool IsName(AutomationElement element, string name)
        {
            if (element == null) return false;
            return string.Equals((element.Name ?? string.Empty).Trim(), name, StringComparison.Ordinal);
        }

        private static void ClickElement(AutomationElement element)
        {
            if (element == null) return;
            var rect = element.BoundingRectangle;
            if (rect.Width <= 0 || rect.Height <= 0) return;
            FlaUI.Core.Input.Mouse.Click(new System.Drawing.Point(
                (int)(rect.Left + rect.Width / 2),
                (int)(rect.Top + rect.Height / 2)));
        }

        private static async Task<List<string>> ReadVisibleChatBuyerListContactsAsync(StringBuilder diagnostics, CancellationToken token)
        {
            return await Task.Run(() =>
            {
                var names = new HashSet<string>(StringComparer.Ordinal);
                try
                {
                    if (Desk.Inst == null)
                    {
                        diagnostics.AppendLine("无法读取“全部买家”：未找到千牛主窗口进程。");
                        return names.ToList();
                    }

                    var app = FlaUI.Core.Application.Attach(Desk.Inst.ProcessId);
                    using (var automation = new UIA3Automation())
                    {
                        Window chatWindow = null;
                        AutomationElement allBuyerHeader = null;
                        foreach (var window in app.GetAllTopLevelWindows(automation))
                        {
                            token.ThrowIfCancellationRequested();
                            var header = window.FindAllDescendants()
                                .FirstOrDefault(x => IsAllBuyerHeader(x.Name));
                            if (header == null) continue;
                            chatWindow = window;
                            allBuyerHeader = header;
                            break;
                        }

                        if (chatWindow == null || allBuyerHeader == null)
                        {
                            diagnostics.AppendLine("千牛聊天主窗口中未找到“全部买家”栏。");
                            return names.ToList();
                        }

                        TryBringToFront(chatWindow);
                        var windowBounds = chatWindow.BoundingRectangle;
                        var headerBounds = allBuyerHeader.BoundingRectangle;
                        var panelLeft = windowBounds.Left;
                        var panelRight = Math.Min(
                            windowBounds.Right,
                            windowBounds.Left + Math.Max(260, Math.Min(340, windowBounds.Width * 0.27)));
                        var panelTop = Math.Max(headerBounds.Bottom, windowBounds.Top + 250);
                        var panelBottom = windowBounds.Bottom - 28;
                        if (panelRight <= panelLeft || panelBottom <= panelTop)
                        {
                            diagnostics.AppendLine("“全部买家”列表区域尺寸无效。");
                            return names.ToList();
                        }

                        var focusPoint = new System.Drawing.Point(
                            (int)(panelLeft + Math.Min(150, (panelRight - panelLeft) * 0.55)),
                            (int)(panelTop + (panelBottom - panelTop) * 0.55));
                        var stableRounds = 0;

                        for (var round = 0; round < 180 && stableRounds < 10; round++)
                        {
                            token.ThrowIfCancellationRequested();
                            var before = names.Count;
                            var elements = chatWindow.FindAllDescendants()
                                .Where(x => IsInsideBuyerPanel(x, panelLeft, panelRight, panelTop, panelBottom))
                                .Where(x => !string.IsNullOrWhiteSpace(x.Name))
                                .ToList();

                            var timeElements = elements
                                .Where(x => Regex.IsMatch((x.Name ?? string.Empty).Trim(), @"^\d{1,2}:\d{2}$"))
                                .Where(x => x.BoundingRectangle.Left > panelLeft + (panelRight - panelLeft) * 0.62)
                                .ToList();

                            foreach (var timeElement in timeElements)
                            {
                                var timeBounds = timeElement.BoundingRectangle;
                                var candidate = elements
                                    .Where(x => !object.ReferenceEquals(x, timeElement))
                                    .Where(x => x.BoundingRectangle.Left > panelLeft + 42)
                                    .Where(x => x.BoundingRectangle.Right < timeBounds.Left - 2)
                                    .Where(x => Math.Abs(x.BoundingRectangle.Top - timeBounds.Top) <= 11)
                                    .OrderBy(x => x.BoundingRectangle.Left)
                                    .FirstOrDefault();
                                AddBuyerListCandidate(names, candidate == null ? string.Empty : candidate.Name);
                            }

                            if (timeElements.Count == 0)
                            {
                                foreach (var group in elements
                                    .Where(x => x.BoundingRectangle.Left > panelLeft + 42)
                                    .Where(x => x.BoundingRectangle.Right < panelRight - 18)
                                    .Where(x => !Regex.IsMatch((x.Name ?? string.Empty).Trim(), @"^\d{1,2}:\d{2}$"))
                                    .GroupBy(x => (int)Math.Floor((x.BoundingRectangle.Top - panelTop) / 48.0)))
                                {
                                    var candidate = group
                                        .OrderBy(x => x.BoundingRectangle.Top)
                                        .ThenBy(x => x.BoundingRectangle.Left)
                                        .FirstOrDefault();
                                    AddBuyerListCandidate(names, candidate == null ? string.Empty : candidate.Name);
                                }
                            }

                            stableRounds = names.Count == before ? stableRounds + 1 : 0;
                            ClickPoint(focusPoint);
                            PressPageDown();
                            Thread.Sleep(160);
                        }
                    }
                }
                catch (Exception ex)
                {
                    diagnostics.AppendLine("读取聊天界面“全部买家”列表失败：" + Safe(ex.Message));
                }

                diagnostics.AppendLine("聊天界面“全部买家”列表读取到 " + names.Count + " 个联系人。");
                return names.ToList();
            }, token);
        }

        private static bool IsAllBuyerHeader(string value)
        {
            value = (value ?? string.Empty).Trim();
            return value.StartsWith("全部买家", StringComparison.Ordinal);
        }

        private static bool IsInsideBuyerPanel(
            AutomationElement element,
            double left,
            double right,
            double top,
            double bottom)
        {
            if (element == null) return false;
            var rect = element.BoundingRectangle;
            if (rect.Width <= 0 || rect.Height <= 0) return false;
            var centerX = rect.Left + rect.Width / 2;
            var centerY = rect.Top + rect.Height / 2;
            return centerX >= left && centerX <= right && centerY >= top && centerY <= bottom;
        }

        private static void AddBuyerListCandidate(HashSet<string> names, string value)
        {
            var nick = CleanBuyerListNick(value);
            if (!IsPlausibleBuyerNick(nick) || IsBuyerListNoise(nick)) return;
            names.Add(nick);
        }

        private static string CleanBuyerListNick(string value)
        {
            value = (value ?? string.Empty).Trim();
            var lines = Regex.Split(value, @"[\r\n\t]+")
                .Select(x => x.Trim())
                .Where(x => x.Length > 0)
                .ToList();
            if (lines.Count > 0) value = lines[0];
            value = Regex.Replace(value, @"\s+\d{1,2}:\d{2}$", string.Empty).Trim();
            var columns = Regex.Split(value, @"\s{2,}")
                .Select(x => x.Trim())
                .Where(x => x.Length > 0)
                .ToList();
            if (columns.Count > 0) value = columns[0];
            return CleanNick(value);
        }

        private static bool IsBuyerListNoise(string value)
        {
            value = (value ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(value)) return true;
            var exact = new[]
            {
                "全部买家", "最近联系", "最近星标", "咨询未下单", "咨询未付款",
                "正在接待", "其他消息", "联系人", "搜索", "消息", "订单号", "聊天记录"
            };
            if (exact.Any(x => string.Equals(value, x, StringComparison.Ordinal))) return true;
            if (value.StartsWith("全部买家", StringComparison.Ordinal)) return true;
            if (value.StartsWith("最近星标", StringComparison.Ordinal)) return true;
            if (value.StartsWith("咨询未下单", StringComparison.Ordinal)) return true;
            if (value.StartsWith("咨询未付款", StringComparison.Ordinal)) return true;
            if (Regex.IsMatch(value, @"^\d{1,2}:\d{2}$")) return true;
            if (value.IndexOf("撤回了一条消息", StringComparison.Ordinal) >= 0) return true;
            if (value.IndexOf("系统消息", StringComparison.Ordinal) >= 0) return true;
            return false;
        }

        private static async Task<List<string>> ReadVisibleMessageManagerContactsAsync(StringBuilder diagnostics, CancellationToken token)
        {
            return await Task.Run(() =>
            {
                var names = new HashSet<string>(StringComparer.Ordinal);
                try
                {
                    if (Desk.Inst == null) return names.ToList();
                    var app = FlaUI.Core.Application.Attach(Desk.Inst.ProcessId);
                    using (var automation = new UIA3Automation())
                    {
                        var manager = FindMessageManagerWindow(app, automation);
                        if (manager == null) return names.ToList();
                        var bounds = manager.BoundingRectangle;
                        var focusPoint = new System.Drawing.Point(
                            (int)(bounds.Left + Math.Max(60, bounds.Width * 0.12)),
                            (int)(bounds.Top + Math.Max(120, bounds.Height * 0.45)));

                        var stableRounds = 0;
                        for (var round = 0; round < 120 && stableRounds < 6; round++)
                        {
                            token.ThrowIfCancellationRequested();
                            var before = names.Count;
                            foreach (var element in manager.FindAllDescendants())
                            {
                                var rect = element.BoundingRectangle;
                                if (rect.Width <= 0 || rect.Height <= 0) continue;
                                if (rect.Left > bounds.Left + bounds.Width * 0.30) continue;
                                if (rect.Top < bounds.Top + 50 || rect.Bottom > bounds.Bottom - 25) continue;
                                var value = CleanNick(element.Name);
                                if (IsPlausibleBuyerNick(value)) names.Add(value);
                            }

                            stableRounds = names.Count == before ? stableRounds + 1 : 0;
                            ClickPoint(focusPoint);
                            PressPageDown();
                            Thread.Sleep(120);
                        }
                    }
                }
                catch (Exception ex)
                {
                    diagnostics.AppendLine("读取消息管理器联系人失败：" + Safe(ex.Message));
                }
                return names.ToList();
            }, token);
        }

        private static void ClickPoint(System.Drawing.Point point)
        {
            try { FlaUI.Core.Input.Mouse.Click(point); } catch { }
        }

        private static void PressPageDown()
        {
            try
            {
                Bot.Automation.WinApi.Api.keybd_event(0x22, 0, 0, 0);
                Thread.Sleep(20);
                Bot.Automation.WinApi.Api.keybd_event(0x22, 0, 2, 0);
            }
            catch { }
        }

        private static async Task<List<string>> ReadContactsFromApisAsync(QN qn, StringBuilder diagnostics, CancellationToken token)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            try
            {
                token.ThrowIfCancellationRequested();
                var relation = await qn.CDP.InvokeMTop<JObject>(
                    "mtop.taobao.wireless.amp2.im.relation.rebase",
                    new
                    {
                        accessKey = "qianniu-pc",
                        accessSecret = "qianniu-pc-secret",
                        accountType = 3,
                        bottomOffset = 2000,
                        topOffset = 2000
                    });
                ExtractNicks(relation, names);
            }
            catch (Exception ex)
            {
                diagnostics.AppendLine("联系人接口 relation.rebase 失败：" + Safe(ex.Message));
            }

            try
            {
                token.ThrowIfCancellationRequested();
                var search = await qn.CDP.InvokeMTop<JObject>(
                    "mtop.taobao.qianniu.airisland.contact.search",
                    new
                    {
                        accessKey = "qianniu-pc",
                        accessSecret = "qianniu-pc-secret",
                        accountType = 3,
                        searchQuery = string.Empty
                    });
                ExtractNicks(search, names);
            }
            catch (Exception ex)
            {
                diagnostics.AppendLine("联系人接口 contact.search 失败：" + Safe(ex.Message));
            }

            return names.ToList();
        }

        private static void ExtractNicks(JToken token, HashSet<string> names)
        {
            if (token == null) return;
            var obj = token as JObject;
            if (obj != null)
            {
                foreach (var field in NickFields)
                {
                    var property = obj.Properties().FirstOrDefault(
                        x => string.Equals(x.Name, field, StringComparison.OrdinalIgnoreCase));
                    if (property == null || property.Value == null || property.Value.Type != JTokenType.String) continue;
                    var nick = CleanNick(property.Value.ToString());
                    if (IsPlausibleBuyerNick(nick)) names.Add(nick);
                }
                foreach (var property in obj.Properties()) ExtractNicks(property.Value, names);
                return;
            }

            var array = token as JArray;
            if (array != null)
            {
                foreach (var item in array) ExtractNicks(item, names);
            }
        }

        private static void AddContact(HashSet<string> contacts, string value, string seller)
        {
            var nick = CleanNick(value);
            if (!IsPlausibleBuyerNick(nick)) return;
            if (string.Equals(nick, CleanNick(seller), StringComparison.Ordinal)) return;
            contacts.Add(nick);
        }

        private static string CleanNick(string value)
        {
            value = (value ?? string.Empty).Trim();
            if (value.StartsWith("cntaobao", StringComparison.OrdinalIgnoreCase))
            {
                value = value.Substring("cntaobao".Length);
            }
            var newline = value.IndexOfAny(new[] { '\r', '\n', '\t' });
            if (newline >= 0) value = value.Substring(0, newline).Trim();
            return value;
        }

        private static bool IsPlausibleBuyerNick(string value)
        {
            value = CleanNick(value);
            if (value.Length < 2 || value.Length > 64) return false;
            if (value.IndexOf("最近联系", StringComparison.Ordinal) >= 0) return false;
            if (value.IndexOf("开始日期", StringComparison.Ordinal) >= 0) return false;
            if (value.IndexOf("结束日期", StringComparison.Ordinal) >= 0) return false;
            if (value.IndexOf("消息管理器", StringComparison.Ordinal) >= 0) return false;
            if (value.IndexOf("退款", StringComparison.Ordinal) >= 0 && value.Length < 8) return false;
            if (Regex.IsMatch(value, @"^\d{1,2}:\d{2}$")) return false;
            if (Regex.IsMatch(value, @"^\d{4}-\d{1,2}-\d{1,2}")) return false;
            return Regex.IsMatch(value, @"[\p{L}\d_]");
        }

        private static async Task<string> ResolveConversationCodeAsync(QN qn, string buyer, CancellationToken token)
        {
            qn.OpenChat(buyer);
            for (var attempt = 0; attempt < 28; attempt++)
            {
                token.ThrowIfCancellationRequested();
                await Task.Delay(250, token);
                var current = await qn.CDP.Invoke<JObject>("im.uiutil.GetCurrentConversationID");
                var currentNick = FindString(current, "nick", "buyerNick", "display");
                if (!string.Equals(CleanNick(currentNick), CleanNick(buyer), StringComparison.Ordinal)) continue;
                var ccode = FindString(current, "ccode");
                if (!string.IsNullOrWhiteSpace(ccode)) return ccode;
            }
            return string.Empty;
        }

        private static string FindString(JToken token, params string[] names)
        {
            if (token == null) return string.Empty;
            var obj = token as JObject;
            if (obj != null)
            {
                foreach (var name in names)
                {
                    var property = obj.Properties().FirstOrDefault(
                        x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
                    if (property != null && property.Value != null && property.Value.Type == JTokenType.String)
                    {
                        var value = property.Value.ToString().Trim();
                        if (!string.IsNullOrWhiteSpace(value)) return value;
                    }
                }
                foreach (var property in obj.Properties())
                {
                    var nested = FindString(property.Value, names);
                    if (!string.IsNullOrWhiteSpace(nested)) return nested;
                }
            }
            else
            {
                var array = token as JArray;
                if (array != null)
                {
                    foreach (var item in array)
                    {
                        var nested = FindString(item, names);
                        if (!string.IsNullOrWhiteSpace(nested)) return nested;
                    }
                }
            }
            return string.Empty;
        }

        private static async Task<List<HistoryTurn>> ReadHistoryAsync(
            QN qn,
            string seller,
            string buyer,
            string ccode,
            ChatHistoryScanOptions options,
            CancellationToken token)
        {
            var turns = new List<HistoryTurn>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var cursorId = "-1";
            var cursorTime = "-1";
            var previousCursor = string.Empty;

            for (var page = 0; page < MaxHistoryPages; page++)
            {
                token.ThrowIfCancellationRequested();
                var response = await qn.CDP.Invoke<JObject>("im.singlemsg.GetRemoteHisMsg", new
                {
                    cid = new { ccode = ccode, type = 1 },
                    count = PageSize,
                    gohistory = 1,
                    msgid = cursorId,
                    msgtime = cursorTime
                });
                if (response == null) break;

                var result = response["result"] ?? response;
                var resultObject = result as JObject;
                var msgsToken = resultObject == null
                    ? result as JArray
                    : (resultObject["msgs"] ?? resultObject["messages"] ?? resultObject["list"]);
                var messages = msgsToken == null
                    ? new List<QNChatMessage>()
                    : msgsToken.ToObject<List<QNChatMessage>>() ?? new List<QNChatMessage>();
                if (messages.Count < 1) break;

                foreach (var message in messages)
                {
                    var key = IncomingMessageSafety.BuildMessageKey(message, ExtractMessageText(message));
                    if (!seen.Add(key)) continue;
                    HistoryTurn turn;
                    if (TryConvertTurn(message, seller, buyer, options, out turn)) turns.Add(turn);
                }

                var oldest = messages.OrderBy(GetMessageTime).FirstOrDefault();
                if (oldest == null) break;
                cursorId = oldest.mcode == null || string.IsNullOrWhiteSpace(oldest.mcode.messageId)
                    ? (oldest.ext == null ? "-1" : oldest.ext.ww_msgid.ToString())
                    : oldest.mcode.messageId;
                cursorTime = !string.IsNullOrWhiteSpace(oldest.sortTimeMicrosecond)
                    ? oldest.sortTimeMicrosecond
                    : (!string.IsNullOrWhiteSpace(oldest.sendTime) ? oldest.sendTime : "-1");

                var cursor = cursorId + "|" + cursorTime;
                if (cursor == previousCursor) break;
                previousCursor = cursor;

                if (!options.ScanAll && options.StartTime.HasValue)
                {
                    var oldestTime = GetMessageTime(oldest);
                    if (oldestTime != DateTime.MinValue && oldestTime < options.StartTime.Value) break;
                }

                var hasMore = resultObject == null ? 0 : ToInt(resultObject["hasMore"]);
                if (hasMore == 0 && messages.Count < PageSize) break;
            }

            return turns.OrderBy(x => x.Time).ToList();
        }

        private static int ToInt(JToken token)
        {
            if (token == null) return 0;
            int value;
            return int.TryParse(token.ToString(), out value) ? value : 0;
        }

        private static bool TryConvertTurn(
            QNChatMessage message,
            string seller,
            string buyer,
            ChatHistoryScanOptions options,
            out HistoryTurn turn)
        {
            turn = null;
            if (message == null || message.fromid == null || message.toid == null) return false;
            var time = GetMessageTime(message);
            if (!options.ScanAll)
            {
                if (options.StartTime.HasValue && time != DateTime.MinValue && time < options.StartTime.Value) return false;
                if (options.EndTime.HasValue && time != DateTime.MinValue && time > options.EndTime.Value) return false;
            }

            var text = ExtractMessageText(message);
            if (ConversationContextStore.IsPlatformSystemTip(message, text)) return false;
            if (ConversationContextStore.IsWithdrawalNotice(message, text)) return false;
            text = NormalizeText(text);
            if (string.IsNullOrWhiteSpace(text)) return false;

            var from = CleanNick(message.fromid.nick);
            var to = CleanNick(message.toid.nick);
            string role;
            if (string.Equals(from, CleanNick(buyer), StringComparison.Ordinal)
                && string.Equals(to, CleanNick(seller), StringComparison.Ordinal)) role = "buyer";
            else if (string.Equals(from, CleanNick(seller), StringComparison.Ordinal)
                && string.Equals(to, CleanNick(buyer), StringComparison.Ordinal)) role = "seller";
            else return false;

            turn = new HistoryTurn
            {
                Role = role, Text = text, Time = time,
                MessageKey = IncomingMessageSafety.BuildMessageKey(message, text)
            };
            return true;
        }

        private static string ExtractMessageText(QNChatMessage message)
        {
            if (message == null) return string.Empty;
            var sb = new StringBuilder();
            if (message.originalData != null)
            {
                if (!string.IsNullOrWhiteSpace(message.originalData.text)) sb.Append(message.originalData.text);
                if (message.originalData.header != null)
                {
                    if (!string.IsNullOrWhiteSpace(message.originalData.header.title)) sb.Append(" ").Append(message.originalData.header.title);
                    if (!string.IsNullOrWhiteSpace(message.originalData.header.summary)) sb.Append(" ").Append(message.originalData.header.summary);
                }
            }
            if (sb.Length == 0 && !string.IsNullOrWhiteSpace(message.summary)) sb.Append(message.summary);
            return sb.ToString();
        }

        private static DateTime GetMessageTime(QNChatMessage message)
        {
            if (message == null) return DateTime.MinValue;
            DateTime value;
            if (TryParseTime(message.sendTime, out value)) return value;
            if (TryParseTime(message.sortTimeMicrosecond, out value)) return value;
            return DateTime.MinValue;
        }

        private static bool TryParseTime(string value, out DateTime local)
        {
            local = DateTime.MinValue;
            if (string.IsNullOrWhiteSpace(value)) return false;
            long raw;
            if (long.TryParse(value.Trim(), out raw))
            {
                try
                {
                    if (raw > 1000000000000000L) local = DateTimeOffset.FromUnixTimeMilliseconds(raw / 1000L).LocalDateTime;
                    else if (raw > 100000000000L) local = DateTimeOffset.FromUnixTimeMilliseconds(raw).LocalDateTime;
                    else if (raw > 1000000000L) local = DateTimeOffset.FromUnixTimeSeconds(raw).LocalDateTime;
                    if (local != DateTime.MinValue) return true;
                }
                catch { }
            }
            DateTime parsed;
            if (DateTime.TryParse(value, out parsed))
            {
                local = parsed;
                return true;
            }
            return false;
        }

        private static string NormalizeText(string value)
        {
            value = Regex.Replace((value ?? string.Empty).Replace("\r", " ").Replace("\n", " "), @"\s+", " ").Trim();
            if (value.Length > 1000) value = value.Substring(0, 1000) + "...";
            return RedactSensitive(value);
        }

        private static string RedactSensitive(string value)
        {
            value = value ?? string.Empty;
            value = Regex.Replace(value, @"(?<!\d)1\d{10}(?!\d)", "[手机号]");
            value = Regex.Replace(value, @"(?<!\d)\d{12,22}(?!\d)", "[敏感编号]");
            value = Regex.Replace(value, @"(?i)sk-[a-z0-9_-]{12,}", "[API_KEY]");
            return value;
        }

        private static string BuildTranscript(List<ContactHistory> histories, ChatHistoryScanOptions options)
        {
            var sb = new StringBuilder();
            sb.AppendLine("以下资料来自同一店铺的历史客服聊天。只生成可复用、客服已经明确回答且不依赖某个具体订单/买家身份的知识库问答。");
            sb.AppendLine("忽略退款成功卡片、系统通知、寒暄、纯表情、未回答问题和一次性订单状态；账号、手机号、订单号等必须泛化为字段说明，不得保留个人数据。");
            sb.AppendLine("当买家只回复数字、账号、型号等短内容时，必须结合同一块中的“客服前置提问”理解含义。");
            sb.AppendLine();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var history in histories)
            {
                foreach (var pair in BuildPairs(history))
                {
                    var key = KnowledgeAiService.ContentHash(pair.Item1, pair.Item2);
                    if (!seen.Add(key)) continue;
                    sb.AppendLine("【历史会话问答】");
                    sb.AppendLine("买家：" + history.Buyer);
                    if (!string.IsNullOrWhiteSpace(pair.Item3)) sb.AppendLine("客服前置提问：" + pair.Item3);
                    sb.AppendLine("买家问题：" + pair.Item1);
                    sb.AppendLine("客服回答：" + pair.Item2);
                    sb.AppendLine();
                }
            }
            return sb.ToString().Trim();
        }

        private static List<Tuple<string, string, string>> BuildPairs(ContactHistory history)
        {
            var result = new List<Tuple<string, string, string>>();
            var buyers = new List<string>();
            var sellers = new List<string>();
            var previousSeller = string.Empty;
            var previousSellerTime = DateTime.MinValue;

            Action flush = () =>
            {
                if (buyers.Count < 1 || sellers.Count < 1) return;
                var question = NormalizeText(string.Join(" ", buyers));
                var answer = NormalizeText(string.Join(" ", sellers));
                var context = previousSeller;
                if (IsUsefulPair(question, answer))
                {
                    result.Add(Tuple.Create(question, answer, context));
                }
                buyers.Clear();
                sellers.Clear();
                previousSeller = string.Empty;
                previousSellerTime = DateTime.MinValue;
            };

            foreach (var turn in history.Turns.OrderBy(x => x.Time))
            {
                if (turn.Role == "buyer")
                {
                    if (sellers.Count > 0) flush();
                    if (buyers.Count == 0 && !string.IsNullOrWhiteSpace(previousSeller)
                        && previousSellerTime != DateTime.MinValue && turn.Time != DateTime.MinValue
                        && turn.Time - previousSellerTime > TimeSpan.FromMinutes(30))
                    {
                        previousSeller = string.Empty;
                    }
                    buyers.Add(turn.Text);
                }
                else
                {
                    if (buyers.Count > 0)
                    {
                        sellers.Add(turn.Text);
                    }
                    else
                    {
                        previousSeller = turn.Text;
                        previousSellerTime = turn.Time;
                    }
                }
            }
            flush();
            return result;
        }

        private static bool IsUsefulPair(string question, string answer)
        {
            if (string.IsNullOrWhiteSpace(question) || string.IsNullOrWhiteSpace(answer)) return false;
            if (answer.Length < 2 || question.Length < 1) return false;
            var compact = Regex.Replace(answer, @"[\s\p{P}\p{S}]", string.Empty);
            if (compact.Length < 2) return false;
            var trivial = new[] { "好的", "可以", "可以的", "嗯嗯", "收到", "谢谢", "不客气", "ok", "OK" };
            return !trivial.Contains(answer.Trim());
        }

        private static int CountPairs(List<ContactHistory> histories)
        {
            return histories.Sum(x => BuildPairs(x).Count);
        }

        private static int TextChars(List<ContactHistory> histories)
        {
            return histories.Sum(x => x.Turns.Sum(t => (t.Text ?? string.Empty).Length));
        }

        private static int CountTranscriptPairs(string transcript)
        {
            return Regex.Matches(transcript ?? string.Empty, "【历史会话问答】").Count;
        }

        private static void MarkImportedSource(KnowledgeImportResult result, string source)
        {
            if (result == null || result.AddedItems == null || result.AddedItems.Count < 1) return;
            var ids = new HashSet<string>(result.AddedItems.Select(x => x.Id ?? string.Empty));
            var list = BotFeatureStore.GetKnowledgeBase();
            var changed = false;
            foreach (var item in list)
            {
                if (item == null || !ids.Contains(item.Id ?? string.Empty)) continue;
                item.SourceType = source;
                item.AiGenerated = true;
                changed = true;
            }
            if (changed) BotFeatureStore.SaveKnowledgeBase(list);
        }

        private static string Safe(string value)
        {
            value = (value ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim();
            return value.Length > 300 ? value.Substring(0, 300) + "..." : value;
        }
    }
}
