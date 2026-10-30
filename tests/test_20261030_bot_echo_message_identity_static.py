"""Regressions for per-Qianniu-message Bot source identity."""
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def read(path):
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_echo_link_uses_transport_identity_before_answer_similarity():
    history = read("src/Bot/ChromeNs/BotConversationHistoryStore.cs")
    method = history.split("public static string RecordBotEcho(", 1)[1].split(
        "public static string BuildBotEchoEntityId(", 1
    )[0]
    assert "internal sealed class BotReplyEchoLinkEntity" in history
    assert "EchoId text primary key not null" in history
    assert "IX_BotReplyEchoLink_Record" in history
    assert "var echoId = BuildBotEchoEntityId(seller, buyer, messageKey, sentAt, answer);" in method
    assert "x => x.EchoId == echoId" in method
    assert "string.Equals(x.EntityId, originalId, StringComparison.Ordinal)" in method
    assert method.index("var match = candidates.FirstOrDefault") < method.index(
        "if (match == null && existingLink == null)"
    )
    assert 'x.EntityId.StartsWith("bot-echo-", StringComparison.Ordinal)' in method
    assert "var candidateRecordId = candidate.Row.EntityId;" in method
    assert "x => x.RecordId == candidateRecordId" in method
    assert "if (claimed != null) continue;" in method
    assert "DbHelper.Db.SaveOneRecord(new BotReplyEchoLinkEntity" in method
    assert "RecordId = match.EntityId" in method
    assert "var id = echoId;" in method


def test_orphaned_echo_links_are_collected_with_history():
    history = read("src/Bot/ChromeNs/BotConversationHistoryStore.cs")
    assert "delete from BotReplyEchoLinkEntity where RecordId not in " in history


def test_manual_correction_does_not_bleed_into_other_messages():
    mirror = read("src/Bot/AssistWindow/Widget/Robot/CtlRobot.MessageMirror.cs")
    manager = read("src/Bot/Knowledge/KnowledgeV2BotReplyManagementPage.cs")
    history = read("src/Bot/ChromeNs/BotConversationHistoryStore.cs")
    assert "BotConversationHistoryStore.GetManualSource(seller, botEchoRecordId)" in mirror
    assert "BotConversationHistoryStore.UpdateAnswerSource(seller, sourceRecordId, edited)" in mirror
    assert "BotConversationHistoryStore.UpdateAnswerSource(_seller, _selectedSource.EntityId, source)" in manager
    assert "KnowledgeLearningService.OverrideAnswerSource(" not in mirror
    assert "KnowledgeLearningService.OverrideAnswerSource(" not in manager
    assert "KnowledgeLearningService.RegisterAnswerSource(" not in mirror
    assert "OriginalSource = row.AnswerSource" in history
