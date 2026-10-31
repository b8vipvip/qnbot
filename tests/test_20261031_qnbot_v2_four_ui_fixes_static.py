"""Regression for Knowledge Center V2 fixes: presets, settings, short texts and archive grouping."""
from pathlib import Path
ROOT = Path(__file__).resolve().parents[1]
def read(file):
    return (ROOT / file).read_text(encoding="utf-8-sig")

def test_preset_save_keeps_id_before_grid_rebinding_and_button_is_under_enabled():
    ui = read("src/Bot/Knowledge/KnowledgeV2BotReplyManagementPage.cs")
    block = ui.split("private void SavePreset()", 1)[1].split("private void DeletePreset()", 1)[0]
    assert "var id = editing == null" in block
    assert "BotReplyPresetService.SavePresets(_seller, candidate);" in block
    assert "RefreshPresets();" in block
    assert "_selectedPreset.Id" not in block
    assert "var restored = _presetView.FirstOrDefault(x => x.Id == id);" in block
    assert block.index("SavePresets") < block.index("RefreshPresets")
    construction = ui.split("private UIElement BuildPresetTab()",1)[1].split("private UIElement BuildKnowledgeSearchTab()",1)[0]
    assert construction.index("editor.Children.Add(_presetEnabled);") < construction.index('var save = Button("保存话术", 115);')
    assert construction.index('var save = Button("保存话术", 115);') < construction.index("editor.Children.Add(_presetStatus);")
    assert "toolbar.Children.Add(save)" not in construction

def test_settings_navigation_no_longer_aliases_distinct_policy_tab():
    ui = read("src/Bot/Options/FeatureSettingsOptionsControl.cs")
    segment = ui.split("public void NavigateTo(string pageTitle)",1)[1].split("public void Save(string seller)",1)[0]
    assert 'pageTitle = "自动回复规则";' not in segment
    tabs = read("src/Bot/Options/CtlRobotOptions.xaml.cs")
    assert 'Header = "自动回复规则", Content = BuildRulesTab()' in tabs
    assert 'Header = "消息策略", Content = BuildPolicyTab()' in tabs

def test_existing_local_short_replies_get_hello_once_without_overwriting_customizations():
    service = read("src/Bot/ChromeNs/DeterministicAutoReplyService.cs")
    assert "default-greeting" in service
    for word in ("你好", "您好", "在么", "在吗", "谢谢", "好的"):
        assert word in service
    assert "GreetingMigrationSuffix" in service
    assert "File.Exists(markerPath)" in service
    assert "if (!alreadyCovered)" in service
    assert "entries.Add(greetings);" in service
    assert "SaveLocked(path, entries, false);" in service
    assert "NormalizePhrase(phrase), normalized, StringComparison.Ordinal" in service
    coordinator = read("src/Bot/ChromeNs/BuyerMessageBurstCoordinator.cs")
    assert "LocalShortReplyService.TryResolve(" in coordinator
    assert "CanonicalPreMergeOutcome.Consumed" in coordinator

def test_buyer_list_collapses_identical_messages_and_uses_ai_semantic_keys():
    ui = read("src/Bot/Knowledge/BuyerMessageManagementPage.cs")
    archive = read("src/Bot/Knowledge/BuyerMessageArchiveService.cs")
    assert 'grouped.Select(g => new BuyerMessageListItem' in ui
    assert "Messages = g.Messages," in ui
    assert "new FrameworkElementFactory(typeof(Expander))" in ui
    assert "new FrameworkElementFactory(typeof(ListBox))" in ui
    assert "Details = g.Messages.OrderByDescending" in ui
    assert "SelectMany(x => x.Messages.Select(m => m.Id))" in ui
    assert "GroupBy(x => x.GroupKey ?? x.Id)" in archive
    assert '"exact-" + Hash(' in archive
    assert '"ai-" + Hash(' in archive
    assert "BuyerMessageArchiveService.Groups(_seller, start, end, _search.Text)" in ui

def test_one_button_runs_ai_semantic_grouping_then_reviewable_optimization():
    ui = read("src/Bot/Knowledge/BuyerMessageManagementPage.cs")
    archive = read("src/Bot/Knowledge/BuyerMessageArchiveService.cs")
    page = ui.split("internal sealed class BuyerMessageManagementPage",1)[1].split(
        "internal sealed class BuyerQuestionOptimizationWindow",1)[0]
    assert page.count('Button("AI分析优化"') == 1
    assert "AI统计优化" not in ui
    assert "AI分析并归类" not in ui
    assert "new BuyerQuestionOptimizationWindow(owner, _seller, true).ShowDialog()" in page
    assert "if (autoRun) await GenerateAsync();" in ui
    assert "BuyerMessageArchiveService.OptimizeTop(" in ui
    assert "await GroupByAi(seller, progress, ct, days)" in archive
    assert '"pending"' in archive
    assert '"人工确认并应用直答"' in ui

def test_product_url_is_scoped_not_silently_generic_and_saved_without_nullref():
    preset = read("src/Bot/ChromeNs/BotReplyPresetService.cs")
    assert "ExactProductUrlTrigger" in preset
    assert 'if (!match.Success) match = ExactProductUrlTrigger.Match(value);' in preset
    assert 'normalized.StartsWith("https://", StringComparison.OrdinalIgnoreCase)' in preset
    assert '只能是“买家消息包含商品链接”' in preset
    assert 'return enabled.Where(x => !IsItemScopedTrigger(x.Trigger)).ToList();' in preset
