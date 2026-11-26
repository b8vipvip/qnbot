import importlib
import os
import sys
from datetime import datetime, timedelta, timezone
from pathlib import Path
from types import SimpleNamespace

import pytest


@pytest.fixture()
def control_plane(tmp_path, monkeypatch):
    monkeypatch.setenv("DATABASE_PATH", str(tmp_path / "test.db"))
    monkeypatch.setenv("DATA_DIR", str(tmp_path))
    monkeypatch.setenv("APP_SECRET", "test-secret-for-conditional-advice")
    monkeypatch.setenv("API_KEY_ENCRYPTION_KEY", "iV7XoPa3z4i44n5gP5gsLt5mFMQbdbGICxVMJ4K7VQk=")
    monkeypatch.setenv("COOKIE_SECURE", "false")
    monkeypatch.setenv("DISABLE_SCHEDULER", "true")
    service_dir = Path(__file__).resolve().parents[1]
    if str(service_dir) not in sys.path:
        sys.path.insert(0, str(service_dir))
    sys.modules.pop("app", None)
    module = importlib.import_module("app")
    module.init_db()
    yield module
    sys.modules.pop("app", None)
    if str(service_dir) in sys.path:
        sys.path.remove(str(service_dir))


@pytest.fixture()
def advice_api(control_plane, monkeypatch):
    service_dir = Path(__file__).resolve().parents[1]
    if str(service_dir) not in sys.path:
        sys.path.insert(0, str(service_dir))
    sys.modules.pop("conditional_business_advice", None)
    module = importlib.import_module("conditional_business_advice")

    class Console:
        @staticmethod
        def _runtime_client(request):
            return {"id": int(request.client_id)}

    class Binding:
        @staticmethod
        def ensure_binding(client_id, shop_key, force=False, seller=""):
            assert int(client_id) > 0
            assert shop_key.startswith("qn_")
            return {"ok": True}

    module.install(control_plane, Console, Binding)
    module.init_db()
    return module


def request_for(client_id):
    return SimpleNamespace(client_id=client_id)


def claim(module, client_id, shop, buyer="buyer-1", order="order-1", advice="knowledge-1", request_id=None):
    return module.runtime_claim(
        module.ConditionalAdviceClaimInput(
            buyer=buyer,
            order_id=order,
            advice_key=advice,
            request_id=request_id or ("request-" + str(client_id) + "-000000000"),
            lease_seconds=120,
        ),
        request_for(client_id),
        x_shop_key=shop,
    )


def test_claim_is_global_across_different_client_tokens_for_the_same_shop(advice_api):
    shop = "qn_0123456789ab"
    first = claim(advice_api, 11, shop)
    second = claim(advice_api, 12, shop, request_id="request-second-000000")
    assert first["claimed"] is True
    assert second["claimed"] is False
    assert second["state"] == "reserved"

    completed = advice_api.runtime_complete(
        advice_api.ConditionalAdviceCompleteInput(
            buyer="buyer-1",
            order_id="order-1",
            advice_key="knowledge-1",
            lease_token=first["lease_token"],
            outcome="delivered",
        ),
        request_for(11),
        x_shop_key=shop,
    )
    assert completed["state"] == "delivered"
    third = claim(advice_api, 12, shop, request_id="request-third-000000")
    assert third["claimed"] is False
    assert third["state"] == "delivered"


def test_expired_lease_becomes_uncertain_until_operator_reconciles(advice_api, monkeypatch):
    shop = "qn_0123456789ab"
    start = datetime(2026, 11, 26, 8, 0, tzinfo=timezone.utc)
    monkeypatch.setattr(advice_api, "_utc_now", lambda: start)
    first = claim(advice_api, 11, shop)
    monkeypatch.setattr(advice_api, "_utc_now", lambda: start + timedelta(minutes=5))

    blocked = claim(advice_api, 12, shop, request_id="request-second-000000")
    assert blocked["claimed"] is False
    assert blocked["state"] == "uncertain"

    pending = advice_api.runtime_pending(request_for(12), x_shop_key=shop)
    assert pending["items"][0]["buyer"] == "buyer-1"
    assert pending["items"][0]["order_id"] == "order-1"
    scope_hash = pending["items"][0]["scope_hash"]
    monkeypatch.setattr(advice_api, "_utc_now", lambda: start + timedelta(minutes=7))

    resolved = advice_api.runtime_resolve(
        advice_api.ConditionalAdviceResolveInput(scope_hash=scope_hash, confirmed_delivered=False),
        request_for(12),
        x_shop_key=shop,
    )
    assert resolved["state"] == "released"
    retried = claim(advice_api, 12, shop, request_id="request-third-000000")
    assert retried["claimed"] is True

    # A late completion from the original sender cannot complete the new lease.
    with pytest.raises(Exception):
        advice_api.runtime_complete(
            advice_api.ConditionalAdviceCompleteInput(
                buyer="buyer-1",
                order_id="order-1",
                advice_key="knowledge-1",
                lease_token=first["lease_token"],
                outcome="delivered",
            ),
            request_for(11),
            x_shop_key=shop,
        )


