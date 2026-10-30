"""Bot message context-menu source path and direct settings navigation regressions."""
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def src(path):
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_source_is_evidence_only_and_view_navigates_to_source_configuration():
    mirror = src("src/Bot/AssistWindow/Widget/Robot/CtlRobot.MessageMirror.cs")
    assert 'Header = "查看"' in mirror
    assert "KnowledgeCenterV2UiBridge.ShowBotReplyTarget(" in mirror
    assert 'Header = "来源"' in mirror
    assert '"Bot 消息来源路径"' in mirror
    assert "BotReplySourceRouting.Resolve(source," in mirror
    assert "GetOriginalSourceBeforeCorrection" in mirror
    assert "sourceRecordId" in mirror
    assert 'Header = "修改来源"' in mirror
    # There must not be a write operation in the source-display click handler.
    source_item = mirror.split('var provenance = new MenuItem { Header = "来源" };', 1)[1]
    display = source_item.split('menu.Items.Add(provenance);', 1)[0]
    assert "UpdateAnswerSource" not in display
    assert "BotReplySourceEditorWindow.Edit" not in display


def test_producer_path_parser_is_evidence_first_and_store_scoped():
    nav = src("src/Bot/Knowledge/KnowledgeCenterV2Ui.cs")
    assert "ShowBotReplyTarget(" in nav
    assert "BotReplySourceRouting.Resolve(source, manuallyCorrected)" in nav
    assert "if (manuallyCorrected) return fallback;" in nav
    assert 'const string presetPrefix = "内置话术：";' in nav
    assert 'const string knowledgePrefix = "本地知识V2（记录ID：";' in nav
    assert 'RouteKind.AutoReplyRule' in nav
    assert 'RouteKind.SourceRecord' in nav
    assert "ShopContextLocator.ResolveRuntimeBySellerNick(seller)" in nav
    assert "GetActiveSellerNick()" in nav
    assert "OpenPresetById(route.TargetId)" in nav
    assert "OpenKnowledgeRecordById(route.TargetId)" in nav
    assert "OpenReplySourceById(sourceRecordId, answer)" in nav


def test_exact_record_navigation_uses_ids_not_answer_similarity():
    manager = src("src/Bot/Knowledge/KnowledgeV2BotReplyManagementPage.cs")
    records = src("src/Bot/Knowledge/KnowledgeCenterV2RecordsPage.cs")
    assert "OpenPresetById(string presetId)" in manager
    assert "string.Equals(x.Id, presetId, StringComparison.Ordinal)" in manager
    assert "OpenSourceById(string recordId, string answer)" in manager
    assert "string.Equals(x.Entity.EntityId, recordId, StringComparison.Ordinal)" in manager
    assert "OpenRecordById(string recordId)" in records
    assert "string.Equals(x.Id, targetId, StringComparison.Ordinal)" in records
    assert "不会自动选择相似条目" in records


def test_v2_direct_answers_record_actual_match_id_before_ready():
    runtime = src("src/Bot/ChromeNs/KnowledgeEngineV2RuntimeBridge.cs")
    assert 'var sourceLabel = best != null && best.Record != null' in runtime
    assert '"本地知识V2（记录ID：" + best.Record.Id + "）"' in runtime
    assert runtime.index("var best = decision.Matches.FirstOrDefault(IsApprovedMatchForLogging);") < runtime.index(
        "sourceLabel,", runtime.index("var best = decision.Matches.FirstOrDefault(IsApprovedMatchForLogging);")
    )


def test_old_echo_text_similarity_cannot_forge_preset_navigation():
    mirror = src("src/Bot/AssistWindow/Widget/Robot/CtlRobot.MessageMirror.cs")
    tracker = src("src/Bot/ChromeNs/ResponseProgressTracker.cs")
    learning = src("src/Bot/ChromeNs/KnowledgeLearningService.cs")
    assert "历史记录未保存话术ID" in mirror
    assert "BotConversationHistoryStore.ResolveAnswerSourceStamp(" in mirror
    assert 'source.StartsWith("内置话术：", StringComparison.Ordinal)' not in mirror
    assert 'string.Equals(source, "商品链接预设回复", StringComparison.Ordinal)' in tracker
    assert 'if ((string.IsNullOrWhiteSpace(source)' not in tracker
    resolver = learning.split("public static string ResolveAnswerSource(", 1)[1].split(
        "public static bool TryFindLocalAnswer(", 1
    )[0]
    assert "return string.Empty;" in resolver
    assert "BotReplyPresetService.BuildSourceLabel(preset)" not in resolver
