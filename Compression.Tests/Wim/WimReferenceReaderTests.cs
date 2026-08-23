#pragma warning disable CS1591
using System.Diagnostics;
using FileFormat.Wim;

namespace Compression.Tests.Wim;

/// <summary>
/// A WIM we wrote has to be one the tools that own the format will open, read
/// and check — not merely one we can read back ourselves.
/// </summary>
/// <remarks>
/// <para>Two readers are asked, because they disagree about what they will put
/// up with. <c>wimlib-imagex verify</c> walks the image metadata, decompresses
/// every resource and checks each one against the SHA-1 in the lookup table, so
/// it catches a wrong hash, a wrong tree and a wrong codec alike. 7-Zip is
/// stricter about the shape of a compressed chunk in one respect neither the
/// format documents nor the other reader minds — it takes one symbol more than
/// the chunk needs — and it is the reader most people actually have.</para>
///
/// <para>Both are needed. Every one of these cases passed one of them while
/// failing the other at some point during the writing of this: a container both
/// accept is the only evidence worth having.</para>
/// </remarks>
[TestFixture]
public class WimReferenceReaderTests {

  private static readonly uint[] CompressionsOthersRead = [
    WimConstants.CompressionNone,
    WimConstants.CompressionXpress,
    WimConstants.CompressionXpressHuffman,
    WimConstants.CompressionLzx,
  ];

  /// <summary>
  /// Files chosen for how they compress: one that barely does, one that is all
  /// one byte, one that is a repeated phrase, one that cannot be compressed at
  /// all, an empty one, a copy of another, and one large enough to be cut into
  /// several chunks.
  /// </summary>
  private static List<(string Name, byte[] Data)> ProbeSet() {
    var repeated = new byte[20_000];
    Array.Fill(repeated, (byte)0x11);

    var random = new byte[5_000];
    new Random(17).NextBytes(random);

    var phrase = "The quick brown fox jumps over the lazy dog. ";
    var text = System.Text.Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat(phrase, 3_000)));

    var large = new byte[300_000];
    for (var i = 0; i < large.Length; ++i) large[i] = (byte)(i * 37 + 11);

    return [
      ("A.TXT", "a short line of text\n"u8.ToArray()),
      ("B.BIN", random),
      ("C.BIN", repeated),
      ("EMPTY.TXT", []),
      ("nested/D.TXT", "a file a directory down\n"u8.ToArray()),
      ("nested/COPY.BIN", (byte[])repeated.Clone()),
      ("TEXT.TXT", text),
      ("deep/down/BIG.BIN", large),
    ];
  }

  [TestCaseSource(nameof(CompressionsOthersRead)), Category("Interop")]
  public void AWimWeWrote_PassesTheToolsThatOwnTheFormat(uint compression) {
    if (!OperatingSystem.IsLinux()) Assert.Ignore("The reference readers run on Linux.");

    var wimlib = Which("wimlib-imagex");
    var sevenZip = Which("7z");
    if (wimlib == null && sevenZip == null)
      Assert.Ignore("neither wimlib-imagex nor 7z is installed.");

    var files = ProbeSet();
    var path = Path.Combine(Path.GetTempPath(), "cwb_wim_" + Guid.NewGuid().ToString("N")[..8] + ".wim");
    using (var output = File.Create(path))
      new WimWriter(output, compression).Write(files);

    try {
      if (wimlib != null) {
        var (exit, text) = Run(wimlib, $"verify \"{path}\"");
        Assert.That(exit, Is.Zero,
          $"wimlib would not verify a WIM we wrote with compression {compression}: "
          + FirstComplaint(text));
      }

      if (sevenZip != null) {
        var (exit, text) = Run(sevenZip, $"t \"{path}\"");
        Assert.That(exit, Is.Zero,
          $"7-Zip would not test a WIM we wrote with compression {compression}: "
          + FirstComplaint(text));
      }
    } finally {
      try { File.Delete(path); } catch { /* the scratch image is already gone */ }
    }
  }

  [Test, Category("Interop")]
  public void TheirWim_ReadsBackHereExactly() {
    if (!OperatingSystem.IsLinux()) Assert.Ignore("The reference writer runs on Linux.");
    var capture = Which("wimcapture");
    if (capture == null) Assert.Ignore("wimcapture is not installed.");

    var work = Path.Combine(Path.GetTempPath(), "cwb_wimref_" + Guid.NewGuid().ToString("N")[..8]);
    var from = Path.Combine(work, "src");
    Directory.CreateDirectory(Path.Combine(from, "nested"));

    var files = ProbeSet().Where(f => f.Data.Length > 0).ToList();
    foreach (var (name, data) in files) {
      var full = Path.Combine(from, name.Replace('/', Path.DirectorySeparatorChar));
      Directory.CreateDirectory(Path.GetDirectoryName(full)!);
      File.WriteAllBytes(full, data);
    }

    var archive = Path.Combine(work, "theirs.wim");
    try {
      var (exit, text) = Run(capture, $"\"{from}\" \"{archive}\" --compress=XPRESS");
      if (exit != 0) Assert.Ignore($"wimcapture would not build the reference image: {FirstComplaint(text)}");

      using var stream = File.OpenRead(archive);
      using var reader = new WimReader(stream);
      var seen = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
      foreach (var file in reader.GetNamedFiles())
        seen[file.FileName] = file.ResourceIndex < 0 ? [] : reader.ReadResource(file.ResourceIndex);

      foreach (var (name, data) in files) {
        var leaf = name[(name.LastIndexOf('/') + 1)..];
        var found = seen.FirstOrDefault(kv => kv.Key.EndsWith(leaf, StringComparison.OrdinalIgnoreCase));
        Assert.That(found.Key, Is.Not.Null, $"'{leaf}' is missing from the reference image");
        Assert.That(found.Value, Is.EqualTo(data).AsCollection,
          $"'{leaf}' came back from the reference image with different bytes");
      }
    } finally {
      try { Directory.Delete(work, true); } catch { /* best effort */ }
    }
  }

  private static string FirstComplaint(string output) {
    foreach (var line in output.Split('\n')) {
      var trimmed = line.Trim();
      if (trimmed.Length == 0) continue;
      if (trimmed.StartsWith("Verifying file data", StringComparison.Ordinal)) continue;
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
