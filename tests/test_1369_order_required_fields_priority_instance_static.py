from pathlib import Path


PRIORITY = Path("src/Bot/ChromeNs/OrderTemplateRequiredFieldsPriority.cs")


def read() -> str:
    return PRIORITY.read_text(encoding="utf-8-sig")


def test_priority_guard_is_bound_to_every_qn_instance():
    source = read()
    assert "_orderRequiredFieldsPriorityInstanceBootstrap" in source
    assert "OrderTemplateRequiredFieldsPriority.InitializeForApp()" in source
    assert "V2 已确认成为 messageCenterNotify 第一消费者" in source


def test_priority_guard_keeps_v2_first_with_fast_recheck():
    source = read()
    assert "new Timer(_ => ReorderAll(), null, 1, 100)" in source
    assert "d.Method.DeclaringType == typeof(OrderTemplateRequiredFieldsV2)" in source
    assert 'string.Equals(d.Method.Name, "OnMessageNotify", StringComparison.Ordinal)' in source
    assert "EvMessageNotity = null;" in source
    assert "EvMessageNotity += (EventHandler<MessageNotifyEventArgs>)handler;" in source
