const OCR_MIB = 1024 * 1024;

function ocrEl(id){return document.getElementById(id)}

async function ocrApi(path,options={}){
  const response=await fetch(path,{
    credentials:"same-origin",
    headers:{"Content-Type":"application/json",...(options.headers||{})},
    ...options
  });
  let data=null;
  try{data=await response.json()}catch{}
  if(!response.ok){
    if(response.status===401){
      throw new Error("管理员登录已失效，请返回控制台重新登录");
    }
    throw new Error((data&&data.detail)||`请求失败 HTTP ${response.status}`);
  }
  return data||{};
}

function ocrSourceLabel(value){
  if(value==="database")return "控制台 / SQLite";
  if(value==="environment-defaults")return "环境变量默认值";
  return value||"-";
}

function ocrPriorityLabel(value){
  return value==="ai_first"?"AI 视觉接口优先":"OCR 优先";
}

function ocrSetResult(message,ok=true){
  const box=ocrEl("ocrResult");
  box.textContent=message||"";
  box.className=`ocr-result ${ok?"good":"bad"}`;
}

function ocrRender(data){
  ocrEl("ocrStatus").textContent=data.enabled?"已启用":"已停用";
  ocrEl("ocrEngine").textContent=data.engine||"RapidOCR/ONNXRuntime";
  ocrEl("ocrEngineLoaded").textContent=data.engine_loaded?"已加载":"按需加载（尚未执行识别）";
  ocrEl("ocrActiveRequests").textContent=String(data.active_requests||0);
  ocrEl("ocrSource").textContent=ocrSourceLabel(data.source);
  ocrEl("ocrUpdatedAt").textContent=data.updated_at||"-";
  ocrEl("ocrEnabled").checked=!!data.enabled;
  ocrEl("ocrMaxImageMb").value=(Number(data.max_image_bytes||0)/OCR_MIB).toFixed(2).replace(/\.00$/,"");
  ocrEl("ocrTimeoutSeconds").value=String(data.timeout_seconds??16);
  ocrEl("ocrMaxConcurrency").value=String(data.max_concurrency??2);
  ocrEl("ocrMaxTextChars").value=String(data.max_text_chars??6000);
}

function ocrRenderPriority(data){
  const priority=data&&data.vision_priority==="ai_first"?"ai_first":"ocr_first";
  ocrEl("ocrVisionPriority").value=priority;
  ocrEl("ocrVisionPriorityStatus").textContent=ocrPriorityLabel(priority);
}

async function loadOcrSettings(){
  const refresh=ocrEl("ocrRefreshBtn");
  if(refresh)refresh.disabled=true;
  try{
    const [data,priority]=await Promise.all([
      ocrApi("/api/admin/ocr/settings"),
      ocrApi("/api/admin/ocr/vision-priority")
    ]);
    ocrRender(data);
    ocrRenderPriority(priority);
    ocrSetResult("");
  }catch(err){
    ocrSetResult(err.message||String(err),false);
  }finally{
    if(refresh)refresh.disabled=false;
  }
}

async function saveOcrSettings(event){
  if(event)event.preventDefault();
  const save=ocrEl("ocrSaveBtn");
  save.disabled=true;
  try{
    const maxImageMb=Number(ocrEl("ocrMaxImageMb").value);
    const timeoutSeconds=Number(ocrEl("ocrTimeoutSeconds").value);
    const maxConcurrency=Number(ocrEl("ocrMaxConcurrency").value);
    const maxTextChars=Number(ocrEl("ocrMaxTextChars").value);
    const visionPriority=ocrEl("ocrVisionPriority").value;
    if(!Number.isFinite(maxImageMb)||maxImageMb<0.25||maxImageMb>64)throw new Error("最大图片大小必须在 0.25–64 MB 之间");
    if(!Number.isFinite(timeoutSeconds)||timeoutSeconds<1||timeoutSeconds>30)throw new Error("OCR 超时必须在 1–30 秒之间");
    if(!Number.isInteger(maxConcurrency)||maxConcurrency<1||maxConcurrency>8)throw new Error("最大并发必须是 1–8 的整数");
    if(!Number.isInteger(maxTextChars)||maxTextChars<256||maxTextChars>12000)throw new Error("最大返回文本字符必须是 256–12000 的整数");
    if(!["ocr_first","ai_first"].includes(visionPriority))throw new Error("视觉理解优先级无效");

    const data=await ocrApi("/api/admin/ocr/settings",{
      method:"PUT",
      body:JSON.stringify({
        enabled:ocrEl("ocrEnabled").checked,
        max_image_bytes:Math.round(maxImageMb*OCR_MIB),
        timeout_seconds:timeoutSeconds,
        max_concurrency:maxConcurrency,
        max_text_chars:maxTextChars
      })
    });
    const priority=await ocrApi("/api/admin/ocr/vision-priority",{
      method:"PUT",
      body:JSON.stringify({vision_priority:visionPriority})
    });
    ocrRender(data);
    ocrRenderPriority(priority);
    ocrSetResult("OCR 参数和视觉理解优先级已保存并立即生效，无需重启服务。",true);
  }catch(err){
    ocrSetResult(err.message||String(err),false);
  }finally{
    save.disabled=false;
  }
}

