// Admin-only AI request/response history; never display secrets from browser storage.
titles["ai-requests"]=["AI请求记录","查看客户端的 AI 提示词、回复、耗时和失败详情，支持按条数导出。"];

const priorAiRefreshCurrent=refreshCurrent;
refreshCurrent=async function(){
  if(state.currentPage==="ai-requests"){
    try{await loadAiRequestRecords()}catch(err){console.warn("AI request history",err)}
    return;
  }
  return priorAiRefreshCurrent();
};

let aiRequestOffset=0;
function aiRequestFilters(){
  const qs=new URLSearchParams();
  const pairs=[["client_id","aiRequestClientId"],["shop_key","aiRequestShopKey"],
               ["status","aiRequestStatus"],["model","aiRequestModel"]];
  for(const pair of pairs){
    const field=document.getElementById(pair[1]);
    const value=String(field?.value||"").trim();
    if(value)qs.set(pair[0],value);
  }
  return qs;
}

function aiRequestSearch(){
  aiRequestOffset=0;
  loadAiRequestRecords();
}

function aiRequestReset(){
  for(const id of ["aiRequestClientId","aiRequestShopKey","aiRequestModel"]){
    const el=document.getElementById(id);if(el)el.value="";
  }
  document.getElementById("aiRequestStatus").value="";
  aiRequestSearch();
}

async function loadAiRequestRecords(){
  const box=document.getElementById("aiRequestTable");if(!box)return;
  const qs=aiRequestFilters(),pageSize=Number(document.getElementById("aiRequestPageSize")?.value||50);
  qs.set("limit",String(pageSize));
  qs.set("offset",String(aiRequestOffset));
  const result=await api("/api/admin/ai-requests?"+qs.toString());
  const rows=result.items||[];
  state.aiRequestRecords=rows;
  const count=document.getElementById("aiRequestCount");
  if(count)count.textContent="共 "+result.total+" 条记录；已展示 "+rows.length+" 条；仅管理员可查看，记录保留14天。";
  if(!rows.length){
    box.innerHTML='<div class="empty">暂无符合条件的 AI 请求记录。新版本服务端升级后，将记录后续认证成功的客户端 AI 调用。</div>';
  }else{
    box.innerHTML='<div style="overflow-x:auto"><table><thead><tr>'+
      '<th>时间</th><th>客户端 / 店铺</th><th>接口 / 请求模型</th><th>实际供应商 / 模型</th>'+
      '<th>结果</th><th>耗时</th><th>详情</th></tr></thead><tbody>'+
      rows.map(function(r){
        const status=r.success?badge("成功","good"):badge("失败","bad");
        return '<tr><td>'+esc(cnTime(r.created_at))+'</td>'+
          '<td><strong>'+esc(r.client_name||("#"+r.client_id))+'</strong><div class="hint">ID '+esc(r.client_id)+' · '+esc(r.shop_key||"-")+'</div></td>'+
          '<td>'+esc(r.endpoint)+'<div class="hint">'+esc(r.request_model)+'</div></td>'+
          '<td>'+esc(r.provider_name||"-")+'<div class="hint">'+esc(r.resolved_model||"-")+' · '+esc(r.protocol||"-")+'</div></td>'+
          '<td>'+status+'<div class="hint">HTTP '+esc(r.http_status)+'</div></td>'+
          '<td>'+esc(r.duration_ms)+' ms</td>'+
          '<td><button class="primary" type="button" onclick="viewAiRequest('+Number(r.id)+')">查看</button></td></tr>';
      }).join("")+'</tbody></table></div>';
  }
  const prev=document.getElementById("aiRequestPrev"),next=document.getElementById("aiRequestNext");
  if(prev)prev.disabled=aiRequestOffset===0;
  if(next)next.disabled=aiRequestOffset+rows.length>=result.total;
}

function aiRequestPage(direction){
  const limit=Number(document.getElementById("aiRequestPageSize")?.value||50);
  aiRequestOffset=Math.max(0,aiRequestOffset+direction*limit);
  loadAiRequestRecords().catch(err=>toast(err.message));
}

