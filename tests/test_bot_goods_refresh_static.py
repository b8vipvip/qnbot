from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_goods_refresh_has_single_authoritative_async_generation():
    source = read("src/Bot/AssistWindow/Widget/Robot/CtlRobot.xaml.cs")

    assert "private int _goodsRefreshGeneration;" in source
    assert "Interlocked.Increment(ref _goodsRefreshGeneration)" in source
    assert "IsGoodsRefreshCurrent(generation, requestQn, buyerTargetId)" in source
    assert "if (generation == _goodsRefreshGeneration)" in source
    assert "RemoveCtlGoods();" in source


def test_goods_rows_are_deduplicated_by_upstream_and_visible_identity():
    source = read("src/Bot/AssistWindow/Widget/Robot/CtlRobot.xaml.cs")

    assert "if (item.itemId > 0)" in source
    assert "BuildGoodsDisplayIdentity" in source
    assert ".GroupBy(BuildGoodsIdentity, StringComparer.OrdinalIgnoreCase)" in source
    assert ".GroupBy(BuildGoodsDisplayIdentity, StringComparer.OrdinalIgnoreCase)" in source
    assert ".Take(8)" in source
