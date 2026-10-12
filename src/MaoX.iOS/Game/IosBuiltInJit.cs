using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Foundation;
using MaoX.Core;
using static MaoX.Core.I18n;

namespace MaoX.iOS.Game;

/// <summary>
/// 内置 JIT（实验）：安装包里的 JIT 助手扩展（PlugIns/MaoXJITHelper.appex，源码在 src/MaoX.iOS/JITHelper）用
/// StikJIT 1.9.0 通过 LocalDevVPN 连接本机的调试服务，给启动器进程开启 JIT。仍然需要 LocalDevVPN、开发者模式和配对文件。
/// 只在用户打开设置里的开关时使用；是否开启以 GameNative.JitEnabled()（CS_DEBUGGED）为准，不以助手的回答为准。
/// </summary>
internal static unsafe class IosBuiltInJit
{
    private const string HelperBundle = "MaoXJITHelper.appex";

    /// <summary>Native/jit_helper.m 的状态码</summary>
    public enum Status
    {
        Ok = 0, UnsupportedOs = 1, LiveContainer = 2, Missing = 3, ApiMissing = 4, NotRegistered = 5, Busy = 6,
        BadRequest = 7, StartFailed = 8, Cancelled = 9, Interrupted = 10, NoResponse = 11,
    }

    /// <summary>一次请求的结果。Status 不是 Ok 时助手没有回答，Code / Message 是本地的说明。</summary>
    public sealed record Reply(Status Status, bool Ok, string Code, string Message, string Stage, bool? Txm)
    {
        public bool Succeeded => Status == Status.Ok && Ok;
    }

    private static readonly TimeSpan EnableTimeout = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan ResetTimeout = TimeSpan.FromSeconds(30);
    private static readonly ConcurrentDictionary<long, TaskCompletionSource<Reply>> Pending = new();
    private static long _nextToken;

    public static bool Included => Directory.Exists(Path.Combine(NSBundle.MainBundle.BundlePath, "PlugIns", HelperBundle));

    /// <summary>用户放配对文件的位置：“文件”App 里 MaoX Launcher/StikJIT/pairingFile.plist（和 StikJIT 文档一致）</summary>
    public static string PairingFile => Path.Combine(AppPaths.BaseDir, "StikJIT", "pairingFile.plist");

