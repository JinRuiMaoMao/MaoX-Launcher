using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Provider;
using Android.Views;
using Avalonia;
using Avalonia.Android;
using Avalonia.Media;
using MaoX.Core;

namespace MaoX.Android;

[Activity(Name = "io.github.jinruimaomao.maox.MainActivity", Label = "MaoX Launcher", Theme = "@style/MaoX.Theme", Icon = "@drawable/icon", MainLauncher = true,
          ScreenOrientation = ScreenOrientation.SensorLandscape,
          ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode
                                 | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Keyboard
                                 | ConfigChanges.KeyboardHidden | ConfigChanges.Navigation)]
public class MainActivity : AvaloniaMainActivity<App>
{
    private const int PhotoRequestCode = 0x5048;

    private AndroidTerracotta _terracotta;
    private TaskCompletionSource<string> _photo;

    protected override void OnActivityResult(int requestCode, Result resultCode, Intent data)
    {
        base.OnActivityResult(requestCode, resultCode, data);
        if (requestCode == PhotoRequestCode)
        {
            FinishPhoto(resultCode, data);
            return;
        }
        _terracotta?.OnActivityResult(requestCode, resultCode);
    }

    protected override void OnCreate(Bundle savedInstanceState)
    {
        // 数据放在 Android/data/<包名>/files 下：卸载时一起删除，也能用文件管理器或电脑访问
        var home = GetExternalFilesDir(null)?.AbsolutePath ?? FilesDir!.AbsolutePath;
        System.Environment.SetEnvironmentVariable("MAOX_HOME", home);
        I18n.SetLanguage(LauncherConfig.Load().Language);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => App.WriteCrashLog(e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => e.SetObserved();
        GameHost.Install(this);
        Terracotta.Mobile = _terracotta = new AndroidTerracotta(this);
        Platform.MobilePickImage = PickImageAsync;
        MojangGames.MobileProbe = ProbeApp;
        MojangGames.MobileLaunch = LaunchApp;
#if !DEBUG
        // 调试版的签名和发布版不同，不能覆盖安装
        Updater.MobileInstaller = ApkInstaller.Install;
#endif
        base.OnCreate(savedInstanceState);
    }

    private Task<string> PickImageAsync(string[] _)
    {
        _photo?.TrySetResult(null);
        var tcs = _photo = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        RunOnUiThread(() =>
        {
            try
            {
                StartActivityForResult(PhotoIntent(), PhotoRequestCode);
            }
            catch (ActivityNotFoundException)
            {
                try
                {
                    StartActivityForResult(new Intent(Intent.ActionGetContent).SetType("image/*").AddCategory(Intent.CategoryOpenable),
                        PhotoRequestCode);
                }
                catch (Exception e)
                {
                    tcs.TrySetException(e);
                }
            }
            catch (Exception e)
            {
                tcs.TrySetException(e);
            }
        });
        return tcs.Task;
    }

    private static Intent PhotoIntent()
    {
        if (Build.VERSION.SdkInt >= BuildVersionCodes.Tiramisu)
            return new Intent(MediaStore.ActionPickImages);
        return new Intent(Intent.ActionPick, MediaStore.Images.Media.ExternalContentUri);
    }

    private void FinishPhoto(Result resultCode, Intent data)
    {
        var tcs = _photo;
        _photo = null;
        if (tcs == null)
            return;
        var uri = data?.Data;
        if (resultCode != Result.Ok || uri == null)
        {
            tcs.TrySetResult(null);
            return;
        }
        Task.Run(() =>
        {
            try
            {
                tcs.TrySetResult(CopyPhoto(uri));
            }
            catch (Exception e)
            {
                tcs.TrySetException(e);
            }
        });
    }

    private string CopyPhoto(global::Android.Net.Uri uri)
    {
        using var input = ContentResolver!.OpenInputStream(uri)
                          ?? throw new IOException(I18n.T("无法读取这张图片"));
        using var buffer = new MemoryStream();
        input.CopyTo(buffer);
        var bytes = buffer.ToArray();
        var ext = Platform.ImageExt(bytes);
        if (string.IsNullOrEmpty(ext))
        {
            bytes = ToJpeg(bytes) ?? throw new IOException(I18n.T("无法读取这张图片"));
            ext = ".jpg";
        }
        return Platform.SavePicked(bytes, ext);
    }

    private static byte[] ToJpeg(byte[] data)
    {
        using var input = new MemoryStream(data);
        using var bitmap = global::Android.Graphics.BitmapFactory.DecodeStream(input);
        if (bitmap == null)
            return null;
        using var output = new MemoryStream();
        bitmap.Compress(global::Android.Graphics.Bitmap.CompressFormat.Jpeg, 92, output);
        return output.ToArray();
    }

    private string ProbeApp(string package)
    {
        try
        {
            return PackageManager!.GetPackageInfo(package, 0)?.VersionName ?? "";
        }
        catch (global::Android.Content.PM.PackageManager.NameNotFoundException)
        {
            return null;
        }
    }

    private bool LaunchApp(string package)
    {
        var intent = PackageManager!.GetLaunchIntentForPackage(package);
        if (intent == null)
            return false;
        intent.AddFlags(global::Android.Content.ActivityFlags.NewTask);
        StartActivity(intent);
        return true;
    }

    // 切回应用、旋转或分屏后系统会把状态栏放出来，拿到焦点时重新隐藏
    public override void OnWindowFocusChanged(bool hasFocus)
    {
        base.OnWindowFocusChanged(hasFocus);
        if (hasFocus && Window?.DecorView is { } decor)
            decor.SystemUiVisibility = (StatusBarVisibility)(SystemUiFlags.ImmersiveSticky | SystemUiFlags.Fullscreen
                                                             | SystemUiFlags.HideNavigation | SystemUiFlags.LayoutStable
                                                             | SystemUiFlags.LayoutFullscreen | SystemUiFlags.LayoutHideNavigation);
    }

    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder)
    {
        builder = base.CustomizeAppBuilder(builder).LogToTrace();
        var font = ChineseFont.Load();
        var fallbacks = new List<FontFallback>();
        if (font != null)
            fallbacks.Add(new FontFallback { FontFamily = ChineseFont.Family });
        fallbacks.AddRange(MaoX.UiFonts.Fallbacks);
        builder = builder.With(new FontManagerOptions { FontFallbacks = fallbacks });
        return font == null ? builder : builder.AfterSetup(_ => FontManager.Current.AddFontCollection(font));
    }
}
