from __future__ import annotations

import base64
import hashlib
import os
import secrets
import sqlite3
from contextlib import contextmanager
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Dict, Iterable, List, Optional, Tuple

from cryptography.fernet import Fernet, InvalidToken
from fastapi import APIRouter, Depends, HTTPException, Request, status
from pydantic import BaseModel, Field


DATA_DIR = Path(os.getenv("DATA_DIR", "/data")).resolve()
DATA_DIR.mkdir(parents=True, exist_ok=True)
DB_PATH = Path(os.getenv("DATABASE_PATH", str(DATA_DIR / "api-control-plane.db"))).resolve()
PUBLIC_BASE_URL = os.getenv("PUBLIC_BASE_URL", "").rstrip("/")
APP_SECRET = os.getenv("APP_SECRET", "change-me-in-production")
router = APIRouter()


def iso_now() -> str:
    return datetime.now(timezone.utc).isoformat(timespec="seconds")


def new_revision() -> str:
    return iso_now() + "-" + secrets.token_hex(4)


def derive_fernet_key() -> bytes:
    explicit = os.getenv("API_KEY_ENCRYPTION_KEY", "").strip()
    if explicit:
        try:
            Fernet(explicit.encode("ascii"))
            return explicit.encode("ascii")
        except Exception as exc:
            raise RuntimeError("API_KEY_ENCRYPTION_KEY 必须是有效的 Fernet key") from exc
    digest = hashlib.sha256(APP_SECRET.encode("utf-8")).digest()
    return base64.urlsafe_b64encode(digest)


FERNET = Fernet(derive_fernet_key())


def encrypt_secret(value: str) -> str:
    value = (value or "").strip()
    return FERNET.encrypt(value.encode("utf-8")).decode("ascii") if value else ""


def decrypt_secret(value: str) -> str:
    value = (value or "").strip()
    if not value:
        return ""
    try:
        return FERNET.decrypt(value.encode("ascii")).decode("utf-8")
    except InvalidToken as exc:
        raise RuntimeError("无法解密企业微信配置，请确认 API_KEY_ENCRYPTION_KEY 未变化") from exc


@contextmanager
def db(path: Optional[Path] = None) -> Iterable[sqlite3.Connection]:
    connection = sqlite3.connect(str(path or DB_PATH), timeout=30, check_same_thread=False)
    connection.row_factory = sqlite3.Row
    connection.execute("PRAGMA journal_mode=WAL")
    try:
        yield connection
        connection.commit()
    except Exception:
        connection.rollback()
        raise
    finally:
        connection.close()


def default_handoff_rules() -> List[Dict[str, Any]]:
    manual = [
        "退款", "退货", "投诉", "差评", "赔偿", "发票", "税票",
        "订单隐私", "身份证", "银行卡", "法律", "维权", "平台介入",
    ]
    confirm = ["手机号", "地址", "隐私", "密码", "验证码", "转账", "补偿", "客服主管"]
    rules: List[Dict[str, Any]] = []
    order = 10
    for keyword in manual:
        rules.append(
            {
                "enabled": True,
                "rule_type": "manual",
                "keyword": keyword,
                "match_mode": "contains",
                "risk_terms": "",
                "exceptions": "",
                "safe_reply": "",
                "note": "命中后转人工，不自动回答具体结论。",
                "sort_order": order,
            }
        )
        order += 10
    for keyword in confirm:
        rules.append(
            {
                "enabled": True,
                "rule_type": "confirm",
                "keyword": keyword,
                "match_mode": "contains",
                "risk_terms": "",
                "exceptions": "",
                "safe_reply": "",
                "note": "命中后仅由人工确认。",
                "sort_order": order,
            }
        )
        order += 10
    rules.append(
        {
            "enabled": True,
            "rule_type": "confirm",
            "keyword": "账号",
            "match_mode": "sensitive_context",
            "risk_terms": "密码|验证码|登录|登陆|找回|被盗|冻结|封禁|绑定|解绑|实名|身份证|泄露|安全|申诉|修改账号|换绑",
            "exceptions": "另一个账号|其他账号|别的账号|朋友账号|好友账号|给朋友|给别人|帮朋友|帮别人|再拍|再买|购买|充值|充到|月卡",
            "safe_reply": "可以的，月卡可以给朋友或其他账号充值，您再拍对应月卡即可；下单后按页面提示提供需要充值的账号。",
            "note": "账号安全问题转人工；给朋友或其他账号购买充值属于正常业务，不转人工。",
            "sort_order": order,
        }
    )
    return rules