async function resetOcrSettings(){
  if(!confirm("恢复环境变量/内置默认值？当前控制台 OCR 参数和视觉理解优先级会被覆盖。"))return;
  const button=ocrEl("ocrResetBtn");
  button.disabled=true;
  try{
    const [data,priority]=await Promise.all([
      ocrApi("/api/admin/ocr/settings/reset",{method:"POST",body:"{}"}),
      ocrApi("/api/admin/ocr/vision-priority/reset",{method:"POST",body:"{}"})
    ]);
    ocrRender(data);
    ocrRenderPriority(priority);
    ocrSetResult("已恢复环境默认值并立即生效。",true);
  }catch(err){
    ocrSetResult(err.message||String(err),false);
  }finally{
    button.disabled=false;
  }
}


const ocrTaskState={page:1,pageSize:20,status:"all",generation:0,detailTaskId:"",detailData:null};
const OCR_TASK_LABELS={
  queued:"排队中",running:"识别中",success:"成功",timeout:"超时",
  failed:"失败",rejected:"已拒绝",interrupted:"已中断"
};
const OCR_TASK_PHASES={
  admission:"等待执行",inference:"模型推理",validation:"输入校验",
  returned:"识别返回",server_restarted:"服务重启"
};
const OCR_TASK_ERRORS={
  invalid_image_or_hash:"图片或哈希无效",
  invalid_image:"图片格式无效",
  image_too_large:"图片过大",
  ocr_disabled_or_unavailable:"OCR 已停用或不可用",
  inference_timeout:"OCR 推理超时",
  request_cancelled:"请求已取消",
  server_restarted:"服务重启导致中断",
  internal_ocr_error:"OCR 内部错误",
  http_error:"服务响应异常"
};
function ocrSafeNumber(v){return Number.isFinite(Number(v))?Number(v):0}
function ocrTaskDate(value){
  if(!value)return "-";
  const parsed=new Date(value);
  return Number.isNaN(parsed.getTime())?String(value):parsed.toLocaleString("zh-CN",{hour12:false});
}
function ocrTaskCell(tr,value,cssClass=""){
  const td=document.createElement("td");
  td.textContent=String(value??"-");
  if(cssClass)td.className=cssClass;
  tr.appendChild(td);
  return td;
}
function ocrRenderTaskRows(items){
  const tbody=ocrEl("ocrTaskRows");
  tbody.replaceChildren();
  if(!items.length){
    const tr=document.createElement("tr");
    const td=document.createElement("td");
    td.colSpan=10;
    td.className="ocr-task-empty";
    td.textContent="当前筛选条件下没有 OCR 任务";
    tr.appendChild(td);tbody.appendChild(tr);
    return;
  }
  items.forEach(task=>{
    const tr=document.createElement("tr");
    ocrTaskCell(tr,ocrTaskDate(task.started_at));
    const id=ocrTaskCell(tr,task.task_id||"-");
    id.title=task.task_id||"";
    id.style.fontFamily="monospace";
    id.style.fontSize="11px";
    const status=ocrTaskCell(tr,OCR_TASK_LABELS[task.status]||task.status||"-","ocr-task-state");
    status.dataset.state=task.status||"";
    ocrTaskCell(tr,OCR_TASK_PHASES[task.phase]||task.phase||"-");
    ocrTaskCell(tr,`${ocrSafeNumber(task.queue_ms)} / ${ocrSafeNumber(task.inference_ms)} / ${ocrSafeNumber(task.elapsed_ms)} ms`);
    ocrTaskCell(tr,`${(ocrSafeNumber(task.image_bytes)/1024).toFixed(1)} KB`);
    ocrTaskCell(tr,task.status==="success"?
      `${ocrSafeNumber(task.text_chars)} 字 / ${(ocrSafeNumber(task.confidence)*100).toFixed(1)}%`:"-");
    ocrTaskCell(tr,task.http_status?String(task.http_status):"-");
    ocrTaskCell(tr,OCR_TASK_ERRORS[task.error_code]||task.error_code||"-");
    const actions=document.createElement("td");
    actions.className="ocr-task-actions";
    const view=document.createElement("button");
    view.type="button";
    view.className="secondary";
    view.textContent="查看";
    view.addEventListener("click",()=>viewOcrTask(task.task_id));
    const exportButton=document.createElement("button");
    exportButton.type="button";
    exportButton.className="secondary";
    exportButton.textContent="导出";
    exportButton.addEventListener("click",()=>exportOcrTask(task.task_id));
    actions.append(view,exportButton);
    tr.appendChild(actions);
    tbody.appendChild(tr);
  });
}
function ocrTaskDetailSummaryCell(container,label,value){
  const card=document.createElement("div");
  const caption=document.createElement("span");
  caption.textContent=label;
  const strong=document.createElement("strong");
  strong.textContent=String(value??"-");
  card.append(caption,strong);
  container.appendChild(card);
}
function ocrRenderTaskDetail(data){
  const task=data&&data.task?data.task:{};
  ocrTaskState.detailData=data||null;
  ocrEl("ocrTaskDetailTitle").textContent="OCR 任务详情";
  ocrEl("ocrTaskDetailSubtitle").textContent=task.task_id||"未知任务";
  const summary=ocrEl("ocrTaskDetailSummary");
  summary.replaceChildren();
  ocrTaskDetailSummaryCell(summary,"开始时间",ocrTaskDate(task.started_at));
  ocrTaskDetailSummaryCell(summary,"结束时间",ocrTaskDate(task.finished_at));
  ocrTaskDetailSummaryCell(summary,"状态",OCR_TASK_LABELS[task.status]||task.status||"-");
  ocrTaskDetailSummaryCell(summary,"处理阶段",OCR_TASK_PHASES[task.phase]||task.phase||"-");
  ocrTaskDetailSummaryCell(summary,"识别引擎",task.engine||"-");
  ocrTaskDetailSummaryCell(summary,"HTTP 响应",task.http_status||"-");
  ocrTaskDetailSummaryCell(summary,"图片大小",(ocrSafeNumber(task.image_bytes)/1024).toFixed(1)+" KB");
  ocrTaskDetailSummaryCell(summary,"SHA-256 前缀",task.image_hash_prefix||"-");
  ocrTaskDetailSummaryCell(summary,"排队 / 推理 / 总耗时",ocrSafeNumber(task.queue_ms)+" / "+ocrSafeNumber(task.inference_ms)+" / "+ocrSafeNumber(task.elapsed_ms)+" ms");
  ocrTaskDetailSummaryCell(summary,"识别字数 / 置信度",ocrSafeNumber(task.text_chars)+" 字 / "+(ocrSafeNumber(task.confidence)*100).toFixed(1)+"%");
  ocrTaskDetailSummaryCell(summary,"识别文本处理",task.result_text_redacted?"包含自动脱敏":"未触发脱敏");
  ocrTaskDetailSummaryCell(summary,"原因代码",OCR_TASK_ERRORS[task.error_code]||task.error_code||"-");

  const eventList=ocrEl("ocrTaskDetailEvents");
  eventList.replaceChildren();
  const events=Array.isArray(data&&data.events)?data.events:[];
  if(!events.length){
    const empty=document.createElement("li");
    empty.textContent="没有保存处理事件";
    eventList.appendChild(empty);
  }else{
    events.forEach(event=>{
      const li=document.createElement("li");
      const title=document.createElement("strong");
      title.textContent=ocrTaskDate(event.created_at)+" · "+(OCR_TASK_PHASES[event.stage]||event.stage||"-")+" · "+(OCR_TASK_LABELS[event.status]||event.status||"-");
      const message=document.createElement("small");
      message.textContent=event.message||"-";
      li.append(title,message);
      const metadata=event.metadata&&Object.keys(event.metadata).length?document.createElement("pre"):null;
      if(metadata)metadata.textContent=JSON.stringify(event.metadata,null,2);
      if(metadata)li.appendChild(metadata);
      eventList.appendChild(li);
    });
  }
  const resultText=task.result_text||"没有可用的 OCR 识别文本（任务可能失败、超时或识别结果为空）";
  ocrEl("ocrTaskDetailResult").textContent=resultText;
  ocrEl("ocrTaskDetailExport").disabled=!task.task_id;
}
async function viewOcrTask(taskId){
  if(!taskId)return;
  ocrTaskState.detailTaskId=taskId;
  ocrTaskState.detailData=null;
  const dialog=ocrEl("ocrTaskDetailDialog");
  ocrEl("ocrTaskDetailSubtitle").textContent="正在读取任务详情…";
  ocrEl("ocrTaskDetailSummary").replaceChildren();
  ocrEl("ocrTaskDetailEvents").replaceChildren();
  ocrEl("ocrTaskDetailResult").textContent="正在读取 OCR 结果…";
  ocrEl("ocrTaskDetailExport").disabled=true;
  if(!dialog.open)dialog.showModal();
  try{
    const data=await ocrApi("/api/admin/ocr/tasks/"+encodeURIComponent(taskId));
    if(ocrTaskState.detailTaskId!==taskId)return;
    ocrRenderTaskDetail(data);
  }catch(err){
    if(ocrTaskState.detailTaskId===taskId){
      ocrEl("ocrTaskDetailSubtitle").textContent=err.message||String(err);
      ocrEl("ocrTaskDetailResult").textContent="读取任务详情失败";
    }
  }
}
async function exportOcrTask(taskId){
  if(!taskId)return;
  const button=ocrEl("ocrTaskDetailExport");
  if(button)button.disabled=true;
  try{
    const data=await ocrApi("/api/admin/ocr/tasks/"+encodeURIComponent(taskId));
    const blob=new Blob([JSON.stringify(data,null,2)],{type:"application/json;charset=utf-8"});
    const url=URL.createObjectURL(blob);
    const anchor=document.createElement("a");
    anchor.href=url;
    anchor.download="ocr-task-"+taskId+".json";
    document.body.appendChild(anchor);
    anchor.click();
    anchor.remove();
    setTimeout(()=>URL.revokeObjectURL(url),1000);
  }catch(err){
    const error=ocrEl("ocrTaskError");
    error.textContent=err.message||String(err);
    error.className="ocr-result bad";
  }finally{
    if(button)button.disabled=false;
  }
}
ocrEl("ocrTaskDetailClose").addEventListener("click",()=>ocrEl("ocrTaskDetailDialog").close());
ocrEl("ocrTaskDetailExport").addEventListener("click",()=>{
  if(ocrTaskState.detailTaskId)exportOcrTask(ocrTaskState.detailTaskId);
});

