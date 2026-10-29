const versionUpdateState={loading:false,last:null,proxy:null,proxyLoading:false,proxyLoadedAt:0};
titles["version-update"]=["版本更新","同步 GitHub 版本、在线更新服务端，并缓存客户端正式版后推送更新通知。"];

function vuBytes(value){
  const n=Number(value||0);if(!n)return "0 B";
  const units=["B","KB","MB","GB"];let x=n,i=0;while(x>=1024&&i<units.length-1){x/=1024;i++}
  return `${x.toFixed(i?1:0)} ${units[i]}`;
}
function vuPercent(value){return Math.max(0,Math.min(100,Number(value||0)));}
function vuProgress(value){const p=vuPercent(value);return `<div style="min-width:220px"><progress max="100" value="${p}" style="width:100%;height:18px"></progress><div class="hint">${p.toFixed(0)}%</div></div>`;}
function vuStateBadge(stateText){const s=String(stateText||"");if(s==="completed"||s==="ready")return badge("已完成","good");if(s==="failed")return badge("失败","bad");if(["running","queued","downloading","verifying","connecting","resuming","retrying"].includes(s))return badge(s==="retrying"?"网络重试中":"进行中","warn");return badge(s||"等待","gray");}
function vuEta(seconds){const n=Number(seconds);if(!Number.isFinite(n)||n<0)return "--";if(n<60)return `${Math.ceil(n)} 秒`;if(n<3600)return `${Math.ceil(n/60)} 分钟`;return `${(n/3600).toFixed(1)} 小时`;}

async function loadVersionUpdate(){
  if(versionUpdateState.loading)return;
  versionUpdateState.loading=true;
  try{const data=await api("/api/admin/version-update/status");versionUpdateState.last=data;renderVersionUpdate(data);loadGithubProxySettings(false).catch(err=>console.warn(err))}
  catch(err){if($("versionUpdateStatus"))$("versionUpdateStatus").innerHTML=`<div class="empty">${esc(err.message)}</div>`}
  finally{versionUpdateState.loading=false}
}

async function loadGithubProxySettings(force=false){
  if(versionUpdateState.proxyLoading)return versionUpdateState.proxy;
  if(!force&&versionUpdateState.proxy&&Date.now()-versionUpdateState.proxyLoadedAt<5000)return versionUpdateState.proxy;
  versionUpdateState.proxyLoading=true;
  try{
    const data=await api("/api/admin/version-update/proxy");
    versionUpdateState.proxy=data;versionUpdateState.proxyLoadedAt=Date.now();renderGithubProxySettings(data);return data;
  }catch(err){
    if($("githubProxyState"))$("githubProxyState").innerHTML=badge("读取失败","bad");
    if($("githubProxyDetail"))$("githubProxyDetail").textContent="读取 GitHub 下载代理配置失败："+err.message;
    throw err;
  }finally{versionUpdateState.proxyLoading=false}
}

function renderGithubProxySettings(data){
  const stateBox=$("githubProxyState"),detail=$("githubProxyDetail"),enabled=$("githubProxyEnabled"),input=$("githubProxyVlessUrl");
  if(!stateBox||!detail||!enabled||!input)return;
  enabled.checked=Boolean(data.enabled);
  input.placeholder=data.configured?"已安全保存节点；留空表示不修改":"vless://...";
  if(data.enabled&&data.running)stateBox.innerHTML=badge("VLESS 已启用","good");
  else if(data.enabled)stateBox.innerHTML=badge("VLESS 启动失败","bad");
  else if(data.configured)stateBox.innerHTML=badge("已配置但停用","gray");
  else stateBox.innerHTML=badge("未配置","gray");
  const node=data.node||{},parts=[];
  if(data.configured){
    const nodeName=node.name?`节点：${esc(node.name)} · `:"";
    parts.push(`${nodeName}${esc(node.server||"已保存")} : ${esc(node.port||"-")} · ${esc(node.security||"-")} · ${esc(node.transport||"-")}${node.flow?` · ${esc(node.flow)}`:""}`);
  }else parts.push("尚未保存 VLESS 节点。");
  parts.push("隔离范围：仅本控制面 GitHub HTTPS；本地 SOCKS 仅监听容器 127.0.0.1，不启用 TUN，不修改服务器系统代理或默认路由。");
  const test=data.last_test||{};
  if(test.tested_at)parts.push(test.ok?`最近测试成功：${esc(cnTime(test.tested_at))} · ${esc(test.latency_ms)}ms`:`最近测试失败：${esc(cnTime(test.tested_at))} · ${esc(test.error||"")}`);
  if(data.last_error)parts.push(`当前错误：${esc(data.last_error)}`);
  detail.innerHTML=parts.map(x=>`<div>${x}</div>`).join("");
}