def init_wecom_settings_db(path: Optional[Path] = None) -> None:
    with db(path) as conn:
        conn.executescript(
            """
            CREATE TABLE IF NOT EXISTS wecom_settings (
                id INTEGER PRIMARY KEY CHECK(id=1),
                enabled INTEGER NOT NULL DEFAULT 0,
                corp_id TEXT NOT NULL DEFAULT '',
                app_secret_cipher TEXT NOT NULL DEFAULT '',
                agent_id TEXT NOT NULL DEFAULT '',
                to_users TEXT NOT NULL DEFAULT '',
                callback_token_cipher TEXT NOT NULL DEFAULT '',
                callback_aes_key_cipher TEXT NOT NULL DEFAULT '',
                allowed_reply_users TEXT NOT NULL DEFAULT '',
                ticket_hours INTEGER NOT NULL DEFAULT 24,
                updated_at TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS wecom_client_settings (
                client_id INTEGER PRIMARY KEY,
                enabled INTEGER NOT NULL DEFAULT 0,
                corp_id TEXT NOT NULL DEFAULT '',
                app_secret_cipher TEXT NOT NULL DEFAULT '',
                agent_id TEXT NOT NULL DEFAULT '',
                to_users TEXT NOT NULL DEFAULT '',
                callback_token_cipher TEXT NOT NULL DEFAULT '',
                callback_aes_key_cipher TEXT NOT NULL DEFAULT '',
                allowed_reply_users TEXT NOT NULL DEFAULT '',
                ticket_hours INTEGER NOT NULL DEFAULT 24,
                updated_at TEXT NOT NULL,
                FOREIGN KEY(client_id) REFERENCES client_tokens(id)
            );

            CREATE TABLE IF NOT EXISTS wecom_handoff_rules (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                enabled INTEGER NOT NULL DEFAULT 1,
                rule_type TEXT NOT NULL,
                keyword TEXT NOT NULL,
                match_mode TEXT NOT NULL DEFAULT 'contains',
                risk_terms TEXT NOT NULL DEFAULT '',
                exceptions TEXT NOT NULL DEFAULT '',
                safe_reply TEXT NOT NULL DEFAULT '',
                note TEXT NOT NULL DEFAULT '',
                sort_order INTEGER NOT NULL DEFAULT 0,
                updated_at TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS wecom_handoff_rule_meta (
                id INTEGER PRIMARY KEY CHECK(id=1),
                initialized INTEGER NOT NULL DEFAULT 1,
                revision TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );

            CREATE INDEX IF NOT EXISTS idx_wecom_handoff_rules_order
                ON wecom_handoff_rules(enabled, sort_order, id);
            """
        )
        meta = conn.execute("SELECT id FROM wecom_handoff_rule_meta WHERE id=1").fetchone()
        if not meta:
            now = iso_now()
            conn.executemany(
                """
                INSERT INTO wecom_handoff_rules(
                    enabled,rule_type,keyword,match_mode,risk_terms,exceptions,
                    safe_reply,note,sort_order,updated_at
                ) VALUES(?,?,?,?,?,?,?,?,?,?)
                """,
                [
                    (
                        1 if rule["enabled"] else 0,
                        rule["rule_type"],
                        rule["keyword"],
                        rule["match_mode"],
                        rule["risk_terms"],
                        rule["exceptions"],
                        rule["safe_reply"],
                        rule["note"],
                        rule["sort_order"],
                        now,
                    )
                    for rule in default_handoff_rules()
                ],
            )
            conn.execute(
                "INSERT INTO wecom_handoff_rule_meta(id,initialized,revision,updated_at) VALUES(1,1,?,?)",
                (new_revision(), now),
            )


def split_users(value: str) -> Tuple[str, ...]:
    output = []
    for item in (value or "").replace(",", "|").replace("，", "|").replace(";", "|").replace("；", "|").split("|"):
        item = item.strip()
        if item and item not in output:
            output.append(item)
    return tuple(output)


def default_settings() -> Dict[str, Any]:
    return {
        "exists": False,
        "enabled": False,
        "corp_id": "",
        "app_secret": "",
        "agent_id": "",
        "to_users": "",
        "callback_token": "",
        "callback_aes_key": "",
        "allowed_reply_users": "",
        "ticket_hours": 24,
        "updated_at": None,
    }


