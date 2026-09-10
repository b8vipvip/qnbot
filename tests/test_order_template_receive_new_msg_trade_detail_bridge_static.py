from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
LEGACY = ROOT / "src" / "Bot" / "ChromeNs" / "OrderTemplateReceiveNewMessageTradeDetailBridge.cs"
V2 = ROOT / "src" / "Bot" / "ChromeNs" / "OrderTemplateRequiredFieldsV2.cs"
PROPS = ROOT / "src" / "Directory.Build.props"


def read(path: Path) -> str:
    return path.read_text(encoding="utf-8-sig")


def test_legacy_receive_new_msg_bridge_is_inert_and_not_compiled():
    legacy = read(LEGACY)
    props = read(PROPS)
    assert "OrderTemplateReceiveNewMessageTradeDetailBridge.InitializeForApp()" in legacy
    assert "return new object();" in legacy
    assert "new Timer(_ => Attach()" not in legacy
    assert "qn.EvRecieveNewMessage +=" not in legacy
    assert '<Compile Include="$(MSBuildProjectDirectory)\\ChromeNs\\OrderTemplateReceiveNewMessageTradeDetailBridge.cs"' not in props


def test_v2_owns_both_receive_new_msg_and_message_center_sources():
    v2 = read(V2)
    assert "qn.EvRecieveNewMessage += OnReceiveNewMessage" in v2
    assert "qn.EvMessageNotity += OnMessageNotify" in v2
    assert "JsonConvert.DeserializeObject<ChatResponse>(raw)" in v2
    assert "OrderPlacedAutoReplyService.TryCreatePlan" in v2
    assert "StartOwnedPlan(qn, plan" in v2


def test_receive_new_msg_path_keeps_strict_order_evidence():
    v2 = read(V2)
    assert "if (qn == null || raw.Length < 8 || !LooksPotential(raw)) return" in v2
    assert "if (!MessageLooksPotential(message)) continue" in v2
    assert "if (plan == null || plan.IsBuyerFollowUp) return" in v2
    assert "订单号|订单编号|主订单号|子订单号|交易号|订单" in v2


def test_v2_sends_first_then_runs_passive_trade_probe():
    v2 = read(V2)
    scope = v2[v2.index("private static async Task EnrichValidateAndSendAsync"):v2.index("private static async Task<EnrichmentProbe> TryEnrichFromTradeApiAsync")]
    assert scope.index("ProcessOrderTemplateRequiredFieldsPlanAsync") < scope.index("TryEnrichFromTradeApiAsync")
    assert "postSendProbe" in scope
    assert "不影响已执行自动回复" in scope
