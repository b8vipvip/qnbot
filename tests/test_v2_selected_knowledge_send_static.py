from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]

def read(path):
    return (ROOT / path).read_text(encoding="utf-8-sig")

def test_explicit_selection_preserves_manual_priority_and_conditional_advice_identity():
    public = read("src/Bot/Knowledge/KnowledgeEngineV2.Service.Public.cs")
    typed = read("src/Bot/ChromeNs/ConditionalBusinessAdviceService.cs")
    runtime = read("src/Bot/ChromeNs/KnowledgeEngineV2RuntimeBridge.cs")
    assert "var best = manualExact ?? manualSimilar ?? productionMatches.FirstOrDefault();" in public
    assert "decision.SelectedMatch = decision.CanDirectReply ? best : null;" in public
    assert "decision.SelectedMatch = match;" in typed
    assert "var best = decision.SelectedMatch;" in runtime
    assert "decision.Matches.FirstOrDefault(IsApprovedMatchForLogging)" not in runtime
    assert "best.Record.Id" in runtime

def test_delayed_direct_reply_revalidates_selected_record_and_mode_before_transport():
    runtime = read("src/Bot/ChromeNs/KnowledgeEngineV2RuntimeBridge.cs")
    guard = runtime.split("Func<bool> preSendGuard = () =>", 1)[1].split("bool sendOk;", 1)[0]
    assert "!ReplyModeService.IsLocalFirst(burst.SellerNick)" in guard
    assert "!IsSelectedKnowledgeCurrent(burst.SellerNick, burst.ShopKey, decision)" in guard
    assert guard.index("IsSelectedKnowledgeCurrent") < guard.index("RenewDistributedDelivery")
    current = runtime.split("internal static bool IsSelectedKnowledgeCurrent(", 1)[1]
    assert "LoadCommittedRecord(seller, record.Id)" in current
    assert "EditorRecordFingerprint(record)" in current
    assert "EditorRecordFingerprint(committed)" in current
    assert "ShopReceptionPolicy.IsQuarantined" in current
    assert 'string.Equals(committed.Status, "deleted"' in current
    assert 'string.Equals(committed.Status, "disabled"' in current
    assert "if (!sendOk && knowledgeVersionSuppressed)" in runtime