def load_settings(path: Optional[Path] = None) -> Dict[str, Any]:
    init_wecom_settings_db(path)
    with db(path) as conn:
        row = conn.execute("SELECT * FROM wecom_settings WHERE id=1").fetchone()
    if not row:
        return default_settings()
    return {
        "exists": True,
        "enabled": bool(row["enabled"]),
        "corp_id": str(row["corp_id"] or "").strip(),
        "app_secret": decrypt_secret(row["app_secret_cipher"]),
        "agent_id": str(row["agent_id"] or "").strip(),
        "to_users": str(row["to_users"] or "").strip(),
        "callback_token": decrypt_secret(row["callback_token_cipher"]),
        "callback_aes_key": decrypt_secret(row["callback_aes_key_cipher"]),
        "allowed_reply_users": str(row["allowed_reply_users"] or "").strip(),
        "ticket_hours": max(1, min(168, int(row["ticket_hours"] or 24))),
        "updated_at": row["updated_at"],
    }


def load_handoff_rules(path: Optional[Path] = None) -> Dict[str, Any]:
    init_wecom_settings_db(path)
    with db(path) as conn:
        rows = conn.execute(
            "SELECT * FROM wecom_handoff_rules ORDER BY sort_order ASC,id ASC"
        ).fetchall()
        meta = conn.execute(
            "SELECT revision,updated_at FROM wecom_handoff_rule_meta WHERE id=1"
        ).fetchone()
    return {
        "revision": str(meta["revision"] if meta else ""),
        "updated_at": meta["updated_at"] if meta else None,
        "rules": [
            {
                "id": int(row["id"]),
                "enabled": bool(row["enabled"]),
                "rule_type": str(row["rule_type"] or "confirm"),
                "keyword": str(row["keyword"] or ""),
                "match_mode": str(row["match_mode"] or "contains"),
                "risk_terms": str(row["risk_terms"] or ""),
                "exceptions": str(row["exceptions"] or ""),
                "safe_reply": str(row["safe_reply"] or ""),
                "note": str(row["note"] or ""),
                "sort_order": int(row["sort_order"] or 0),
                "updated_at": row["updated_at"],
            }
            for row in rows
        ],
    }


def validate_aes_key(value: str) -> bool:
    value = (value or "").strip()
    if len(value) != 43:
        return False
    try:
        return len(base64.b64decode(value + "=")) == 32
    except Exception:
        return False


def callback_url() -> str:
    return (PUBLIC_BASE_URL + "/api/wecom/callback") if PUBLIC_BASE_URL else "/api/wecom/callback"


def public_settings(settings: Dict[str, Any]) -> Dict[str, Any]:
    recipients = split_users(settings.get("to_users", ""))
    allowed = split_users(settings.get("allowed_reply_users", "")) or recipients
    outbound = bool(
        settings.get("enabled")
        and settings.get("corp_id")
        and settings.get("app_secret")
        and str(settings.get("agent_id") or "").isdigit()
        and recipients
    )
    callback = bool(
        outbound
        and settings.get("callback_token")
        and validate_aes_key(str(settings.get("callback_aes_key") or ""))
    )
    return {
        "enabled": bool(settings.get("enabled")),
        "corp_id": settings.get("corp_id", ""),
        "app_secret_configured": bool(settings.get("app_secret")),
        "agent_id": settings.get("agent_id", ""),
        "to_users": "|".join(recipients),
        "callback_token_configured": bool(settings.get("callback_token")),
        "callback_aes_key_configured": validate_aes_key(str(settings.get("callback_aes_key") or "")),
        "allowed_reply_users": "|".join(split_users(str(settings.get("allowed_reply_users") or ""))),
        "effective_allowed_reply_users": "|".join(allowed),
        "ticket_hours": int(settings.get("ticket_hours") or 24),
        "callback_url": callback_url(),
        "outbound_configured": outbound,
        "callback_configured": callback,
        "updated_at": settings.get("updated_at"),
    }


def require_admin(request: Request) -> str:
    username = request.session.get("admin_username")
    if not username:
        raise HTTPException(status_code=401, detail="请先登录控制面")
    return str(username)


