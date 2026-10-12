using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using Foundation;
using MaoX.Core;
using static MaoX.Core.I18n;

namespace MaoX.iOS.Game;

/// <summary>
/// 内置 JIT 的配对文件：导入时检查是不是 StikJIT 能用的 RPPairing 格式；iOS 27+ 还可以在本机生成
/// （Native/pairing.m + libmaoxpair，源码在 src/MaoX.iOS/Pairing，基于 idevice：启动器作为“可配对的电脑”，
/// 用户在 设置 › 隐私与安全性 › 开发者模式 里选它并输入配对码）。
/// </summary>
internal static class IosPairing
{
    /// <summary>Native/pairing.m 的状态码和事件</summary>
    private enum Availability { Ok = 0, UnsupportedOs = 1, Missing = 2, Busy = 3, BadRequest = 4 }

    private enum Event { Advertising = 1, Pin = 2, Done = 3, Failed = 4, Cancelled = 5, Expired = 6, PublishFailed = 7 }

    private const long MaxFileSize = 64 * 1024;

    private static readonly ConcurrentDictionary<long, Action<Event, string>> Sessions = new();
    private static long _nextToken;

    private static string LibraryPath => Path.Combine(NSBundle.MainBundle.BundlePath, "Frameworks", "libmaoxpair.dylib");

    public static void Install()
    {
        MobileJit.PairingFileStatus = Status;
        MobileJit.ImportPairingFile = Import;
        if (!File.Exists(LibraryPath))
            return;
        MobileJit.GeneratePairingUnavailableReason = GenerateUnavailableReason;
        MobileJit.GeneratePairingFile = GenerateAsync;
    }

    // ------------------------------------------------------------------ 配对文件

    public static string Status()
    {
        var path = IosBuiltInJit.PairingFile;
        try
        {
            if (!File.Exists(path))
                return T("还没有配对文件");
            var error = Validate(File.ReadAllBytes(path));
            return error == null
                ? F("已有配对文件（{0:yyyy-MM-dd HH:mm} 更新）", File.GetLastWriteTime(path))
                : F("配对文件不能用：{0}", error);
        }
        catch (Exception e)
        {
            return F("读取配对文件失败：{0}", e.Message);
        }
    }

    /// <summary>
    /// null 表示是 StikJIT 能用的 RPPairing 配对文件（idevice 的 RpPairingFile：32 字节的 public_key、private_key 和 identifier），
    /// 否则是给用户看的原因。
    /// </summary>
    public static string Validate(byte[] data)
    {
        if (data.Length == 0 || data.Length > MaxFileSize)
            return T("这不是配对文件");
        NSDictionary dict;
        var temp = Path.Combine(Path.GetTempPath(), $"pairing-{Guid.NewGuid():N}.plist");
        try
        {
            File.WriteAllBytes(temp, data);
            dict = NSMutableDictionary.FromFile(temp);
        }
        finally
        {
            try
            {
                File.Delete(temp);
            }
            catch (Exception)
            {
                // 临时文件删不掉不影响结果
            }
        }
        if (dict == null)
            return T("这不是配对文件（不是 plist 格式）");
        if (dict["public_key"] is NSData { Length: 32 } && dict["private_key"] is NSData { Length: 32 } && dict["identifier"] is NSString)
            return null;
        if (dict.ContainsKey(new NSString("HostPrivateKey")) || dict.ContainsKey(new NSString("DeviceCertificate")))
            return T("这是旧格式（lockdown）的配对文件，StikJIT 只能用 RPPairing 配对文件：请用 iloader 或 idevice_pair 生成 Remote pairing 配对文件，iOS 27 以上也可以在本机生成");
        return T("文件里没有 RPPairing 配对信息（public_key、private_key、identifier）");
    }

    public static string Import(byte[] data)
    {
        var error = Validate(data);
        if (error != null)
            return error;
        try
        {
            Save(data);
            return null;
        }
        catch (Exception e)
        {
            return F("保存配对文件失败：{0}", e.Message);
        }
    }

    private static void Save(byte[] data)
    {
        var path = IosBuiltInJit.PairingFile;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllBytes(temp, data);
        File.Move(temp, path, true);
    }

    // ------------------------------------------------------------------ 在本机生成（iOS 27+）