async function loadOcrTasks(){
  const current=++ocrTaskState.generation;
  const refresh=ocrEl("ocrTaskRefreshBtn");
  refresh.disabled=true;
  try{
    const params=new URLSearchParams({
      page:String(ocrTaskState.page),
      page_size:String(ocrTaskState.pageSize),
      status:ocrTaskState.status
    });
    const data=await ocrApi("/api/admin/ocr/tasks?"+params.toString());
    if(current!==ocrTaskState.generation)return;
    const total=Math.max(0,ocrSafeNumber(data.total));
    const pages=Math.max(1,Math.ceil(total/ocrTaskState.pageSize));
    if(ocrTaskState.page>pages){
      ocrTaskState.page=pages;
      await loadOcrTasks();
      return;
    }
    ocrRenderTaskRows(Array.isArray(data.items)?data.items:[]);
    const counts=data.counts||{};
    const all=Object.values(counts).reduce((sum,count)=>sum+ocrSafeNumber(count),0);
    ocrEl("ocrTaskSummary").textContent=
      `已保留 ${all} 条 · 成功 ${ocrSafeNumber(counts.success)} · 超时 ${ocrSafeNumber(counts.timeout)} · 失败 ${ocrSafeNumber(counts.failed)} · 正在执行 ${ocrSafeNumber(counts.running)+ocrSafeNumber(counts.queued)} · 当前筛选 ${total} 条`;
    ocrEl("ocrTaskPageInfo").textContent=`第 ${ocrTaskState.page}/${pages} 页 · 每页 ${ocrTaskState.pageSize} 条`;
    ocrEl("ocrTaskPrev").disabled=ocrTaskState.page<=1;
    ocrEl("ocrTaskNext").disabled=ocrTaskState.page>=pages;
    ocrEl("ocrTaskError").textContent="";
  }catch(err){
    if(current===ocrTaskState.generation){
      ocrEl("ocrTaskError").textContent=err.message||String(err);
      ocrEl("ocrTaskError").className="ocr-result bad";
    }
  }finally{
    if(current===ocrTaskState.generation)refresh.disabled=false;
  }
}
ocrEl("ocrTaskFilter").addEventListener("change",()=>{
  ocrTaskState.status=ocrEl("ocrTaskFilter").value;
  ocrTaskState.page=1;
  loadOcrTasks();
});
ocrEl("ocrTaskRefreshBtn").addEventListener("click",loadOcrTasks);
ocrEl("ocrTaskPrev").addEventListener("click",()=>{
  if(ocrTaskState.page>1){ocrTaskState.page--;loadOcrTasks()}
});
ocrEl("ocrTaskNext").addEventListener("click",()=>{
  ocrTaskState.page++;
  loadOcrTasks();
});
// Refresh active tasks while the administrator is viewing this page.
setInterval(()=>{if(!document.hidden)loadOcrTasks()},15000);

ocrEl("ocrSettingsForm").addEventListener("submit",saveOcrSettings);
ocrEl("ocrResetBtn").addEventListener("click",resetOcrSettings);
ocrEl("ocrRefreshBtn").addEventListener("click",()=>{loadOcrSettings();loadOcrTasks()});
loadOcrSettings();
loadOcrTasks();