def bearer_token(request: Request) -> str:
    header = request.headers.get("authorization", "")
    if not header.lower().startswith("bearer "):
        return ""
    return header.split(" ", 1)[1].strip()


def require_runtime_client(request: Request) -> Dict[str, Any]:
    token = bearer_token(request)
    if not token:
        raise HTTPException(status_code=status.HTTP_401_UNAUTHORIZED, detail="客户端令牌无效")
    token_hash = hashlib.sha256(token.encode("utf-8")).hexdigest()
    with db() as conn:
        row = conn.execute(
            "SELECT * FROM client_tokens WHERE token_hash=? AND enabled=1",
            (token_hash,),
        ).fetchone()
        if not row:
            raise HTTPException(status_code=status.HTTP_401_UNAUTHORIZED, detail="客户端令牌无效")
        conn.execute("UPDATE client_tokens SET last_used_at=? WHERE id=?", (iso_now(), row["id"]))
    return dict(row)


class WeComSettingsInput(BaseModel):
    enabled: bool = False
    corp_id: str = Field(default="", max_length=128)
    app_secret: str = Field(default="", max_length=512)
    agent_id: str = Field(default="", max_length=32)
    to_users: str = Field(default="", max_length=2000)
    callback_token: str = Field(default="", max_length=512)
    callback_aes_key: str = Field(default="", max_length=128)
    allowed_reply_users: str = Field(default="", max_length=2000)
    ticket_hours: int = Field(default=24, ge=1, le=168)
    clear_app_secret: bool = False
    clear_callback_token: bool = False
    clear_callback_aes_key: bool = False


class HandoffRuleInput(BaseModel):
    id: int = 0
    enabled: bool = True
    rule_type: str = Field(default="confirm", max_length=32)
    keyword: str = Field(default="", max_length=120)
    match_mode: str = Field(default="contains", max_length=32)
    risk_terms: str = Field(default="", max_length=3000)
    exceptions: str = Field(default="", max_length=3000)
    safe_reply: str = Field(default="", max_length=1200)
    note: str = Field(default="", max_length=1000)
    sort_order: int = Field(default=0, ge=0, le=100000)


class HandoffRuleSetInput(BaseModel):
    rules: List[HandoffRuleInput] = Field(default_factory=list)


def save_settings(data: WeComSettingsInput, path: Optional[Path] = None) -> Dict[str, Any]:
    existing = load_settings(path)
    app_secret = "" if data.clear_app_secret else ((data.app_secret or "").strip() or existing.get("app_secret", ""))
    callback_token = "" if data.clear_callback_token else ((data.callback_token or "").strip() or existing.get("callback_token", ""))
    callback_aes_key = "" if data.clear_callback_aes_key else ((data.callback_aes_key or "").strip() or existing.get("callback_aes_key", ""))
    corp_id = (data.corp_id or "").strip()
    agent_id = (data.agent_id or "").strip()
    recipients = split_users(data.to_users)
    allowed = split_users(data.allowed_reply_users) or recipients

    if data.enabled:
        if not corp_id:
            raise HTTPException(status_code=400, detail="CorpID 不能为空")
        if not app_secret:
            raise HTTPException(status_code=400, detail="应用 Secret 不能为空")
        if not agent_id.isdigit():
            raise HTTPException(status_code=400, detail="AgentId 必须是数字")
        if not recipients:
            raise HTTPException(status_code=400, detail="至少填写一个接收成员 UserID")
    if bool(callback_token) != bool(callback_aes_key):
        raise HTTPException(status_code=400, detail="回调 Token 和 EncodingAESKey 必须同时配置")
    if callback_aes_key and not validate_aes_key(callback_aes_key):
        raise HTTPException(status_code=400, detail="EncodingAESKey 必须是可解码为32字节的43位值")

    now = iso_now()
    init_wecom_settings_db(path)
    with db(path) as conn:
        conn.execute(
            """
            INSERT INTO wecom_settings(
                id,enabled,corp_id,app_secret_cipher,agent_id,to_users,
                callback_token_cipher,callback_aes_key_cipher,
                allowed_reply_users,ticket_hours,updated_at
            ) VALUES(1,?,?,?,?,?,?,?,?,?,?)
            ON CONFLICT(id) DO UPDATE SET
                enabled=excluded.enabled,
                corp_id=excluded.corp_id,
                app_secret_cipher=excluded.app_secret_cipher,
                agent_id=excluded.agent_id,
                to_users=excluded.to_users,
                callback_token_cipher=excluded.callback_token_cipher,
                callback_aes_key_cipher=excluded.callback_aes_key_cipher,
                allowed_reply_users=excluded.allowed_reply_users,
                ticket_hours=excluded.ticket_hours,
                updated_at=excluded.updated_at
            """,
            (
                1 if data.enabled else 0,
                corp_id,
                encrypt_secret(app_secret),
                agent_id,
                "|".join(recipients),
                encrypt_secret(callback_token),
                encrypt_secret(callback_aes_key),
                "|".join(allowed),
                max(1, min(168, int(data.ticket_hours))),
                now,
            ),
        )
    return load_settings(path)


