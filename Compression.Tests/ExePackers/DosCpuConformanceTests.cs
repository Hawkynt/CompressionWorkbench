using System.IO.Compression;
using System.Text;
using System.Text.Json;
using FileFormat.ExePackers;

namespace Compression.Tests.ExePackers;

/// <summary>
/// Runs the emulator against the hardware-captured SingleStepTests 8088 suite (v2, MIT,
/// https://github.com/SingleStepTests/8088, captured from an AMD D8088): every test sets up
/// registers and memory, executes one instruction and compares registers, flags (undefined ones
/// masked as the suite's metadata says) and memory. Needs <c>CWB_8088_TESTS</c> naming a
/// directory with the suite's <c>v2</c> files.
/// <para>
/// Where the 8088 and the 80186 differ, the emulator follows the 80186 (DOSBox and every packer
/// stub here assume at least that): those opcodes (0F, 60-6F, C0, C1, C8, C9), shift counts of
/// 32 and more, REP-prefixed DIV/IDIV and the undocumented forms are not compared.
/// </para>
/// </summary>
[TestFixture]
[Category("ExternalInterop")]
public class DosCpuConformanceTests {
  private static string? Suite => Environment.GetEnvironmentVariable("CWB_8088_TESTS") is { Length: > 0 } p && Directory.Exists(p) ? p : null;

  private static readonly HashSet<string> Skipped = ["0F", "60", "61", "62", "63", "64", "65", "66", "67", "68", "69", "6A", "6B", "6C", "6D", "6E", "6F", "C0", "C1", "C8", "C9"];

  [Test]
  public void Given_TheHardwareCapturedSuite_When_EveryTestRuns_Then_TheEmulatorMatchesTheCpu() {
    if (Suite == null) Assert.Ignore("Set CWB_8088_TESTS to the SingleStepTests/8088 v2 directory.");
    using var metaDoc = JsonDocument.Parse(File.ReadAllText(Path.Combine(Suite, "metadata.json")));
    var opcodes = metaDoc.RootElement.GetProperty("opcodes");
    var report = new StringBuilder();
    var failures = 0;
    var cpu = new DosCpu();
    foreach (var file in Directory.GetFiles(Suite, "*.json.gz").OrderBy(f => f, StringComparer.Ordinal)) {
      var name = Path.GetFileName(file)[..^".json.gz".Length];
      var parts = name.Split('.');
      if (Skipped.Contains(parts[0]))
        continue;
      if (!opcodes.TryGetProperty(parts[0], out var meta))
        continue;
      if (parts.Length > 1) {
        if (!meta.TryGetProperty("reg", out var regs) || !regs.TryGetProperty(parts[1], out meta))
          continue;
      }
      if (meta.TryGetProperty("status", out var status) && status.GetString() != "normal")
        continue;
      var mask = meta.TryGetProperty("flags-mask", out var fm) ? fm.GetInt32() : 0xFFFF;
      var shift = parts[0] is "D0" or "D1" or "D2" or "D3";

      using var gz = new GZipStream(File.OpenRead(file), CompressionMode.Decompress);
      using var doc = JsonDocument.Parse(gz);
      var count = 0;
      var bad = 0;
      string? first = null;
      foreach (var test in doc.RootElement.EnumerateArray()) {
        var init = test.GetProperty("initial");
        var initRegs = init.GetProperty("regs");
        if (shift && parts[0] is "D2" or "D3" && (initRegs.GetProperty("cx").GetInt32() & 0xFF) >= 32)
          continue;
        // The 8088 negates the quotient of a REP-prefixed IDIV; the 80186 does not.
        if (parts[0] is "F6" or "F7" && test.GetProperty("bytes").EnumerateArray().Any(b => b.GetInt32() is 0xF2 or 0xF3))
          continue;
        ++count;
        var touched = new List<int>();
        foreach (var r in init.GetProperty("ram").EnumerateArray()) {
          var a = r[0].GetInt32();
          cpu.Memory[a] = (byte)r[1].GetInt32();
          touched.Add(a);
        }
        Load(cpu, initRegs);
        cpu.Halted = false;
        string? error = null;
        try {
          cpu.Step();
        } catch (InvalidOperationException ex) {
          error = ex.Message;
        }
        var fin = test.GetProperty("final");
        var expectRegs = fin.GetProperty("regs");
        if (error == null) {
          foreach (var p in expectRegs.EnumerateObject()) {
            var expect = p.Value.GetInt32();
            var actual = Get(cpu, p.Name);
            if (p.Name == "flags" ? ((actual ^ expect) & mask) != 0 : actual != expect) {
              error = $"{p.Name}={actual:X4} expected {expect:X4}";
              break;
            }
          }
          if (error == null && !expectRegs.TryGetProperty("flags", out _) && ((cpu.Flags ^ initRegs.GetProperty("flags").GetInt32()) & mask) != 0)
            error = $"flags={cpu.Flags:X4} expected unchanged {initRegs.GetProperty("flags").GetInt32():X4}";
          // A divide error pushes the flags the division left behind; the undefined ones are
          // masked there as in the register.
          var pushedFlags = -1;
          if (expectRegs.TryGetProperty("cs", out _) && parts[0] is "F6" or "F7" or "D4")
            pushedFlags = ((U(initRegs, "ss") << 4) + (ushort)(U(initRegs, "sp") - 2)) & 0xFFFFF;
          if (error == null)
            foreach (var r in fin.GetProperty("ram").EnumerateArray()) {
              var a = r[0].GetInt32();
              touched.Add(a);
              var diff = cpu.Memory[a] ^ r[1].GetInt32();
              if (a == pushedFlags) diff &= mask & 0xFF;
              else if (a == pushedFlags + 1) diff &= mask >> 8;
              if (diff != 0) {
                error = $"mem[{a:X5}]={cpu.Memory[a]:X2} expected {r[1].GetInt32():X2}";
                break;
              }
            }
        }
        if (error != null) {
          ++bad;
          first ??= $"{test.GetProperty("name").GetString()}: {error}";
        }
        foreach (var a in touched) cpu.Memory[a] = 0;
        foreach (var r in fin.GetProperty("ram").EnumerateArray()) cpu.Memory[r[0].GetInt32()] = 0;
        // Memory the instruction wrote outside the listed addresses is cleared lazily: the
        // suite lists every address an instruction touches.
      }
      if (bad > 0) {
        failures += bad;
        report.AppendLine($"{name}: {bad}/{count} failed, first: {first}");
      }
    }
    var reportPath = Environment.GetEnvironmentVariable("CWB_8088_REPORT");
    if (!string.IsNullOrEmpty(reportPath))
      File.WriteAllText(reportPath, report.ToString());
    Assert.That(failures, Is.Zero, report.ToString());
  }

