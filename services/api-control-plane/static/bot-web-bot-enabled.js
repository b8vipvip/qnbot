(() => {
  let botState = null;
  let refreshing = false;
  let botTimer = null;

  function renderBotControl() {
    const checkbox = $("botEnabled");
    const hint = $("botEnabledHint");
    if (!checkbox || !hint || !botState) return;

    checkbox.checked = botState.desired_enabled !== false;
    if (!botState.online) {
      hint.textContent = botState.pending
        ? "Windows Bot 当前离线，开关会在下次上线后下发。"
        : `Windows Bot 当前离线；上次状态：${botState.current_enabled === false ? "已停用" : "已启用"}。`;
      return;
    }
    if (botState.pending) {
      hint.textContent = "设置已保存，正在等待 Windows Bot 应用。";
      return;
    }
    hint.textContent = `Windows 当前实际状态：${botState.current_enabled === false ? "已停用" : "已启用"}。`;
  }

  function renderBotMetric() {
    const metrics = $("metrics");
    if (!metrics || !botState) return;
    let metric = metrics.querySelector("[data-bot-enabled-metric]");
    if (!metric) {
      metric = document.createElement("div");
      metric.className = "metric";
      metric.dataset.botEnabledMetric = "1";
      metrics.prepend(metric);
    }
    const value = !botState.online && botState.pending
      ? "待下发"
      : (botState.current_enabled === false ? "停用" : "启用");
    metric.innerHTML = `<span>Bot 总开关</span><strong>${esc(value)}</strong>`;
  }

  async function refreshBotEnabled(showError = false) {
    if (refreshing) return;
    refreshing = true;
    try {
      botState = await api("/api/bot-web/bot-enabled");
      renderBotControl();
      renderBotMetric();
    } catch (err) {
      if (showError && err.message !== "登录已失效") toast(err.message);
    } finally {
      refreshing = false;
    }
  }

  function stopBotTimer() {
    if (botTimer) clearInterval(botTimer);
    botTimer = null;
  }

  function startBotTimer() {
    stopBotTimer();
    refreshBotEnabled(false);
    botTimer = setInterval(() => refreshBotEnabled(false), 2500);
  }

  const baseShowLogin = showLogin;
  showLogin = function () {
    stopBotTimer();
    botState = null;
    baseShowLogin();
  };

  const baseShowApp = showApp;
  showApp = function (name) {
    baseShowApp(name);
    startBotTimer();
  };

  const baseRenderSettings = renderSettings;
  renderSettings = function () {
    baseRenderSettings();
    renderBotControl();
  };

  const baseRenderStatus = renderStatus;
  renderStatus = function () {
    baseRenderStatus();
    renderBotMetric();
  };

  const saveButton = $("saveSettingsBtn");
  saveButton.onclick = async () => {
    const data = {
      auto_reply_enabled: $("autoReplyEnabled").checked,
      message_sync_enabled: $("messageSyncEnabled").checked,
      allow_web_manual_reply: $("manualReplyEnabled").checked,
      sync_interval_seconds: Number($("syncInterval").value),
      message_retention_days: Number($("retentionDays").value)
    };
    saveButton.disabled = true;
    try {
      await Promise.all([
        api("/api/bot-web/settings", {
          method: "PUT",
          body: JSON.stringify(data)
        }),
        api("/api/bot-web/bot-enabled", {
          method: "PUT",
          body: JSON.stringify({ enabled: $("botEnabled").checked })
        })
      ]);
      toast("设置已保存并等待 Windows Bot 应用");
      await Promise.all([refreshAll(true), refreshBotEnabled(true)]);
    } catch (err) {
      toast(err.message);
    } finally {
      saveButton.disabled = false;
    }
  };

  if (!$("appView").classList.contains("hidden")) startBotTimer();
})();

(() => {
  if (document.querySelector('script[data-bot-web-auto-reply-rules]')) return;
  const script = document.createElement("script");
  script.src = "/static/bot-web-auto-reply-rules.js?v=2";
  script.dataset.botWebAutoReplyRules = "1";
  document.body.appendChild(script);
})();


(() => {
  if (document.querySelector('script[data-bot-web-ai-model-settings]')) return;
  const script = document.createElement("script");
  script.src = "/static/bot-web-ai-model-settings.js?v=1";
  script.dataset.botWebAiModelSettings = "1";
  document.body.appendChild(script);
})();