async function saveGithubProxySettings(){
  const button=$("saveGithubProxyBtn"),input=$("githubProxyVlessUrl"),enabled=$("githubProxyEnabled");
  if(!input||!enabled)return;
  try{
    if(button)button.disabled=true;
    const value=String(input.value||"").trim();
    const data=await api("/api/admin/version-update/proxy",{method:"PUT",body:JSON.stringify({enabled:enabled.checked,vless_url:value})});
    input.value="";versionUpdateState.proxy=data;versionUpdateState.proxyLoadedAt=Date.now();renderGithubProxySettings(data);
    toast(data.enabled&&data.running?"VLESS GitHub 下载代理已保存并启用":"GitHub 下载代理配置已保存");
    await loadVersionUpdate();
  }catch(err){toast(err.message);await loadGithubProxySettings(true).catch(()=>{})}
  finally{if(button)button.disabled=false}
}

async function testGithubProxySettings(){
  const button=$("testGithubProxyBtn");
  try{
    if(button)button.disabled=true;toast("正在通过 VLESS 节点测试 GitHub…");
    const data=await api("/api/admin/version-update/proxy/test",{method:"POST"});
    versionUpdateState.proxy=data;versionUpdateState.proxyLoadedAt=Date.now();renderGithubProxySettings(data);
    const test=data.last_test||{};toast(`VLESS 节点测试成功${test.latency_ms!=null?`，GitHub 延迟 ${test.latency_ms}ms`:""}`);
  }catch(err){toast(err.message);await loadGithubProxySettings(true).catch(()=>{})}
  finally{if(button)button.disabled=false}
}

async function clearGithubProxySettings(){
  if(!confirm("确认清除已保存的 VLESS 节点？GitHub HTTPS 下载将恢复直连，服务器其他网络不会变化。"))return;
  const button=$("clearGithubProxyBtn");
  try{
    if(button)button.disabled=true;
    const data=await api("/api/admin/version-update/proxy",{method:"PUT",body:JSON.stringify({enabled:false,clear:true})});
    const input=$("githubProxyVlessUrl");if(input)input.value="";
    versionUpdateState.proxy=data;versionUpdateState.proxyLoadedAt=Date.now();renderGithubProxySettings(data);toast("VLESS 节点已清除，GitHub HTTPS 恢复直连");
    await loadVersionUpdate();
  }catch(err){toast(err.message)}finally{if(button)button.disabled=false}
}

