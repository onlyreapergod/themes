using Win11ProSuite.Native;

namespace Win11ProSuite.Services;

/// <summary>Samples system-wide CPU and RAM load using cheap kernel32 calls.</summary>
public sealed class SystemStatsService
{
    private ulong _prevIdle, _prevKernel, _prevUser;
    private bool _hasPrevious;

    public (int Cpu, int Ram) Sample()
    {
        int cpu = 0;

        if (NativeMethods.GetSystemTimes(out NativeFileTime idle, out NativeFileTime kernel, out NativeFileTime user))
        {
            ulong i = ToUInt64(idle), k = ToUInt64(kernel), u = ToUInt64(user);

            if (_hasPrevious)
            {
                ulong idleDelta = i - _prevIdle;
                ulong total = (k - _prevKernel) + (u - _prevUser); // kernel time already includes idle time
                if (total > 0)
                {
                    cpu = (int)Math.Clamp((total - idleDelta) * 100.0 / total, 0, 100);
                }
            }

            _prevIdle = i;
            _prevKernel = k;
            _prevUser = u;
            _hasPrevious = true;
        }

        int ram = 0;
        var memory = new MemoryStatusEx { dwLength = (uint)System.Runtime.InteropServices.Marshal.SizeOf<MemoryStatusEx>() };
        if (NativeMethods.GlobalMemoryStatusEx(ref memory))
        {
            ram = (int)memory.dwMemoryLoad;
        }

        return (cpu, ram);
    }

    private static ulong ToUInt64(NativeFileTime ft) => ((ulong)ft.High << 32) | ft.Low;
}
