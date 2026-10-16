(function(){
  const embeddedPages={
    wecom:{frameId:"wecomFrame",src:"/static/wecom.html?embedded=1"},
    "recharge-query":{frameId:"rechargeQueryFrame",src:"/static/recharge-query.html?embedded=1"},
    ocr:{frameId:"ocrSettingsFrame",src:"/static/ocr-settings.html?embedded=1"}
  };
  const knownPages=new Set(["dashboard","providers","tests","clients","message-traces","ai-requests","wecom","recharge-query","ocr","version-update","deploy"]);

  if(typeof titles!=="undefined"){
    titles.wecom=["企业微信","配置企业微信应用、加密回调和 AI 转人工策略。"];
    titles["recharge-query"]=["充值结果自动查询","配置充值状态查询、后台访问 Key 和即时测试。"];
    titles.ocr=["服务端 OCR","配置 RapidOCR/ONNXRuntime 的启用状态、图片限制、超时、并发和文本上限。"];
  }

  function makePrimaryButton(anchor,page){
    if(!anchor||!anchor.parentNode)return null;
    const button=document.createElement("button");
    button.className=anchor.className||"nav";
    button.type="button";
    button.dataset.page=page;
    button.textContent=anchor.textContent;
    anchor.replaceWith(button);
    button.addEventListener("click",()=>navigate(page));
    return button;
  }

  function insertPrimaryButton(page,label){
    if(document.querySelector(`button.nav[data-page="${page}"]`))return;
    const nav=document.querySelector("#appView .sidebar nav");
    if(!nav)return;
    const button=document.createElement("button");
    button.className="nav";
    button.type="button";
    button.dataset.page=page;
    button.textContent=label;
    const before=nav.querySelector('button.nav[data-page="version-update"]')||nav.querySelector('button.nav[data-page="deploy"]');
    if(before)nav.insertBefore(button,before);else nav.appendChild(button);
    button.addEventListener("click",()=>navigate(page));
  }

  // Promote configuration utilities from standalone secondary pages to the same first-level
  // navigation as dashboard/providers/tests. The legacy HTML remains as the content source.
  makePrimaryButton(document.querySelector('a.nav[href="/static/wecom.html"]'),"wecom");
  makePrimaryButton(document.querySelector('a.nav[href="/static/recharge-query.html"]'),"recharge-query");
  insertPrimaryButton("ocr","服务端 OCR");

  // app.js historically attaches switchPage() to every .nav element, including real links.
  // Restore normal anchor behaviour for Bot Web and any future external first-level entry.
  document.querySelectorAll("a.nav:not([data-page])").forEach(link=>{link.onclick=null;});

  function ensureSections(){
    const main=document.querySelector("#appView main.main");
    if(!main)return;
    const deploy=document.getElementById("page-deploy");
    Object.entries(embeddedPages).forEach(([page,config])=>{
      if(document.getElementById(`page-${page}`))return;
      const section=document.createElement("section");
      section.id=`page-${page}`;
      section.className="page";
      const frame=document.createElement("iframe");
      frame.id=config.frameId;
      frame.title=page==="wecom"?"企业微信配置":page==="recharge-query"?"充值结果自动查询配置":"服务端 OCR 配置";
      frame.loading="lazy";
      frame.style.cssText="display:block;width:100%;height:760px;min-height:720px;border:0;background:transparent;";
      section.appendChild(frame);
      if(deploy)main.insertBefore(section,deploy);else main.appendChild(section);
    });
  }

  function hideLegacyChildShell(frame,page){
    try{
      const doc=frame.contentDocument;
      if(!doc)return;
      const aside=doc.querySelector("aside");
      if(aside)aside.style.display="none";
      const shell=doc.querySelector(".shell");
      if(shell){
        shell.style.display="block";
        shell.style.minHeight="0";
      }
      const main=doc.querySelector("main");
      if(main){
        main.style.maxWidth="none";
        main.style.width="100%";
        main.style.margin="0";
        main.style.padding="0 2px 8px";
      }
      doc.documentElement.style.background="transparent";
      doc.body.style.background="transparent";
      if(page==="wecom"){
        const header=doc.querySelector("header.top");
        if(header)header.style.display="none";
      }else if(page==="recharge-query"&&main){
        const heading=main.querySelector(":scope > h1");
        if(heading)heading.style.display="none";
      }else if(page==="ocr"){
        const header=doc.querySelector("header.ocr-page-head");
        if(header)header.style.display="none";
      }

      const resize=()=>{
        try{
          const height=Math.max(720,doc.documentElement.scrollHeight,doc.body?doc.body.scrollHeight:0);
          frame.style.height=(height+12)+"px";
        }catch{}
      };
      resize();
      if(typeof ResizeObserver!=="undefined"){
        if(frame._consoleResizeObserver)frame._consoleResizeObserver.disconnect();
        const observer=new ResizeObserver(resize);
        observer.observe(doc.documentElement);
        frame._consoleResizeObserver=observer;
      }else{
        setTimeout(resize,300);
        setTimeout(resize,1200);
      }
    }catch(err){
      console.warn("嵌入控制台页面样式同步失败",err);
    }
  }

  function ensureEmbeddedPage(page){
    const config=embeddedPages[page];
    if(!config)return;
    ensureSections();
    const frame=document.getElementById(config.frameId);
    if(!frame)return;
    if(!frame.dataset.loaded){
      frame.dataset.loaded="1";
      frame.addEventListener("load",()=>hideLegacyChildShell(frame,page));
      frame.src=config.src;
    }
  }

  function routeUrl(page){
    return page==="dashboard"?"/":`/?page=${encodeURIComponent(page)}`;
  }

  function navigate(page,updateHistory=true){
    if(!knownPages.has(page)||typeof switchPage!=="function")return;
    ensureEmbeddedPage(page);
    switchPage(page);
    if(updateHistory)history.replaceState({page},"",routeUrl(page));
  }

  ensureSections();

  // Keep existing SPA entries addressable so a user can leave an embedded utility page and
  // jump directly to Providers/Tests/Clients instead of first returning to the dashboard.
  document.querySelectorAll("button.nav[data-page]").forEach(button=>{
    if(button.dataset.consoleRouteBound==="1")return;
    button.dataset.consoleRouteBound="1";
    button.addEventListener("click",()=>{
      const page=button.dataset.page;
      if(knownPages.has(page)){
        ensureEmbeddedPage(page);
        history.replaceState({page},"",routeUrl(page));
      }
    });
  });

  const requested=new URLSearchParams(location.search).get("page");
  if(requested&&knownPages.has(requested)&&requested!=="dashboard")navigate(requested,false);

  window.addEventListener("popstate",()=>{
    const page=new URLSearchParams(location.search).get("page")||"dashboard";
    if(knownPages.has(page))navigate(page,false);
  });
})();
