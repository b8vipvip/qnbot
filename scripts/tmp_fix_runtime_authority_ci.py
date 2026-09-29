from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def rw(rel, transform):
    p = ROOT / rel
    text = p.read_text(encoding="utf-8-sig")
    new = transform(text)
    if new == text:
        raise RuntimeError(f"no change for {rel}")
    p.write_text(new, encoding="utf-8-sig")


def commerce(text):
    if "using BotLib;" not in text:
        marker = "using System.Text.RegularExpressions;\n"
        if marker not in text:
            raise RuntimeError("commerce using marker missing")
        text = text.replace(marker, marker + "using BotLib;\n", 1)
    return text


def test1330(text):
    old = '''def test_commerce_context_prefers_verified_order_over_text_fallback():\n    text = read("src/Bot/ChromeNs/CommerceContextService.cs")\n    structured = text.index("OrderGuidanceDeliveryGuard.TryGetLatestOrderSnapshot")\n    fallback = text.index("ApplyConversationFallback(result")\n    assert structured < fallback\n    assert "ApplyOrder(result, order);\\n                return result;" in text\n    assert 'SetPhase(target, "paid", "已付款/待履约")' in text\n    assert 'SetPhase(target, "unpaid", "已下单/待付款")' in text\n    assert 'SetPhase(target, "after_sale", "售后/退款处理中")' in text\n'''
    new = '''def test_commerce_context_prefers_verified_order_over_text_fallback():\n    text = read("src/Bot/ChromeNs/CommerceContextService.cs")\n    structured = text.index("SelectRelevantOrderSnapshot(")\n    fallback = text.index("ApplyConversationFallback(result")\n    assert structured < fallback\n    build = text[text.index("public static CommerceContextSnapshot Build"):text.index("public static void EnrichState")]\n    assert "TryGetLatestOrderSnapshot" not in build\n    assert "ApplyOrder(result, order);" in build\n    assert "result.SelectionReason = selectionReason;" in build\n    assert 'SetPhase(target, "paid", "已付款/待履约")' in text\n    assert 'SetPhase(target, "unpaid", "已下单/待付款")' in text\n    assert 'SetPhase(target, "after_sale", "售后/退款处理中")' in text\n'''
    if old not in text:
        raise RuntimeError("test1330 old block missing")
    return text.replace(old, new, 1)


def test1581(text):
    old = '''    assert "IsAuthoritativeSellerSession" in coordinator, (\n        "Fail-closed send validation must remain in place after authority rebinding."\n    )\n    assert "activeSessionRef" in coordinator and "targetSessionRef" in coordinator\n'''
    new = '''    assert "IsAuthoritativeSellerSession" in server, (\n        "The WebSocket authority registry must remain fail-closed after foreground rebinding."\n    )\n    assert "ValidateNativeSend" in coordinator, (\n        "Native send must still reject a seller/shop/CDP session that is not the active authority."\n    )\n    validate = coordinator[coordinator.index("internal static bool ValidateNativeSend"):coordinator.index("internal static void ReassertCurrent")]\n    assert "activeSession=" in validate and "targetSession=" in validate\n    assert "return false" in validate\n'''
    if old not in text:
        raise RuntimeError("test1581 old block missing")
    return text.replace(old, new, 1)


rw("src/Bot/ChromeNs/CommerceContextService.cs", commerce)
rw("tests/test_1330_commerce_reply_agent_static.py", test1330)
rw("tests/test_1581_runtime_authority_regressions_static.py", test1581)
print("CI repair applied")
