using FanPilot.Hardware;

namespace FanPilot.Tests;

/// <summary>Records what the engine writes; temperatures are set by the test.</summary>
internal sealed class FakeBackend(int fanCount = 2) : IFanBackend
{
    public int?[] Duties { get; } = new int?[fanCount];
    public int Writes { get; private set; }
    public int? CpuTemp { get; set; } = 50;
    public int? GpuTemp { get; set; }

    public string Description => "Fake";
    public int FanCount => fanCount;
    public int GetRpm(int fan) => Duties[fan] is int d ? d * 50 : 2500;

    public void SetDuty(int fan, int percent)
    {
        Duties[fan] = percent;
        Writes++;
    }

    public void Release(int fan)
    {
        Duties[fan] = null;
        Writes++;
    }

    public int? ReadCpuTemp() => CpuTemp;

    public void Dispose() { }
}
