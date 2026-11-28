// Native Knowledge V2 operations. All remote mutations use the selected
// device's own V2 service and explicitly confirmed, server-claimed commands.
let v2BulkIds=new Set(),v2BulkRevision=null,v2BulkBusy=false;
function isV2Selected(id){return v2BulkIds.has(id)}
function checkV2BulkRevision(revision){
  if(v2BulkRevision!==revision){v2BulkIds.clear();v2BulkRevision=revision}
}
function renderV2BulkSelection(){
  $("knowledgeV2SelectedCount").textContent="已选 "+v2BulkIds.size+" 条";
  for(const id of ["knowledgeV2BulkDisable","knowledgeV2BulkEnable","knowledgeV2BulkDelete"])
    $(id).disabled=v2BulkBusy||!v2BulkIds.size;
}
$("knowledgeV2List").addEventListener("change",e=>{
  const id=e.target.dataset.v2SelectId;if(!id)return;
  if(e.target.checked)v2BulkIds.add(id);else v2BulkIds.delete(id);renderV2BulkSelection();
});
$("knowledgeV2SelectAll").onclick=()=>{
  for(const el of $("knowledgeV2List").querySelectorAll("[data-v2-select-id]")){v2BulkIds.add(el.dataset.v2SelectId);el.checked=true}
  renderV2BulkSelection();
};
$("knowledgeV2SelectNone").onclick=()=>{v2BulkIds.clear();renderKnowledgeV2({})};
async function v2BulkApply(action){
  if(v2BulkBusy||!v2BulkIds.size||v2BulkRevision!==state.knowledgeV2Revision)return;
  const expected=v2BulkRevision,ids=new Set(v2BulkIds),selected=state.knowledgeV2.filter(x=>ids.has(x.Id)&&x.Status!=="deleted");
  if(selected.length!==ids.size){toast("选中知识已变化，请重新选择");await loadKnowledgeV2(false);return}
  // Enabling candidates must go through the actual revision/review path, never
  // change candidate provenance or resurrect a deletion from a bulk button.
  if(action==="enable"&&selected.some(x=>!["active","disabled"].includes(x.Status)||x.Type==="learning_candidate"||/candidate/i.test(x.SourceType||""))){
    toast("选择中包含候选或其他非生产知识，不能批量启用");return;
  }
  const label={enable:"启用",disable:"停用",delete:"删除"}[action];
  if(!confirm(label+"所选 "+selected.length+" 条知识？全部修改将在同一云端版本中提交。"))return;
  v2BulkBusy=true;renderV2BulkSelection();
  try{
    const now=new Date().toISOString();
    const records=state.knowledgeV2.map(x=>!ids.has(x.Id)?x:action==="delete"?{...x,Enabled:false,Status:"deleted",UpdatedAt:now}:action==="disable"?{...x,Enabled:false,Status:x.Status==="active"?"disabled":x.Status,UpdatedAt:now}:{...x,Enabled:true,Status:"active",UpdatedAt:now});
    const r=await api("/api/bot-web/knowledge-v2",{method:"PUT",body:JSON.stringify({records,expected_revision:expected})});
    state.knowledgeV2LoadToken++;state.knowledgeV2=r.records||[];v2BulkIds.clear();renderKnowledgeV2(r);toast(label+"完成，等待 Windows 同步");
  }catch(err){toast(err.status===409?"云端知识已变化，未应用批量操作":err.message);await loadKnowledgeV2(false)}
  finally{v2BulkBusy=false;renderV2BulkSelection()}
}
$("knowledgeV2BulkDisable").onclick=()=>v2BulkApply("disable");
$("knowledgeV2BulkEnable").onclick=()=>v2BulkApply("enable");
$("knowledgeV2BulkDelete").onclick=()=>v2BulkApply("delete");
const v2Ops={id:null,epoch:0,timer:null,busy:false,checkpoint:null,checkpointDevice:null,task:null};
const v2OpsBase="/api/bot-web/knowledge-v2/operations";
const v2OpsActive=s=>["pending","running","cancel_requested"].includes(s);
const v2OpsLabels={history_status:"读取历史断点",history_scan:"历史扫描",history_resume:"继续扫描",revision_generate:"生成修订候选",revision_list:"修订候选",revision_apply:"应用修订",revision_reject:"驳回修订",conflict_list:"事实冲突",conflict_resolve:"解决冲突",rollback_list:"可回滚修订",rollback:"回滚修订"};
const v2OpsStatuses={pending:"等待设备领取",running:"执行中",cancel_requested:"等待设备取消",completed:"已完成",failed:"失败",cancelled:"已取消",abandoned:"已放弃（结果未知）"};
const v2OpsHint=t=>$("v2OpsStatus").textContent=t;
function resetV2Operations(){
  v2Ops.epoch++;clearTimeout(v2Ops.timer);v2Ops.id=null;v2Ops.task=null;v2Ops.busy=false;v2Ops.checkpoint=null;v2Ops.checkpointDevice=null;
  v2BulkIds.clear();v2BulkRevision=null;state.knowledgeV2LoadToken++;state.knowledgeV2=[];state.knowledgeV2Revision=0;state.knowledgeV2Feedback=null;
  $("v2OpsResult").replaceChildren();$("v2OpsTaskList").replaceChildren();$("v2OpsDevice").replaceChildren(new Option("请选择 Windows 设备",""));
  v2OpsHint("请选择设备。");updateV2OpsControls();
}
function updateV2OpsControls(){
  const busy=v2Ops.busy||v2OpsActive(v2Ops.task?.status);
  for(const id of ["v2OpsHistoryStatus","v2OpsHistoryScan","v2OpsRevisionGenerate","v2OpsRevisionList","v2OpsConflictList","v2OpsRollbackList"])$(id).disabled=busy;
  $("v2OpsHistoryResume").disabled=busy||!v2Ops.checkpoint||v2Ops.checkpointDevice!==$("v2OpsDevice").value;
  $("v2OpsCancel").disabled=!v2Ops.task||!(v2Ops.task.status==="pending"||(["running","cancel_requested"].includes(v2Ops.task.status)&&["history_scan","history_resume","revision_generate"].includes(v2Ops.task.action)));
  $("v2OpsAbandon").disabled=!["running","cancel_requested"].includes(v2Ops.task?.status);
}
$("v2OpsDevice").onchange=()=>{v2Ops.checkpoint=null;v2Ops.checkpointDevice=null;$("v2OpsResult").replaceChildren();updateV2OpsControls()};
async function loadV2Operations(){
  const epoch=v2Ops.epoch;
  try{
    const [devices,history]=await Promise.all([api("/api/bot-web/knowledge-v2/devices"),api(v2OpsBase)]);
    if(epoch!==v2Ops.epoch)return;
    const chosen=$("v2OpsDevice").value;
    $("v2OpsDevice").replaceChildren(new Option("请选择 Windows 设备",""),...(devices.items||[]).map(x=>new Option("设备 "+x.id.slice(0,8)+(x.online?" · 在线":" · 离线"),x.id)));
    if((devices.items||[]).some(x=>x.id===chosen))$("v2OpsDevice").value=chosen;
    else if(devices.items?.length===1)$("v2OpsDevice").value=devices.items[0].id;
    $("v2OpsTaskList").replaceChildren(...(history.items||[]).map(x=>{
      const b=document.createElement("button");b.type="button";b.textContent="#"+x.command_id+" "+(v2OpsLabels[x.action]||x.action)+" · "+(v2OpsStatuses[x.status]||x.status);
      b.onclick=()=>pollV2Operation(x.command_id,true);return b;
    }));
    const active=(history.items||[]).find(x=>v2OpsActive(x.status));
    if(active&&!v2OpsActive(v2Ops.task?.status))pollV2Operation(active.command_id,true);
    updateV2OpsControls();
  }catch(err){if(epoch===v2Ops.epoch)v2OpsHint("设备/任务读取失败："+err.message)}
}
function v2OpsCard(title,lines,buttons=[]){
  const card=document.createElement("article");card.className="knowledge-card";
  const h=document.createElement("h4");h.textContent=title;card.append(h);
  for(const line of lines){const d=document.createElement("div");d.className="knowledge-answer";d.textContent=String(line??"");card.append(d)}
  for(const [label,handler] of buttons){const b=document.createElement("button");b.type="button";b.textContent=label;b.onclick=handler;card.append(b)}
  return card;
}
async function startV2Operation(action,args={},confirmed=false,device=null){
  if(v2Ops.busy||v2OpsActive(v2Ops.task?.status)){toast("已有知识操作执行中");return}
  const selected=device||$("v2OpsDevice").value;
  if(!selected){toast("请选择执行设备");return}
  const epoch=v2Ops.epoch;v2Ops.busy=true;updateV2OpsControls();
  try{
    const r=await api(v2OpsBase,{method:"POST",body:JSON.stringify({action,confirmed,device_id:selected,...args})});
    if(epoch!==v2Ops.epoch)return;
    v2Ops.task={status:"pending",action,device_id:selected};await pollV2Operation(r.command_id,true);
  }catch(err){if(epoch!==v2Ops.epoch)return;v2OpsHint("操作提交失败："+err.message);if(err.status===409&&err.detail?.command_id)await pollV2Operation(err.detail.command_id,true)}
  finally{if(epoch===v2Ops.epoch){v2Ops.busy=false;updateV2OpsControls()}}
}
function renderV2OperationResult(task){
  const data=task.result||{},list=$("v2OpsResult"),device=task.device_id,items=data.items||[];
  if(task.action==="history_status"||task.action==="history_scan"||task.action==="history_resume"){
    const cp=data.checkpoint;
    v2Ops.checkpoint=cp?.Id||data.checkpoint_id||null;v2Ops.checkpointDevice=device;v2Ops.learnKnowledge=cp?.LearnKnowledge??data.learn_knowledge??false;
    list.replaceChildren(v2OpsCard("历史扫描状态",cp?["断点："+cp.Id,"已完成联系人："+cp.Completed+"/"+cp.Total,"状态："+cp.State,"学习模式："+(cp.LearnKnowledge?"扫描并学习":"仅归档"),"有效消息："+cp.ObservedTurns,"已分析问答："+cp.AnalyzedHumanPairs,"已发布："+cp.PublishedKnowledge]:[data.coverage_warning||"本机暂无可恢复断点","归档买家消息："+(data.messages??0),"全部有效消息："+(data.observed_turns??0),"已分析问答："+(data.analyzed_pairs??0),"模型返回证据轮次："+(data.model_referenced_pairs??0),"发布："+(data.published??0)+" · 候选："+(data.candidates??0),"成功联系人："+(data.contacts??0)+" / "+(data.total_contacts??0),"失败："+(data.failed??0),"全量覆盖证明："+(data.coverage_verified?"已验证":"未验证")]));
  }else if(task.action==="revision_list"){
    list.replaceChildren(...items.map(x=>v2OpsCard(x.KnowledgeTitle||x.KnowledgeId,["原答案："+x.OriginalAnswer,"建议："+x.ProposedAnswer,"证据："+x.EvidenceCount+" 条 / 买家："+x.DistinctBuyerCount+" · "+x.Status],x.Status==="pending"?[
      ["应用",()=>{if(confirm("确认应用此修订？"))startV2Operation("revision_apply",{candidate_id:x.Id},true,device)}],
      ["驳回",()=>{if(confirm("确认驳回此修订？"))startV2Operation("revision_reject",{candidate_id:x.Id,reason:"Web人工驳回"},true,device)}]]:[])));
  }else if(task.action==="conflict_list"){
    list.replaceChildren(...items.flatMap(x=>(x.Records||[]).map(y=>v2OpsCard(x.Subject+" / "+x.Predicate,[y.Title,y.Answer],[
      ["保留此答案并停用其他",()=>{if(confirm("确认解决此冲突？"))startV2Operation("conflict_resolve",{fact_key:x.FactKey,knowledge_id:y.Id,fingerprint:data.fingerprint},true,device)}],
      ["其余转为候选",()=>{if(confirm("确认保留此答案并将其余转为禁用候选？"))startV2Operation("conflict_resolve",{fact_key:x.FactKey,knowledge_id:y.Id,fingerprint:data.fingerprint,reason:"candidate"},true,device)}]]))));
  }else if(task.action==="rollback_list"){
    list.replaceChildren(...items.map(x=>v2OpsCard(x.title,["当前："+x.current_answer,"回滚为："+x.previous_answer],[
      ["回滚此修订",()=>{if(confirm("确认回滚此修订？"))startV2Operation("rollback",{knowledge_id:x.knowledge_id,revision_id:x.revision_id,fingerprint:x.fingerprint},true,device)}]])));
  }else{
    list.replaceChildren(v2OpsCard("操作结果",Object.entries(data).map(([k,v])=>k+"："+String(v))));
    if(["revision_apply","conflict_resolve","rollback"].includes(task.action))loadKnowledgeV2(false);
  }
  if(!list.childNodes.length)list.append(v2OpsCard("暂无记录",[]));
}
async function pollV2Operation(id,select=false){
  if(select){clearTimeout(v2Ops.timer);v2Ops.id=id}
  const epoch=v2Ops.epoch;
  try{
    const task=await api(v2OpsBase+"/"+Number(id));
    if(epoch!==v2Ops.epoch||v2Ops.id!==id)return;
    v2Ops.task=task;const p=task.result||{};
    v2OpsHint("#"+id+" "+(v2OpsLabels[task.action]||task.action)+" · "+(v2OpsStatuses[task.status]||task.status)+(p.phase?" · "+p.phase+" · 联系人 "+(p.contacts??0)+"/"+(p.total_contacts??0)+" · 消息 "+(p.messages??0):"")+(task.error?" · "+task.error:""));
    if(v2OpsActive(task.status))v2Ops.timer=setTimeout(()=>pollV2Operation(id),2500);
    else if(task.status==="completed")renderV2OperationResult(task);
    updateV2OpsControls();
  }catch(err){if(epoch===v2Ops.epoch&&v2Ops.id===id){v2OpsHint("任务查询失败："+err.message);v2Ops.timer=setTimeout(()=>pollV2Operation(id),5000)}}
}
$("v2OpsRefresh").onclick=loadV2Operations;
$("v2OpsHistoryStatus").onclick=()=>startV2Operation("history_status",{learn_knowledge:$("v2OpsLearnKnowledge").checked});
$("v2OpsHistoryScan").onclick=()=>{const n=Number($("v2OpsMaxContacts").value);if(!Number.isInteger(n)||n<1||n>5000){toast("联系人上限须为1至5000");return}if(confirm("开始扫描所选设备的千牛历史消息？扫描会切换聊天窗口。"))startV2Operation("history_scan",{max_contacts:n,learn_knowledge:$("v2OpsLearnKnowledge").checked},true)};
$("v2OpsHistoryResume").onclick=()=>{if(v2Ops.checkpoint&&v2Ops.checkpointDevice===$("v2OpsDevice").value&&confirm("继续原设备的历史扫描断点？"))startV2Operation("history_resume",{checkpoint_id:v2Ops.checkpoint,learn_knowledge:v2Ops.learnKnowledge},true,v2Ops.checkpointDevice)};
$("v2OpsRevisionGenerate").onclick=()=>{if(confirm("基于真实人工反馈生成修订候选？"))startV2Operation("revision_generate",{},true)};
$("v2OpsRevisionList").onclick=()=>startV2Operation("revision_list");
$("v2OpsConflictList").onclick=()=>startV2Operation("conflict_list");
$("v2OpsRollbackList").onclick=()=>startV2Operation("rollback_list");
async function changeV2Task(action){
  if(!v2Ops.id||!confirm(action==="abandon"?"确认放弃失联任务？原任务执行结果可能未知，请先查看设备和知识状态。":"确认取消当前任务？"))return;
  try{await api(v2OpsBase+"/"+v2Ops.id+"/"+action,{method:"POST"});await pollV2Operation(v2Ops.id,true)}catch(err){toast(err.message)}
}
$("v2OpsCancel").onclick=()=>changeV2Task("cancel");
$("v2OpsAbandon").onclick=()=>changeV2Task("abandon");
renderV2BulkSelection();updateV2OpsControls();