function renderVersionUpdate(data){
  const server=data.server||{}, update=server.update||{}, gh=server.github||{}, agent=update.agent||{}, serverNet=server.network||{};
  const client=data.client||{}, pkg=client.package||{}, push=client.push||{}, clientNet=client.network||{};
  const serverCurrent=server.current_short_sha||"未知";
  const serverLatest=gh.short_sha||"同步失败";
  const buildCommit=server.running_image_commit||"";
  const buildHint=buildCommit
    ?`<p class="hint">当前运行镜像已标注 Git 提交 ${esc(buildCommit.slice(0,12))}；此值来自容器镜像，不受正在执行的更新影响。</p>`
    :'<p class="hint">旧镜像未嵌入构建提交：当前版本仅来自更新状态记录，不能证明实际容器已切换。建议在旧更新任务结束后执行一次版本重新构建核验。</p>';

  const runtimeUnverified=!!server.needs_runtime_verification;
  const sameSourceVersion=!!(server.current_commit&&gh.sha&&server.current_commit===gh.sha);
  const serverStatus=server.sync_error?badge("GitHub 同步失败","bad"):
    (runtimeUnverified?badge("运行镜像版本未验证","warn"):
     (!server.current_commit?badge("等待识别当前版本","gray"):
      (server.update_available?badge("发现新版本","warn"):badge("已是最新","good"))));
  const agentHint=agent.online?"":`<p class="hint">首次启用服务端网页更新需要在 Ubuntu 执行一次：<code>sudo bash /opt/qnbot/scripts/install-api-control-plane-update-agent.sh</code></p>`;
  const gitTransport=(agent.git_transport||serverNet.git_transport||"ssh-443")==="ssh-443"?"Git SSH 443":esc(agent.git_transport||serverNet.git_transport||"SSH");
  $("serverVersionCard").innerHTML=`
    <div class="provider-card">
      <div class="provider-top"><strong>API 控制面</strong>${serverStatus}</div>
      <div class="form-grid" style="margin-top:12px">
        <label>${buildCommit?"当前运行容器提交":"更新状态记录提交"}<div class="code-card"><code>${esc(serverCurrent)}</code></div></label>
        <label>GitHub master<div class="code-card"><code>${esc(serverLatest)}</code></div></label>
      </div>
      <p class="hint">${esc(gh.message||server.sync_error||"")}${gh.committed_at?` · ${esc(cnTime(gh.committed_at))}`:""}</p>
      ${buildHint}
      <div class="provider-meta">${agent.online?badge("主机更新代理在线","good"):badge("主机更新代理离线","bad")}${badge(gitTransport,"blue")}${badge(`源码失败最多重试 ${Number(agent.git_fetch_attempts||serverNet.git_fetch_attempts||5)} 次`,"gray")}${vuStateBadge(update.state)}</div>
      ${agentHint}
      ${update.state==="completed"&&runtimeUnverified?'<p class="hint">上方 100% 仅表示上一次更新任务完成；实际运行镜像的版本仍需重新构建核验。</p>':""}
      <div style="margin-top:12px">${vuProgress(update.progress_percent)}</div>
      <p><strong>${esc(update.phase||"等待更新")}</strong> · ${esc(update.message||"")}</p>
      ${update.build_phase_not_percentage?'<p class="hint">35% 是镜像构建阶段标记，不代表镜像已构建 35%。</p>':""}
      ${update.diagnostic?`<p class="hint" style="${update.build_suspected_stalled?'color:#b45309':''}">${esc(update.diagnostic)}</p>`:""}
      <p class="hint">更新状态：${update.status_age_seconds==null?"--":Number(update.status_age_seconds)+"秒前"} · 最后日志输出：${update.log_age_seconds==null?"--":Number(update.log_age_seconds)+"秒前"} · 日志大小：${vuBytes(update.log_size_bytes)}</p>
      <div class="actions"><button class="secondary" onclick="switchPage('server-runtime');document.getElementById('serverRuntimeSource').value='build';loadServerRuntimeLogs()">查看构建日志</button><button class="secondary" onclick="switchPage('server-runtime');document.getElementById('serverRuntimeSource').value='update';loadServerRuntimeLogs()">查看更新日志</button></div>
      <p class="hint">服务端源码仍由主机更新代理通过 GitHub 官方 <code>ssh.github.com:443</code> 拉取；上方 VLESS 配置只作用于控制面内部的 GitHub HTTPS / Release 下载，不改变主机 SSH 更新链路。</p>
      <div class="actions"><button class="primary" onclick="startServerVersionUpdate()" ${agent.online?"":"disabled"}>${runtimeUnverified&&sameSourceVersion?"重新构建并验证当前服务端版本":"更新服务端到 GitHub 最新版"}</button></div>
    </div>`;

  const pkgPhase=pkg.ready?"ready":(pkg.error?"failed":(pkg.phase||(pkg.downloading?"downloading":"waiting")));
  const clientProgress=pkg.ready?100:vuPercent(pkg.progress_percent);
  const downloadText=pkg.ready?`服务端已完整缓存并校验 ${vuBytes(pkg.downloaded_bytes||client.size)}`:`${vuBytes(pkg.downloaded_bytes)} / ${vuBytes(pkg.total_bytes||client.size)}`;
  const speed=Number(pkg.speed_bps||0)>0?`${vuBytes(pkg.speed_bps)}/s`:"--";
  const retryText=Number(pkg.max_attempts||clientNet.max_attempts||0)>0?`第 ${Number(pkg.attempt||0)}/${Number(pkg.max_attempts||clientNet.max_attempts)} 次`:"";
  const retryWait=pkgPhase==="retrying"&&Number(pkg.retry_in_seconds||0)>0?` · ${Number(pkg.retry_in_seconds)} 秒后续传`:"";
  const proxyBadge=(pkg.proxy_enabled||clientNet.proxy_enabled)?badge("HTTPS VLESS 代理","blue"):badge("HTTPS 直连","gray");
  $("clientVersionCard").innerHTML=`
    <div class="provider-card">
      <div class="provider-top"><strong>Windows 客户端正式版</strong>${client.sync_error?badge("同步失败","bad"):badge(client.version||"未知","blue")}</div>
      <div class="form-grid" style="margin-top:12px">
        <label>正式版版本<div class="code-card"><code>${esc(client.version||"-")}</code></div></label>
        <label>Release Tag<div class="code-card"><code>${esc(client.tag||"-")}</code></div></label>
        <label>安装包大小<div class="code-card"><code>${esc(vuBytes(client.size))}</code></div></label>
        <label>构建提交<div class="code-card"><code>${esc(String(client.commit||"").slice(0,12)||"-")}</code></div></label>
      </div>
      <p class="hint">发布时间：${esc(cnTime(client.published_at,"-"))}　SHA-256：${esc(client.sha256||"-")}</p>
      <div class="provider-meta">${vuStateBadge(pkgPhase)}${badge("HTTP Range 断点续传","good")}${proxyBadge}${badge(`SSE 在线连接 ${Number(push.active_streams||0)}`,push.active_streams?"good":"gray")}${push.last_push_version?badge(`最近推送 ${push.last_push_version}`,"blue"):badge("尚无推送记录","gray")}</div>
      <div style="margin-top:12px">${vuProgress(clientProgress)}</div>
      <p><strong>${pkg.ready?"安装包已就绪":pkgPhase==="failed"?"安装包准备失败":pkgPhase==="retrying"?"GitHub 网络异常，自动续传重试":pkgPhase==="verifying"?"正在校验安装包":pkgPhase==="resuming"?"正在从断点恢复":"服务端正在准备安装包"}</strong> · ${esc(downloadText)}${pkg.error?` · ${esc(pkg.error)}`:""}</p>
      <p class="hint">速度：${esc(speed)}　预计剩余：${esc(vuEta(pkg.eta_seconds))}　${esc(retryText)}${esc(retryWait)}${pkg.last_error&&!pkg.error?`　最近网络错误：${esc(pkg.last_error)}`:""}</p>
      <p class="hint">网络中断、服务端重启或切换 VLESS 节点时保留 <code>.partial</code>，下一次使用 HTTP Range 从已下载位置继续；完整下载后才执行 SHA-256/大小校验并通过 SSE 推送，客户端安装包不会直连 GitHub。</p>
      <p class="hint">如 GitHub 直连速度慢，请直接在页面上方“GitHub 下载代理”粘贴 <code>vless://</code> 节点并启用，无需修改服务器 <code>.env</code> 或系统网络。</p>
      <div class="actions"><button class="primary" onclick="startClientReleaseUpdate()">同步并缓存 GitHub 最新正式版</button></div>
    </div>`;
}

