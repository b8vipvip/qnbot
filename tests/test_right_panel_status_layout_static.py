from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_right_panel_hides_the_redundant_status_tab_header():
    xaml = read("src/Bot/AssistWindow/Widget/RightPanel.xaml")

    assert 'x:Key="RightPanelContentOnlyTabControl"' in xaml
    template = xaml[
        xaml.index('x:Key="RightPanelContentOnlyTabControl"'):
        xaml.index("</ControlTemplate>", xaml.index('x:Key="RightPanelContentOnlyTabControl"'))
    ]
    assert 'ContentSource="SelectedContent"' in template
    assert "TabPanel" not in template
    assert 'Template="{StaticResource RightPanelContentOnlyTabControl}"' in xaml


def test_status_area_has_tighter_vertical_spacing_without_hiding_status_fields():
    xaml = read("src/Bot/AssistWindow/Widget/Robot/CtlRobot.xaml")

    assert 'Grid.Row="0" Background="{StaticResource CardBg}"' in xaml
    assert 'Padding="9 2"' in xaml
    assert '<Setter Property="Margin" Value="0 0 14 0"/>' in xaml
    assert 'Padding="8 0"' in xaml

    for field in (
        'x:Name="txtConnectionStatus"',
        'x:Name="txtLanguageStatus"',
        'x:Name="txtStatusApi"',
        'x:Name="txtSeller"',
        'x:Name="txtRunStatus"',
    ):
        assert field in xaml
