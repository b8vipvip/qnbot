(() => {
  const IDS = { panel:"storeRulePanel", list:"storeRuleList", raw:"storeRuleRawInput", core:"storeRuleCorePrompt", add:"addStoreRuleBtn", save:"saveStoreRulesBtn", hint:"storeRuleHint" };
  let storeState=null, loading=false, timer=null;

  function ensurePanel(){
    if($(IDS.panel)) return;
    const page=$("page-settings"); if(!page) return;
    const panel=document.createElement("section");
    panel.id=IDS.panel; panel.className="panel settings-panel";
    panel.innerHTML=`
      <div class="panel-head"><h3>店铺规则中心</h3><span class="pill gray">云同步</span></div>
      <p class="hint">与 Windows“店铺规则”共用同一份 StorePromptProfile。修改后由现有云同步服务应用到当前店铺；不会上传 API Key、Token 或其他凭据。</p>
      <label class="rule-field"><span>店铺原始资料</span><textarea id="${IDS.raw}" maxlength="50000" rows="4" placeholder="可填写店铺服务范围、商品说明、售后边界等原始资料"></textarea></label>
      <label class="rule-field"><span>核心规则</span><textarea id="${IDS.core}" maxlength="2500" rows="3" placeholder="所有回复必须遵守的核心边界"></textarea></label>
      <div class="panel-head"><h4>结构化规则</h4><button id="${IDS.add}" type="button" class="secondary">新增规则</button></div>
      <div id="${IDS.list}"></div>
      <button id="${IDS.save}" class="primary wide" type="button">保存店铺规则</button>
      <div id="${IDS.hint}" class="hint"></div>`;
    const info=$("clientInfo"), infoPanel=info&&info.closest?info.closest("section.panel"):null;
    if(infoPanel&&infoPanel.parentNode===page) page.insertBefore(panel,infoPanel); else page.appendChild(panel);
    $(IDS.add).onclick=()=>{ addRuleRow({Enabled:true,Scope:"both",Priority:50,Triggers:[]}); };
    $(IDS.save).onclick=saveRules;
  }

  function addRuleRow(rule={}){
    const host=$(IDS.list); if(!host) return;
    const row=document.createElement("article"); row.className="knowledge-card store-rule-card";
    row.innerHTML=`
      <div class="knowledge-card-head"><strong>规则</strong><button type="button" class="danger store-rule-remove">删除</button></div>
      <label class="rule-field"><span>标题</span><input data-k="Title" maxlength="160" value="${esc(rule.Title||"")}"></label>
      <div class="rule-times">
        <label class="rule-field"><span>分类</span><input data-k="Category" maxlength="80" value="${esc(rule.Category||"通用")}"></label>
        <label class="rule-field"><span>范围</span><select data-k="Scope"><option value="both">售前+售后</option><option value="presale">售前</option><option value="aftersale">售后</option></select></label>
      </div>
      <label class="rule-field"><span>触发词</span><input data-k="Triggers" maxlength="1200" value="${esc((rule.Triggers||[]).join(","))}" placeholder="支持,能不能,售后"></label>
      <label class="rule-field"><span>规则内容</span><textarea data-k="Content" maxlength="2200" rows="3">${esc(rule.Content||"")}</textarea></label>
      <div class="rule-times"><label class="rule-field"><span>优先级</span><input data-k="Priority" type="number" min="0" max="1000" value="${Number(rule.Priority||50)}"></label><label class="switch-row"><span><strong>启用</strong></span><input data-k="Enabled" type="checkbox" ${rule.Enabled===false?"":"checked"}></label></div>`;
    row.querySelector('[data-k="Scope"]').value=rule.Scope||"both";
    row.querySelector(".store-rule-remove").onclick=()=>row.remove();
    host.appendChild(row);
  }

  function setForm(profile){
    ensurePanel(); profile=profile||{};
    $(IDS.raw).value=profile.rawInput||"";
    $(IDS.core).value=profile.corePrompt||"";
    $(IDS.list).innerHTML="";
    (Array.isArray(profile.rules)?profile.rules:[]).forEach(addRuleRow);
  }

  function collectProfile(){
    const old=(storeState&&storeState.profile)||{};
    const rules=[...document.querySelectorAll(".store-rule-card")].map((row,i)=>{
      const get=k=>row.querySelector('[data-k="'+k+'"]');
      const title=get("Title").value.trim(), content=get("Content").value.trim();
      if(!title||!content) throw new Error("第 "+(i+1)+" 条规则的标题和内容不能为空");
      return {
        Id:String((old.rules&&old.rules[i]&&old.rules[i].Id)||("web-rule-"+Date.now()+"-"+i)),
        Title:title, Category:get("Category").value.trim()||"通用", Scope:get("Scope").value,
        Priority:Number(get("Priority").value||50), Enabled:get("Enabled").checked,
        Triggers:get("Triggers").value.split(/[，,;；\n]/).map(x=>x.trim()).filter(Boolean).slice(0,20),
        Content:content
      };
    });
    if(rules.length>80) throw new Error("店铺规则最多 80 条");
    return { ...old, schemaVersion:Number(old.schemaVersion||2), rawInput:$(IDS.raw).value.trim(), corePrompt:$(IDS.core).value.trim(), rules };
  }

  function renderMeta(){
    if(!storeState||!$(IDS.hint)) return;
    const rev=Number(storeState.revision||0);
    $(IDS.hint).textContent=rev?("云端版本 "+rev+" · "+(storeState.updated_by==="web"?"手机端更新":"Windows 更新")+" · "+fullTime(storeState.updated_at)):"等待 Windows Bot 首次同步；也可以先在手机端建立规则。";
  }

  async function refresh(showError=false){
    if(loading) return; loading=true;
    try{
      const next=await api("/api/bot-web/store-rules");
      const changed=!storeState||Number(next.revision||0)!==Number(storeState.revision||0);
      storeState=next; ensurePanel();
      if(changed&&(!document.activeElement||!$(IDS.panel).contains(document.activeElement))) setForm(next.profile||{});
      renderMeta();
    }catch(err){if(showError&&err.message!=="登录已失效") toast(err.message)}finally{loading=false}
  }

  async function saveRules(){
    let profile; try{profile=collectProfile()}catch(err){toast(err.message);return}
    const button=$(IDS.save); button.disabled=true;
    try{
      storeState=await api("/api/bot-web/store-rules",{method:"PUT",body:JSON.stringify({profile})});
      setForm(storeState.profile||profile); renderMeta(); toast("店铺规则已保存，等待 Windows Bot 云同步应用");
    }catch(err){toast(err.message)}finally{button.disabled=false}
  }

  function stop(){if(timer)clearInterval(timer);timer=null}
  function start(){stop();ensurePanel();refresh(false);timer=setInterval(()=>{if(!$("appView").classList.contains("hidden"))refresh(false)},4000)}
  const oldLogin=showLogin; showLogin=function(){stop();storeState=null;oldLogin()};
  const oldApp=showApp; showApp=function(name){oldApp(name);start()};
  const oldSettings=renderSettings; renderSettings=function(){oldSettings();ensurePanel();renderMeta()};
  ensurePanel(); if(!$("appView").classList.contains("hidden"))start();
})();