def save_handoff_rules(data: HandoffRuleSetInput, path: Optional[Path] = None) -> Dict[str, Any]:
    if len(data.rules) > 300:
        raise HTTPException(status_code=400, detail="转人工规则最多300条")

    normalized: List[Dict[str, Any]] = []
    seen = set()
    for index, item in enumerate(data.rules):
        keyword = (item.keyword or "").strip()
        if not keyword:
            raise HTTPException(status_code=400, detail=f"第 {index + 1} 条规则关键词不能为空")
        key = keyword.casefold()
        if key in seen:
            raise HTTPException(status_code=400, detail="关键词不能重复：" + keyword)
        seen.add(key)
        rule_type = (item.rule_type or "confirm").strip().lower()
        if rule_type not in {"manual", "confirm"}:
            raise HTTPException(status_code=400, detail="规则类型必须是 manual 或 confirm")
        match_mode = (item.match_mode or "contains").strip().lower()
        if match_mode not in {"contains", "sensitive_context"}:
            raise HTTPException(status_code=400, detail="匹配方式必须是 contains 或 sensitive_context")
        normalized.append(
            {
                "enabled": bool(item.enabled),
                "rule_type": rule_type,
                "keyword": keyword,
                "match_mode": match_mode,
                "risk_terms": (item.risk_terms or "").strip(),
                "exceptions": (item.exceptions or "").strip(),
                "safe_reply": (item.safe_reply or "").strip(),
                "note": (item.note or "").strip(),
                "sort_order": int(item.sort_order or ((index + 1) * 10)),
            }
        )

    now = iso_now()
    revision = new_revision()
    init_wecom_settings_db(path)
    with db(path) as conn:
        conn.execute("DELETE FROM wecom_handoff_rules")
        if normalized:
            conn.executemany(
                """
                INSERT INTO wecom_handoff_rules(
                    enabled,rule_type,keyword,match_mode,risk_terms,exceptions,
                    safe_reply,note,sort_order,updated_at
                ) VALUES(?,?,?,?,?,?,?,?,?,?)
                """,
                [
                    (
                        1 if rule["enabled"] else 0,
                        rule["rule_type"],
                        rule["keyword"],
                        rule["match_mode"],
                        rule["risk_terms"],
                        rule["exceptions"],
                        rule["safe_reply"],
                        rule["note"],
                        rule["sort_order"],
                        now,
                    )
                    for rule in normalized
                ],
            )
        conn.execute(
            """
            INSERT INTO wecom_handoff_rule_meta(id,initialized,revision,updated_at)
            VALUES(1,1,?,?)
            ON CONFLICT(id) DO UPDATE SET
                initialized=1,revision=excluded.revision,updated_at=excluded.updated_at
            """,
            (revision, now),
        )
    return load_handoff_rules(path)


def apply_to_bridge(bridge: Any) -> Dict[str, Any]:
    settings = load_settings()
    bridge.WECOM_ENABLED = bool(settings.get("enabled"))
    bridge.WECOM_CORP_ID = str(settings.get("corp_id") or "")
    bridge.WECOM_APP_SECRET = str(settings.get("app_secret") or "")
    bridge.WECOM_AGENT_ID = str(settings.get("agent_id") or "")
    bridge.WECOM_TO_USERS = str(settings.get("to_users") or "")
    bridge.WECOM_CALLBACK_TOKEN = str(settings.get("callback_token") or "")
    bridge.WECOM_CALLBACK_AES_KEY = str(settings.get("callback_aes_key") or "")
    bridge.WECOM_ALLOWED_REPLY_USERS = str(settings.get("allowed_reply_users") or "")
    bridge.WECOM_TICKET_HOURS = max(1, min(168, int(settings.get("ticket_hours") or 24)))
    if hasattr(bridge, "TOKEN_LOCK") and hasattr(bridge, "TOKEN_CACHE"):
        with bridge.TOKEN_LOCK:
            bridge.TOKEN_CACHE.clear()
            bridge.TOKEN_CACHE.update({"value": "", "expires_at": 0.0})
    return settings