function aiRequestDialog(){
  let dialog=document.getElementById("aiRequestDetailDialog");
  if(dialog)return dialog;
  dialog=document.createElement("dialog");
  dialog.id="aiRequestDetailDialog";
  dialog.className="modal";
  dialog.style.width="min(1100px,96vw)";
  dialog.style.maxWidth="96vw";
  dialog.innerHTML='<div class="modal-head"><div><h2 id="aiRequestDetailTitle">AI 请求详情</h2><p id="aiRequestDetailMeta"></p></div>'+
    '<button class="icon-btn" type="button" onclick="document.getElementById(\'aiRequestDetailDialog\').close()">×</button></div>'+
    '<div class="ai-request-detail"><h3>本次提示词 / 消息</h3><pre id="aiRequestPrompt"></pre>'+
    '<h3>AI 回复内容</h3><pre id="aiRequestReply"></pre>'+
    '<h3>失败原因 / 路由尝试</h3><pre id="aiRequestAttempts"></pre></div>'+
    '<div class="modal-actions"><button class="secondary" type="button" onclick="document.getElementById(\'aiRequestDetailDialog\').close()">关闭</button></div>';
  document.body.appendChild(dialog);
  return dialog;
}

async function viewAiRequest(id){
  const dialog=aiRequestDialog();
  document.getElementById("aiRequestDetailTitle").textContent="AI 请求 #"+id;
  document.getElementById("aiRequestDetailMeta").textContent="加载中…";
  for(const field of ["aiRequestPrompt","aiRequestReply","aiRequestAttempts"]){
    document.getElementById(field).textContent="";
  }
  if(!dialog.open)dialog.showModal();
  try{
    const r=await api("/api/admin/ai-requests/"+encodeURIComponent(String(id)));
    document.getElementById("aiRequestDetailMeta").textContent=
      cnTime(r.created_at)+" · 客户端 "+r.client_name+" (ID "+r.client_id+") · 店铺 "+(r.shop_key||"-")+
      " · "+r.endpoint+" · "+r.request_model+" → "+(r.resolved_model||"-")+
      " · "+(r.success?"成功":"失败")+" HTTP "+r.http_status+
      " · "+r.duration_ms+" ms"+(r.truncated?" · 部分内容因大小限制截断":"");
    document.getElementById("aiRequestPrompt").textContent=JSON.stringify(r.prompt,null,2);
    document.getElementById("aiRequestReply").textContent=r.response_text||"（无模型回复）";
    document.getElementById("aiRequestAttempts").textContent=
      (r.error?"错误："+r.error+"\n\n":"")+"路由尝试：\n"+JSON.stringify(r.attempts||[],null,2);
  }catch(err){
    document.getElementById("aiRequestDetailMeta").textContent="读取失败："+err.message;
  }
}

async function exportAiRequests(){
  const count=Number(document.getElementById("aiRequestExportCount").value);
  if(!Number.isInteger(count)||count<1||count>5000){
    toast("导出条数应在1到5000之间");return;
  }
  const format=document.getElementById("aiRequestExportFormat").value;
  const query=aiRequestFilters();query.set("count",String(count));query.set("format",format);
  const button=document.getElementById("aiRequestExportButton");
  button.disabled=true;
  try{
    const response=await fetch("/api/admin/ai-requests/export?"+query.toString(),{
      credentials:"same-origin",cache:"no-store"
    });
    if(response.status===401){showLogin();throw new Error("管理员登录已失效")}
    if(!response.ok){throw new Error("导出失败：HTTP "+response.status)}
    const blob=await response.blob();
    const url=URL.createObjectURL(blob);
    const anchor=document.createElement("a");
    anchor.href=url;anchor.download="ai-requests-"+new Date().toISOString().slice(0,10)+"."+format;
    document.body.appendChild(anchor);anchor.click();anchor.remove();
    setTimeout(()=>URL.revokeObjectURL(url),1000);
    toast("已导出最近最多 "+count+" 条记录");
  }catch(err){toast(err.message)}finally{button.disabled=false}
}
