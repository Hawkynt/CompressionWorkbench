using System.Diagnostics;
using Compression.Core.Dictionary.Csc;

namespace Compression.Tests.Csc;

/// <summary>
/// Live round trips against libcsc's reference <c>csc</c> tool: it must decode what we write, we must
/// decode what it writes, and with the same switches our stream must equal its stream apart from
/// its uninitialised guard bytes.
/// </summary>
/// <remarks>
/// The tool is located as <c>$LIBCSC_CSC</c> (a path), else <c>libcsc-csc</c> on the host PATH, else
/// <c>libcsc-csc</c> inside WSL. (It is not looked up as <c>csc</c>, which is the C# compiler on most
/// PATHs.) Build it from <see href="https://github.com/fusiyuan2010/CSC"/> at commit
/// <c>c5dbe0944d07acbc97d2c04ec9f99a139c6f3931</c>: <c>make -C src csc</c> with g++, or compile
/// <c>src/libcsc/*.cpp</c> (all but <c>decomp.cpp</c>) with <c>cl /O2 /EHsc /D_7Z_TYPES_</c>, and
/// install the binary under that name. Without it every test here is ignored.
/// </remarks>
[TestFixture]
[Category("ExternalInterop")]
public sealed class CscLibcscInteropTests {
  private static readonly Lazy<Func<string[], (int ExitCode, string Output)>?> _tool = new(LocateTool);
  private string _tmpDir = null!;

  [SetUp]
  public void Setup() {
    this._tmpDir = Path.Combine(Path.GetTempPath(), $"cwb_csc_interop_{Guid.NewGuid():N}");
    Directory.CreateDirectory(this._tmpDir);
  }

  [TearDown]
  public void Teardown() {
    try { Directory.Delete(this._tmpDir, recursive: true); } catch { /* best effort */ }
  }

  public sealed record Case(string Name, Func<byte[]> Input, int Level, int WindowKiB = 0, string[]? Switches = null) {
    public override string ToString() => $"{this.Name}-m{this.Level}{(this.WindowKiB > 0 ? $"-d{this.WindowKiB}k" : "")}{string.Concat(this.Switches ?? [])}";

    public CscEncoderOptions Options => new(
      this.Level,
      this.WindowKiB > 0 ? this.WindowKiB * 1024L : CscEncoderOptions.DefaultDictionarySize,
      DeltaFilter: !(this.Switches?.Contains("-fdelta0") ?? false),
      TextFilter: !(this.Switches?.Contains("-ftxt0") ?? false),
      ExecutableFilter: !(this.Switches?.Contains("-fexe0") ?? false));

    public string[] ToolSwitches => [$"-m{this.Level}", .. this.WindowKiB > 0 ? [$"-d{this.WindowKiB}k"] : Array.Empty<string>(), .. this.Switches ?? []];
  }

  private static IEnumerable<Case> Cases() {
    for (var level = 1; level <= 5; ++level) {
      yield return new("empty", () => [], level);
      yield return new("one-byte", () => [0x7F], level);
      yield return new("text-100k", () => CscSamples.Text(100_000, 21), level);
      yield return new("random-64k", () => CscSamples.RandomBytes(65_536, 22), level);
      yield return new("mixed", CscSamples.Mixed, level);
      yield return new("wrap-200k", () => [.. CscSamples.Text(120_000, 23), .. CscSamples.X86(40_000, 24), .. CscSamples.Text(40_000, 23)], level, WindowKiB: 32);
    }

    yield return new("segments-4500k", () => CscSamples.Ramp(4_500_000), 2);
    yield return new("segments-mixed-5m", () => [.. Repeat(CscSamples.Mixed(), 40), .. CscSamples.RandomBytes(1_500_000, 25)], 3, WindowKiB: 1024);
    yield return new("filters-off", CscSamples.Mixed, 4, Switches: ["-fdelta0", "-ftxt0", "-fexe0"]);
  }

  [Test, TestCaseSource(nameof(Cases))]
  public void OurStream_IsDecodedByLibcsc(Case testCase) {
    var tool = RequireTool();
    var data = testCase.Input();
    var ours = Path.Combine(this._tmpDir, "ours.csc");
    var decoded = Path.Combine(this._tmpDir, "ours.out");
    File.WriteAllBytes(ours, CscCodec.Compress(data, testCase.Options));

    var result = tool(["d", ours, decoded]);

    Assert.Multiple(() => {
      Assert.That(result.ExitCode, Is.Zero, result.Output);
      Assert.That(File.ReadAllBytes(decoded), Is.EqualTo(data));
    });
  }

  [Test, TestCaseSource(nameof(Cases))]
  public void LibcscStream_IsDecodedByUs_AndEqualsOurs(Case testCase) {
    var tool = RequireTool();
    var data = testCase.Input();
    var raw = Path.Combine(this._tmpDir, "input.bin");
    var theirs = Path.Combine(this._tmpDir, "theirs.csc");
    File.WriteAllBytes(raw, data);

    var result = tool(["c", .. testCase.ToolSwitches, raw, theirs]);
    Assert.That(result.ExitCode, Is.Zero, result.Output);
    var stream = File.ReadAllBytes(theirs);

    Assert.Multiple(() => {
      Assert.That(CscCodec.Decompress(stream), Is.EqualTo(data));
      Assert.That(CscGuardBytes.Mask(CscCodec.Compress(data, testCase.Options)), Is.EqualTo(CscGuardBytes.Mask(stream)));
    });
  }

  private static byte[] Repeat(byte[] block, int count) {
    var result = new byte[block.Length * count];
    for (var i = 0; i < count; ++i) {
      block.CopyTo(result, i * block.Length);
      result[i * block.Length] = (byte)i;
    }

    return result;
  }

  private static Func<string[], (int ExitCode, string Output)> RequireTool() {
    var tool = _tool.Value;
    if (tool is null)
      Assert.Ignore("libcsc's csc tool not found: set LIBCSC_CSC or install it as libcsc-csc (see the class remarks).");
    return tool!;
  }

  private static Func<string[], (int ExitCode, string Output)>? LocateTool() {
    var configured = Environment.GetEnvironmentVariable("LIBCSC_CSC");
    if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
      return arguments => Run(configured, arguments);

    if (FsInteropToolbox.HostPathHasTool("libcsc-csc"))
      return arguments => Run("libcsc-csc", arguments);

    if (OperatingSystem.IsWindows() && FsInteropToolbox.WslAvailable && FsInteropToolbox.WslHasTool("libcsc-csc"))
      return arguments => {
        var line = string.Join(' ', arguments.Select(a => a.Contains(Path.DirectorySeparatorChar) ? FsInteropToolbox.WinToWsl(a) : a));
        var (stdOut, stdErr, exitCode) = FsInteropToolbox.RunWsl($"libcsc-csc {line}");
        return (exitCode, stdOut + stdErr);
      };

    return null;
  }

  private static (int ExitCode, string Output) Run(string executable, string[] arguments) {
    var start = new ProcessStartInfo(executable) {
      RedirectStandardOutput = true,
      RedirectStandardError = true,
      UseShellExecute = false,
    };
    foreach (var argument in arguments)
      start.ArgumentList.Add(argument);

    using var process = Process.Start(start)!;
    var stdOut = process.StandardOutput.ReadToEndAsync();
    var stdErr = process.StandardError.ReadToEndAsync();
    process.WaitForExit();
    return (process.ExitCode, stdOut.Result + stdErr.Result);
  }
}