  private static void Load(DosCpu cpu, JsonElement regs) {
    cpu.R[DosCpu.AX] = U(regs, "ax"); cpu.R[DosCpu.BX] = U(regs, "bx"); cpu.R[DosCpu.CX] = U(regs, "cx"); cpu.R[DosCpu.DX] = U(regs, "dx");
    cpu.R[DosCpu.SP] = U(regs, "sp"); cpu.R[DosCpu.BP] = U(regs, "bp"); cpu.R[DosCpu.SI] = U(regs, "si"); cpu.R[DosCpu.DI] = U(regs, "di");
    cpu.S[DosCpu.CS] = U(regs, "cs"); cpu.S[DosCpu.DS] = U(regs, "ds"); cpu.S[DosCpu.ES] = U(regs, "es"); cpu.S[DosCpu.SS] = U(regs, "ss");
    cpu.IP = U(regs, "ip");
    cpu.Flags = U(regs, "flags");
  }

  private static ushort U(JsonElement e, string n) => (ushort)e.GetProperty(n).GetInt32();

  private static int Get(DosCpu cpu, string n) => n switch {
    "ax" => cpu.R[DosCpu.AX], "bx" => cpu.R[DosCpu.BX], "cx" => cpu.R[DosCpu.CX], "dx" => cpu.R[DosCpu.DX],
    "sp" => cpu.R[DosCpu.SP], "bp" => cpu.R[DosCpu.BP], "si" => cpu.R[DosCpu.SI], "di" => cpu.R[DosCpu.DI],
    "cs" => cpu.S[DosCpu.CS], "ds" => cpu.S[DosCpu.DS], "es" => cpu.S[DosCpu.ES], "ss" => cpu.S[DosCpu.SS],
    "ip" => cpu.IP, "flags" => cpu.Flags, _ => -1,
  };
}
