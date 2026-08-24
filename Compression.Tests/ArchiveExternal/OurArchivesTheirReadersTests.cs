#pragma warning disable CS1591
using System.Diagnostics;
using Compression.Registry;

namespace Compression.Tests.ArchiveExternal;

/// <summary>
/// An archive we write has to be one the format's own tools can open.
/// </summary>
/// <remarks>
/// <para>The sibling fixture covers tar, cpio, ar, ISO, zip, cab and lha. This
/// adds the formats a reader happens to be installed for and nothing was asking:
/// 7z, arj, squashfs, xar and wim. Four of the five were already right. The fifth
/// was not, and nothing here could have told us — our own reader reads our own
/// writer's output happily, which is the same trap that hid a private LZO
/// encoding, a UBIFS name four bytes out of place, and a JFFS2 volume that read
/// back as holes.</para>
///
/// <para>Written as its own fixture rather than folded into the sibling so the
/// set of formats and the tools they need stay easy to read.</para>
/// </remarks>
[TestFixture]
public class OurArchivesTheirReadersTests {

  public sealed record Oracle(string FormatId, string Extension, string Tool,
      Func<string, string, string> Extract) {
    public override string ToString() => this.FormatId;
  }

  private static readonly Oracle[] Oracles = [
    new("SevenZip", "7z", "7z", (archive, into) => $"x -y -o\"{into}\" \"{archive}\""),
    new("Zip", "zip", "7z", (archive, into) => $"x -y -o\"{into}\" \"{archive}\""),
    new("Tar", "tar", "7z", (archive, into) => $"x -y -o\"{into}\" \"{archive}\""),
    new("Cab", "cab", "7z", (archive, into) => $"x -y -o\"{into}\" \"{archive}\""),
    new("Xar", "xar", "7z", (archive, into) => $"x -y -o\"{into}\" \"{archive}\""),
    new("SquashFs", "sqfs", "unsquashfs", (archive, into) => $"-d \"{into}\" \"{archive}\""),
    // -y answers every prompt: arj otherwise waits for a keypress and the test
    // simply hangs.
    new("Arj", "arj", "arj", (archive, into) => $"x -y \"{archive}\" \"{into}/\""),
    new("Wim", "wim", "7z", (archive, into) => $"x -y -o\"{into}\" \"{archive}\""),
  ];

  /// <summary>
  /// What a reference reader cannot open yet, and why.
  /// </summary>
  /// <remarks>
  /// Recorded rather than dropped from the list: a case nobody runs and nobody
  /// wrote down is a case nobody knows is missing.
  /// </remarks>
  private static readonly Dictionary<string, string> KnownGaps = new(StringComparer.Ordinal) {
    ["Wim"] =
      "our writer stores each file as an anonymous resource with no metadata resource and no "
      + "directory tree, and declares one image per file rather than one image holding them all, "
      + "so 7-Zip will not open it at all; our reader is fine and reads 7-Zip's WIM exactly",
  };

  private static IEnumerable<Oracle> Available() => Oracles;

  private static Dictionary<string, byte[]> ProbeSet() {
    var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);

    var text = "a short line of text\n"u8.ToArray();
    files["A.TXT"] = text;

    var random = new byte[5_000];
    new Random(17).NextBytes(random);
    files["B.BIN"] = random;                      // nothing to compress

    var repeated = new byte[20_000];
    Array.Fill(repeated, (byte)0x11);
    files["C.BIN"] = repeated;                    // compresses to almost nothing

    files["nested/D.TXT"] = "a file a directory down\n"u8.ToArray();
    return files;
  }

  [TestCaseSource(nameof(Available)), Category("Interop")]
  public void AnArchiveWeWrote_OpensInTheirReader(Oracle oracle) {
    if (!OperatingSystem.IsLinux()) Assert.Ignore("The reference readers run on Linux.");
    if (Which(oracle.Tool) == null) Assert.Ignore($"{oracle.Tool} is not installed.");
    if (KnownGaps.TryGetValue(oracle.FormatId, out var gap))
      Assert.Ignore($"{oracle.FormatId}: known gap — {gap}.");

    var ops = FormatRegistry.GetArchiveOps(oracle.FormatId);
    if (ops is not IArchiveCreatable creatable) {
      Assert.Ignore($"{oracle.FormatId} cannot create an archive.");
      return;
    }

    var work = Path.Combine(Path.GetTempPath(), "cwb_ours_" + Guid.NewGuid().ToString("N")[..8]);
    var into = Path.Combine(work, "out");
    Directory.CreateDirectory(into);

    try {
      var expected = ProbeSet();
      var archive = Path.Combine(work, "ours." + oracle.Extension);
      using (var output = File.Create(archive))
        creatable.Create(output,
          expected.Select(kv => ArchiveInputInfo.InMemory(kv.Key, kv.Value)).ToList(),
          new FormatCreateOptions());

      var (exit, text) = Run(oracle.Tool, oracle.Extract(archive, into));
      Assert.That(exit, Is.EqualTo(0),
        $"{oracle.Tool} would not open an archive we wrote: {text.Split('\n').FirstOrDefault()}");

      foreach (var (name, want) in expected) {
        var leaf = Path.GetFileName(name);
        var found = Directory.EnumerateFiles(into, "*", SearchOption.AllDirectories)
          .FirstOrDefault(f => string.Equals(Path.GetFileName(f), leaf, StringComparison.Ordinal));

        Assert.That(found, Is.Not.Null,
          $"{oracle.Tool} opened our archive and did not find '{leaf}' in it");
        Assert.That(File.ReadAllBytes(found!), Is.EqualTo(want).AsCollection,
          $"{oracle.Tool} read '{leaf}' out of our archive and got different bytes");
      }
    } finally {
      try { Directory.Delete(work, true); } catch { /* best effort */ }
    }
  }

  private static string? Which(string tool) {
    foreach (var dir in new[] { "/usr/bin", "/bin", "/usr/local/bin", "/usr/sbin" }) {
      var path = Path.Combine(dir, tool);
      if (File.Exists(path)) return path;
    }
    return null;
  }

  private static (int Exit, string Output) Run(string tool, string arguments) {
    var start = new ProcessStartInfo(Which(tool) ?? tool, arguments) {
      RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
    };
    using var process = Process.Start(start)!;
    var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
    process.WaitForExit(120_000);
    return (process.HasExited ? process.ExitCode : -1, output);
  }
}
