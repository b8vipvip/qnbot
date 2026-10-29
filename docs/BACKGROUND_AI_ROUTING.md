# 后台 AI 任务路由与超时

## 为什么需要独立后台路由

实时买家聊天和知识库批量优化的时延特征不同：

- 实时客服需要快速切换失效中转站/模型；
- 知识库优化一次需要审校多条问答并输出结构化 JSON，正常生成可能需要 20～60 秒甚至更久。

如果后台任务继续使用实时路由的 6 秒完整响应超时，会出现：

1. 上游已经开始生成，但统一 API 在 6 秒主动放弃；
2. 立即切换下一个模型/中转站；
3. 已放弃的上游请求可能仍在后台完成并计费；
4. 控制面最终在总预算耗尽后自行返回 502 `upstream_exhausted`；
5. 中转站后台可能显示这些上游请求成功，因此看不到对应的 502。

## 当前策略

### 实时非流式请求

```text
RUNTIME_TOTAL_BUDGET_SECONDS=45
RUNTIME_ATTEMPT_TIMEOUT_SECONDS=6
```

### 重型后台结构化请求

当请求 `max_tokens >= BACKGROUND_MIN_MAX_TOKENS` 时自动进入后台路由：

```text
BACKGROUND_MIN_MAX_TOKENS=4000
BACKGROUND_TOTAL_BUDGET_SECONDS=240
BACKGROUND_ATTEMPT_TIMEOUT_SECONDS=90
```

知识库优化当前使用 `max_tokens=5000`，因此自动进入后台路由。

## 知识库优化批次

当前每批从 12 条降低为 5 条，并将 Bot 侧单次等待上限提高到 300 秒。

快速失败（30 秒内结束的网络或上游错误）最多重试一次；已经等待较久的失败不立即重复请求，避免上游仍在后台生成时产生重复计费。

## 宝塔 / Nginx 反向代理

生产 Bot 通过 `https://<CONTROL_PLANE_HOST>` 访问统一 API。后台请求可能超过 60 秒，因此宝塔反向代理的读取超时必须覆盖后台任务最长等待时间。

建议至少设置：

```nginx
proxy_connect_timeout 30s;
proxy_send_timeout 300s;
proxy_read_timeout 300s;
```

不要修改现有域名、SSL 证书或反代目标；现有目标仍为：

```text
http://127.0.0.1:18081
```

如果代码更新后出现约 60 秒整齐超时或 504，而控制面容器日志仍在继续调用上游，应优先检查宝塔/Nginx 的 `proxy_read_timeout`。
