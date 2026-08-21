#pragma warning disable CS1591
using System.Diagnostics;
using Compression.Registry;

namespace Compression.Tests.Operations;

/// <summary>
/// Volumes written by the reference tools have to read back here exactly.
/// </summary>
/// <remarks>
/// <para>Almost everything else here writes a volume and reads it back with the
/// same code. That answers whether this project agrees with itself, which is a
/// weaker question than whether it agrees with the implementations everyone else
/// uses — and the difference is not academic. Reading a hole as the end of a file
/// was invisible to every round-trip check in this codebase for exactly that
/// reason: these writers allocate every block, so no volume written here has a
/// hole in it, and the bug only appeared on volumes written by something else.
/// </para>
///
/// <para>So the volume is built by the format's own tool and only read here. No
/// mounting and no root: mke2fs, mkfs.erofs, mksquashfs, mkfs.cramfs and
/// mkfs.btrfs will all populate an image from a directory, which is what makes
/// this runnable rather than aspirational.</para>
///
/// <para>The file set is the shapes that have actually caught defects: two files
/// of one length with different contents, a file of bytes with the high bit set,
/// a file long enough to need indirect blocks, a file that is mostly hole, an
/// empty one, and one down a nested path.</para>
/// </remarks>
[TestFixture]
public class TheirWriterOurReaderTests {

  public sealed record Oracle(string Tool, string FormatId, Func<string, string, string> Arguments) {
    public override string ToString() => this.FormatId;
  }

  private static readonly Oracle[] Oracles = [
    // mke2fs wants a block count; 16k blocks of 1 KiB is ample for the probe set.
    new("mke2fs", "Ext", (dir, img) => $"-q -F -d \"{dir}\" -b 1024 -I 128 \"{img}\" 16384"),
    new("mkfs.erofs", "Erofs", (dir, img) => $"\"{img}\" \"{dir}\""),
    new("mksquashfs", "SquashFs", (dir, img) => $"\"{dir}\" \"{img}\" -no-progress -quiet -noappend"),
    new("mkfs.cramfs", "CramFs", (dir, img) => $"\"{dir}\" \"{img}\""),
    new("mkfs.btrfs", "Btrfs", (dir, img) => $"-q -f --rootdir \"{dir}\" \"{img}\""),
  ];

  private static IEnumerable<Oracle> Available() => Oracles;

  /// <summary>The shapes that have caught real defects in this codebase.</summary>
  private static Dictionary<string, byte[]> ProbeSet() {
    var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);

    // Two of one length, different contents. Matching a file by its size rather
    // than its identity swapped these in seven filesystems.
    var same1 = new byte[20_000];
    var same2 = new byte[20_000];
    for (var i = 0; i < same1.Length; ++i) { same1[i] = 0x11; same2[i] = 0x77; }
    files["SAME1.BIN"] = same1;
    files["SAME2.BIN"] = same2;

    // Every byte over 0x7F. Treating content as text destroyed these in Shar.
    var high = new byte[9_000];
    for (var i = 0; i < high.Length; ++i) high[i] = (byte)(0x80 + i % 0x80);
    files["HIGHBIT.BIN"] = high;

    // Long enough to need more than the direct block pointers.
    var big = new byte[300_000];
    for (var i = 0; i < big.Length; ++i) big[i] = (byte)(i * 31 + 7 + (i >> 11));
    files["BIG.BIN"] = big;

    // Mostly zeros, with something at each end.
    var holey = new byte[200_000];
    for (var i = 0; i < 2_000; ++i) holey[i] = (byte)(i * 13 + 1);
    for (var i = 198_000; i < holey.Length; ++i) holey[i] = (byte)(i * 13 + 1);
    files["HOLEY.BIN"] = holey;

    files["EMPTY.BIN"] = [];
    files["nested/deep/DOWN.BIN"] = "a file a few directories down"u8.ToArray();
    return files;
  }

  [TestCaseSource(nameof(Available)), Category("Interop")]
  public void AVolumeTheirToolWrote_ReadsBackExactly(Oracle oracle) {
    if (!OperatingSystem.IsLinux()) Assert.Ignore("The reference tools run on Linux.");
    if (Which(oracle.Tool) == null)
      Assert.Ignore($"{oracle.Tool} is not installed; nothing to compare against.");

    var ops = FormatRegistry.GetArchiveOps(oracle.FormatId);
    if (ops == null) Assert.Ignore($"{oracle.FormatId} has no reader registered.");

    var work = Path.Combine(Path.GetTempPath(), "cwb_theirs_" + Guid.NewGuid().ToString("N")[..8]);
    var source = Path.Combine(work, "src");
    Directory.CreateDirectory(source);

    try {
      var expected = ProbeSet();
      foreach (var (name, data) in expected) {
        var path = Path.Combine(source, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, data);
      }

      var image = Path.Combine(work, "theirs.img");
      // btrfs will not format an image it has to create itself.
      if (oracle.Tool == "mkfs.btrfs")
        using (var f = File.Create(image)) f.SetLength(320L * 1024 * 1024);

      var (exit, output) = Run(oracle.Tool, oracle.Arguments(source, image));
      if (exit != 0 || !File.Exists(image)) {
        Assert.Ignore($"{oracle.Tool} would not build a probe image here (exit {exit}): "
          + output.Split('\n').FirstOrDefault());
        return;
      }

      var got = ReadAll(ops!, image);
      Assert.That(got, Is.Not.Empty,
        $"{oracle.FormatId}: nothing read back from a volume {oracle.Tool} wrote");

      foreach (var (name, want) in expected) {
        var leaf = Path.GetFileName(name);
        Assert.That(got.ContainsKey(leaf), Is.True,
          $"{oracle.FormatId}: '{leaf}' is on the volume {oracle.Tool} wrote and not in what we read");

        var mine = got[leaf];
        Assert.That(mine.Length, Is.EqualTo(want.Length),
          $"{oracle.FormatId}: '{leaf}' came back {mine.Length} of {want.Length} bytes");
        Assert.That(mine, Is.EqualTo(want).AsCollection,
          $"{oracle.FormatId}: '{leaf}' does not match what {oracle.Tool} was given");
      }
    } finally {
      try { Directory.Delete(work, true); } catch { /* best effort */ }
    }
  }

  private static Dictionary<string, byte[]> ReadAll(IArchiveFormatOperations ops, string image) {
    var outDir = Path.Combine(Path.GetTempPath(), "cwb_theirs_out_" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(outDir);
    var result = new Dictionary<string, byte[]>(StringComparer.Ordinal);
    try {
      using (var stream = File.OpenRead(image))
        ops.Extract(stream, outDir, null, null);

      foreach (var file in Directory.EnumerateFiles(outDir, "*", SearchOption.AllDirectories))
        result[Path.GetFileName(file)] = File.ReadAllBytes(file);
    } catch (Exception ex) {
      Assert.Fail($"reading a volume the reference tool wrote threw {ex.GetType().Name}: {ex.Message}");
    } finally {
      try { Directory.Delete(outDir, true); } catch { /* best effort */ }
    }
    return result;
  }

  private static string? Which(string tool) {
    foreach (var dir in new[] { "/usr/sbin", "/sbin", "/usr/bin", "/bin", "/usr/local/sbin", "/usr/local/bin" }) {
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
