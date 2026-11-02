"""Buyer message context menu offers a reviewed direct answer, not a send action."""
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def read(path):
    return (ROOT / path).read_text(encoding="utf-8-sig")


MIRROR = read("src/Bot/AssistWindow/Widget/Robot/CtlRobot.MessageMirror.cs")
EDITOR = read("src/Bot/AssistWindow/Widget/Robot/ConversationEditWindow.cs")
KNOWLEDGE = read("src/Bot/Knowledge/KnowledgeEngineV2.Repository.cs")


def test_buyer_only_context_action_preserves_copy_and_seller_bot_menus():
    assert 'BuildMessageMirrorBotContextMenu(botAction, turn, seller, buyer,' in MIRROR
    assert ': BuildCopyOnlyContextMenu(messageText, bubble);' in MIRROR
    assert 'if (!isSeller' in MIRROR
    assert 'string.Equals(turn.Role, "user", StringComparison.Ordinal)' in MIRROR
    assert '&& !turn.Withdrawn' in MIRROR
    assert '&& !string.IsNullOrWhiteSpace(turn.Text)' in MIRROR
    assert 'BuildMessageMirrorBuyerContextMenu(' in MIRROR
    assert 'var menu = BuildCopyOnlyContextMenu(question, placementTarget);' in MIRROR
    assert 'var add = new MenuItem { Header = "添加直答消息" };' in MIRROR
    assert 'menu.Items.Add(add);' in MIRROR


def test_confirmed_operator_answer_is_saved_to_scoped_v2_knowledge():
    assert 'new ConversationEditWindow(question, string.Empty, true)' in MIRROR
    assert 'if (editor.ShowDialog() != true) return;' in MIRROR
    assert 'var approvedQuestion = editor.EditedQuestion;' in MIRROR
    assert 'var approvedAnswer = editor.EditedAnswer;' in MIRROR
    assert 'Task.Run(() =>' in MIRROR
    assert 'KnowledgeEngineV2Repository.TrySaveReviewedDirectAnswer(' in MIRROR
    assert 'seller, approvedQuestion, approvedAnswer, sourceMessageId,' in MIRROR
    assert 'out status, out savedId' in MIRROR
    assert 'private bool _buyerDirectAnswerSaveInProgress;' in MIRROR
    assert 'finally' in MIRROR
    assert '_buyerDirectAnswerSaveInProgress = false;' in MIRROR
    assert 'Nothing is sent to Qianniu.' in MIRROR


def test_dialog_requires_complete_question_and_answer_before_save():
    assert 'bool addDirectAnswer = false' in EDITOR
    assert 'Title = addDirectAnswer ? "添加直答消息" : "修改答案";' in EDITOR
    assert 'Content = addDirectAnswer ? "保存直答" : "保存修改"' in EDITOR
    assert 'string.IsNullOrWhiteSpace(EditedAnswer)' in EDITOR
    assert 'string.IsNullOrWhiteSpace(EditedQuestion)' in EDITOR
    assert 'parsed.ContextDependent' in EDITOR
    assert 'string.IsNullOrWhiteSpace(parsed.Subject)' in EDITOR


def test_existing_reviewed_answer_path_is_durable_deduplicated_and_immediate():
    assert 'Exact-title duplicates are updated instead of creating competing facts' in KNOWLEDGE
    assert 'KnowledgeEngineV2Semantics.Compact(x.Title)' in KNOWLEDGE
    assert 'record.SourceType = "manual_reviewed_direct";' in KNOWLEDGE
    assert 'Save(seller, record); // Durable write + immediate immutable snapshot update.' in KNOWLEDGE
