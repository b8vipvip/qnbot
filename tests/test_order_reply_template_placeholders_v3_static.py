from pathlib import Path

SERVICE = Path("src/Bot/ChromeNs/OrderPlacedAutoReplyService.cs").read_text(encoding="utf-8-sig")
HUB = Path("src/Bot/ChromeNs/OrderEventHub.cs").read_text(encoding="utf-8-sig")
V2 = Path("src/Bot/ChromeNs/OrderTemplateRequiredFieldsV2.cs").read_text(encoding="utf-8-sig")
QN = Path("src/Bot/ChromeNs/QN.cs").read_text(encoding="utf-8-sig")


def test_supported_tokens_include_new_real_order_fields():
    assert '"{sku}"' in V2
    assert '"{买家备注}"' in V2
    assert '"{分段符}"' in V2
    assert 'public string BuyerRemark { get; set; }' in HUB
    for key in ("buyerremark", "buyermemo", "buyernote", "buyermessage", "remarkfrombuyer", "memofrombuyer"):
        assert f'"{key}"' in HUB
    keys = HUB.split("BuyerRemarkKeys", 1)[1].split("};", 1)[0]
    assert '"remark"' not in keys
    assert '"memo"' not in keys


def test_template_preserves_authored_layout():
    assert 'Regex.Replace(rendered, @"[ \\t]{2,}", " ")' not in SERVICE
    assert 'Regex.Replace(rendered, @"(?m)[ \\t]+$", string.Empty).Trim()' not in SERVICE
    assert '.Replace("{买家备注}", snapshot == null ? string.Empty : snapshot.BuyerRemark ?? string.Empty)' in SERVICE


def test_segment_token_sends_sequentially_without_trim():
    assert 'text.Split(new[] { segmentToken }, StringSplitOptions.None)' in QN
    assert 'var segment = segments[segmentIndex];' in QN
    assert 'await SendTextWithRetryAsync(buyer, segment, retryCount, cancellationToken, preSendGuard)' in QN
    block = QN.split('const string segmentToken = "{分段符}";', 1)[1].split('await _sendGate.WaitAsync(cancellationToken);', 1)[0]
    assert '.Trim()' not in block


def test_placeholder_hint_is_blue_clickable_and_caret_aware():
    assert 'Color.FromRgb(37, 99, 235)' in V2
    assert 'new Hyperlink(new Run(token))' in V2
    assert 'link.Click += delegate { InsertAtCaret(target, token); };' in V2
    assert 'box.SelectionStart' in V2
    assert 'box.SelectionLength' in V2


def test_buyer_remark_enrichment_is_strict_and_real_only():
    assert 'FindValue(tradeFlat, BuyerRemarkKeys)' in V2
    assert 'FindValue(flat, BuyerRemarkKeys)' in V2
    assert 'trade_found_but_buyer_remark_empty' in V2


def test_fixed_template_bypasses_output_policy_to_preserve_layout():
    assert 'var preserveTemplateLayout' in SERVICE
    assert 'resolution.Source.IndexOf("固定预设", StringComparison.Ordinal)' in SERVICE
    assert 'resolution.Source.IndexOf("接口失败兜底", StringComparison.Ordinal)' in SERVICE
    preserve = SERVICE.split('var preserveTemplateLayout', 1)[1].split('string duplicateReason;', 1)[0]
    assert 'rawReply + " [AI]"' in preserve


def test_order_status_placeholder_is_customer_facing_not_qianniu_internal_code():
    assert '.Replace("{订单状态}", FormatTradeStatusForTemplate(snapshot))' in SERVICE
    assert 'case "tradebuyerpay":' in SERVICE
    assert 'case "waitbuyerpay":' in SERVICE
    assert 'case "waitbuyerconfirmgoods":' in SERVICE
    assert 'case "tradefinished":' in SERVICE
    assert 'case "tradeclosed":' in SERVICE
    assert 'return "已付款";' in SERVICE
    assert 'return "待付款";' in SERVICE
    assert 'return "已发货";' in SERVICE
    assert 'return "交易完成";' in SERVICE
    assert 'return "已关闭";' in SERVICE
    render = SERVICE.split("private static string RenderTemplate", 1)[1].split("private static string FormatTradeStatusForTemplate", 1)[0]
    assert 'snapshot.TradeStatus ?? string.Empty' not in render
