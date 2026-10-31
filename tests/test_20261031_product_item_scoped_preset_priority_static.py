"""Product-link reply priority: item-scoped rules must win safely without leaking to other items."""
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
def code(path):
    return (ROOT / path).read_text(encoding="utf-8-sig")

def test_exact_item_trigger_is_executable_and_validated():
    preset = code("src/Bot/ChromeNs/BotReplyPresetService.cs")
    assert 'ExactItemTrigger = new Regex(' in preset
    assert "商品ID\\s*[:：]\\s*(\\d{6,20})" in preset
    assert "TryParseExactItemTrigger(" in preset
    assert "enabledExactItemIds.Add(itemId)" in preset
    assert "已有一条启用的专属话术" in preset
    assert "SelectForProductItem(GetEnabledPresets(seller), itemId)" in preset
    assert "if (exact.Count > 0) return exact;" in preset
    assert "return enabled.Where(x => !IsItemScopedTrigger(x.Trigger)).ToList();" in preset
    assert "new BotReplyPreset { Id = \"product-link-ack-01\"" in preset
    assert "1057505313937" not in preset

def test_only_trusted_item_id_or_recognized_item_url_scopes_reply():
    store = code("src/Bot/ChromeNs/ConversationContextStore.cs")
    assert "original.itemId" in store
    assert "original.actionUrl" in store
    assert "original.url" in store
    assert "item\\.taobao\\.com|detail\\.tmall\\.com|h5\\.m\\.taobao\\.com" in store
    assert "GetPresetsForProductLink(seller, itemId)" in store
    assert "var selectedPreset = presets[index];" in store
    assert "ProductItemId = itemId" in store
    assert "PresetId = selectedPreset.Id" in store
    assert 'rule=" + (exactMatch ? "exact_item" : "generic")' in store
    # Catalog edits while queued do not allow an edited or disabled item rule to send.
    assert "GetPresetsForProductLink(seller, pending.ProductItemId)" in store
    assert "GetEnabledPresets(seller).Any" in store

def test_item_scoped_rule_stays_in_existing_preset_management_ui():
    ui = code("src/Bot/Knowledge/KnowledgeV2BotReplyManagementPage.cs")
    sandbox = code("src/Bot/Knowledge/KnowledgeV2SandboxConversation.cs")
    router = code("src/Bot/ChromeNs/BuyerMessageBurstCoordinator.cs")
    assert "商品ID:1234567890123" in ui
    assert "优先于所有通用商品链接话术" in ui
    assert "保存话术" in ui and "启用此话术" in ui
    assert "GetEnabledPresetsForProductLinkPreview(_seller, itemId)" in sandbox
    assert "ResolveProductItemId(null, message)" in sandbox
    assert "BotReplyPresetService.ResolveSourceLabel(item.SellerNick, productLinkReply)" in router
    assert "aiCalled=false" in router

def test_no_broad_product_preset_shall_auto_send_for_unmatched_specific_item():
    preset = code("src/Bot/ChromeNs/BotReplyPresetService.cs")
    section = preset.split("private static List<BotReplyPreset> SelectForProductItem(", 1)[1].split(
        "internal static bool IsItemScopedTrigger", 1)[0]
    assert "if (exact.Count > 0) return exact;" in section
    assert "return enabled.Where(x => !IsItemScopedTrigger(x.Trigger)).ToList();" in section
