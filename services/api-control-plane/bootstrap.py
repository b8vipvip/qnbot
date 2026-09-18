from __future__ import annotations

import os

import uvicorn

import app as control_plane
import bot_client_shop_binding
import bot_update_cache
import bot_update_prefetch
import bot_update_progress
import bot_update_push
import bot_web_admin
import bot_web_ai_model_settings
import bot_web_auto_reply_rules
import bot_web_bot_enabled
import bot_web_bot_qa
import bot_web_console
import bot_web_conversation_knowledge
import bot_web_settings_ack
import chat2api_runtime_guard
import client_data_backup
import console_cache_guard
import deep_test_guard
import github_vless_proxy
import message_processing_traces
import recharge_status_query
import runtime_embedding_guard
import runtime_ocr
import runtime_ocr_priority
import runtime_routing_guard
import runtime_shop_ai_proxy
import runtime_streaming_guard
import scheduled_deep_test_retry
import store_rule_sync
import version_update_admin
import wecom_bridge
import wecom_policy_migration
import wecom_settings
from wecom_crypto import install_on_bridge


console_cache_guard.install(control_plane)
runtime_routing_guard.install(control_plane)
chat2api_runtime_guard.install(control_plane)
runtime_streaming_guard.install(control_plane)
runtime_embedding_guard.install(control_plane)
runtime_ocr.install(control_plane)
runtime_ocr_priority.install(control_plane)
deep_test_guard.install(control_plane)
scheduled_deep_test_retry.install(control_plane)
wecom_policy_migration.install(control_plane)
install_on_bridge(wecom_bridge)
control_plane.app.include_router(wecom_bridge.router)
control_plane.app.include_router(wecom_settings.router)
control_plane.app.include_router(recharge_status_query.router)
bot_update_progress.install()
github_vless_proxy.install(control_plane)
control_plane.app.include_router(bot_update_cache.router)
control_plane.app.include_router(bot_update_push.router)
bot_web_console.install(control_plane)
bot_web_ai_model_settings.install(control_plane, bot_web_console)
bot_web_auto_reply_rules.install(control_plane, bot_web_console)
bot_web_settings_ack.install(control_plane, bot_web_console)
bot_client_shop_binding.install(control_plane)
bot_web_bot_enabled.install(control_plane)
bot_web_admin.install(control_plane)
version_update_admin.install(control_plane)
bot_web_bot_qa.install(control_plane)
bot_web_conversation_knowledge.install(control_plane)
client_data_backup.install(control_plane)
store_rule_sync.install(control_plane)
runtime_shop_ai_proxy.install(control_plane)
message_processing_traces.install(control_plane)


@control_plane.app.on_event("startup")
def initialize_control_plane_extensions() -> None:
    runtime_ocr.init_db(control_plane)
    runtime_ocr_priority.init_db(control_plane)
    wecom_bridge.init_wecom_db()
    wecom_settings.init_wecom_settings_db()
    recharge_status_query.init_recharge_query_db()
    bot_web_console.init_bot_web_db()
    bot_web_ai_model_settings.init_db()
    bot_web_auto_reply_rules.init_db()
    bot_web_settings_ack.init_db()
    bot_client_shop_binding.init_db()
    bot_web_bot_enabled.init_db()
    bot_web_conversation_knowledge.init_db()
    client_data_backup.init_db()
    store_rule_sync.init_db()
    message_processing_traces.init_db()
    github_vless_proxy.init_github_vless_proxy()
    bot_update_cache.init_bot_update_cache()
    bot_update_prefetch.init_bot_update_prefetch()
    wecom_settings.apply_to_bridge(wecom_bridge)


@control_plane.app.on_event("shutdown")
def shutdown_control_plane_extensions() -> None:
    bot_update_prefetch.stop_bot_update_prefetch()
    bot_update_cache.stop_bot_update_cache()
    github_vless_proxy.stop_github_vless_proxy()


if __name__ == "__main__":
    uvicorn.run(control_plane.app, host="0.0.0.0", port=int(os.getenv("PORT", "8080")))
