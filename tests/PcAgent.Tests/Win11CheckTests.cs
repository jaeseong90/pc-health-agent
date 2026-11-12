using PcAgent.Analysis;
using PcAgent.Inventory;

namespace PcAgent.Tests;

public class Win11CheckTests
{
    [Theory]
    [InlineData("Intel(R) Core(TM) i5-8250U CPU @ 1.60GHz", CpuSupport.Supported)]
    [InlineData("Intel(R) Core(TM) i7-7700 CPU @ 3.60GHz", CpuSupport.Unsupported)]
    [InlineData("Intel(R) Core(TM) i7-4790 CPU @ 3.60GHz", CpuSupport.Unsupported)]
    [InlineData("Intel(R) Core(TM) i5-1035G1 CPU @ 1.00GHz", CpuSupport.Supported)]
    [InlineData("11th Gen Intel(R) Core(TM) i7-1165G7 @ 2.80GHz", CpuSupport.Supported)]
    [InlineData("12th Gen Intel(R) Core(TM) i7-1260P", CpuSupport.Supported)]
    [InlineData("13th Gen Intel(R) Core(TM) i5-13400", CpuSupport.Supported)]
    [InlineData("Intel(R) Core(TM) i5 CPU 650 @ 3.20GHz", CpuSupport.Unsupported)]
    [InlineData("Intel(R) Core(TM) Ultra 7 155H", CpuSupport.Supported)]
    [InlineData("Intel(R) Core(TM) 5 120U", CpuSupport.Supported)]
    [InlineData("AMD Ryzen 5 3600 6-Core Processor", CpuSupport.Supported)]
    [InlineData("AMD Ryzen 7 1700 Eight-Core Processor", CpuSupport.Unsupported)]
    [InlineData("AMD Ryzen 7 PRO 4750U with Radeon Graphics", CpuSupport.Supported)]
    [InlineData("AMD Ryzen AI 9 HX 370 w/ Radeon 890M", CpuSupport.Supported)]
    [InlineData("Snapdragon(R) X Elite - X1E78100 - Qualcomm(R) Oryon(TM) CPU", CpuSupport.Supported)]
    [InlineData("Intel(R) Xeon(R) CPU E5-2680 v4 @ 2.40GHz", CpuSupport.Unknown)]
    [InlineData(null, CpuSupport.Unknown)]
    public void Cpu_support_heuristic(string? cpu, CpuSupport expected) =>
        Assert.Equal(expected, Win11Check.EvaluateCpu(cpu));

    [Fact]
    public void Windows10_without_tpm2_is_not_ready()
    {
        var inv = TestData.Inventory(win11: false);
        inv.TpmVersion = "1.2";
        var r = Win11Check.Evaluate(inv);
        Assert.Equal("NotReady", r.Status);
        Assert.Contains(r.Issues, i => i.Contains("TPM 1.2"));
    }

    [Fact]
    public void Unknown_tpm_with_otherwise_ok_hardware_is_unknown()
    {
        var inv = TestData.Inventory(win11: false);
        inv.TpmVersion = null;
        Assert.Equal("Unknown", Win11Check.Evaluate(inv).Status);
    }

    [Fact]
    public void Modern_windows10_pc_is_ready()
    {
        Assert.Equal("Ready", Win11Check.Evaluate(TestData.Inventory(win11: false)).Status);
    }
}
