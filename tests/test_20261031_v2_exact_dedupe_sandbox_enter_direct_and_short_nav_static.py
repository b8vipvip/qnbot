"""Knowledge V2: raw duplicate counts, contextual reviewed answers and short reply UI."""
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]

def source(path):
    return (ROOT / path).read_text(encoding="utf-8-sig")

def test_duplicate_messages_show_one_plain_row_with_frequency_no_time_column():
    ui = source("src/Bot/Knowledge/BuyerMessageManagementPage.cs")
    archive = source("src/Bot/Knowledge/BuyerMessageArchiveService.cs")
    assert 'Header = "接收时间"' not in ui
    assert 'Header = "接收次数"' in ui
    assert 'GroupCount = g.Count' in ui
    assert 'var wordings = g.Messages.GroupBy(x => x.Text ?? string.Empty,' in ui
    assert 'IsSemanticMerged = wordings.Count > 1' in ui
    assert 'new FrameworkElementFactory(typeof(TextBlock))' in ui
    assert 'new FrameworkElementFactory(typeof(Expander))' in ui
    assert 'CellTemplateSelector = new BuyerMessageContentSelector' in ui
    assert 'row.IsSemanticMerged ? SemanticTemplate : ExactTemplate' in ui
    assert '"（接收" + x.Count() + "次）"' in ui
    assert 'Messages = g.Messages' in ui  # source events stay available for counted deletes
    assert 'GroupBy(x => x.GroupKey ?? x.Id)' in archive
    assert 'Count = g.Count()' in archive
    assert 'state.Db.ReadOneRecord<BuyerQuestionRow>(x => x.ReceiptHash == receipt)' in archive

def test_sandbox_plain_enter_sends_shift_enter_still_inserts_newline():
    sandbox = source("src/Bot/Knowledge/KnowledgeV2SandboxConversation.cs")
    assert 'e.Key == System.Windows.Input.Key.Enter' in sandbox
    assert 'System.Windows.Input.ModifierKeys.Shift) == 0' in sandbox
    assert 'await RunAsync();' in sandbox
    assert 'ToolTip = "回车模拟发送；Shift+Enter 换行。不会真正联系买家"' in sandbox
    assert 'AcceptsReturn = true' in sandbox
    assert 'if (_busy) return;' in sandbox

def test_direct_answer_requires_explicit_independent_business_subject_before_confirmation():
    sandbox = source("src/Bot/Knowledge/KnowledgeV2SandboxConversation.cs")
    repository = source("src/Bot/Knowledge/KnowledgeEngineV2.Repository.cs")
    assert 'KnowledgeEngineV2Semantics.IsContextDependent(question)' in sandbox
    assert 'var fullQuestion = productSubject + " " + question;' in sandbox
    assert 'prompt.Text = fullQuestion;' in sandbox
    assert 'var parsedQuestion = KnowledgeEngineV2Semantics.Parse(question, null);' in sandbox
    assert 'parsedQuestion.ContextDependent' in sandbox
    assert '请补全直答问题' in sandbox
    assert 'TrySaveReviewedDirectAnswer(' in sandbox
    assert 'if (parsed.ContextDependent || string.IsNullOrWhiteSpace(parsed.Subject))' in repository
    assert 'sourceMessageId' in repository

def test_short_reply_management_is_visible_in_v2_and_uses_explicit_shop():
    nav = source("src/Bot/Knowledge/KnowledgeCenterV2Ui.cs")
    service = source("src/Bot/ChromeNs/DeterministicAutoReplyService.cs")
    assert 'Nav("短消息回复", () => new LocalShortReplyManagerControl(_seller))' in nav
    assert 'public LocalShortReplyManagerControl(string seller = null)' in service
    assert 'LocalShortReplyService.LoadForSellerUi(_seller)' in service
    assert 'LocalShortReplyService.SaveForSellerUi(_seller, _all)' in service
    assert 'LocalShortReplyService.SaveForSellerUi(_seller, list)' in service
    assert 'LocalShortReplyService.RestoreDefaultsForSellerUi(_seller)' in service
    assert 'ShopContextLocator.ResolveRuntimeBySellerNick(seller)' in service
    assert 'Header = "短消息回复"' in service
    assert 'AddButton(tools, "编辑所选"' in service