async function syncVersionUpdate(){
  try{$("syncVersionBtn").disabled=true;toast("正在同步 GitHub 版本状态…");const data=await api("/api/admin/version-update/sync",{method:"POST"});versionUpdateState.last=data;renderVersionUpdate(data);await loadGithubProxySettings(true);toast("GitHub 版本状态已同步")}
  catch(err){toast(err.message)}finally{$("syncVersionBtn").disabled=false}
}
async function startServerVersionUpdate(){
  const needsReverify=!!(versionUpdateState.status&&versionUpdateState.status.server&&versionUpdateState.status.server.needs_runtime_verification);
  const text=needsReverify
    ?"确认重新构建并核验实际运行的服务端镜像？该镜像缺少构建提交标记，当前后台的『已更新』仅来自更新状态。操作会先构建，再备份并切换容器，期间可能短暂出现 502；失败将按现有流程回滚。"
    :"确认将 API 控制面自动更新到 GitHub master 最新版本？源码将通过 Git SSH 443 自动重试，随后先构建新镜像、备份数据，再切换服务；失败会按现有部署脚本回滚。";
  if(!confirm(text))return;
  try{const r=await api("/api/admin/version-update/server/start",{method:"POST"});toast(r.already_latest?"服务端已经是 GitHub 最新版":"服务端更新任务已启动");await loadVersionUpdate()}catch(err){toast(err.message)}
}
async function startClientReleaseUpdate(){
  try{const r=await api("/api/admin/version-update/client/start",{method:"POST"});toast(r.ready?"客户端正式版已缓存，更新通知将由服务端推送":"服务端已开始从 GitHub 断点续传客户端正式版");await loadVersionUpdate()}catch(err){toast(err.message)}
}

const originalRefreshCurrent=refreshCurrent;
refreshCurrent=async function(){if(state.currentPage==="version-update")return loadVersionUpdate();return originalRefreshCurrent();};
setInterval(()=>{if(state.currentPage==="version-update")loadVersionUpdate()},1000);
