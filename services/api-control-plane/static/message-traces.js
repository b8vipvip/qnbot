titles["message-traces"]=["消息处理日志","按北京时间日期把同一买家的全部 Bot 处理事件合并为一条记录，可查看完整链路并按时间范围导出。"];

const previousRefreshCurrent=refreshCurrent;
refreshCurrent=async function(){
  if(state.currentPage==="message-traces"){
    try{await loadMessageTraces()}catch(err){console.warn(err)}
    return;
  }
  return previousRefreshCurrent();
};

function traceStatusKind(value){
  value=String(value||"").toLowerCase();
  if(value==="success"||value==="ready")return "good";
  if(value==="failed")return "bad";
  if(value==="cancelled")return "warn";
  if(value==="processing")return "blue";
  return "gray";
}

function traceStageName(value){
  const map={
    message_received:"收到买家消息",
    answer_generation_started:"开始获取答案",
    answer_ready:"答案生成完成",
    delivery_confirmed:"发送确认",
    delivery_failed:"发送失败",
    manual_intervention:"人工介入",
    processing_failed:"处理失败"
  };
  return map[value]||value||"-";
}

function traceQuery(){
  const query=new URLSearchParams();
  const fields=[
    ["client_id","traceClientId"],
    ["shop_key","traceShopKey"],
    ["seller","traceSeller"],
    ["buyer","traceBuyer"],
    ["status","traceStatus"],
    ["trace_id","traceId"]
  ];
  fields.forEach(([key,id])=>{
    const el=$(id);const value=el?String(el.value||"").trim():"";
    if(value)query.set(key,value);
  });
  query.set("limit","500");
  return query.toString();
}

function ensureTraceExportControls(){
  if($("traceExportWindow"))return;
  const table=$("messageTraceTable");
  const panel=table&&table.closest?table.closest(".panel"):null;
  const actions=panel&&panel.querySelector?panel.querySelector(".actions"):null;
  if(!actions)return;
  const wrap=document.createElement("span");
  wrap.style.display="inline-flex";
  wrap.style.gap="8px";
  wrap.style.alignItems="center";
  wrap.innerHTML=`<span class="hint">导出最近</span><select id="traceExportWindow" style="width:auto"><option value="2h">2小时</option><option value="6h">6小时</option><option value="1d" selected>1天</option><option value="3d">3天</option><option value="7d">7天</option><option value="14d">14天</option></select><button class="secondary" type="button" onclick="exportMessageConversations()">导出所有买家</button>`;
  actions.appendChild(wrap);
}

async function loadMessageTraces(){
  const box=$("messageTraceTable");
  if(!box)return;
  ensureTraceExportControls();
  box.innerHTML=`<div class="empty">正在按买家和日期整理消息处理日志...</div>`;
  try{
    const rows=await api("/api/admin/message-processing-conversations?"+traceQuery());
    state.messageTraces=rows;
    if(!rows.length){box.innerHTML=`<div class="empty">暂无符合条件的买家会话处理日志。新版 Windows Bot 在线后会自动上报。</div>`;return;}
    box.innerHTML=`<table><thead><tr><th>日期 / 最近处理</th><th>客户端 / 店铺</th><th>客服 → 买家</th><th>链路 / 事件</th><th>成功 / 失败</th><th>最新状态</th><th>最新摘要</th><th>详情</th></tr></thead><tbody>${rows.map((r,index)=>`<tr>
    <td><strong>${esc(r.conversation_date||"-")}</strong><div class="hint">${esc(cnTime(r.last_at||r.first_at,""))}</div></td>
    <td><strong>${esc(r.client_name||("#"+r.client_id))}</strong><div class="hint">${esc(r.shop_key||"-")}</div></td>
    <td>${esc(r.seller||"-")}<div class="hint">→ ${esc(r.buyer||"-")}</div></td>
    <td>${esc(String(r.trace_count||0))} 条链路<div class="hint">${esc(String(r.event_count||0))} 个处理事件</div></td>
    <td>${esc(String(r.success_count||0))} / ${esc(String(r.failed_count||0))}</td>
    <td>${badge(r.latest_status||"-",traceStatusKind(r.latest_status))}<div class="hint">${esc(traceStageName(r.latest_stage))}</div></td>
    <td><strong>${esc(r.latest_summary||"-")}</strong>${r.latest_detail?`<div class="hint">${esc(r.latest_detail)}</div>`:""}</td>
    <td><button class="primary" type="button" onclick="viewMessageConversation(${index})">查看</button></td>
  </tr>`).join("")}</tbody></table>`;
  }catch(err){
    const message=err&&err.message?err.message:String(err||"未知错误");
    box.innerHTML=`<div class="empty">查询消息处理日志失败：${esc(message)}<div class="actions" style="justify-content:center;margin-top:12px"><button class="secondary" onclick="loadMessageTraces()">重试</button></div></div>`;
    throw err;
  }
}

