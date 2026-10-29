titles["server-runtime"]=["服务端运行日志","查看服务器应用、主机更新和 Docker 镜像构建的脱敏日志。"];
let serverRuntimeLoading=false;
async function loadServerRuntimeLogs(){
  if(serverRuntimeLoading||!document.getElementById("serverRuntimeSource"))return;
  serverRuntimeLoading=true;
  const source=document.getElementById("serverRuntimeSource").value;
  const lines=Number(document.getElementById("serverRuntimeLines").value||300);
  const meta=document.getElementById("serverRuntimeMeta");
  const out=document.getElementById("serverRuntimeOutput");
  try{
    const data=await api("/api/admin/server-runtime/logs?source="+encodeURIComponent(source)+"&lines="+lines);
    const age=data.modified_at_unix?Math.max(0,Math.round(Date.now()/1000-data.modified_at_unix)):null;
    meta.textContent=(data.available?"已读取":"尚无日志")
      +" · "+(data.lines||[]).length+" 行"
      +" · 文件大小 "+vuBytes(data.size_bytes)
      +" · 最后写入 "+(age===null?"未知":age+" 秒前")
      +" · 只读取末尾 "+vuBytes(data.truncated_to_last_bytes);
    out.textContent=(data.lines||[]).join("\n")||"暂无记录。应用日志仅从安装本版本后开始保存；旧容器历史请在服务器使用 docker logs 查看。";
  }catch(err){meta.textContent="读取失败："+err.message;out.textContent="";}
  finally{serverRuntimeLoading=false;}
}
function downloadServerRuntimeLogs(){
  const source=document.getElementById("serverRuntimeSource").value;
  const lines=Number(document.getElementById("serverRuntimeLines").value||300);
  window.open("/api/admin/server-runtime/logs/download?source="+encodeURIComponent(source)+"&lines="+lines,"_blank","noopener");
}
const previousServerRuntimeRefresh=refreshCurrent;
refreshCurrent=async function(){
  if(state.currentPage==="server-runtime")return loadServerRuntimeLogs();
  return previousServerRuntimeRefresh();
};
