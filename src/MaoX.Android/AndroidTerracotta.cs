using Android.App;
using Android.Content;
using Android.Net;
using IO.Github.Jinruimaomao.Maox;
using MaoX.Core;

namespace MaoX.Android;

/// <summary>陶瓦联机安卓版：在启动器进程里加载 libterracotta.so，EasyTier 通过 VpnService 建立虚拟网卡。</summary>
internal class AndroidTerracotta : IMobileTerracotta
{
    public const int VpnRequestCode = 0x4d58;

    private readonly Activity _activity;
    private TaskCompletionSource<bool> _vpn;

    public AndroidTerracotta(Activity activity) => _activity = activity;

    private static Context Ctx => global::Android.App.Application.Context;

    public string LibraryDir => Path.Combine(Ctx.FilesDir!.AbsolutePath, "terracotta");

    public bool Started => TerracottaBridge.IsStarted;

    public Task<string> StartAsync(string libraryPath) => Task.Run(() =>
    {
        try
        {
            var meta = TerracottaBridge.Start(Ctx, libraryPath, Path.Combine(LibraryDir, "data"));
            return meta.Split('\0')[0];
        }
        catch (Java.Lang.Throwable e)
        {
            throw new TerracottaException(I18n.F("陶瓦联机启动失败：{0}", e.Message), e);
        }
    });

    public string State() => TerracottaBridge.State;

    public void SetWaiting() => TerracottaBridge.SetWaiting();

    public void SetScanning(string player) => TerracottaBridge.SetScanning(player);

    public Task<bool> SetGuestingAsync(string room, string player) =>
        Task.Run(() => TerracottaBridge.SetGuesting(room, player));

    public Task<bool> PrepareVpnAsync()
    {
        var intent = VpnService.Prepare(_activity);
        if (intent == null)
            return Task.FromResult(true);
        _vpn?.TrySetResult(false);
        var tcs = _vpn = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _activity.RunOnUiThread(() => _activity.StartActivityForResult(intent, VpnRequestCode));
        return tcs.Task;
    }

    public void OnActivityResult(int requestCode, Result result)
    {
        if (requestCode == VpnRequestCode)
            _vpn?.TrySetResult(result == Result.Ok);
    }
}