function ensureTraceDetailDialog(){
  let dialog=$("messageTraceDetailDialog");
  if(dialog)return dialog;
  dialog=document.createElement("dialog");
  dialog.id="messageTraceDetailDialog";
  dialog.className="modal";
  dialog.innerHTML=`<div style="min-width:min(1100px,90vw);max-width:90vw"><div class="modal-head"><div><h2 id="messageTraceDetailTitle">买家消息处理详情</h2><p id="messageTraceDetailSubtitle"></p></div><button class="icon-btn" type="button" onclick="document.getElementById('messageTraceDetailDialog').close()">×</button></div><div id="messageTraceDetailBody"><div class="empty">正在加载...</div></div><div class="modal-actions"><button class="secondary" type="button" onclick="document.getElementById('messageTraceDetailDialog').close()">关闭</button></div></div>`;
  document.body.appendChild(dialog);
  return dialog;
}

async function viewMessageConversation(index){
  const row=(state.messageTraces||[])[Number(index)];
  if(!row)return;
  const dialog=ensureTraceDetailDialog();
  const title=$("messageTraceDetailTitle");
  const subtitle=$("messageTraceDetailSubtitle");
  const body=$("messageTraceDetailBody");
  if(title)title.textContent=`${row.conversation_date||""} · ${row.buyer||"买家"}`;
  if(subtitle)subtitle.textContent=`${row.client_name||("#"+row.client_id)} / ${row.shop_key||"-"} / ${row.seller||"-"} → ${row.buyer||"-"}`;
  if(body)body.innerHTML=`<div class="empty">正在加载当天该买家的全部 Bot 处理记录...</div>`;
  if(!dialog.open)dialog.showModal();
  const query=new URLSearchParams({
    client_id:String(row.client_id||""),
    shop_key:String(row.shop_key||""),
    seller:String(row.seller||""),
    buyer:String(row.buyer||""),
    conversation_date:String(row.conversation_date||"")
  });
  try{
    const data=await api("/api/admin/message-processing-conversations/detail?"+query.toString());
    const events=(data&&data.events)||[];
    if(!body)return;
    if(!events.length){body.innerHTML=`<div class="empty">当天没有可显示的处理事件。</div>`;return;}
    body.innerHTML=`<div class="hint" style="margin-bottom:10px">共 ${esc(String(events.length))} 个处理事件，按发生时间从早到晚显示。</div><div style="max-height:65vh;overflow:auto"><table><thead><tr><th>时间（北京时间）</th><th>链路ID</th><th>阶段</th><th>状态</th><th>耗时</th><th>摘要 / 详情</th></tr></thead><tbody>${events.map(e=>`<tr><td>${esc(cnTime(e.occurred_at||e.created_at,""))}</td><td>${esc(e.trace_id||"-")}</td><td>${esc(traceStageName(e.stage))}</td><td>${badge(e.status||"-",traceStatusKind(e.status))}</td><td>${Number(e.duration_ms||0)>0?esc(e.duration_ms+"ms"):"-"}</td><td><strong>${esc(e.summary||"-")}</strong>${e.detail?`<div class="hint">${esc(e.detail)}</div>`:""}</td></tr>`).join("")}</tbody></table></div>`;
  }catch(err){
    if(body)body.innerHTML=`<div class="empty">加载详情失败：${esc(err&&err.message?err.message:String(err||"未知错误"))}</div>`;
  }
}

function exportMessageConversations(){
  const query=new URLSearchParams();
  const windowEl=$("traceExportWindow");
  query.set("window",windowEl?String(windowEl.value||"1d"):"1d");
  [["client_id","traceClientId"],["shop_key","traceShopKey"],["seller","traceSeller"],["status","traceStatus"]].forEach(([key,id])=>{
    const el=$(id);const value=el?String(el.value||"").trim():"";
    if(value)query.set(key,value);
  });
  const anchor=document.createElement("a");
  anchor.href="/api/admin/message-processing-conversations/export?"+query.toString();
  anchor.download="";
  document.body.appendChild(anchor);
  anchor.click();
  anchor.remove();
}

function filterTrace(traceId){
  const input=$("traceId");
  if(input)input.value=traceId||"";
  loadMessageTraces().catch(err=>toast(err.message));
}

function clearTraceFilters(){
  ["traceClientId","traceShopKey","traceSeller","traceBuyer","traceId"].forEach(id=>{const el=$(id);if(el)el.value=""});
  const status=$("traceStatus");if(status)status.value="";
  loadMessageTraces().catch(err=>toast(err.message));
}
