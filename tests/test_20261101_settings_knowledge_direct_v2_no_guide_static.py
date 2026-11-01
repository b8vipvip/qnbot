"""The settings Knowledge entry is a direct V2 destination, with no intermediary launcher."""
from pathlib import Path

ROOT=Path(__file__).resolve().parents[1]
def read(path):
    return (ROOT/path).read_text(encoding="utf-8-sig")

WINDOW=read("src/Bot/Options/WndOption.xaml.cs")
FEATURE=read("src/Bot/Options/FeatureSettingsOptionsControl.cs")
CENTER=read("src/Bot/Knowledge/KnowledgeCenterWindow.cs")
SHELL=read("src/Bot/Knowledge/KnowledgeCenterV2Ui.cs")

def test_settings_left_knowledge_opens_real_v2_directly():
    assert 'OptionEnum.GoodsKnowledge' in WINDOW
    assert 'OpenKnowledgeCenterDirectly();' in WINDOW
    assert 'KnowledgeCenterWindow.ShowForSeller(this, Seller)' in WINDOW
    assert 'RestoreCurrentSettingsSelection();' in WINDOW
    assert 'if (_knowledgeCenterWindow != null)' in WINDOW
    assert '_knowledgeCenterWindow.Activate();' in WINDOW
    assert 'knowledge.Closed += delegate' in WINDOW
    assert 'event=open_knowledge_center_direct' in WINDOW

def test_shopkey_and_seller_are_preserved_on_open():
    assert 'internal static KnowledgeCenterWindow ShowForSeller(Window owner, string seller)' in CENTER
    assert 'ShopContextLocator.ResolveRuntimeBySellerNick(seller)' in CENTER
    assert 'RequestedSeller = seller' in CENTER
    assert 'ShopScopedUiBridge.Attach(window, shop);' in CENTER
    assert 'if (owner != null) window.Owner = owner;' in CENTER

def test_knowledge_launcher_page_is_removed_without_deleting_business_settings():
    assert 'RemoveLegacyKnowledgeGuideTab();' in FEATURE
    assert '_tabs.Items.Remove(knowledgeTab);' in FEATURE
    assert 'ReplaceKnowledgePageWithEmbeddedLauncher' not in FEATURE
    assert 'Content = "打开知识库中心"' not in FEATURE
    assert 'NavigateTo("自动回复规则");' in FEATURE
    assert 'Save(string seller)' in FEATURE
    assert 'SyncOffHoursToLegacyControls();' in FEATURE

def test_former_guide_actions_exist_inside_v2():
    assert 'var help = Button("使用帮助", 92);' in SHELL
    assert 'KnowledgeCenterHelpWindow.MyShow(_owner, _seller);' in SHELL
    assert 'Button("旧版知识库预览", 136)' in SHELL
    assert 'LegacyKnowledgePreviewWindow.MyShow(_owner, _seller);' in SHELL
    assert 'ToolTip = "只读查看当前店铺的旧版知识库，不启用旧版功能"' in SHELL
