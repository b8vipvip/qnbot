from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def test_legacy_spec_placeholder_is_absent_from_active_source():
    retired = "{" + "规" + "格" + "}"
    suffixes = {".cs", ".xaml", ".ps1", ".js", ".json", ".config"}
    offenders = []
    for path in (ROOT / "src").rglob("*"):
        if not path.is_file() or path.suffix.lower() not in suffixes:
            continue
        try:
            text = path.read_text(encoding="utf-8-sig")
        except UnicodeDecodeError:
            continue
        if retired in text:
            offenders.append(str(path.relative_to(ROOT)))
    assert not offenders, "retired SKU placeholder remains in: " + ", ".join(offenders)


def test_canonical_sku_placeholder_is_used_by_order_renderer():
    renderer = (ROOT / "src/Bot/ChromeNs/OrderPlacedAutoReplyService.cs").read_text(encoding="utf-8-sig")
    assert '.Replace("{sku}", snapshot == null ? string.Empty : snapshot.SkuText ?? string.Empty)' in renderer
