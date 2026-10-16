using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace FanPilot.Hardware;

public sealed record Temperatures(int? Cpu, string? CpuSource, int? Gpu);

/// <summary>
/// Where the engine gets temperatures. CPU: the fan backend's own reading, falling back to the ACPI
/// thermal zone when the backend reports nothing (some ASUS models read 0). GPU: optional.
/// </summary>
public sealed class TemperatureReader(IFanBackend backend, Func<int?>? readGpu = null, Func<int?>? readAcpi = null)
{
    public Temperatures Read()
    {
        int? cpu = Safe(backend.ReadCpuTemp);
        string? source = cpu is null ? null : "ASUS";
        if (cpu is null && readAcpi is not null)
        {
            cpu = Safe(readAcpi);
            if (cpu is not null) source = "ACPI thermal zone";
        }
        return new Temperatures(cpu, source, readGpu is null ? null : Safe(readGpu));
    }

    static int? Safe(Func<int?> read)
    {
        try { return read(); }
        catch (Exception) { return null; }
    }
}

/// <summary>
/// The ACPI thermal zones Windows exposes as performance counters. Coarser than the CPU package
/// sensor and on some laptops a fixed dummy value, so it's only a fallback.
/// </summary>
public sealed class AcpiThermalZone : IDisposable
{
    readonly List<PerformanceCounter> _counters = [];

    public AcpiThermalZone()
    {
        try
        {
            var category = new PerformanceCounterCategory("Thermal Zone Information");
            foreach (var instance in category.GetInstanceNames())
                _counters.Add(new PerformanceCounter("Thermal Zone Information", "High Precision Temperature", instance, readOnly: true));
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException or Win32Exception)
        {
            Log.Write($"No ACPI thermal zones: {ex.Message}");
        }
    }

    /// <summary>Hottest zone in C, or null.</summary>
    public int? Read()
    {
        int? hottest = null;
        foreach (var counter in _counters)
        {
            try
            {
                // Tenths of a kelvin.
                double c = counter.NextValue() / 10.0 - 273.15;
                if (c is > 5 and < 125 && (hottest is null || c > hottest)) hottest = (int)Math.Round(c);
            }
            catch (InvalidOperationException) { }
        }
        return hottest;
    }

    public void Dispose()
    {
        foreach (var counter in _counters) counter.Dispose();
    }
}

/// <summary>
/// GPU temperature from the WDDM adapter performance data (what Task Manager shows), through
/// D3DKMT. Works for any vendor and doesn't wake a sleeping Optimus GPU: an idle dGPU just reports
/// no temperature, which is shown as "--".
/// </summary>
public sealed class GpuTemperature
{
    const uint KMTQAITYPE_ADAPTERPERFDATA = 62;

    public int? Read()
    {
        int? hottest = null;
        foreach (var luid in EnumerateAdapters())
        {
            var open = new D3DKMT_OPENADAPTERFROMLUID { AdapterLuid = luid };
            if (D3DKMTOpenAdapterFromLuid(ref open) != 0) continue;
            try
            {
                var perf = new D3DKMT_ADAPTER_PERFDATA();
                int size = Marshal.SizeOf<D3DKMT_ADAPTER_PERFDATA>();
                IntPtr buffer = Marshal.AllocHGlobal(size);
                try
                {
                    Marshal.StructureToPtr(perf, buffer, false);
                    var query = new D3DKMT_QUERYADAPTERINFO
                    {
                        hAdapter = open.hAdapter,
                        Type = KMTQAITYPE_ADAPTERPERFDATA,
                        pPrivateDriverData = buffer,
                        PrivateDriverDataSize = (uint)size,
                    };
                    if (D3DKMTQueryAdapterInfo(ref query) != 0) continue;
                    perf = Marshal.PtrToStructure<D3DKMT_ADAPTER_PERFDATA>(buffer);
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }

                // Deci-Celsius. Zero means the adapter isn't reporting (asleep, or no sensor).
                double c = perf.Temperature / 10.0;
                if (c is > 5 and < 125 && (hottest is null || c > hottest)) hottest = (int)Math.Round(c);
            }
            finally
            {
                var close = new D3DKMT_CLOSEADAPTER { hAdapter = open.hAdapter };
                D3DKMTCloseAdapter(ref close);
            }
        }
        return hottest;
    }

    static List<LUID> EnumerateAdapters()
    {
        var result = new List<LUID>();
        var enumArgs = new D3DKMT_ENUMADAPTERS2();
        if (D3DKMTEnumAdapters2(ref enumArgs) != 0 || enumArgs.NumAdapters == 0) return result;

        int itemSize = Marshal.SizeOf<D3DKMT_ADAPTERINFO>();
        enumArgs.pAdapters = Marshal.AllocHGlobal(itemSize * (int)enumArgs.NumAdapters);
        try
        {
            if (D3DKMTEnumAdapters2(ref enumArgs) != 0) return result;
            for (int i = 0; i < enumArgs.NumAdapters; i++)
            {
                var info = Marshal.PtrToStructure<D3DKMT_ADAPTERINFO>(enumArgs.pAdapters + i * itemSize);
                result.Add(info.AdapterLuid);
                var close = new D3DKMT_CLOSEADAPTER { hAdapter = info.hAdapter };
                D3DKMTCloseAdapter(ref close);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(enumArgs.pAdapters);
        }
        return result;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct LUID { public uint LowPart; public int HighPart; }

    [StructLayout(LayoutKind.Sequential)]
    struct D3DKMT_ADAPTERINFO
    {
        public uint hAdapter;
        public LUID AdapterLuid;
        public uint NumOfSources;
        public int bPrecisePresentRegionsPreferred;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct D3DKMT_ENUMADAPTERS2 { public uint NumAdapters; public IntPtr pAdapters; }

    [StructLayout(LayoutKind.Sequential)]
    struct D3DKMT_OPENADAPTERFROMLUID { public LUID AdapterLuid; public uint hAdapter; }

    [StructLayout(LayoutKind.Sequential)]
    struct D3DKMT_CLOSEADAPTER { public uint hAdapter; }

    [StructLayout(LayoutKind.Sequential)]
    struct D3DKMT_QUERYADAPTERINFO
    {
        public uint hAdapter;
        public uint Type;
        public IntPtr pPrivateDriverData;
        public uint PrivateDriverDataSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct D3DKMT_ADAPTER_PERFDATA
    {
        public uint PhysicalAdapterIndex;
        public ulong MemoryFrequency;
        public ulong MaxMemoryFrequency;
        public ulong MaxMemoryFrequencyOC;
        public ulong MemoryBandwidth;
        public ulong PCIEBandwidth;
        public uint FanRPM;
        public uint Power;
        public uint Temperature;
        public byte PowerStateOverride;
    }

    [DllImport("gdi32.dll")] static extern int D3DKMTEnumAdapters2(ref D3DKMT_ENUMADAPTERS2 args);
    [DllImport("gdi32.dll")] static extern int D3DKMTOpenAdapterFromLuid(ref D3DKMT_OPENADAPTERFROMLUID args);
    [DllImport("gdi32.dll")] static extern int D3DKMTCloseAdapter(ref D3DKMT_CLOSEADAPTER args);
    [DllImport("gdi32.dll")] static extern int D3DKMTQueryAdapterInfo(ref D3DKMT_QUERYADAPTERINFO args);
}
