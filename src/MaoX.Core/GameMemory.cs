using System.Runtime.InteropServices;

namespace MaoX.Core;

/// <summary>和 PCL 一样按可用内存、模组数量自动分配 -Xmx，并提供调优的 G1GC 参数。</summary>
public static class GameMemory
{
    private static readonly string[] ModExtensions = [".jar", ".zip", ".litemod"];

    public static (long TotalMb, long AvailableMb) SystemRam()
    {
        if (OperatingSystem.IsWindows() && WindowsRam(out var total, out var available))
            return (total, available);
        if ((OperatingSystem.IsLinux() || OperatingSystem.IsAndroid()) && LinuxRam(out total, out available))
            return (total, available);
        if ((OperatingSystem.IsMacOS() || OperatingSystem.IsIOS()) && MacTotal(out total))
        {
            var gc = GC.GetGCMemoryInfo();
            var used = gc.MemoryLoadBytes > 0 ? gc.MemoryLoadBytes / 1048576 : total / 2;
            return (total, Math.Max(512, total - used));
        }
        var info = GC.GetGCMemoryInfo();
        total = Math.Max(1024, info.TotalAvailableMemoryBytes / 1048576);
        var load = info.MemoryLoadBytes > 0 ? info.MemoryLoadBytes / 1048576 : total / 2;
        return (total, Math.Max(512, total - load));
    }

    public static long TotalMb() => SystemRam().TotalMb;

    public static long AvailableMb() => SystemRam().AvailableMb;

    public static int CountMods(string modsDir)
    {
        if (string.IsNullOrEmpty(modsDir) || !Directory.Exists(modsDir))
            return 0;
        try
        {
            return Directory.EnumerateFiles(modsDir)
                .Count(path =>
                {
                    var name = Path.GetFileName(path);
                    if (name.StartsWith('.') || name.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase))
                        return false;
                    return ModExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);
                });
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    /// <summary>当前配置下应使用的最大堆内存（MB）。版本单独指定了内存时不再自动算。</summary>
    public static int Allocate(GameLauncher launcher, string versionId, LauncherConfig cfg)
    {
        var settings = launcher.VersionSettings(versionId);
        if (settings.Bool("custom") && settings.Get("max_memory") != null)
            return Clamp((int)settings.Int("max_memory", cfg.MaxMemory));
        if (cfg.AutoMemoryOn)
        {
            var loader = launcher.DetectLoader(versionId);
            var mods = Mods.ModLoaders.Contains(loader.Loader)
                ? CountMods(Path.Combine(launcher.GameDirFor(versionId), "mods"))
                : 0;
            return Auto(AvailableMb(), mods, loader.Loader);
        }
        return Clamp(cfg.MaxMemory);
    }

    /// <summary>PCL GetRam 自动档：单位 MB。availableMb 为当前可用物理内存。</summary>
    public static int Auto(long availableMb, int mods, string loader)
    {
        double min, target1, target2, target3;
        if (Mods.ModLoaders.Contains(loader))
        {
            min = 0.5 + mods / 150.0;
            target1 = 1.5 + mods / 90.0;
            target2 = 2.7 + mods / 50.0;
            target3 = 4.5 + mods / 25.0;
        }
        else if (loader == "optifine")
        {
            min = 0.5;
            target1 = 1.5;
            target2 = 3;
            target3 = 5;
        }
        else
        {
            min = 0.5;
            target1 = 1.5;
            target2 = 2.5;
            target3 = 4;
        }

        var available = Math.Round(availableMb / 1024.0 * 10) / 10;
        var give = 0.0;
        void Stage(double delta, double ratio)
        {
            if (available < 0.1 || delta <= 0)
                return;
            give += Math.Min(available * ratio, delta);
            available -= delta / ratio;
        }
        Stage(target1, 1);
        Stage(target2 - target1, 0.7);
        Stage(target3 - target2, 0.4);
        Stage(target3, 0.15);
        give = Math.Round(Math.Max(give, min), 1);
        return Clamp((int)Math.Floor(give * 1024));
    }

    public static int Clamp(int mb)
    {
        mb = Math.Max(512, mb);
        var (total, _) = SystemRam();
        if (Platform.IsIOS)
            mb = Math.Min(mb, Math.Max(512, (int)(total / 4)));
        else if (Platform.IsAndroid)
            mb = Math.Min(mb, Math.Max(512, (int)(total / 2)));
        else
            mb = Math.Min(mb, Math.Max(512, (int)Math.Max(512, total - 512)));
        return mb;
    }

    /// <summary>基础 G1GC；开启内存优化时加上 PCL 同款调优参数。</summary>
    public static IEnumerable<string> JvmFlags(LauncherConfig cfg)
    {
        yield return "-XX:+UseG1GC";
        yield return "-XX:-OmitStackTraceInFastThrow";
        if (!cfg.OptimizeMemoryOn)
            yield break;
        yield return "-XX:+UnlockExperimentalVMOptions";
        yield return "-XX:-UseAdaptiveSizePolicy";
        yield return "-XX:MaxGCPauseMillis=50";
        yield return "-XX:G1NewSizePercent=20";
        yield return "-XX:G1MaxNewSizePercent=60";
        yield return "-XX:G1HeapRegionSize=16M";
        yield return "-XX:G1ReservePercent=15";
    }

    private static bool WindowsRam(out long totalMb, out long availableMb)
    {
        totalMb = availableMb = 0;
        var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        if (!GlobalMemoryStatusEx(ref status) || status.TotalPhys == 0)
            return false;
        totalMb = (long)(status.TotalPhys / 1048576);
        availableMb = (long)(status.AvailPhys / 1048576);
        return true;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    private static bool LinuxRam(out long totalMb, out long availableMb)
    {
        totalMb = availableMb = 0;
        if (!File.Exists("/proc/meminfo"))
            return false;
        foreach (var line in File.ReadLines("/proc/meminfo"))
        {
            if (line.StartsWith("MemTotal:", StringComparison.Ordinal))
                totalMb = ParseKb(line);
            else if (line.StartsWith("MemAvailable:", StringComparison.Ordinal))
                availableMb = ParseKb(line);
        }
        return totalMb > 0 && availableMb > 0;
    }

    private static long ParseKb(string line)
    {
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > 1 && long.TryParse(parts[1], out var kb) ? kb / 1024 : 0;
    }

    private static bool MacTotal(out long totalMb)
    {
        totalMb = 0;
        ulong value = 0;
        nuint size = sizeof(ulong);
        if (sysctlbyname("hw.memsize", ref value, ref size, 0, 0) != 0 || value == 0)
            return false;
        totalMb = (long)(value / 1048576);
        return true;
    }

    [DllImport("libc", EntryPoint = "sysctlbyname")]
    private static extern int sysctlbyname(string name, ref ulong oldp, ref nuint oldlenp, nint newp, nuint newlen);
}
