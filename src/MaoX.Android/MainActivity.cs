using Android.App;
using Android.Content.PM;
using Android.OS;
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
    protected override void OnCreate(Bundle savedInstanceState)
    {
        // 数据放在 Android/data/<包名>/files 下：卸载时一起删除，也能用文件管理器或电脑访问
        var home = GetExternalFilesDir(null)?.AbsolutePath ?? FilesDir!.AbsolutePath;
        System.Environment.SetEnvironmentVariable("MAOX_HOME", home);
        I18n.SetLanguage(LauncherConfig.Load().Language);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => App.WriteCrashLog(e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => e.SetObserved();
        GameHost.Install(this);
#if !DEBUG
        // 调试版的签名和发布版不同，不能覆盖安装
        Updater.MobileInstaller = ApkInstaller.Install;
#endif
        base.OnCreate(savedInstanceState);
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
        if (font == null)
            return builder;
        return builder
               .With(new FontManagerOptions { FontFallbacks = [new FontFallback { FontFamily = ChineseFont.Family }] })
               .AfterSetup(_ => FontManager.Current.AddFontCollection(font));
    }
}