    public static void Install()
    {
        if (!Included)
            return;
        MobileJit.BuiltInUnavailableReason = UnavailableReason;
        MobileJit.ResetBuiltInDdi = ResetDdiAsync;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PairingFile)!);
        }
        catch (Exception)
        {
            // 文稿目录不可写时忽略
        }
    }

    /// <summary>null 表示可以用内置 JIT。</summary>
    public static string UnavailableReason()
    {
        if (!Included)
            return T("这个安装包没有带内置 JIT 助手，或安装工具移除了 App 扩展");
        if (!GameNative.Available)
            return T("这个安装包没有带游戏运行环境");
        if (GameNative.HasEntitlement("jb.pmap_cs.custom_trust"))
            return T("TrollStore 安装的启动器使用 TrollStore 自带的方式开启 JIT");
        return (Status)GameNative.JitHelperAvailability() switch
        {
            Status.Ok => null,
            Status.UnsupportedOs => T("内置 JIT 需要 iOS 17.4 或更高版本"),
            Status.LiveContainer => T("在 LiveContainer 里不能使用内置 JIT"),
            Status.Missing => T("这个安装包没有带内置 JIT 助手，或安装工具移除了 App 扩展"),
            var status => F("这台设备不支持内置 JIT（{0}）", status),
        };
    }

    /// <summary>用户在设置里选了内置 JIT 并且可用。</summary>
    public static bool Selected(LauncherConfig cfg, out string unavailable)
    {
        unavailable = null;
        if (cfg.IosJitMethod != "builtin")
            return false;
        unavailable = UnavailableReason();
        return unavailable == null;
    }

    // ------------------------------------------------------------------ 请求

    private sealed class Call(long token, Task<Reply> reply)
    {
        public long Token { get; } = token;
        public Task<Reply> Reply { get; } = reply;
    }

    private static Call Send(JsonObject request)
    {
        var token = Interlocked.Increment(ref _nextToken);
        var tcs = new TaskCompletionSource<Reply>(TaskCreationOptions.RunContinuationsAsynchronously);
        Pending[token] = tcs;
        var started = (Status)GameNative.JitHelperRequest(request.ToJsonString(), token, &OnReply);
        if (started != Status.Ok)
        {
            Pending.TryRemove(token, out _);
            tcs.TrySetResult(new Reply(started, false, started.ToString(), "", null, null));
        }
        return new Call(token, tcs.Task);
    }

    [UnmanagedCallersOnly]
    private static void OnReply(long token, int status, nint text)
    {
        try
        {
            if (!Pending.TryRemove(token, out var tcs))
                return;
            var message = Marshal.PtrToStringUTF8(text) ?? "";
            tcs.TrySetResult((Status)status == Status.Ok ? Parse(message) : new Reply((Status)status, false,
                                 ((Status)status).ToString(), message, null, null));
        }
        catch (Exception)
        {
            // 回调在原生线程上，不能把异常抛回去
        }
    }

    private static Reply Parse(string json)
    {
        try
        {
            var node = JsonNode.Parse(json)!.AsObject();
            return new Reply(Status.Ok, node.Bool("ok"), node.Str("code"), node.Str("message"), node.Str("stage"),
                             node["txm"] is JsonValue txm ? txm.GetValue<bool>() : null);
        }
        catch (Exception)
        {
            return new Reply(Status.NoResponse, false, "badResponse", json, null, null);
        }
    }

    /// <summary>不在主线程上发送：查找扩展要和系统服务通信。</summary>
    private static Task<Call> SendInBackground(JsonObject request) => Task.Run(() => Send(request));

    private static JsonObject NewRequest(string op, LauncherConfig cfg)
    {
        var request = new JsonObject { ["op"] = op };
        var address = cfg?.IosJitAddress?.Trim();
        if (!string.IsNullOrEmpty(address))
            request["address"] = address;
        return request;
    }

    // ------------------------------------------------------------------ 开启 JIT

    /// <summary>
    /// 请助手给当前进程开启 JIT，直到进程真的有了 JIT（CS_DEBUGGED）才返回。失败时抛出带说明的 InvalidOperationException，
    /// 不会自动改用 StikDebug（StikJIT 的集成要求：失败后不要悄悄换成别的方式）。
    /// </summary>
    public static async Task EnableAsync(GameLauncher launcher)
    {
        if (GameNative.JitEnabled())
            return;
        var pairing = PairingFile;
        if (!File.Exists(pairing))
            throw new InvalidOperationException(
                T("内置 JIT 需要配对文件：请用 iloader 等工具生成，放到“文件”App 的 MaoX Launcher/StikJIT/pairingFile.plist；也可以在设置里关闭内置 JIT，改用 StikDebug"));

        var request = NewRequest("enable", launcher.Cfg);
        request["pid"] = Environment.ProcessId;
        request["pairing"] = Convert.ToBase64String(await File.ReadAllBytesAsync(pairing));
        // 和请 StikDebug 时一样：有 TXM（或需要镜像映射）时才必须跑脚本，脚本用启动器自带的 UniversalJIT26.js
        request["forceScript"] = (GameNative.Flags() & (GameNative.JitFlags.ForceMirrored | GameNative.JitFlags.HasTxm)) != 0;
        if (File.Exists(GameHost.JitScript))
            request["script"] = Convert.ToBase64String(await File.ReadAllBytesAsync(GameHost.JitScript));

        launcher.Log(T("正在用内置 JIT 开启 JIT（需要打开 LocalDevVPN）..."));
        var call = await SendInBackground(request);
        var deadline = DateTime.UtcNow + EnableTimeout;
        while (!GameNative.JitEnabled())
        {
            if (call.Reply.IsCompleted)
            {
                var reply = await call.Reply;
                if (!reply.Succeeded)
                    throw new InvalidOperationException(Explain(reply));
                // StikJIT 正常结束（调试器已附加并断开）：进程应该已经是 CS_DEBUGGED，再给一点时间后确认
                var settle = DateTime.UtcNow.AddSeconds(5);
                while (!GameNative.JitEnabled())
                {
                    if (DateTime.UtcNow > settle)
                        throw new InvalidOperationException(
                            T("StikJIT 报告已完成，但启动器仍然没有 JIT：请重启手机后再试，或在设置里关闭内置 JIT，改用 StikDebug"));
                    await Task.Delay(200);
                }
                break;
            }
            if (DateTime.UtcNow > deadline)
            {
                GameNative.JitHelperCancel(call.Token);
                throw new InvalidOperationException(
                    T("内置 JIT 超时：请确认 LocalDevVPN 已连接、手机连着 Wi-Fi，然后再点启动"));
            }
            await Task.Delay(300);
        }

        // 有 TXM 时脚本要一直连着，直到游戏启动后断开；之后助手的结果只记进日志
        if (!call.Reply.IsCompleted)
            _ = call.Reply.ContinueWith(t =>
            {
                var reply = t.Result;
                if (!reply.Succeeded)
                    launcher.Log(F("内置 JIT 助手已结束：{0}", Explain(reply)));
            }, TaskScheduler.Default);
    }

    public static async Task<string> ResetDdiAsync()
    {
        var call = await SendInBackground(NewRequest("resetDDI", null));
        if (await Task.WhenAny(call.Reply, Task.Delay(ResetTimeout)) != call.Reply)
        {
            GameNative.JitHelperCancel(call.Token);
            return T("内置 JIT 助手没有响应，请稍后再试");
        }
        var reply = await call.Reply;
        return reply.Succeeded
            ? T("已删除缓存的开发者磁盘映像，下次开启 JIT 时会重新下载（已经挂载的映像要重启手机后才会卸载）")
            : Explain(reply);
    }

    /// <summary>给用户看的失败说明</summary>
    public static string Explain(Reply reply)
    {
        var detail = string.IsNullOrWhiteSpace(reply.Message) ? reply.Code : reply.Message;
        if (reply.Status != Status.Ok)
            return reply.Status switch
            {
                Status.Busy => T("上一次内置 JIT 请求还没有结束，请稍后再试"),
                Status.Missing or Status.NotRegistered or Status.StartFailed =>
                    F("JIT 助手没有启动（{0}）：请重新安装启动器，安装工具询问时选择保留 App 扩展；或在设置里关闭内置 JIT，改用 StikDebug", detail),
                Status.Interrupted => T("JIT 助手意外退出了，请再试一次；多次失败请重启手机，或在设置里关闭内置 JIT，改用 StikDebug"),
                Status.Cancelled => F("内置 JIT 请求被取消（{0}）", detail),
                Status.UnsupportedOs or Status.LiveContainer or Status.ApiMissing => UnavailableReason() ?? detail,
                _ => F("内置 JIT 失败：{0}", detail),
            };
        return reply.Code switch
        {
            "unreachable" => F("连接不到本机的调试服务（{0}）：请打开 LocalDevVPN 并连接 Wi-Fi，确认开发者模式已开启", detail),
            "pairingFile" => F("配对文件无效或已过期（{0}）：请用 iloader 重新生成，替换 MaoX Launcher/StikJIT/pairingFile.plist", detail),
            "preparationFailed" or "ddiDownload" or "ddiFilesMissing" or "ddiNotMounted" or "deviceNotReady" =>
                F("准备开发者磁盘映像失败（{0}）：可以在设置里重置开发者磁盘映像，或重启手机后再试", detail),
            "txmDetectionUnavailable" => F("无法判断这台设备是否有 TXM（{0}）：请改用 StikDebug", detail),
            _ => F("内置 JIT 失败（{0}）：{1}", reply.Code, detail),
        };
    }
}
