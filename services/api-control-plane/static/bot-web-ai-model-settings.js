(() => {
  let aiState = null;
  let aiRefreshing = false;
  let aiTimer = null;

  function currentById() {
    const map = new Map();
    const endpoints = aiState && aiState.current && Array.isArray(aiState.current.endpoints)
      ? aiState.current.endpoints
      : [];
    endpoints.forEach(item => map.set(item.id, item));
    return map;
  }

  function syncHint() {
    if (!aiState || !aiState.initialized) return "等待 Windows Bot 首次同步 AI 模型设置。";
    if (aiState.last_error) return "应用失败：" + aiState.last_error;
    if (Number(aiState.applied_revision || 0) < Number(aiState.revision || 0)) {
      return aiState.online
        ? "已保存，等待 Windows Bot 应用。"
        : "已保存；Windows Bot 当前离线，将在上线后应用。";
    }
    return "Windows Bot 已应用当前 AI 模型设置。";
  }

  function renderAiSettings() {
    const list = $("aiEndpointList");
    const hint = $("aiModelSettingsHint");
    const save = $("saveAiModelSettingsBtn");
    if (!list || !hint || !save) return;

    hint.textContent = syncHint();
    if (!aiState || !aiState.initialized) {
      list.innerHTML = '<div class="empty">等待 Windows Bot 上报现有 AI 接口。Web 不会创建接口，也不会读取 BaseUrl / API Key。</div>';
      save.disabled = true;
      return;
    }

    const desired = aiState.desired && Array.isArray(aiState.desired.endpoints)
      ? aiState.desired.endpoints
      : [];
    const current = currentById();
    save.disabled = desired.length < 1;
    if (desired.length < 1) {
      list.innerHTML = '<div class="empty">Windows Bot 当前没有可管理的 AI 接口。</div>';
      return;
    }

    list.innerHTML = desired.map((item, index) => {
      const actual = current.get(item.id) || {};
      const status = actual.last_status || "未测试";
      const latency = Number(actual.last_latency_ms || 0);
      const tested = actual.last_test_time || "";
      const statusText = status
        + (latency > 0 ? " · " + latency + "ms" : "")
        + (tested ? " · " + tested : "");
      return '<div class="ai-endpoint-card" data-ai-endpoint="' + esc(item.id) + '">'
        + '<div class="panel-head"><h4>' + esc(item.name || ("AI接口 " + (index + 1))) + '</h4><span class="pill gray">#' + esc(String(item.priority || index + 1)) + '</span></div>'
        + '<label class="switch-row"><span><strong>启用接口</strong><small>只控制此 Windows 本地接口是否参与调用。</small></span><input data-ai-field="enabled" type="checkbox" ' + (item.enabled ? "checked" : "") + '></label>'
        + '<label class="field-row"><span>接口名称</span><input data-ai-field="name" maxlength="120" value="' + esc(item.name || "") + '"></label>'
        + '<label class="field-row"><span>文本模型</span><input data-ai-field="text_model" maxlength="200" value="' + esc(item.text_model || "") + '"></label>'
        + '<label class="switch-row"><span><strong>启用图片视觉理解</strong><small>视觉请求仍使用 Windows 本机保存的接口地址和密钥。</small></span><input data-ai-field="supports_vision" type="checkbox" ' + (item.supports_vision ? "checked" : "") + '></label>'
        + '<label class="field-row"><span>视觉模型</span><input data-ai-field="vision_model" maxlength="200" value="' + esc(item.vision_model || "") + '"></label>'
        + '<label class="field-row"><span>文本超时（秒）</span><input data-ai-field="timeout_seconds" type="number" min="5" max="300" value="' + esc(String(item.timeout_seconds || 35)) + '"></label>'
        + '<label class="field-row"><span>重试次数</span><input data-ai-field="retry_count" type="number" min="0" max="10" value="' + esc(String(item.retry_count || 0)) + '"></label>'
        + '<label class="field-row"><span>视觉超时（秒）</span><input data-ai-field="vision_timeout_seconds" type="number" min="10" max="180" value="' + esc(String(item.vision_timeout_seconds || 45)) + '"></label>'
        + '<label class="field-row"><span>最大图片（MB）</span><input data-ai-field="max_image_size_mb" type="number" min="1" max="20" value="' + esc(String(item.max_image_size_mb || 5)) + '"></label>'
        + '<label class="field-row"><span>优先级</span><input data-ai-field="priority" type="number" min="1" max="1000" value="' + esc(String(item.priority || 1)) + '"></label>'
        + '<label class="field-row"><span>权重</span><input data-ai-field="weight" type="number" min="1" max="100" value="' + esc(String(item.weight || 1)) + '"></label>'
        + '<label class="ai-prompt-field"><span>系统提示词</span><textarea data-ai-field="system_prompt" rows="6" maxlength="12000">' + esc(item.system_prompt || "") + '</textarea></label>'
        + '<div class="hint">Windows 连接测试：' + esc(statusText) + '</div>'
        + '</div>';
    }).join("");
  }

  function collectAiSettings() {
    return Array.from(document.querySelectorAll("[data-ai-endpoint]")).map(card => {
      const field = name => card.querySelector('[data-ai-field="' + name + '"]');
      return {
        id: card.dataset.aiEndpoint,
        name: field("name").value.trim(),
        enabled: field("enabled").checked,
        text_model: field("text_model").value.trim(),
        vision_model: field("vision_model").value.trim(),
        supports_vision: field("supports_vision").checked,
        max_image_size_mb: Number(field("max_image_size_mb").value),
        vision_timeout_seconds: Number(field("vision_timeout_seconds").value),
        system_prompt: field("system_prompt").value,
        priority: Number(field("priority").value),
        weight: Number(field("weight").value),
        timeout_seconds: Number(field("timeout_seconds").value),
        retry_count: Number(field("retry_count").value)
      };
    });
  }

  async function refreshAiSettings(showError = false) {
    if (aiRefreshing) return;
    aiRefreshing = true;
    try {
      aiState = await api("/api/bot-web/ai-model-settings");
      renderAiSettings();
    } catch (err) {
      if (showError && err.message !== "登录已失效") toast(err.message);
    } finally {
      aiRefreshing = false;
    }
  }

  async function saveAiSettings() {
    const button = $("saveAiModelSettingsBtn");
    if (!button) return;
    button.disabled = true;
    try {
      aiState = await api("/api/bot-web/ai-model-settings", {
        method: "PUT",
        body: JSON.stringify({ endpoints: collectAiSettings() })
      });
      renderAiSettings();
      toast("AI 模型设置已保存并等待 Windows Bot 应用");
      await refreshAiSettings(true);
    } catch (err) {
      toast(err.message);
    } finally {
      button.disabled = false;
    }
  }

  function stopAiTimer() {
    if (aiTimer) clearInterval(aiTimer);
    aiTimer = null;
  }

  function startAiTimer() {
    stopAiTimer();
    refreshAiSettings(false);
    aiTimer = setInterval(() => refreshAiSettings(false), 3500);
  }

  const baseShowLogin = showLogin;
  showLogin = function () {
    stopAiTimer();
    aiState = null;
    baseShowLogin();
  };

  const baseShowApp = showApp;
  showApp = function (name) {
    baseShowApp(name);
    startAiTimer();
  };

  const baseRenderSettings = renderSettings;
  renderSettings = function () {
    baseRenderSettings();
    renderAiSettings();
  };

  const save = $("saveAiModelSettingsBtn");
  if (save) save.addEventListener("click", saveAiSettings);
  if (!$("appView").classList.contains("hidden")) startAiTimer();
})();
