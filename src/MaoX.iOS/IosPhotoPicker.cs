using Foundation;
using MaoX.Core;
using PhotosUI;
using UIKit;

namespace MaoX.iOS;

/// <summary>系统相册（PHPicker），不用申请完整相册权限。</summary>
internal static class IosPhotoPicker
{
    private static Delegate _current;

    public static Task<string> Pick(string[] _)
    {
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        NSRunLoop.Main.BeginInvokeOnMainThread(() =>
        {
            var host = TopController();
            if (host == null)
            {
                tcs.TrySetResult(null);
                return;
            }
            var picker = new PHPickerViewController(new PHPickerConfiguration
            {
                Filter = PHPickerFilter.ImagesFilter,
                SelectionLimit = 1,
            });
            _current = new Delegate(tcs);
            picker.Delegate = _current;
            host.PresentViewController(picker, true, null);
        });
        return tcs.Task;
    }

    private static UIViewController TopController()
    {
        UIWindow window = null;
        foreach (var item in UIApplication.SharedApplication.ConnectedScenes)
        {
            if (item is not UIWindowScene scene)
                continue;
            foreach (var candidate in scene.Windows)
            {
                if (candidate.IsKeyWindow)
                {
                    window = candidate;
                    break;
                }
                window ??= candidate;
            }
            if (window is { IsKeyWindow: true })
                break;
        }
        var controller = window?.RootViewController;
        while (controller?.PresentedViewController != null)
            controller = controller.PresentedViewController;
        return controller;
    }

    private sealed class Delegate : PHPickerViewControllerDelegate
    {
        private readonly TaskCompletionSource<string> _tcs;

        public Delegate(TaskCompletionSource<string> tcs) => _tcs = tcs;

        public override void DidFinishPicking(PHPickerViewController picker, PHPickerResult[] results)
        {
            picker.DismissViewController(true, null);
            if (results == null || results.Length == 0)
            {
                Finish(null);
                return;
            }
            var provider = results[0].ItemProvider;
            if (provider == null || !provider.HasItemConformingTo("public.image"))
            {
                Finish(null);
                return;
            }
            provider.LoadDataRepresentation("public.image", (data, error) =>
            {
                if (data == null || data.Length == 0)
                {
                    Finish(null);
                    return;
                }
                try
                {
                    Finish(Save(data.ToArray()));
                }
                catch (Exception e)
                {
                    _tcs.TrySetException(e);
                    _current = null;
                }
            });
        }

        private static string Save(byte[] data)
        {
            var ext = Platform.ImageExt(data);
            if (string.IsNullOrEmpty(ext))
            {
                using var image = UIImage.LoadFromData(NSData.FromArray(data))
                                  ?? throw new IOException(I18n.T("无法读取这张图片"));
                using var jpeg = image.AsJPEG(0.92f)
                                 ?? throw new IOException(I18n.T("无法读取这张图片"));
                data = jpeg.ToArray();
                ext = ".jpg";
            }
            return Platform.SavePicked(data, ext);
        }

        private void Finish(string path)
        {
            _tcs.TrySetResult(path);
            if (ReferenceEquals(_current, this))
                _current = null;
        }
    }
}
