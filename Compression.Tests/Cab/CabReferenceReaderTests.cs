#pragma warning disable CS1591
using System.Diagnostics;
using FileFormat.Cab;

namespace Compression.Tests.Cab;

/// <summary>
/// A cabinet we wrote has to be one the tools that own the format will open and
/// check, whichever compression it holds.
/// </summary>
/// <remarks>
/// <para><c>cabextract</c> is libmspack, the reference implementation for every
/// encoding a cabinet may carry, and it checks things 7-Zip lets pass — it is
/// how a deflate tree of ours that described half its code space came to light,
/// and how three separate faults in our LZX did. 7-Zip is asked as well because
/// it is the reader most people have, and the two do not always agree.</para>
///
/// <para>The payloads are sized to span more than one data record, because the
/// faults that survived longest all lived at the joins between them: a record
/// that did not account for exactly 32 768 bytes, and a record that did not
/// begin on a word boundary of the bit stream.</para>
/// </remarks>
[TestFixture]
public class CabReferenceReaderTests {

  private static readonly CabCompressionType[] CompressionsOthersRead = [
    CabCompressionType.None,
    CabCompressionType.MsZip,
    CabCompressionType.Lzx,
  ];

  private static List<(string Name, byte[] Data)> ProbeSet() {
    var phrase = System.Text.Encoding.ASCII.GetBytes("The quick brown fox jumps over the lazy dog. ");
    var text = new byte[200_000];
    for (var i = 0; i < text.Length; ++i) text[i] = phrase[i % phrase.Length];

    var oneByte = new byte[70_000];
    Array.Fill(oneByte, (byte)0x11);

    var random = new byte[40_000];
    new Random(9).NextBytes(random);

    // Dense in x86 call opcodes: the shape a cabinet of executables has, and the
    // one that shows whether call-target rewriting was handled consistently.
    var calls = new byte[80_000];
    var rng = new Random(11);
    for (var i = 0; i + 5 < calls.Length; i += 5) {
      calls[i] = 0xE8;
      var target = rng.Next(-2_000, 2_000_000);
      calls[i + 1] = (byte)target;
      calls[i + 2] = (byte)(target >> 8);
      calls[i + 3] = (byte)(target >> 16);
      calls[i + 4] = (byte)(target >> 24);
    }

    return [
      ("A.TXT", "a short line of text\n"u8.ToArray()),
      ("TEXT.TXT", text),
      ("ONEBYTE.BIN", oneByte),
      ("RANDOM.BIN", random),
      ("CALLS.BIN", calls),
    ];
  }

  [TestCaseSource(nameof(CompressionsOthersRead)), Category("Interop")]
  public void ACabinetWeWrote_PassesTheToolsThatOwnTheFormat(CabCompressionType compression) {
    if (!OperatingSystem.IsLinux()) Assert.Ignore("The reference readers run on Linux.");

    var cabextract = Which("cabextract");
    var sevenZip = Which("7z");
    if (cabextract == null && sevenZip == null)
      Assert.Ignore("neither cabextract nor 7z is installed.");

    var writer = new CabWriter(compressionType: compression);
    var stamp = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
    var files = ProbeSet();
    foreach (var (name, data) in files)
      writer.AddFile(name, data, stamp);

    var path = Path.Combine(Path.GetTempPath(), "cwb_cab_" + Guid.NewGuid().ToString("N")[..8] + ".cab");
    using (var output = File.Create(path))
      writer.WriteTo(output);

    try {
      if (cabextract != null) {
        var (exit, text) = Run(cabextract, $"-t \"{path}\"");
        Assert.That(text, Does.Contain("no errors"),
          $"cabextract would not test a {compression} cabinet we wrote: {FirstComplaint(text)}");
        Assert.That(exit, Is.Zero, $"cabextract exited {exit} on a {compression} cabinet we wrote");
      }

      if (sevenZip != null) {
        var (exit, text) = Run(sevenZip, $"t \"{path}\"");
        Assert.That(exit, Is.Zero,
          $"7-Zip would not test a {compression} cabinet we wrote: {FirstComplaint(text)}");
      }
    } finally {
      try { File.Delete(path); } catch { /* the scratch cabinet is already gone */ }
    }
  }

  private static string FirstComplaint(string output) {
    foreach (var line in output.Split('\n')) {
      var trimmed = line.Trim();
      if (trimmed.Length == 0) continue;
      if (trimmed.Contains("failed", StringComparison.OrdinalIgnoreCase)) return trimmed;
      if (trimmed.Contains("ERROR", StringComparison.Ordinal)) return trimmed;
    }
    return output.Split('\n').LastOrDefault(l => l.Trim().Length > 0)?.Trim() ?? "(no output)";
  }

  private static string? Which(string tool) {
    foreach (var dir in new[] { "/usr/bin", "/bin", "/usr/local/bin", "/usr/sbin" }) {
      var path = Path.Combine(dir, tool);
      if (File.Exists(path)) return path;
    }
    return null;
  }

  private static (int Exit, string Output) Run(string tool, string arguments) {
    var start = new ProcessStartInfo(tool, arguments) {
      RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
    };
    using var process = Process.Start(start)!;
    var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
    process.WaitForExit(180_000);
    return (process.HasExited ? process.ExitCode : -1, output);
  }
}