def test_same_request_id_replays_the_original_lease_token(advice_api):
    shop = "qn_0123456789ab"
    request_id = "request-replay-000000"
    first = claim(advice_api, 11, shop, request_id=request_id)
    replay = claim(advice_api, 11, shop, request_id=request_id)
    assert first["claimed"] is True
    assert replay["claimed"] is True
    assert replay["replayed"] is True
    assert replay["lease_token"] == first["lease_token"]


def test_cancelled_before_send_releases_only_the_owned_lease(advice_api):
    shop = "qn_0123456789ab"
    first = claim(advice_api, 11, shop)
    released = advice_api.runtime_complete(
        advice_api.ConditionalAdviceCompleteInput(
            buyer="buyer-1",
            order_id="order-1",
            advice_key="knowledge-1",
            lease_token=first["lease_token"],
            outcome="cancelled_before_send",
        ),
        request_for(11),
        x_shop_key=shop,
    )
    assert released["state"] == "released"
    assert claim(advice_api, 12, shop, request_id="request-new-00000000")["claimed"] is True


def test_scope_is_shop_isolated_and_raw_identifiers_are_encrypted_at_rest(advice_api, control_plane):
    shop_a = "qn_0123456789ab"
    shop_b = "qn_abcdef012345"
    first = claim(advice_api, 11, shop_a, buyer="private-buyer", order="private-order", advice="private-advice")
    second = claim(advice_api, 12, shop_b, buyer="private-buyer", order="private-order", advice="private-advice")
    assert first["claimed"] is True
    assert second["claimed"] is True

    with control_plane.db() as conn:
        row = conn.execute(
            "SELECT buyer_cipher,order_id_cipher,advice_key_cipher FROM bot_conditional_business_advice WHERE shop_key=?",
            (shop_a,),
        ).fetchone()
    assert "private-buyer" not in row["buyer_cipher"]
    assert "private-order" not in row["order_id_cipher"]
    assert "private-advice" not in row["advice_key_cipher"]


def test_server_audit_events_never_store_raw_buyer_or_order_identifiers(advice_api, control_plane):
    shop = "qn_0123456789ab"
    claim(advice_api, 11, shop, buyer="private-buyer", order="private-order", advice="private-advice")
    with control_plane.db() as conn:
        rows = conn.execute(
            "SELECT scope_hash,event_type,outcome FROM bot_conditional_business_advice_events WHERE shop_key=?",
            (shop,),
        ).fetchall()
    serialized = repr([tuple(row) for row in rows])
    assert "private-buyer" not in serialized
    assert "private-order" not in serialized
    assert "private-advice" not in serialized


def test_concurrent_devices_have_exactly_one_global_winner(advice_api):
    from concurrent.futures import ThreadPoolExecutor

    shop = "qn_0123456789ab"
    with ThreadPoolExecutor(max_workers=8) as pool:
        results = list(pool.map(
            lambda index: claim(
                advice_api,
                100 + index,
                shop,
                request_id=("request-race-" + str(index).zfill(8)),
            ),
            range(8),
        ))
    assert sum(1 for item in results if item["claimed"]) == 1
    assert all(item["state"] == "reserved" for item in results)


