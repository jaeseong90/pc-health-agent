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

    [Fact]
    public void Legacy_boot_on_supported_hardware_needs_config_not_replacement()
    {
        var inv = TestData.Inventory(win11: false);
        inv.IsUefi = false;
        var r = Win11Check.Evaluate(inv);
        Assert.Equal("NeedsConfig", r.Status);
        Assert.Contains(r.Issues, i => i.Contains("MBR2GPT"));
    }

    [Fact]
    public void Missing_tpm_on_supported_cpu_needs_config_but_on_old_cpu_is_not_ready()
    {
        var modern = TestData.Inventory(win11: false);
        modern.TpmVersion = "none";
        Assert.Equal("NeedsConfig", Win11Check.Evaluate(modern).Status);

        var old = TestData.Inventory(win11: false, cpu: "Intel(R) Core(TM) i5-4590 CPU @ 3.30GHz");
        old.TpmVersion = "none";
        Assert.Equal("NotReady", Win11Check.Evaluate(old).Status);
    }

    [Theory]
    [InlineData("EnterpriseS", true)]
    [InlineData("EnterpriseSN", true)]
    [InlineData("IoTEnterpriseS", true)]
    [InlineData("Enterprise", false)]
    [InlineData("Professional", false)]
    [InlineData(null, false)]
    public void Ltsc_edition_detection(string? edition, bool expected) =>
        Assert.Equal(expected, Win11Check.IsLtsc(edition));
}