    public static string GenerateUnavailableReason()
    {
        if (!GameNative.Available)
            return T("这个安装包没有带游戏运行环境");
        try
        {
            return (Availability)GameNative.PairAvailability() switch
            {
                Availability.Ok => null,
                Availability.UnsupportedOs => T("在本机生成配对文件需要 iOS 27 或更高版本"),
                Availability.Busy => T("正在生成配对文件"),
                var status => F("这个安装包不能在本机生成配对文件（{0}）", status),
            };
        }
        catch (Exception e)
        {
            return F("这个安装包不能在本机生成配对文件（{0}）", e.Message);
        }
    }

    public static async Task<string> GenerateAsync(Action<string, string> progress, CancellationToken cancel)
    {
        var reason = GenerateUnavailableReason();
        if (reason != null)
            throw new InvalidOperationException(reason);

        var result = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var token = Interlocked.Increment(ref _nextToken);
        Sessions[token] = (kind, text) =>
        {
            switch (kind)
            {
                case Event.Advertising:
                    progress(T("等待 iPhone 连接：打开 设置 › 隐私与安全性 › 开发者模式，往下滑，点 MaoX Launcher 配对"), null);
                    break;
                case Event.Pin:
                    progress(T("在设置里输入下面的配对码（通知里也会显示）"), text);
                    break;
                case Event.Done:
                    result.TrySetResult(text);
                    break;
                case Event.Cancelled:
                    result.TrySetCanceled();
                    break;
                case Event.Expired:
                    result.TrySetException(new InvalidOperationException(
                        T("系统停止了启动器的后台运行，没来得及完成配对：请再试一次，并尽快在设置里完成配对")));
                    break;
                case Event.PublishFailed:
                    result.TrySetException(new InvalidOperationException(
                        F("无法在本地网络中广播（{0}）：请在 设置 › 隐私与安全性 › 本地网络 中允许 MaoX Launcher，连接 Wi-Fi 后再试", text)));
                    break;
                default:
                    result.TrySetException(new InvalidOperationException(F("配对失败：{0}", text)));
                    break;
            }
        };

        progress(T("正在准备配对...第一次使用时请允许 MaoX Launcher 访问本地网络和发送通知"), null);
        var started = Start(token);
        if (started != Availability.Ok)
        {
            Sessions.TryRemove(token, out _);
            throw new InvalidOperationException(GenerateUnavailableReason() ?? F("无法开始配对（{0}）", started));
        }

        string json;
        using (cancel.Register(() => GameNative.PairCancel(token)))
        {
            try
            {
                json = await result.Task;
            }
            finally
            {
                Sessions.TryRemove(token, out _);
            }
        }

        var node = JsonNode.Parse(json)!.AsObject();
        var file = Encoding.UTF8.GetBytes(node.Str("file") ?? "");
        var error = Validate(file);
        if (error != null)
            throw new InvalidOperationException(F("生成的配对文件无效：{0}", error));
        Save(file);
        return F("已和 {0} 配对，配对文件已保存。打开内置 JIT 后就可以启动游戏（仍需要 LocalDevVPN）", node.Str("device"));
    }

    private static unsafe Availability Start(long token)
    {
        var texts = new JsonObject
        {
            ["name"] = "MaoX Launcher",
            ["model"] = "Mac17,7",
            ["title"] = T("MaoX Launcher 配对"),
            ["waiting"] = T("在 设置 › 隐私与安全性 › 开发者模式 里点 MaoX Launcher"),
            ["pinSubtitle"] = T("输入配对码 {pin}"),
            ["pinTitle"] = T("MaoX Launcher 配对码"),
            ["pinBody"] = T("在设置里输入 {pin} 完成配对"),
            ["doneTitle"] = T("配对完成"),
            ["doneBody"] = T("配对文件已生成，回到 MaoX Launcher 就可以使用内置 JIT"),
        };
        return (Availability)GameNative.PairStart(texts.ToJsonString(), token, &OnEvent);
    }

    [UnmanagedCallersOnly]
    private static void OnEvent(long token, int kind, nint text)
    {
        try
        {
            if (Sessions.TryGetValue(token, out var handler))
                handler((Event)kind, Marshal.PtrToStringUTF8(text) ?? "");
        }
        catch (Exception)
        {
            // 回调在原生线程上，不能把异常抛回去
        }
    }
}
