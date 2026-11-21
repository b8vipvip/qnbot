using Bot.ChatRecord;
using BotLib;
using Bot.ShopScope;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Bot.ChromeNs
{
    public partial class QN
    {
        private const int BackgroundRecoveryInitialDelayMs = 1000;
        private const int BackgroundRecoverySendGateWaitMs = 1200;
        private const int BackgroundRecoveryGateWaitMs = 900;
        private const int BackgroundRecoveryPostSwitchHydrationDelayMs = 450;
        private const int BackgroundRecoveryMaxAttempts = 8;

        private readonly SemaphoreSlim _backgroundRecoveryGate = new SemaphoreSlim(1, 1);
        private readonly ConcurrentDictionary<string, DateTime> _latestBuyerMessageObserved =
            new ConcurrentDictionary<string, DateTime>(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, long> _backgroundRecoveryVersions =
            new ConcurrentDictionary<string, long>(StringComparer.Ordinal);

        private string RecoveryKey(string seller, string buyer)
        {
            seller = (seller ?? string.Empty).Trim();
            var shopKey = string.Empty;
            try
            {
                var shop = ResolveRecoveryShop(seller);
                shopKey = shop.ShopKey;
                using (ShopSettingsScope.Enter(shop, false))
                    buyer = BuyerIdentityAliasService.ResolveInternalNick(seller, buyer);
            }
            catch
            {
                buyer = (buyer ?? string.Empty).Trim();
            }
            return shopKey + "#" + seller + "#" + (buyer ?? string.Empty).Trim();
        }

        private ShopContext ResolveRecoveryShop(string seller)
        {
            var shop = ShopContextLocator.ResolveRuntimeBySeller(this);
            if (shop == null || string.IsNullOrWhiteSpace(shop.ShopKey)
                || !string.Equals((shop.SellerNick ?? string.Empty).Trim(),
                    (seller ?? string.Empty).Trim(), StringComparison.Ordinal))
                throw new InvalidOperationException("恢复客服与当前ShopKey不匹配");
            return shop;
        }

        private string ResolveBuyerInternalNickInShop(string seller, string buyer)
        {
            try
            {
                var shop = ResolveRecoveryShop(seller);
                using (ShopSettingsScope.Enter(shop, false))
                    return BuyerIdentityAliasService.ResolveInternalNick(seller, buyer);
            }
            catch { return (buyer ?? string.Empty).Trim(); }
        }

        private bool AreBuyerAliasesEquivalentInShop(string seller, string left, string right)
        {
            try
            {
                var shop = ResolveRecoveryShop(seller);
                using (ShopSettingsScope.Enter(shop, false))
                    return BuyerIdentityAliasService.AreEquivalent(seller, left, right);
            }
            catch { return string.Equals((left ?? string.Empty).Trim(),
                (right ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase); }
        }

        private void ObserveBuyerAliasInShop(string seller, string internalNick, string display, string targetId)
        {
            try
            {
                var shop = ResolveRecoveryShop(seller);
                using (ShopSettingsScope.Enter(shop, false))
                    BuyerIdentityAliasService.Observe(seller, internalNick, display, targetId);
            }
            catch { }
        }

        private string ResolveBuyerConversationKeyInShop(string seller, string buyer)
        {
            try
            {
                var shop = ResolveRecoveryShop(seller);
                using (ShopSettingsScope.Enter(shop, false))
                    return BuyerIdentityAliasService.ResolveConversationKey(seller, buyer);
            }
            catch { return (buyer ?? string.Empty).Trim(); }
        }

        private void MarkBuyerMessageObserved(string seller, string buyer)
        {
            var key = RecoveryKey(seller, buyer);
            if (key == "#") return;
            _latestBuyerMessageObserved[key] = DateTime.Now;
            long ignored;
            _backgroundRecoveryVersions.TryRemove(key, out ignored);
        }

        private void ScheduleBackgroundMessageRecovery(ShopRobotReceriveNewMessageEventArgs e)
        {
            if (e == null || e.Seller == null || e.Buyer == null) return;
            string nonBuyerReason;
            if (NonBuyerConversationGuard.ShouldBlockConversation(e.Seller, e.Buyer, out nonBuyerReason))
            {
                Log.Info("非买家后台补偿已拒绝，禁止自动切换会话: reason=" + nonBuyerReason);
                return;
            }
            var seller = (e.Seller.Nick ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(seller)) return;
            try
            {
                var shop = ResolveRecoveryShop(seller);
                using (ShopSettingsScope.Enter(shop, false))
                {
                    BuyerIdentityAliasService.Observe(seller, e.Buyer.Nick, e.Buyer.Display, e.Buyer.TargetId);
                }
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount("后台消息补偿无法绑定事件来源ShopKey，已跳过: " + ex.Message, 20);
                return;
            }
            var buyer = ResolveBuyerInternalNickInShop(seller, e.Buyer.Nick);
            if (string.IsNullOrWhiteSpace(buyer)) return;
            if (!Params.Robot.CanUseRobotReal) return;

            var key = RecoveryKey(seller, buyer);
            var scheduledAt = DateTime.Now;
            var version = DateTime.UtcNow.Ticks;
            _backgroundRecoveryVersions[key] = version;

            Task.Run(async () =>
            {
                try
                {
                    // 正常 receiveNewMsg 一般会很快到达。只有详细事件缺失时才自动切到目标买家补抓，
                    // 避免每个后台通知都抢走人工客服正在查看的会话。
                    await Task.Delay(BackgroundRecoveryInitialDelayMs).ConfigureAwait(false);

                    for (var attempt = 1; attempt <= BackgroundRecoveryMaxAttempts; attempt++)
                    {
                        long latestVersion;
                        if (!_backgroundRecoveryVersions.TryGetValue(key, out latestVersion) || latestVersion != version) return;

                        DateTime observedAt;
                        if (_latestBuyerMessageObserved.TryGetValue(key, out observedAt)
                            && observedAt >= scheduledAt.AddMilliseconds(-250))
                        {
                            return;
                        }

                        Log.Info("后台消息补偿调度: seller=" + seller + ", buyer=" + buyer
                            + ", attempt=" + attempt + "/" + BackgroundRecoveryMaxAttempts);

                        if (await RecoverMissedBuyerMessagesAsync(seller, buyer, scheduledAt, version, attempt).ConfigureAwait(false))
                        {
                            return;
                        }

                        if (attempt < BackgroundRecoveryMaxAttempts)
                        {
                            var retryDelayMs = Math.Min(2000, 500 + attempt * 250);
                            Log.Info("后台消息补偿本轮尚未恢复到可处理消息，将重试: seller=" + seller
                                + ", buyer=" + buyer + ", attempt=" + attempt + "/" + BackgroundRecoveryMaxAttempts
                                + ", delayMs=" + retryDelayMs);
                            await Task.Delay(retryDelayMs).ConfigureAwait(false);
                        }
                    }

                    Log.Info("后台消息补偿已耗尽 " + BackgroundRecoveryMaxAttempts
                        + " 次重试仍未恢复到可处理消息，等待下一条后台通知重新触发: seller="
                        + seller + ", buyer=" + buyer);
                }
                catch (Exception ex)
                {
                    Log.Info("后台消息补偿异常: seller=" + seller + ", buyer=" + buyer + ", error=" + ex.Message);
                }
                finally
                {
                    long latestVersion;
                    if (_backgroundRecoveryVersions.TryGetValue(key, out latestVersion) && latestVersion == version)
                    {
                        long ignored;
                        _backgroundRecoveryVersions.TryRemove(key, out ignored);
                    }
                }
            });
        }

        private async Task<bool> RecoverMissedBuyerMessagesAsync(
            string seller,
            string buyer,
            DateTime scheduledAt,
            long version,
            int attempt)
        {
            string shopKey;
            try
            {
                var shop = ResolveRecoveryShop(seller);
                shopKey = shop.ShopKey;
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount("后台消息补偿无法确认唯一ShopKey，已停止本轮恢复: " + ex.Message, 20);
                return false;
            }
            buyer = ResolveBuyerInternalNickInShop(seller, buyer);
            var key = RecoveryKey(seller, buyer);
            var sendGateAcquired = false;
            var recoveryGateAcquired = false;
            List<QNChatMessage> recovered = null;

            try
            {
                // 先等发送锁，且必须有界。旧实现先无限占住 recovery gate 再无限等 send gate，
                // 一次卡住就会让后续所有后台通知都无法自动切换/补抓。
                var waitStartedAt = DateTime.Now;
                sendGateAcquired = await _sendGate.WaitAsync(BackgroundRecoverySendGateWaitMs).ConfigureAwait(false);
                if (!sendGateAcquired)
                {
                    Log.Info("后台消息补偿等待发送锁超时: seller=" + seller + ", buyer=" + buyer
                        + ", attempt=" + attempt + ", waitMs="
                        + (int)(DateTime.Now - waitStartedAt).TotalMilliseconds);
                    return false;
                }

                waitStartedAt = DateTime.Now;
                recoveryGateAcquired = await _backgroundRecoveryGate.WaitAsync(BackgroundRecoveryGateWaitMs).ConfigureAwait(false);
                if (!recoveryGateAcquired)
                {
                    Log.Info("后台消息补偿等待恢复锁超时: seller=" + seller + ", buyer=" + buyer
                        + ", attempt=" + attempt + ", waitMs="
                        + (int)(DateTime.Now - waitStartedAt).TotalMilliseconds);
                    return false;
                }

                using (BotActivityCoordinator.Begin("后台消息补偿", seller, buyer, shopKey))
                {
                    long latestVersion;
                    if (!_backgroundRecoveryVersions.TryGetValue(key, out latestVersion) || latestVersion != version) return true;

                    DateTime observedAt;
                    if (_latestBuyerMessageObserved.TryGetValue(key, out observedAt)
                        && observedAt >= scheduledAt.AddMilliseconds(-250))
                    {
                        return true;
                    }

                    if (cdp == null)
                    {
                        Log.Info("后台消息补偿暂不可用：权威CDP尚未就绪。seller=" + seller + ", buyer=" + buyer);
                        return false;
                    }

                    Log.Info("详细新消息事件未到，Bot准备自动切换目标买家并补抓历史: seller="
                        + seller + ", buyer=" + buyer + ", attempt=" + attempt);
                    OpenChat(buyer);

                    DbEntity.Conversation current = null;
                    for (var switchAttempt = 0; switchAttempt < 16; switchAttempt++)
                    {
                        var response = await GetCurrentConversationID().ConfigureAwait(false);
                        current = response == null ? null : response.Result;
                        if (current != null
                            && AreBuyerAliasesEquivalentInShop(seller, current.Nick, buyer))
                        {
                            ObserveBuyerAliasInShop(seller, current.Nick, current.Display, current.TargetId);
                            break;
                        }
                        await Task.Delay(200).ConfigureAwait(false);
                    }

                    if (current == null || !BuyerIdentityAliasService.AreEquivalent(seller, current.Nick, buyer))
                    {
                        Log.Info("后台消息补偿自动切换失败：无法确认目标买家会话。target=" + buyer
                            + ", current=" + (current == null ? string.Empty : current.Nick)
                            + ", attempt=" + attempt + ", equivalent=false");
                        return false;
                    }

                    Log.Info("后台消息补偿已自动切换到目标买家: seller=" + seller
                        + ", buyer=" + buyer + ", current=" + current.Nick + ", attempt=" + attempt);

                    SetActiveConversationByNick(
                        seller,
                        ResolveBuyerConversationKeyInShop(seller, current.Nick),
                        "backgroundRecoveryAutoSwitch");

                    // 会话 ID 切换成功只代表导航完成，不代表远端历史已经完成装载。
                    // 给千牛短暂的 hydration 窗口，然后再抓历史；如果本轮仍为空，外层继续 2..N 次重试。
                    Log.Info("后台消息补偿等待会话消息加载后补抓历史: seller=" + seller
                        + ", buyer=" + buyer + ", attempt=" + attempt
                        + ", delayMs=" + BackgroundRecoveryPostSwitchHydrationDelayMs);
                    await Task.Delay(BackgroundRecoveryPostSwitchHydrationDelayMs).ConfigureAwait(false);

                    long postSwitchVersion;
                    if (!_backgroundRecoveryVersions.TryGetValue(key, out postSwitchVersion) || postSwitchVersion != version)
                    {
                        return true;
                    }
                    DateTime postSwitchObservedAt;
                    if (_latestBuyerMessageObserved.TryGetValue(key, out postSwitchObservedAt)
                        && postSwitchObservedAt >= scheduledAt.AddMilliseconds(-250))
                    {
                        Log.Info("后台消息补偿等待会话加载期间详细买家事件已到，取消历史补抓: seller="
                            + seller + ", buyer=" + buyer + ", attempt=" + attempt);
                        return true;
                    }

                    var ccode = (current.Ccode ?? string.Empty).Trim();
                    if (string.IsNullOrWhiteSpace(ccode))
                    {
                        Log.Info("后台消息补偿失败：当前会话没有 ccode。buyer=" + buyer + ", attempt=" + attempt);
                        return false;
                    }

                    var history = await cdp.Invoke<JObject>("im.singlemsg.GetRemoteHisMsg", new
                    {
                        cid = new { ccode = ccode, type = 1 },
                        count = 30,
                        gohistory = 1,
                        msgid = "-1",
                        msgtime = "-1"
                    }).ConfigureAwait(false);

                    if (history == null)
                    {
                        Log.Info("后台消息补偿远端历史返回为空，将重试: seller=" + seller
                            + ", buyer=" + buyer + ", attempt=" + attempt);
                        return false;
                    }

                    var messages = history["result"]?["msgs"]?.ToObject<List<QNChatMessage>>();
                    var threshold = scheduledAt.AddMinutes(-2).Ticks;
                    recovered = (messages ?? new List<QNChatMessage>())
                        .Where(m => m != null)
                        .Where(m =>
                            (IsRecoveredBuyerMessageForTarget(m, seller, buyer))
                            || IsPotentialRecoveredOrderCard(m))
                        .Where(m =>
                        {
                            var sort = IncomingMessageSafety.GetSortValue(m);
                            return sort <= 0 || sort >= threshold;
                        })
                        .OrderBy(IncomingMessageSafety.GetSortValue)
                        .ToList();
                }
            }
            catch (Exception ex)
            {
                Log.Info("后台消息补偿失败: seller=" + seller + ", buyer=" + buyer
                    + ", attempt=" + attempt + ", error=" + ex.Message);
                return false;
            }
            finally
            {
                if (recoveryGateAcquired) _backgroundRecoveryGate.Release();
                if (sendGateAcquired) _sendGate.Release();
            }

            // 抓取完成后立即释放会话/发送锁，再处理消息和生成答案，避免 AI/规则处理继续占锁。
            // 关键：切换成功不等于补偿成功。千牛会话刚切换时历史列表可能尚未 hydrated，
            // 本轮没有候选消息必须返回 false 让调度器继续 attempt 2..N，而不是提前结束任务。
            if (recovered == null || recovered.Count < 1)
            {
                Log.Info("后台消息补偿完成但没有发现最近买家消息或订单卡片，将继续重试: seller="
                    + seller + ", buyer=" + buyer + ", attempt=" + attempt + "/" + BackgroundRecoveryMaxAttempts);
                return false;
            }

            // 只有紧贴本次后台通知的买家消息允许绕过全局入站去重。两分钟前的历史消息仍走
            // 正常去重路径，避免为了修复漏答而把已经处理过的旧问题重新回复一次。
            var bypassThreshold = scheduledAt.AddSeconds(-8).Ticks;
            var recoveredBuyerMessages = recovered
                .Where(m => IsRecoveredBuyerMessageForTarget(m, seller, buyer))
                .Where(m =>
                {
                    var sort = IncomingMessageSafety.GetSortValue(m);
                    return sort > 0 && sort >= bypassThreshold;
                })
                .ToList();
            var bypassBuyerDedup = false;
            if (recoveredBuyerMessages.Count > 0)
            {
                // 与正常 receiveNewMsg 使用同一把入站锁完成最后一次判定。这样如果详细事件刚好在
                // 补抓历史期间到达，它会先设置 observed 标记，补偿路径立即退出；反之则由补偿
                // 路径声明本轮买家消息，避免“重复CDP页先占去重key，但权威处理未发生”把消息永久吃掉。
                await _incomingMessageGate.WaitAsync().ConfigureAwait(false);
                try
                {
                    DateTime observedAt;
                    if (_latestBuyerMessageObserved.TryGetValue(key, out observedAt)
                        && observedAt >= scheduledAt.AddMilliseconds(-250))
                    {
                        Log.Info("后台消息补偿处理前检测到详细买家事件已到，取消历史重放: seller="
                            + seller + ", buyer=" + buyer);
                        return true;
                    }
                    MarkBuyerMessageObserved(seller, buyer);
                    bypassBuyerDedup = true;
                }
                finally
                {
                    _incomingMessageGate.Release();
                }
            }

            Log.Info("后台消息补偿找回 " + recovered.Count + " 条候选消息/订单卡片。seller=" + seller + ", buyer=" + buyer
                + ", bypassBuyerDedupCount=" + (bypassBuyerDedup ? recoveredBuyerMessages.Count : 0));
            foreach (var message in recovered)
            {
                try
                {
                    var bypassThisBuyerDedup = bypassBuyerDedup && recoveredBuyerMessages.Contains(message);
                    await ProcessRecoveredMessageWithKnownBuyerAsync(
                        message, seller, buyer, bypassThisBuyerDedup).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Log.Info("后台补偿候选消息处理失败: seller=" + seller + ", buyer=" + buyer
                        + ", error=" + ex.Message);
                }
                await Task.Delay(30).ConfigureAwait(false);
            }
            return true;
        }

        private async Task ProcessRecoveredMessageWithKnownBuyerAsync(
            QNChatMessage message,
            string seller,
            string buyer,
            bool bypassBuyerDedup)
        {
            if (message == null) return;
            var text = GetMessageText(message);
            string nonBuyerReason;
            if (NonBuyerConversationGuard.ShouldBlockMessage(message, seller, text, out nonBuyerReason))
            {
                Log.Info("后台补偿非买家消息已丢弃，未进入订单/回复链: reason=" + nonBuyerReason);
                return;
            }
            if (IsPotentialRecoveredOrderCard(message))
            {
                OrderPlacedReplyPlan orderPlan;
                if (OrderPlacedAutoReplyService.TryCreatePlan(
                    message,
                    text,
                    seller,
                    buyer,
                    _messageSafetyStartedAt,
                    out orderPlan,
                    null,
                    ShopContextLocator.ResolveRuntimeBySeller(this).ShopKey))
                {
                    if (orderPlan != null)
                    {
                        Log.Info("后台补偿识别到直接下单订单卡片: seller=" + seller
                            + ", buyer=" + buyer + ", orderId=" + orderPlan.OrderId);
                        await ProcessOrderPlacedReplyAsync(orderPlan).ConfigureAwait(false);
                    }
                    return;
                }
            }

            if (bypassBuyerDedup
                && IsRecoveredBuyerMessageForTarget(message, seller, buyer))
            {
                await ProcessRecoveredBuyerMessageAfterMissAsync(message, seller, buyer).ConfigureAwait(false);
                return;
            }

            using (ShopSettingsScope.Enter(ShopContextLocator.ResolveRuntimeBySeller(this)))
                await ProcessIncomingMessageAsync(message).ConfigureAwait(false);
        }

        private Task ProcessRecoveredBuyerMessageAfterMissAsync(
            QNChatMessage message,
            string sellerNick,
            string buyerNick)
        {
            if (message == null) return Task.CompletedTask;
            string shopKey;
            try
            {
                shopKey = ShopContextLocator.ResolveRuntimeBySeller(this).ShopKey;
                if (string.IsNullOrWhiteSpace(shopKey)) throw new InvalidOperationException("ShopKey为空");
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount("后台补偿买家消息无法确认唯一ShopKey，已阻止恢复回复: " + ex.Message, 20);
                return Task.CompletedTask;
            }
            var messageText = GetMessageText(message);
            string nonBuyerReason;
            if (NonBuyerConversationGuard.ShouldBlockMessage(message, sellerNick, messageText, out nonBuyerReason))
            {
                Log.Info("后台补偿非买家买家候选已丢弃，未进入首问/订单/AI链: reason=" + nonBuyerReason);
                return Task.CompletedTask;
            }
            var messageKey = IncomingMessageSafety.BuildMessageKey(message, messageText);
            var detectedAt = DateTime.Now;
            if (!_handledBuyerMessageDeduplicator.TryAccept(messageKey))
            {
                Log.Info("后台补偿跳过已由权威业务链处理的买家消息: seller=" + sellerNick
                    + ", buyer=" + buyerNick + ", key=" + messageKey);
                return Task.CompletedTask;
            }
            MarkBuyerMessageObserved(sellerNick, buyerNick);

            OrderPlacedReplyPlan orderPlan;
            if (OrderPlacedAutoReplyService.TryCreatePlan(
                message,
                messageText,
                sellerNick,
                buyerNick,
                _messageSafetyStartedAt,
                out orderPlan,
                null,
                shopKey))
            {
                if (orderPlan != null && orderPlan.IsBuyerFollowUp)
                    ResponseProgressTracker.ObserveNewBuyerTurn(sellerNick, buyerNick, shopKey);
                return orderPlan == null
                    ? Task.CompletedTask
                    : ProcessOrderPlacedReplyAsync(orderPlan);
            }

            var decision = IncomingMessageSafety.Evaluate(message, messageText, _messageSafetyStartedAt, shopKey);
            var displayQuestion = IncomingMessageSafety.GetDisplayText(message, messageText);
            var visionDecision = VisionMessageDecision.Decide(
                message,
                messageText,
                decision,
                shopKey);

            if (!Params.Robot.CanUseRobotReal)
            {
                AddSkippedConversation(sellerNick, buyerNick, displayQuestion, "Bot已停用，未调用AI，也未发送给买家。");
                return Task.CompletedTask;
            }

            if (visionDecision.Kind == VisionDecisionKind.Skip
                && !IncomingMessageSafety.IsMediaPlaceholder(displayQuestion))
            {
                AddSkippedConversation(sellerNick, buyerNick, visionDecision.QuestionLabel, visionDecision.Note);
                Log.Info("后台补偿买家消息安全跳过: buyer=" + buyerNick + ", reason=" + visionDecision.Note);
                return Task.CompletedTask;
            }

            ResponseProgressTracker.ObserveQuestion(sellerNick, buyerNick, displayQuestion, detectedAt, shopKey);
            if (visionDecision.Kind == VisionDecisionKind.Text)
            {
                BotFlowTestService.RecordCandidate(sellerNick, buyerNick, displayQuestion, detectedAt);
            }
            Log.Info("后台补偿买家消息已进入权威回复队列: seller=" + sellerNick + ", buyer=" + buyerNick
                + ", detectedAt=" + detectedAt.ToString("HH:mm:ss.fff") + ", question=" + displayQuestion
                + ", key=" + messageKey);

            _buyerMessageBurstCoordinator.Enqueue(new BuyerMessageBurstItem
            {
                ShopKey = shopKey,
                SellerNick = sellerNick,
                BuyerNick = buyerNick,
                MessageKey = messageKey,
                DisplayText = displayQuestion,
                Message = message,
                SafetyDecision = decision,
                VisionDecision = visionDecision,
                SortValue = IncomingMessageSafety.GetSortValue(message),
                ReceivedAt = detectedAt
            });
            return Task.CompletedTask;
        }

        private static bool IsPotentialRecoveredOrderCard(QNChatMessage message)
        {
            if (message == null) return false;
            OrderSnapshot snapshot;
            return OrderCardParser.TryParse(
                message,
                GetMessageText(message),
                string.Empty,
                string.Empty,
                "千牛远端历史订单卡片",
                out snapshot);
        }
        private bool IsRecoveredBuyerMessageForTarget(QNChatMessage message, string seller, string buyer)
        {
            if (message == null || message.fromid == null) return false;
            string nonBuyerReason;
            if (NonBuyerConversationGuard.ShouldBlockMessage(message, seller, GetMessageText(message), out nonBuyerReason)) return false;
            // Remote history is fetched only after the target conversation itself has been verified.
            // Therefore the sender identity is the authoritative buyer discriminator. Do not require
            // toid.nick to equal one exact seller alias: media/history cards can target the main seller
            // account while the active QN runtime is a dispatched sub-account.
            return AreBuyerAliasesEquivalentInShop(seller, message.fromid.nick, buyer);
        }

    }
}