def test_original_sender_can_renew_expired_lease_but_other_devices_cannot_claim(advice_api, monkeypatch):
    shop = "qn_0123456789ab"
    start = datetime(2026, 11, 26, 8, 0, tzinfo=timezone.utc)
    monkeypatch.setattr(advice_api, "_utc_now", lambda: start)
    first = claim(advice_api, 11, shop)
    monkeypatch.setattr(advice_api, "_utc_now", lambda: start + timedelta(minutes=3))

    renewed = advice_api.runtime_renew(
        advice_api.ConditionalAdviceRenewInput(
            buyer="buyer-1",
            order_id="order-1",
            advice_key="knowledge-1",
            lease_token=first["lease_token"],
            lease_seconds=180,
        ),
        request_for(11),
        x_shop_key=shop,
    )
    assert renewed["renewed"] is True

    blocked = claim(advice_api, 12, shop, request_id="request-other-device-000")
    assert blocked["claimed"] is False
    assert blocked["state"] == "reserved"

    completed = advice_api.runtime_complete(
        advice_api.ConditionalAdviceCompleteInput(
            buyer="buyer-1",
            order_id="order-1",
            advice_key="knowledge-1",
            lease_token=first["lease_token"],
            outcome="delivered",
        ),
        request_for(11),
        x_shop_key=shop,
    )
    assert completed["state"] == "delivered"


def test_manual_resolution_waits_for_expired_lease_grace_period(advice_api, monkeypatch):
    shop = "qn_0123456789ab"
    start = datetime(2026, 11, 26, 8, 0, tzinfo=timezone.utc)
    monkeypatch.setattr(advice_api, "_utc_now", lambda: start)
    claim(advice_api, 11, shop)
    monkeypatch.setattr(advice_api, "_utc_now", lambda: start + timedelta(minutes=3))
    blocked = claim(advice_api, 12, shop, request_id="request-other-device-000")
    assert blocked["state"] == "uncertain"
    pending = advice_api.runtime_pending(request_for(12), x_shop_key=shop)
    scope_hash = pending["items"][0]["scope_hash"]

    with pytest.raises(Exception):
        advice_api.runtime_resolve(
            advice_api.ConditionalAdviceResolveInput(scope_hash=scope_hash, confirmed_delivered=False),
            request_for(12),
            x_shop_key=shop,
        )

    monkeypatch.setattr(advice_api, "_utc_now", lambda: start + timedelta(minutes=5, seconds=1))
    resolved = advice_api.runtime_resolve(
        advice_api.ConditionalAdviceResolveInput(scope_hash=scope_hash, confirmed_delivered=False),
        request_for(12),
        x_shop_key=shop,
    )
    assert resolved["state"] == "released"


def test_http_routes_read_shop_key_from_header(control_plane, advice_api, monkeypatch):
    from fastapi.testclient import TestClient

    shop = "qn_0123456789ab"
    completed_lease = claim(
        advice_api, 11, shop,
        buyer="complete-buyer", order="complete-order", advice="complete-advice",
    )

    start = datetime(2026, 11, 26, 8, 0, tzinfo=timezone.utc)
    monkeypatch.setattr(advice_api, "_utc_now", lambda: start)
    claim(
        advice_api, 11, shop,
        buyer="pending-buyer", order="pending-order", advice="pending-advice",
        request_id="request-pending-000000",
    )

    # Exercise FastAPI's actual parameter extraction. Direct function calls pass
    # x_shop_key explicitly and would not detect a missing Header(...) declaration.
    monkeypatch.setattr(
        advice_api.bot_web_console,
        "_runtime_client",
        staticmethod(lambda request: {"id": int(request.headers.get("x-test-client-id", "11"))}),
    )
    headers = {"X-Shop-Key": shop, "X-Test-Client-Id": "12"}
    base = "/api/runtime/v1/bot-web/conditional-business-advice"

    with TestClient(control_plane.app) as client:
        completed = client.post(
            base + "/complete",
            headers=headers,
            json={
                "buyer": "complete-buyer",
                "order_id": "complete-order",
                "advice_key": "complete-advice",
                "lease_token": completed_lease["lease_token"],
                "outcome": "delivered",
            },
        )
        assert completed.status_code == 200, completed.text
        assert completed.json()["state"] == "delivered"

        monkeypatch.setattr(
            advice_api, "_utc_now", lambda: start + timedelta(minutes=3)
        )
        pending = client.get(base + "/pending", headers=headers)
        assert pending.status_code == 200, pending.text
        item = next(
            item for item in pending.json()["items"]
            if item["buyer"] == "pending-buyer"
        )

        monkeypatch.setattr(
            advice_api, "_utc_now",
            lambda: start + timedelta(minutes=5, seconds=1),
        )
        resolved = client.post(
            base + "/resolve",
            headers=headers,
            json={"scope_hash": item["scope_hash"], "confirmed_delivered": False},
        )
        assert resolved.status_code == 200, resolved.text
        assert resolved.json()["state"] == "released"
