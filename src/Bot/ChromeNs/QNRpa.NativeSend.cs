using BotLib;
using Newtonsoft.Json;
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Bot.ChromeNs
{
    public partial class QNRpa
    {
        private const uint WmMouseMove = 0x0200;
        private const uint WmLButtonDown = 0x0201;
        private const uint WmLButtonUp = 0x0202;
        private const int MkLButton = 0x0001;
        private const uint GaRoot = 2;
        private const uint CwpSkipInvisible = 0x0001;
        private const uint CwpSkipDisabled = 0x0002;
        private const uint CwpSkipTransparent = 0x0004;
        private const uint ProcessQueryLimitedInformation = 0x1000;
        private const uint TokenQuery = 0x0008;
        private const int TokenIntegrityLevel = 25;

        [StructLayout(LayoutKind.Sequential)]
        private struct NativePoint
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SidAndAttributes
        {
            public IntPtr Sid;
            public int Attributes;
        }

        [DllImport("user32.dll")]
        private static extern IntPtr WindowFromPoint(NativePoint point);

        [DllImport("user32.dll")]
        private static extern IntPtr ChildWindowFromPointEx(IntPtr hwndParent, NativePoint point, uint flags);

        [DllImport("user32.dll")]
        private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool ScreenToClient(IntPtr hwnd, ref NativePoint point);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint access, bool inheritHandle, uint processId);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool GetTokenInformation(
            IntPtr tokenHandle,
            int tokenInformationClass,
            IntPtr tokenInformation,
            int tokenInformationLength,
            out int returnLength);

        [DllImport("advapi32.dll")]
        private static extern IntPtr GetSidSubAuthorityCount(IntPtr sid);

        [DllImport("advapi32.dll")]
        private static extern IntPtr GetSidSubAuthority(IntPtr sid, uint subAuthority);

        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr handle);

        /// <summary>
        /// Preferred text-send pipeline. It never guesses an undocumented IMSDK API and never
        /// invokes the whole Qt split button. First try a send control exposed inside the injected
        /// web page, then a HWND-targeted left-button message at the already verified safe point,
        /// then a proven left/main-action UIA child, and only then retain the legacy physical
        /// coordinate fallback. Every transition revalidates the exact Bot-owned draft to prevent
        /// double sends. A verified send action followed by a stable empty composer is accepted as
        /// a Qianniu submission even if the real-time seller echo is late/missing, so the same text
        /// is never re-written merely because transport evidence arrived late.
        /// </summary>
        private async Task<bool> TrySendTextNativeFirstAsync(string buyer, string text, DateTime sendStart)
        {
            if (!await HasExpectedDraftFastAsync(text, 1000).ConfigureAwait(false))
            {
                SetSendFailure("原生发送前草稿确认", "无法确认输入框仍为本次Bot完整草稿");
                return false;
            }

            if (await StopIfPlatformSendBlockedAsync(buyer, text, "发送前").ConfigureAwait(false)) return false;

            var domTriggered = await TryTriggerSendViaCdpDomAsync(buyer).ConfigureAwait(false);
            if (domTriggered)
            {
                if (await WaitForTextSubmissionAcceptedAsync(
                    buyer, text, sendStart, "CDP页面发送按钮", 1700).ConfigureAwait(false))
                {
                    return true;
                }
                if (LastSendWasCancelled) return false;

                if (!await HasExpectedDraftFastAsync(text, 800).ConfigureAwait(false))
                {
                    SetSendFailure("CDP页面发送按钮后确认", "发送动作后草稿已不存在，但未能完成安全提交确认；禁止重复写入");
                    return false;
                }
                ResetSendFailure();
            }

            if (!await HasExpectedDraftFastAsync(text, 900).ConfigureAwait(false))
            {
                SetSendFailure("原生发送切换前确认", "未执行新的发送动作且本次草稿已不存在，禁止继续发送");
                return false;
            }

            var hwndPosted = await RunUiActionAsync(
                TryPostSafeMainSendMouseMessage,
                "发送按钮HWND安全消息",
                UiActionTimeoutMs).ConfigureAwait(false);
            if (hwndPosted)
            {
                if (await WaitForTextSubmissionAcceptedAsync(
                    buyer, text, sendStart, "发送按钮HWND安全消息", 1800).ConfigureAwait(false))
                {
                    return true;
                }
                if (LastSendWasCancelled) return false;
                if (!await HasExpectedDraftFastAsync(text, 800).ConfigureAwait(false))
                {
                    SetSendFailure("HWND安全消息后确认", "发送动作后草稿已不存在，但未能完成安全提交确认；禁止重复写入");
                    return false;
                }
                ResetSendFailure();
            }

            if (!await HasExpectedDraftFastAsync(text, 900).ConfigureAwait(false))
            {
                SetSendFailure("安全UIA回退前确认", "本次精确草稿已不存在，禁止执行第二次发送动作");
                return false;
            }

            var safeUiaInvoked = await RunUiActionAsync(
                TryInvokeCachedSendButtonNow,
                "发送按钮左侧UIA安全调用（原生前置）",
                UiActionTimeoutMs).ConfigureAwait(false);
            if (safeUiaInvoked)
            {
                if (await WaitForTextSubmissionAcceptedAsync(
                    buyer, text, sendStart, "发送按钮左侧UIA安全调用（原生前置）", 1800).ConfigureAwait(false))
                {
                    return true;
                }
                if (LastSendWasCancelled) return false;
                if (!await HasExpectedDraftFastAsync(text, 800).ConfigureAwait(false))
                {
                    SetSendFailure("安全UIA调用后确认", "发送动作后草稿已不存在，但未能完成安全提交确认；禁止重复写入");
                    return false;
                }
                ResetSendFailure();
            }

            if (!await HasExpectedDraftFastAsync(text, 900).ConfigureAwait(false))
            {
                SetSendFailure("物理/UIA兼容回退前确认", "本次精确草稿已不存在，禁止执行兼容发送动作");
                return false;
            }

            var uiResult = await TrySendTextViaUiaAsync(buyer, text, sendStart).ConfigureAwait(false);
            if (uiResult) return true;
            if (await StopIfPlatformSendBlockedAsync(buyer, text, "UIA发送后").ConfigureAwait(false)) return false;

            if (!await HasExpectedDraftFastAsync(text, 650).ConfigureAwait(false))
            {
                var accepted = await WaitForTextSubmissionAcceptedAsync(
                    buyer, text, sendStart, "UIA兼容发送后提交确认", 1300).ConfigureAwait(false);
                if (accepted) return true;
            }

            if (_lastSendButtonCoordinateClickRejected)
            {
                LogInputIntegrityDiagnostic("物理坐标发送被系统拒绝");
            }
            return false;
        }

        private async Task<bool> TryTriggerSendViaCdpDomAsync(string buyer)
        {
            if (_qn == null || _qn.CDP == null) return false;
            const string expression = @"(function(){
  try {
    var norm=function(v){return String(v||'').replace(/\s+/g,' ').trim();};
    var lower=function(v){return norm(v).toLowerCase();};
    var isSend=function(v){var s=norm(v);return s==='发送'||s==='發送'||s.toLowerCase()==='send';};
    var bad=function(el){var s=lower((el.id||'')+' '+(el.className||'')+' '+(el.getAttribute&&el.getAttribute('aria-label')||'')+' '+(el.getAttribute&&el.getAttribute('title')||''));return /arrow|dropdown|drop-down|menu|more|chevron|downbutton|下拉|展开/.test(s);};
    var visible=function(el){if(!el||!el.getBoundingClientRect)return false;var r=el.getBoundingClientRect();var st=window.getComputedStyle?getComputedStyle(el):null;return r.width>=18&&r.height>=14&&(!st||st.display!=='none'&&st.visibility!=='hidden'&&Number(st.opacity||1)>0);};
    var nodes=document.querySelectorAll('button,[role=button],[aria-label],[title],a,input[type=button],input[type=submit]');
    var hits=[];
    for(var i=0;i<nodes.length;i++){
      var el=nodes[i]; if(bad(el)||!visible(el))continue;
      var label=norm((el.getAttribute&&el.getAttribute('aria-label'))||'')||norm((el.getAttribute&&el.getAttribute('title'))||'')||norm(el.value)||norm(el.innerText)||norm(el.textContent);
      if(!isSend(label))continue;
      var r=el.getBoundingClientRect(); hits.push({el:el,score:(r.top*10000+r.left),label:label,rect:[Math.round(r.left),Math.round(r.top),Math.round(r.width),Math.round(r.height)]});
    }
    if(!hits.length)return '__QNBOT_DOM_SEND__:not_found';
    hits.sort(function(a,b){return b.score-a.score;});
    var hit=hits[0], el=hit.el;
    try{el.focus&&el.focus({preventScroll:true});}catch(_e){}
    try{el.dispatchEvent(new MouseEvent('mousedown',{bubbles:true,cancelable:true,view:window,button:0}));}catch(_e){}
    try{el.dispatchEvent(new MouseEvent('mouseup',{bubbles:true,cancelable:true,view:window,button:0}));}catch(_e){}
    if(typeof el.click==='function')el.click();else el.dispatchEvent(new MouseEvent('click',{bubbles:true,cancelable:true,view:window,button:0}));
    return '__QNBOT_DOM_SEND__:clicked:'+hit.label+':'+hit.rect.join(',');
  } catch(e) { return '__QNBOT_DOM_SEND__:error:'+String(e&&e.message||e); }
})()";

            try
            {
                var response = await _qn.CDP.EvaluateExpressionAsync(
                    expression,
                    "发送当前草稿-CDP页面安全按钮").ConfigureAwait(false);
                response = response ?? string.Empty;
                if (response.IndexOf("__QNBOT_DOM_SEND__:clicked:", StringComparison.Ordinal) >= 0)
                {
                    Log.Info("已触发CDP页面内可验证发送按钮: seller=" + SellerNick
                        + ", buyer=" + buyer + ", response=" + ShortNative(response, 220));
                    return true;
                }
                Log.Info("CDP页面未发现可验证的独立发送按钮，继续HWND/UIA安全链路: seller="
                    + SellerNick + ", buyer=" + buyer + ", response=" + ShortNative(response, 180));
            }
            catch (Exception ex)
            {
                Log.Info("CDP页面发送探测失败，继续安全回退: " + ex.Message);
            }
            return false;
        }

        private bool TryPostSafeMainSendMouseMessage()
        {
            if (_sendMessageButton == null && _sendMessageButtonRect.IsEmpty) return false;
            var desk = ResolveSellerDesk();
            if (desk == null || !EnsureSellerDeskBinding(false)) return false;

            desk.BringTop();
            Thread.Sleep(100);

            var rect = _sendMessageButtonRect;
            if ((rect.Width <= 0 || rect.Height <= 0) && _sendMessageButton != null)
            {
                try { rect = _sendMessageButton.BoundingRectangle; } catch { }
            }
            if (rect.Width <= 0 || rect.Height <= 0) return false;

            var arrowGuard = Math.Max(18, Math.Min(30, rect.Width / 3));
            var mainWidth = rect.Width - arrowGuard;
            if (mainWidth < 16) return false;
            var screenPoint = new NativePoint
            {
                X = rect.Left + Math.Max(8, Math.Min(mainWidth / 2, mainWidth - 8)),
                Y = rect.Top + rect.Height / 2
            };

            var expectedPid = unchecked((uint)desk.ProcessId);
            var expectedRoot = new IntPtr(desk.Hwnd.Handle);
            if (expectedPid == 0 || expectedRoot == IntPtr.Zero) return false;

            uint rootPid;
            GetWindowThreadProcessId(expectedRoot, out rootPid);
            if (rootPid == 0 || rootPid != expectedPid)
            {
                SetSendFailure("HWND安全发送", "当前卖家根窗口进程归属已漂移，拒绝发送");
                Log.Info("HWND安全发送已阻止卖家根窗口进程漂移: seller=" + SellerNick
                    + ", expectedPid=" + expectedPid + ", rootPid=" + rootPid
                    + ", expectedRoot=" + expectedRoot);
                return false;
            }

            var target = WindowFromPoint(screenPoint);
            var root = target == IntPtr.Zero ? IntPtr.Zero : GetAncestor(target, GaRoot);
            if (root == IntPtr.Zero && target != IntPtr.Zero) root = target;

            // Production 1.1.1192 evidence showed WindowFromPoint can be intercepted by an
            // unrelated overlay even while the verified Qianniu seller root and UIA send-button
            // rectangle are correct. Do not weaken the cross-root check. Instead, resolve the same
            // already-verified safe point strictly inside expectedRoot and then re-prove the root.
            if (target == IntPtr.Zero || root != expectedRoot)
            {
                var outsideTarget = target;
                var outsideRoot = root;
                var constrained = ResolveTargetInsideVerifiedSellerRoot(expectedRoot, screenPoint);
                var constrainedRoot = constrained == IntPtr.Zero ? IntPtr.Zero : GetAncestor(constrained, GaRoot);
                if (constrainedRoot == IntPtr.Zero && constrained != IntPtr.Zero) constrainedRoot = constrained;
                if (constrained != IntPtr.Zero && constrainedRoot == expectedRoot)
                {
                    target = constrained;
                    root = constrainedRoot;
                    Log.Info("HWND安全发送已绕过外部覆盖窗口并在已验证卖家根内重新解析安全点: seller="
                        + SellerNick + ", expectedRoot=" + expectedRoot + ", outsideTarget=" + outsideTarget
                        + ", outsideRoot=" + outsideRoot + ", resolvedTarget=" + target);
                }
            }

            if (target == IntPtr.Zero) return false;
            if (root != expectedRoot)
            {
                uint rejectedPid;
                GetWindowThreadProcessId(target, out rejectedPid);
                SetSendFailure("HWND安全发送", "安全点不属于当前已验证卖家根窗口，拒绝向未知窗口投递点击");
                Log.Info("HWND安全发送已阻止跨根窗口点击: seller=" + SellerNick
                    + ", targetPid=" + rejectedPid + ", expectedRoot=" + expectedRoot + ", actualRoot=" + root);
                return false;
            }

            uint targetPid;
            GetWindowThreadProcessId(target, out targetPid);
            if (targetPid == 0)
            {
                Log.Info("HWND安全发送无法读取安全点进程，已拒绝: seller=" + SellerNick
                    + ", expectedRoot=" + expectedRoot + ", target=" + target);
                return false;
            }
            if (targetPid != expectedPid)
            {
                Log.Info("HWND安全发送已验证千牛辅助进程子窗口: seller=" + SellerNick
                    + ", mainPid=" + expectedPid + ", helperPid=" + targetPid
                    + ", verifiedRoot=" + expectedRoot + ", target=" + target);
            }

            var clientPoint = screenPoint;
            if (!ScreenToClient(target, ref clientPoint))
            {
                Log.Info("HWND安全发送 ScreenToClient 失败: win32=" + Marshal.GetLastWin32Error());
                return false;
            }
            var lParam = new IntPtr((clientPoint.Y << 16) | (clientPoint.X & 0xffff));
            Marshal.GetLastWin32Error();
            var moved = PostMessage(target, WmMouseMove, IntPtr.Zero, lParam);
            var down = PostMessage(target, WmLButtonDown, new IntPtr(MkLButton), lParam);
            var up = PostMessage(target, WmLButtonUp, IntPtr.Zero, lParam);
            var error = Marshal.GetLastWin32Error();
            if (!down || !up)
            {
                Log.Info("HWND安全发送被Windows拒绝/失败: seller=" + SellerNick
                    + ", target=" + target + ", moved=" + moved + ", down=" + down + ", up=" + up
                    + ", win32=" + error + ", integrity=" + DescribeInputIntegrity(desk.ProcessId));
                return false;
            }

            Log.Info("已向当前卖家千牛左侧主发送安全点投递HWND鼠标消息: seller=" + SellerNick
                + ", target=" + target + ", targetPid=" + targetPid
                + ", point=" + screenPoint.X + "," + screenPoint.Y
                + ", arrowGuard=" + arrowGuard);
            return true;
        }

        private static IntPtr ResolveTargetInsideVerifiedSellerRoot(IntPtr expectedRoot, NativePoint screenPoint)
        {
            if (expectedRoot == IntPtr.Zero) return IntPtr.Zero;
            const uint flags = CwpSkipInvisible | CwpSkipDisabled | CwpSkipTransparent;
            var current = expectedRoot;
            for (var depth = 0; depth < 10; depth++)
            {
                var local = screenPoint;
                if (!ScreenToClient(current, ref local)) break;
                var child = ChildWindowFromPointEx(current, local, flags);
                if (child == IntPtr.Zero || child == current) break;
                current = child;
            }
            return current;
        }

        private void LogInputIntegrityDiagnostic(string reason)
        {
            try
            {
                var desk = ResolveSellerDesk();
                var detail = desk == null ? "sellerDesk=missing" : DescribeInputIntegrity(desk.ProcessId);
                Log.Info("Windows输入权限诊断: reason=" + reason + ", seller=" + SellerNick + ", " + detail);
            }
            catch (Exception ex)
            {
                Log.Info("Windows输入权限诊断失败: " + ex.Message);
            }
        }

        private static string DescribeInputIntegrity(int targetPid)
        {
            var bot = ReadIntegrityLevel(Process.GetCurrentProcess().Id);
            var qn = ReadIntegrityLevel(targetPid);
            var mismatch = bot.Rid > 0 && qn.Rid > 0 && bot.Rid < qn.Rid;
            return "Bot=" + bot.Name + "(" + bot.Rid + ")"
                + ", Qianniu=" + qn.Name + "(" + qn.Rid + ")"
                + ", targetHigherIntegrity=" + mismatch;
        }

        private sealed class IntegrityInfo
        {
            public int Rid;
            public string Name = "Unknown";
        }

        private static IntegrityInfo ReadIntegrityLevel(int pid)
        {
            IntPtr process = IntPtr.Zero;
            IntPtr token = IntPtr.Zero;
            IntPtr buffer = IntPtr.Zero;
            try
            {
                process = OpenProcess(ProcessQueryLimitedInformation, false, unchecked((uint)pid));
                if (process == IntPtr.Zero) return new IntegrityInfo { Name = "OpenProcessDenied" };
                if (!OpenProcessToken(process, TokenQuery, out token))
                    return new IntegrityInfo { Name = "OpenTokenDenied" };

                int length;
                GetTokenInformation(token, TokenIntegrityLevel, IntPtr.Zero, 0, out length);
                if (length <= 0) return new IntegrityInfo();
                buffer = Marshal.AllocHGlobal(length);
                if (!GetTokenInformation(token, TokenIntegrityLevel, buffer, length, out length))
                    return new IntegrityInfo();

                var label = (SidAndAttributes)Marshal.PtrToStructure(buffer, typeof(SidAndAttributes));
                if (label.Sid == IntPtr.Zero) return new IntegrityInfo();
                var countPtr = GetSidSubAuthorityCount(label.Sid);
                if (countPtr == IntPtr.Zero) return new IntegrityInfo();
                var count = Marshal.ReadByte(countPtr);
                if (count == 0) return new IntegrityInfo();
                var ridPtr = GetSidSubAuthority(label.Sid, (uint)(count - 1));
                if (ridPtr == IntPtr.Zero) return new IntegrityInfo();
                var rid = Marshal.ReadInt32(ridPtr);
                return new IntegrityInfo { Rid = rid, Name = IntegrityName(rid) };
            }
            catch (Exception ex)
            {
                return new IntegrityInfo { Name = "Error:" + ex.GetType().Name };
            }
            finally
            {
                if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
                if (token != IntPtr.Zero) CloseHandle(token);
                if (process != IntPtr.Zero) CloseHandle(process);
            }
        }

        private static string IntegrityName(int rid)
        {
            if (rid >= 0x4000) return "System";
            if (rid >= 0x3000) return "High";
            if (rid >= 0x2000) return "Medium";
            if (rid >= 0x1000) return "Low";
            return "Untrusted";
        }

        private static string ShortNative(string value, int max)
        {
            value = (value ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim();
            return value.Length <= max ? value : value.Substring(0, max) + "...";
        }
    }
}
