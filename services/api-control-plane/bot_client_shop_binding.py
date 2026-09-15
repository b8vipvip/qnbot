from __future__ import annotations

import json
from typing import Any, Dict, List

from fastapi import APIRouter, HTTPException, Request
from fastapi.responses import JSONResponse
from pydantic import BaseModel, Field

import bot_web_console as core


router = APIRouter()
_cp: Any = None
CLAIM_PATH = "/api/runtime/v1/shop-binding/claim"


class ShopBindingClaimInput(BaseModel):
    force: bool = False
    seller: str = Field(default="", max_length=160)


def install(control_plane: Any) -> None:
    global _cp
    _cp = control_plane
    control_plane.app.include_router(router)

    @control_plane.app.middleware("http")
    async def enforce_runtime_shop_binding(request: Request, call_next):
        path = request.url.path
        if path.startswith("/api/runtime/v1/") and path != CLAIM_PATH:
            shop_key = (request.headers.get("x-shop-key") or "").strip()
            token = core._bearer(request)
            if shop_key and token:
                client = core._client_by_token(token, capture_cipher=True)
                if client:
                    try:
                        ensure_binding(int(client["id"]), shop_key, False, "")
                    except HTTPException as exc:
                        return JSONResponse(status_code=exc.status_code, content={"detail": exc.detail})
        return await call_next(request)


def init_db() -> None:
    with _cp.db() as conn:
        conn.executescript(
            """
            CREATE TABLE IF NOT EXISTS bot_client_shop_binding (
                client_id INTEGER PRIMARY KEY,
                shop_key TEXT NOT NULL,
                seller TEXT NOT NULL DEFAULT '',
                bound_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                FOREIGN KEY(client_id) REFERENCES client_tokens(id) ON DELETE CASCADE
            );
            """
        )


def _legacy_bound_shop_key(conn, client_id: int) -> str:
    try:
        row = conn.execute(
            "SELECT shop_key FROM bot_client_bot_enabled WHERE client_id=?",
            (client_id,),
        ).fetchone()
        return ((row["shop_key"] if row else "") or "").strip()
    except Exception:
        return ""


def _binding_row(conn, client_id: int):
    row = conn.execute(
        "SELECT shop_key,seller,bound_at,updated_at FROM bot_client_shop_binding WHERE client_id=?",
        (client_id,),
    ).fetchone()
    if row:
        return row

    legacy = _legacy_bound_shop_key(conn, client_id)
    if not legacy:
        return None

    now = core._now()
    conn.execute(
        """
        INSERT OR IGNORE INTO bot_client_shop_binding(client_id,shop_key,seller,bound_at,updated_at)
        VALUES(?,?, '', ?, ?)
        """,
        (client_id, legacy, now, now),
    )
    return conn.execute(
        "SELECT shop_key,seller,bound_at,updated_at FROM bot_client_shop_binding WHERE client_id=?",
        (client_id,),
    ).fetchone()


def _conflict_detail(bound_shop_key: str) -> Dict[str, Any]:
    return {
        "code": "token_bound_to_other_shop",
        "message": "该 Bot 客户端令牌已绑定其他店铺",
        "bound_shop_key": bound_shop_key,
    }


def _known_runtime_sellers(conn, client_id: int) -> List[str]:
    try:
        row = conn.execute(
            "SELECT seller_nicks_json FROM bot_client_state WHERE client_id=?",
            (client_id,),
        ).fetchone()
        values = json.loads((row["seller_nicks_json"] if row else "") or "[]")
        if not isinstance(values, list):
            return []
        result: List[str] = []
        for value in values:
            seller = str(value or "").strip()
            if seller and seller not in result:
                result.append(seller)
        return result[:8]
    except Exception:
        return []


def _seller_identity_matches(actual: str, expected: str) -> bool:
    actual = (actual or "").strip().casefold()
    expected = (expected or "").strip().casefold()
    if not actual or not expected:
        return False
    if actual == expected:
        return True
    # Console rows may use a short customer-service display name while Qianniu reports
    # account-prefix:name. Treat only an explicit suffix separator as the same identity.
    return actual.endswith(":" + expected) or actual.endswith("：" + expected) \
        or expected.endswith(":" + actual) or expected.endswith("：" + actual)


def _validate_runtime_seller_identity(conn, client_id: int, seller: str) -> None:
    seller = (seller or "").strip()
    if not seller:
        return
    known = _known_runtime_sellers(conn, client_id)
    if not known:
        return
    if any(_seller_identity_matches(seller, expected) for expected in known):
        return
    raise HTTPException(
        status_code=409,
        detail={
            "code": "token_seller_mismatch",
            "message": "该 Bot 客户端令牌最近属于其他千牛客服，拒绝绑定到当前错误店铺",
            "bound_shop_key": "",
            "expected_sellers": known,
        },
    )


def _delete_if_table_exists(conn, table: str, client_id: int) -> None:
    try:
        conn.execute(f"DELETE FROM {table} WHERE client_id=?", (client_id,))
    except Exception:
        pass


def _reset_old_shop_server_state(conn, client_id: int) -> None:
    # A force rebind must never expose the old shop's cloud/runtime state to the
    # new shop. These tables are all client-token scoped in the historical schema.
    for table in (
        "bot_messages",
        "bot_commands",
        "bot_client_state",
        "bot_conversation_reads",
        "bot_knowledge_state",
        "bot_store_rule_state",
        "bot_message_processing_traces",
        "bot_client_bot_enabled",
        "bot_client_data_backups",
    ):
        _delete_if_table_exists(conn, table, client_id)


def ensure_binding(
    client_id: int,
    shop_key: str,
    force: bool = False,
    seller: str = "",
) -> Dict[str, Any]:
    shop_key = (shop_key or "").strip()
    seller = (seller or "").strip()[:160]
    if not shop_key:
        raise HTTPException(status_code=400, detail="缺少 X-Shop-Key")

    now = core._now()
    with _cp.db() as conn:
        # Seller identity is an independent safety dimension from ShopKey. A stale Windows
        # active-shop pointer must never be able to move a known token to another customer
        # service account, even when the user confirms a force rebind prompt.
        _validate_runtime_seller_identity(conn, client_id, seller)

        row = _binding_row(conn, client_id)
        bound = ((row["shop_key"] if row else "") or "").strip()
        if bound and bound != shop_key and not force:
            raise HTTPException(status_code=409, detail=_conflict_detail(bound))

        rebound = bool(bound and bound != shop_key)
        if rebound:
            _reset_old_shop_server_state(conn, client_id)

        if row is None:
            conn.execute(
                """
                INSERT INTO bot_client_shop_binding(client_id,shop_key,seller,bound_at,updated_at)
                VALUES(?,?,?,?,?)
                """,
                (client_id, shop_key, seller, now, now),
            )
        else:
            conn.execute(
                """
                UPDATE bot_client_shop_binding
                SET shop_key=?,seller=?,updated_at=?
                WHERE client_id=?
                """,
                (shop_key, seller, now, client_id),
            )

    return {
        "ok": True,
        "shop_key": shop_key,
        "rebound": rebound,
        "server_state_reset": rebound,
    }


@router.post(CLAIM_PATH)
def runtime_claim_shop_binding(data: ShopBindingClaimInput, request: Request) -> Dict[str, Any]:
    client = core._runtime_client(request)
    shop_key = (request.headers.get("x-shop-key") or "").strip()
    return ensure_binding(
        int(client["id"]),
        shop_key,
        bool(data.force),
        data.seller,
    )