(() => {
  if (document.querySelector('script[data-bot-web-store-rules]')) return;
  const script = document.createElement("script");
  script.src = "/static/bot-web-store-rules.js?v=1";
  script.dataset.botWebStoreRules = "1";
  document.body.appendChild(script);
})();

(function(){
const $=id=>document.getElementById(id);
async function api(path,opt={}){const token=(localStorage.getItem("qnb_bot_token")||sessionStorage.getItem("qnb_bot_token")||"");const r=await fetch(path,{...opt,headers:{"Content-Type":"application/json","X-Bot-Token":token,...(opt.headers||{})}});const x=await r.json().catch(()=>({}));if(!r.ok)throw new Error(x.detail||("HTTP "+r.status));return x}
function renderWeCom(x){if(!$("wecomEnabled"))return;$("wecomEnabled").checked=!!x.enabled;$("wecomCorpId").value=x.corp_id||"";$("wecomAgentId").value=x.agent_id||"";$("wecomToUsers").value=x.to_users||"";$("wecomAllowedUsers").value=x.allowed_reply_users||"";$("wecomTicketHours").value=x.ticket_hours||24;$("wecomCallbackUrl").value=x.callback_url||"";$("wecomAppSecret").value="";$("wecomCallbackToken").value="";$("wecomCallbackAesKey").value="";$("wecomAppSecret").placeholder=x.app_secret_configured?"已配置，留空保持原 Secret":"请输入应用 Secret";$("wecomCallbackToken").placeholder=x.callback_token_configured?"已配置，留空保持原 Token":"可生成或填写回调 Token";$("wecomCallbackAesKey").placeholder=x.callback_aes_key_configured?"已配置，留空保持原 AES Key":"可生成或填写 EncodingAESKey";$("wecomStatusBadge").textContent=x.outbound_configured?"通知已就绪":"未完整配置";$("wecomStatusBadge").className="pill "+(x.outbound_configured?"good":"gray");$("wecomHint").textContent=x.callback_configured?"通知与人工回复回调均已配置。":"通知配置状态已读取；如需企业微信内人工回复，请同时配置回调参数。"}
async function loadWeCom(){if(!$("wecomEnabled"))return;try{renderWeCom(await api("/api/bot-web/wecom/settings"))}catch(e){$("wecomHint").textContent="企业微信配置读取失败："+e.message}}
$("saveWecomBtn").onclick=async()=>{if(!confirm("确定保存当前 Bot 客户端的企业微信通知配置吗？"))return;try{const x=await api("/api/bot-web/wecom/settings",{method:"PUT",body:JSON.stringify({enabled:$("wecomEnabled").checked,corp_id:$("wecomCorpId").value,agent_id:$("wecomAgentId").value,to_users:$("wecomToUsers").value,app_secret:$("wecomAppSecret").value,allowed_reply_users:$("wecomAllowedUsers").value,ticket_hours:Number($("wecomTicketHours").value||24),callback_token:$("wecomCallbackToken").value,callback_aes_key:$("wecomCallbackAesKey").value})});renderWeCom(x);$("wecomHint").textContent="当前 Bot 企业微信配置已保存。" }catch(e){$("wecomHint").textContent="保存失败："+e.message}};
$("generateWecomCallbackBtn").onclick=async()=>{if(!confirm("生成新的回调 Token 与 EncodingAESKey？生成后仍需点击保存才会生效。"))return;try{const x=await api("/api/bot-web/wecom/generate-callback",{method:"POST",body:"{}"});$("wecomCallbackToken").value=x.callback_token||"";$("wecomCallbackAesKey").value=x.callback_aes_key||"";$("wecomCallbackUrl").value=x.callback_url||"";$("wecomHint").textContent="已生成回调参数，请复制到企业微信后台并保存本页配置。"}catch(e){$("wecomHint").textContent="生成失败："+e.message}};
$("testWecomBtn").onclick=async()=>{if(!confirm("向当前配置的企业微信接收成员发送一条测试通知？"))return;try{const x=await api("/api/bot-web/wecom/test",{method:"POST",body:"{}"});$("wecomHint").textContent=x.message||"测试通知发送成功"}catch(e){$("wecomHint").textContent="测试失败："+e.message}};
loadWeCom();
})();