"""Regression: incomplete contact discovery must never be presented as full history success."""
from pathlib import Path
root=Path(__file__).resolve().parents[1]
def read(p): return (root/p).read_text(encoding="utf-8-sig")
scan=read("src/Bot/Knowledge/ChatHistoryScanService.cs")
evidence=read("src/Bot/Knowledge/BuyerHistoryScanEvidenceSession.cs")
page=read("src/Bot/Knowledge/BuyerMessageManagementPage.cs")
audit=read("src/Bot/Options/SettingsOperationAudit.cs")
settings=read("src/Bot/Options/WndOption.xaml.cs")
feature=read("src/Bot/Options/CtlRobotOptions.xaml.cs")
targets=read("src/Directory.Build.targets")

def test_contact_discovery_sources_and_count_logged():
    assert "BotConversationHistoryStore.LoadRecentBySeller(seller, 5000)" in scan
    for name in ("visible_chat_list","message_manager","contacts_api",
                 "bot_reply_history","merged_and_validated"):
        assert 'evidence.Discovery("'+name+'"' in scan
    assert 'history_scan_discovery run=' in scan
    assert 'history_scan_started run=' in scan
    assert 'history_scan_contact_complete run=' in scan
    assert 'history_scan_contact_retry_exhausted run=' in scan
    assert 'scan_contact_source_error source=relation.rebase' in scan
    assert 'scan_contact_source_error source=contact.search' in scan
    assert 'scan_contact_source_warning source=chat_ui' in scan
    assert "DiscoverySourceCounts" in evidence
    assert "DiscoveryUniqueCounts" in evidence
    assert '"discovery_source"' in evidence
    assert "ContactHash(buyer)" in scan

def test_no_false_full_shop_scan_for_one_contact():
    assert 'scanState = failed == 0 && names.Count > 1 ? "completed" : "partial";' in scan
    assert '"incomplete_contact_discovery"' in scan
    assert 'ContactCoverageVerified = false' in scan
    assert "CoverageWarning = names.Count <= 1" in scan
    assert "不能认定已扫描全店历史消息" in scan
    assert "scan.ContactCoverageVerified" in page
    assert '"历史消息扫描 · 覆盖待核实"' in page
    assert "scan.CoverageWarning" in page

def test_settings_operations_are_traced_but_not_secrets():
    assert "SettingsOperationAudit.cs" in targets
    assert "SettingsOperationAudit.Attach(this" in settings
    assert "SettingsOperationAudit.Attach(this" in feature
    assert 'event=navigate;page=' in settings
    assert 'save_started;page=' in settings
    assert 'SettingsOperationAudit.SaveResult("bot_settings"' in settings
    assert 'SettingsOperationAudit.SaveResult("feature_settings"' in feature
    assert "ButtonBase.ClickEvent" in audit
    assert "Selector.SelectionChangedEvent" in audit
    assert "LostKeyboardFocusEvent" in audit
    assert "[SettingsOperation]" in audit
    assert "PasswordBox" not in audit
    assert "element.Text" not in audit
    assert "button.ToolTip" not in audit
    assert 'error.GetType().Name' in audit
