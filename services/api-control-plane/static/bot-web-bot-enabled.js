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