@router.get("/api/admin/wecom/settings")
def admin_get_wecom_settings(_: str = Depends(require_admin)) -> Dict[str, Any]:
    return public_settings(load_settings())


@router.put("/api/admin/wecom/settings")
def admin_save_wecom_settings(data: WeComSettingsInput, _: str = Depends(require_admin)) -> Dict[str, Any]:
    saved = save_settings(data)
    try:
        import wecom_bridge
        apply_to_bridge(wecom_bridge)
    except Exception:
        pass
    return public_settings(saved)


@router.get("/api/admin/wecom/handoff-rules")
def admin_get_handoff_rules(_: str = Depends(require_admin)) -> Dict[str, Any]:
    return load_handoff_rules()


@router.put("/api/admin/wecom/handoff-rules")
def admin_save_handoff_rules(
    data: HandoffRuleSetInput,
    _: str = Depends(require_admin),
) -> Dict[str, Any]:
    return save_handoff_rules(data)


@router.get("/api/runtime/v1/handoff/rules")
def runtime_get_handoff_rules(
    client: Dict[str, Any] = Depends(require_runtime_client),
) -> Dict[str, Any]:
    result = load_handoff_rules()
    result["client"] = client.get("name", "")
    return result


@router.post("/api/admin/wecom/generate-callback")
def admin_generate_wecom_callback(_: str = Depends(require_admin)) -> Dict[str, str]:
    return {
        "callback_token": secrets.token_urlsafe(24),
        "callback_aes_key": base64.b64encode(secrets.token_bytes(32)).decode("ascii").rstrip("="),
    }


@router.post("/api/admin/wecom/test")
def admin_test_wecom(_: str = Depends(require_admin)) -> Dict[str, Any]:
    import wecom_bridge

    settings = load_settings()
    if not public_settings(settings)["outbound_configured"]:
        raise HTTPException(status_code=400, detail="请先保存完整的企业微信应用消息配置")
    content = (
        "【千牛 AI 控制面测试】\n"
        "企业微信应用消息配置已生效。\n"
        "回调地址：" + callback_url() + "\n"
        "人工回复格式：QN-XXXXXXXX 回复内容"
    )
    try:
        result = wecom_bridge.send_app_text(wecom_bridge.configured_recipients(), content)
    except Exception as exc:
        raise HTTPException(status_code=502, detail=str(exc)[:500]) from exc
    return {"ok": True, "msgid": result.get("msgid"), "message": "测试消息发送成功"}


def load_client_settings(client_id: int, path: Optional[Path] = None) -> Dict[str, Any]:
    init_wecom_settings_db(path)
    with db(path) as conn:
        row=conn.execute("SELECT * FROM wecom_client_settings WHERE client_id=?",(int(client_id),)).fetchone()
    if not row:
        return default_settings()
    return {
        "exists":True,"enabled":bool(row["enabled"]),"corp_id":str(row["corp_id"] or "").strip(),
        "app_secret":decrypt_secret(row["app_secret_cipher"]),"agent_id":str(row["agent_id"] or "").strip(),
        "to_users":str(row["to_users"] or "").strip(),"callback_token":decrypt_secret(row["callback_token_cipher"]),
        "callback_aes_key":decrypt_secret(row["callback_aes_key_cipher"]),
        "allowed_reply_users":str(row["allowed_reply_users"] or "").strip(),
        "ticket_hours":max(1,min(168,int(row["ticket_hours"] or 24))),"updated_at":row["updated_at"],
    }

def client_callback_url(client_id: int) -> str:
    path="/api/wecom/callback/"+str(int(client_id))
    return (PUBLIC_BASE_URL+path) if PUBLIC_BASE_URL else path

def public_client_settings(client_id: int, settings: Dict[str, Any]) -> Dict[str, Any]:
    result=public_settings(settings)
    result["callback_url"]=client_callback_url(client_id)
    return result

