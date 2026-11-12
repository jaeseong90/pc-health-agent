using System.Text;
using PcAgent.Collection;
using PcAgent.Export;
using PcAgent.Inventory;
using PcAgent.Storage;

namespace PcAgent.Tests;

public class ParsingTests
{
    [Fact]
    public void Smbios_parser_reads_system_slots_and_modules()
    {
        var table = new List<byte>();

        // Type 1 (System): Manufacturer=1, Product=2, Serial=3
        var t1 = new byte[0x1B];
        t1[0] = 1; t1[1] = 0x1B; t1[4] = 1; t1[5] = 2; t1[7] = 3;
        table.AddRange(t1);
        table.AddRange(Strings("LENOVO", "20XW", "PF123"));

        // Type 16 (Memory Array): Use=System memory, 2 devices
        var t16 = new byte[0x17];
        t16[0] = 16; t16[1] = 0x17; t16[5] = 0x03; t16[0x0D] = 2;
        table.AddRange(t16);
        table.AddRange([0, 0]);

        // Type 17: 8192MB DDR4 SODIMM 3200
        var t17 = new byte[0x28];
        t17[0] = 17; t17[1] = 0x28;
        t17[0x0C] = 0x00; t17[0x0D] = 0x20;   // 0x2000 = 8192 MB
        t17[0x0E] = 0x0D;                     // SODIMM
        t17[0x10] = 1;                        // locator string
        t17[0x12] = 0x1A;                     // DDR4
        t17[0x15] = 0x80; t17[0x16] = 0x0C;   // 3200
        table.AddRange(t17);
        table.AddRange(Strings("DIMM 0"));

        // Type 17: 빈 슬롯
        var empty = new byte[0x28];
        empty[0] = 17; empty[1] = 0x28;
        table.AddRange(empty);
        table.AddRange([0, 0]);

        // Type 127 (End)
        table.AddRange([127, 4, 0, 0, 0, 0]);

        var raw = new byte[8 + table.Count];
        BitConverter.GetBytes(table.Count).CopyTo(raw, 4);
        table.CopyTo(raw, 8);

        var info = SmbiosParser.Parse(raw);

        Assert.Equal("LENOVO", info.SystemManufacturer);
        Assert.Equal("20XW", info.SystemProduct);
        Assert.Equal("PF123", info.SystemSerial);
        Assert.Equal(2, info.MemorySlots);
        Assert.Equal(1, info.EmptySlotEntries);
        var m = Assert.Single(info.Modules);
        Assert.Equal(8192, m.SizeMb);
        Assert.Equal("DDR4", m.Type);
        Assert.Equal("SODIMM", m.FormFactor);
        Assert.Equal(3200, m.SpeedMts);
        Assert.Equal("DIMM 0", m.Locator);
    }

    [Theory]
    [InlineData("To Be Filled By O.E.M.", null)]
    [InlineData("Default string", null)]
    [InlineData("  ABC123 ", "ABC123")]
    public void Smbios_placeholder_strings_are_dropped(string input, string? expected) =>
        Assert.Equal(expected, SmbiosParser.Clean(input));

    [Fact]
    public void Csv_escape_and_parse_round_trip()
    {
        string[] values = ["plain", "with,comma", "with \"quote\"", "=SUM(A1)", "한글"];
        var line = string.Join(',', values.Select(CsvWriter.Escape));
        var parsed = Csv.ParseLine(line);

        Assert.Equal("plain", parsed[0]);
        Assert.Equal("with,comma", parsed[1]);
        Assert.Equal("with \"quote\"", parsed[2]);
        Assert.Equal("'=SUM(A1)", parsed[3]); // 수식 주입 방지
        Assert.Equal("한글", parsed[4]);
    }

    [Fact]
    public void DataStore_round_trips_metrics_and_merges_event_phases()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pcagent-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var store = new DataStore(dir);
            var t = DateTimeOffset.UtcNow.AddMinutes(-10);
            var minute = new DateTimeOffset(t.Year, t.Month, t.Day, t.Hour, t.Minute, 0, TimeSpan.Zero);
            store.AppendMetric(new MetricMinute { MinuteUtc = minute, Samples = 12, CoveredSeconds = 60, CpuAvg = 12.5, CpuMax = 40, MemAvailAvg = 30, MemAvailMin = 25, CommitAvg = 50, CommitMax = 55, DiskActiveAvg = 3.3, MemPressureSeconds = 10 });

            var id = Guid.NewGuid();
            store.AppendPressure(new PressureRecord { Id = id, Kind = PressureKind.Memory, StartedUtc = minute, Peak = 90, Processes = [new ProcessUsage { Name = "app", Instances = 1 }] });
            store.AppendPressure(new PressureRecord { Id = id, Kind = PressureKind.Memory, Phase = PressureRecord.PhaseEnd, StartedUtc = minute, EndedUtc = minute.AddMinutes(2), Peak = 95 });
            File.AppendAllText(Directory.GetFiles(dir, "pressure-*.jsonl")[0], "{\"id\":\"broken"); // 잘린 줄

            var metrics = store.ReadMetrics(minute.AddHours(-1), DateTimeOffset.UtcNow);
            var m = Assert.Single(metrics);
            Assert.Equal(12.5, m.CpuAvg);
            Assert.Equal(3.3, m.DiskActiveAvg);
            Assert.Null(m.HardFaultsAvg);
            Assert.Equal(10, m.MemPressureSeconds);

            var e = Assert.Single(store.ReadPressureEvents(minute.AddHours(-1), DateTimeOffset.UtcNow));
            Assert.Equal(minute.AddMinutes(2), e.EndedUtc);
            Assert.Equal(95, e.Peak);
            Assert.Equal("app", Assert.Single(e.Processes!).Name);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static byte[] Strings(params string[] values)
    {
        var bytes = new List<byte>();
        foreach (var v in values)
        {
            bytes.AddRange(Encoding.ASCII.GetBytes(v));
            bytes.Add(0);
        }
        bytes.Add(0);
        return bytes.ToArray();
    }
}
