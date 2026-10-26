using System.Diagnostics;
using MaoX.Core;
using NAudio.MediaFoundation;
using NAudio.Vorbis;
using NAudio.Wave;

namespace MaoX;

/// <summary>
/// 桌面音频：Windows 用 Media Foundation / Vorbis 解码常见格式；
/// macOS 用 afplay；Linux 尝试 ffplay / mpv。背景音乐和启动音效分开播。
/// </summary>
internal static class DesktopAudio
{
    private static readonly object Gate = new();
    private static bool _mf;
    private static WaveOutEvent _bgmOut, _sfxOut;
    private static WaveStream _bgmStream, _sfxStream;
    private static Process _bgmProcess, _sfxProcess;
    private static CancellationTokenSource _bgmLoop;
    private static int _bgmGen;

    public static void Register()
    {
        LauncherAudio.PlayImpl = Play;
        LauncherAudio.StopImpl = StopBgm;
        LauncherAudio.StopAllImpl = StopAll;
    }

    private static void Play(string path, bool bgm, bool loop)
    {
        if (Platform.IsWindows)
        {
            PlayWindows(path, bgm, loop);
            return;
        }
        PlayExternal(path, bgm, loop);
    }

    private static void PlayWindows(string path, bool bgm, bool loop)
    {
        lock (Gate)
        {
            EnsureMf();
            if (bgm)
            {
                _bgmGen++;
                var gen = _bgmGen;
                StopDevice(ref _bgmOut, ref _bgmStream);
                var source = Open(path);
                _bgmStream = loop ? new LoopStream(source) : source;
                _bgmOut = new WaveOutEvent { DesiredLatency = 200 };
                _bgmOut.Init(_bgmStream);
                if (!loop)
                {
                    _bgmOut.PlaybackStopped += (_, _) =>
                    {
                        lock (Gate)
                        {
                            if (gen != _bgmGen)
                                return;
                            StopDevice(ref _bgmOut, ref _bgmStream);
                        }
                        LauncherAudio.NotifyBgmEnded();
                    };
                }
                _bgmOut.Play();
                return;
            }
            StopDevice(ref _sfxOut, ref _sfxStream);
            _sfxStream = Open(path);
            _sfxOut = new WaveOutEvent { DesiredLatency = 100 };
            _sfxOut.Init(_sfxStream);
            _sfxOut.PlaybackStopped += (_, _) =>
            {
                lock (Gate)
                    StopDevice(ref _sfxOut, ref _sfxStream);
            };
            _sfxOut.Play();
        }
    }

    private static WaveStream Open(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext is ".ogg" or ".oga")
            return new VorbisWaveReader(path);
        try
        {
            return new AudioFileReader(path);
        }
        catch (Exception)
        {
            return new MediaFoundationReader(path);
        }
    }

    private static void EnsureMf()
    {
        if (_mf)
            return;
        try
        {
            MediaFoundationApi.Startup();
        }
        catch (Exception)
        {
        }
        _mf = true;
    }

    private static void PlayExternal(string path, bool bgm, bool loop)
    {
        var start = ExternalStart(path);
        if (start == null)
            return;
        if (bgm)
        {
            _bgmLoop?.Cancel();
            Kill(ref _bgmProcess);
            var cts = new CancellationTokenSource();
            _bgmLoop = cts;
            _bgmProcess = start();
            _ = Task.Run(async () =>
            {
                while (!cts.IsCancellationRequested)
                {
                    var process = _bgmProcess;
                    if (process == null)
                        return;
                    try
                    {
                        await process.WaitForExitAsync(cts.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                    if (cts.IsCancellationRequested)
                        return;
                    if (loop)
                    {
                        _bgmProcess = start();
                        continue;
                    }
                    LauncherAudio.NotifyBgmEnded();
                    return;
                }
            });
            return;
        }
        Kill(ref _sfxProcess);
        _sfxProcess = start();
    }

    private static Func<Process> ExternalStart(string path)
    {
        if (Platform.IsMac)
            return () => Start("afplay", Quote(path));
        foreach (var (file, args) in new[]
                 {
                     ("ffplay", "-nodisp -autoexit -loglevel quiet " + Quote(path)),
                     ("mpv", "--no-video --really-quiet " + Quote(path)),
                 })
        {
            if (HasCommand(file))
                return () => Start(file, args);
        }
        return null;
    }

    private static Process Start(string file, string args)
    {
        try
        {
            return Process.Start(new ProcessStartInfo(file, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            });
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static bool HasCommand(string name)
    {
        try
        {
            var path = Environment.GetEnvironmentVariable("PATH") ?? "";
            var ext = Platform.IsWindows ? ".exe" : "";
            return path.Split(Path.PathSeparator).Any(dir => File.Exists(Path.Combine(dir, name + ext)));
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static string Quote(string path) => "\"" + path.Replace("\"", "\\\"") + "\"";

    private static void StopBgm()
    {
        lock (Gate)
        {
            _bgmGen++;
            _bgmLoop?.Cancel();
            _bgmLoop = null;
            Kill(ref _bgmProcess);
            StopDevice(ref _bgmOut, ref _bgmStream);
        }
    }

    private static void StopAll()
    {
        lock (Gate)
        {
            _bgmGen++;
            _bgmLoop?.Cancel();
            _bgmLoop = null;
            Kill(ref _bgmProcess);
            Kill(ref _sfxProcess);
            StopDevice(ref _bgmOut, ref _bgmStream);
            StopDevice(ref _sfxOut, ref _sfxStream);
        }
    }

    private static void StopDevice(ref WaveOutEvent output, ref WaveStream stream)
    {
        try
        {
            output?.Stop();
        }
        catch (Exception)
        {
        }
        try
        {
            output?.Dispose();
        }
        catch (Exception)
        {
        }
        try
        {
            stream?.Dispose();
        }
        catch (Exception)
        {
        }
        output = null;
        stream = null;
    }

    private static void Kill(ref Process process)
    {
        try
        {
            if (process is { HasExited: false })
                process.Kill(true);
        }
        catch (Exception)
        {
        }
        process?.Dispose();
        process = null;
    }

    private sealed class LoopStream : WaveStream
    {
        private readonly WaveStream _source;

        public LoopStream(WaveStream source) => _source = source;

        public override WaveFormat WaveFormat => _source.WaveFormat;

        public override long Length => _source.Length;

        public override long Position
        {
            get => _source.Position;
            set => _source.Position = value;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var total = 0;
            while (count > 0)
            {
                var read = _source.Read(buffer, offset, count);
                if (read == 0)
                {
                    if (_source.Position == 0)
                        break;
                    _source.Position = 0;
                    continue;
                }
                total += read;
                offset += read;
                count -= read;
            }
            return total;
        }
    }
}