def save_client_settings(client_id: int, data: WeComSettingsInput, path: Optional[Path] = None) -> Dict[str, Any]:
    existing=load_client_settings(client_id,path)
    app_secret="" if data.clear_app_secret else ((data.app_secret or "").strip() or existing.get("app_secret",""))
    callback_token="" if data.clear_callback_token else ((data.callback_token or "").strip() or existing.get("callback_token",""))
    callback_aes_key="" if data.clear_callback_aes_key else ((data.callback_aes_key or "").strip() or existing.get("callback_aes_key",""))
    corp_id=(data.corp_id or "").strip(); agent_id=(data.agent_id or "").strip()
    recipients=split_users(data.to_users); allowed=split_users(data.allowed_reply_users) or recipients
    if data.enabled:
        if not corp_id: raise HTTPException(status_code=400,detail="CorpID 不能为空")
        if not app_secret: raise HTTPException(status_code=400,detail="应用 Secret 不能为空")
        if not agent_id.isdigit(): raise HTTPException(status_code=400,detail="AgentId 必须是数字")
        if not recipients: raise HTTPException(status_code=400,detail="至少填写一个接收成员 UserID")
    if bool(callback_token)!=bool(callback_aes_key):
        raise HTTPException(status_code=400,detail="回调 Token 和 EncodingAESKey 必须同时配置")
    if callback_aes_key and not validate_aes_key(callback_aes_key):
        raise HTTPException(status_code=400,detail="EncodingAESKey 必须是可解码为32字节的43位值")
    now=iso_now(); init_wecom_settings_db(path)
    with db(path) as conn:
        conn.execute("""INSERT INTO wecom_client_settings(client_id,enabled,corp_id,app_secret_cipher,agent_id,to_users,
          callback_token_cipher,callback_aes_key_cipher,allowed_reply_users,ticket_hours,updated_at)
          VALUES(?,?,?,?,?,?,?,?,?,?,?)
          ON CONFLICT(client_id) DO UPDATE SET enabled=excluded.enabled,corp_id=excluded.corp_id,
          app_secret_cipher=excluded.app_secret_cipher,agent_id=excluded.agent_id,to_users=excluded.to_users,
          callback_token_cipher=excluded.callback_token_cipher,callback_aes_key_cipher=excluded.callback_aes_key_cipher,
          allowed_reply_users=excluded.allowed_reply_users,ticket_hours=excluded.ticket_hours,updated_at=excluded.updated_at""",
          (int(client_id),1 if data.enabled else 0,corp_id,encrypt_secret(app_secret),agent_id,"|".join(recipients),
           encrypt_secret(callback_token),encrypt_secret(callback_aes_key),"|".join(allowed),
           max(1,min(168,int(data.ticket_hours))),now))
    return load_client_settings(client_id,path)

def require_bot_web_client(request: Request) -> Dict[str, Any]:
    token=request.headers.get("x-bot-token","").strip() or bearer_token(request)
    if not token:
        raise HTTPException(status_code=401,detail="客户端令牌无效")
    with db() as conn:
        row=conn.execute("SELECT * FROM client_tokens WHERE token_hash=? AND enabled=1",(hashlib.sha256(token.encode("utf-8")).hexdigest(),)).fetchone()
    if not row: raise HTTPException(status_code=401,detail="客户端令牌无效")
    return dict(row)

@router.get("/api/bot-web/wecom/settings")
def bot_web_get_wecom_settings(client: Dict[str, Any]=Depends(require_bot_web_client)) -> Dict[str, Any]:
    return public_client_settings(int(client["id"]),load_client_settings(int(client["id"])))

@router.put("/api/bot-web/wecom/settings")
def bot_web_save_wecom_settings(data: WeComSettingsInput, client: Dict[str, Any]=Depends(require_bot_web_client)) -> Dict[str, Any]:
    return public_client_settings(int(client["id"]),save_client_settings(int(client["id"]),data))

@router.post("/api/bot-web/wecom/generate-callback")
def bot_web_generate_wecom_callback(client: Dict[str, Any]=Depends(require_bot_web_client)) -> Dict[str,str]:
    return {"callback_token":secrets.token_urlsafe(24),
      "callback_aes_key":base64.b64encode(secrets.token_bytes(32)).decode("ascii").rstrip("="),
      "callback_url":client_callback_url(int(client["id"]))}
