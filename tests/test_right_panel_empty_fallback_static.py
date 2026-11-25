from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_right_panel_never_silently_renders_an_empty_bot_tab_host():
    panel = read("src/Bot/AssistWindow/Widget/RightPanel.xaml.cs")

    assert "BuildConfiguredTabs(false)" in panel
    assert "BuildConfiguredTabs(true)" in panel
    assert "EnsureRobotTab(\"InvalidOrEmptyConfiguration\")" in panel
    assert "EnsureRobotTab(\"Loaded\")" in panel
    assert "tabControl.Items.Count == 0" in panel
    assert "CreateRobotInitializationErrorContent" in panel
    assert "Bot工作台初始化失败" in panel
    assert "忽略无法识别的右侧面板标签配置" in panel
    assert 'case "工作台":' in panel
    assert 'case "机器人":' in panel
    assert 'case "Bot":' in panel


def test_default_workbench_name_uses_the_same_visibility_flag_as_legacy_robot_name():
    params = read("src/Bot/StartUp/Params.cs")
    panel = read("src/Bot/AssistWindow/Widget/RightPanel.xaml.cs")

    assert 'RightPanelCompOrderCsvDefault = "工作台"' in params
    assert 'tabName == "工作台" || tabName == "机器人"' in params
    assert "return GetRobotIsVisible(seller);" in params
    assert "Params.Panel.GetPanelOptionVisible(Wnd.Desk.WndTitle, tabName)" in panel
