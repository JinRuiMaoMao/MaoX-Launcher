using System.Diagnostics;

namespace MaoX.Core;

/// <summary>正在运行的游戏。电脑上是 java 子进程；手机上是启动器自己的游戏进程，由平台层实现。</summary>
public abstract class RunningGame
{
    public abstract bool HasExited { get; }

    public abstract void Kill();

    /// <summary>逐行读取游戏输出，游戏退出、输出读完后返回。</summary>
    public abstract Task ReadOutputAsync(Action<string> onLine);

    public abstract Task<int> WaitForExitAsync();

    public static RunningGame Of(Process process) => new ProcessGame(process);

    private sealed class ProcessGame(Process process) : RunningGame
    {
        public override bool HasExited => process.HasExited;

        public override void Kill() => process.Kill(true);

        public override Task ReadOutputAsync(Action<string> onLine) => Task.WhenAll(
            Task.Run(() => GameOutput.ReadLines(process.StandardOutput.BaseStream, onLine)),
            Task.Run(() => GameOutput.ReadLines(process.StandardError.BaseStream, onLine)));

        public override async Task<int> WaitForExitAsync()
        {
            await process.WaitForExitAsync();
            return process.ExitCode;
        }
    }
}
