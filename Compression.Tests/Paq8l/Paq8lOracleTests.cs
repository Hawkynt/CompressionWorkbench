using System.Buffers.Binary;
using FileFormat.Paq8l;

namespace Compression.Tests.Paq8l;

/// <summary>
/// paq8l itself as the oracle on inputs generated here: for each set, paq8l's archive must equal
/// ours byte for byte, and each side must decode the other's. Runs where a paq8l binary is
/// available in WSL (<c>CWB_PAQ8L</c>, default <c>~/paq8l/paq8l-linux</c>, built from paq8l.cpp by
/// <c>ReferenceVectors/generate.sh</c>'s recipe).
/// </summary>
[TestFixture]
[Category("ExternalInterop")]
public sealed class Paq8lOracleTests {

  private static string Tool() {
    var tool = Environment.GetEnvironmentVariable("CWB_PAQ8L") ?? "$HOME/paq8l/paq8l-linux";
    if (!FsInteropToolbox.RunWsl($"test -x {tool} && echo yes").StdOut.Contains("yes"))
      Assert.Ignore($"paq8l is not available in WSL at {tool} (set CWB_PAQ8L).");
    return tool;
  }

  private static byte[] Text(Random r, int n) {
    string[] words = ["alpha", "beta", "gamma", "delta", "model", "mixer", "state", "context", "archive", "predict", "the", "a", "of", "in"];
    var sb = new System.Text.StringBuilder();
    while (sb.Length < n) {
      sb.Append(words[r.Next(words.Length)]);
      sb.Append(r.Next(12) == 0 ? ".\n" : r.Next(9) == 0 ? ", " : " ");
    }
    return System.Text.Encoding.ASCII.GetBytes(sb.ToString(0, n));
  }

  /// <summary>Calls and jumps packed densely, with overlapping E8 .. 00 patterns the transform test rejects.</summary>
  private static byte[] DenseCalls(Random r, int n) {
    var b = new byte[n];
    for (var i = 0; i + 5 <= n;) {
      var k = r.Next(10);
      if (k < 4) {
        b[i] = 0xE8;
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(i + 1), 0x3000 - i - 5 + (r.Next(3) << 4));
        i += 5;
      } else if (k < 6) {
        b[i] = 0xE9;
        b[i + 1] = 0xE8;
        b[i + 2] = 0;
        b[i + 3] = 0;
        b[i + 4] = 0;
        i += 5;
      } else
        b[i++] = (byte)r.Next(256);
    }
    return b;
  }

  /// <summary>An uncompressed RGB TIFF, Intel byte order, directory at offset 8.</summary>
  private static byte[] Tiff(Random r, int width, int height) {
    var pixels = width * height * 3;
    var ms = new MemoryStream();
    var w = new BinaryWriter(ms);
    w.Write("II*\0"u8);
    w.Write(8);
    ushort[] tags = [256, 257, 258, 259, 262, 273, 277, 278, 279];
    w.Write((ushort)tags.Length);
    var dataOffset = 8 + 2 + tags.Length * 12 + 4;
    void Entry(ushort tag, ushort type, int value) { w.Write(tag); w.Write(type); w.Write(1); w.Write(value); }
    Entry(256, 4, width);
    Entry(257, 4, height);
    Entry(258, 3, 8);
    Entry(259, 3, 1);
    Entry(262, 3, 2);
    Entry(273, 4, dataOffset);
    Entry(277, 3, 3);
    Entry(278, 4, height);
    Entry(279, 4, pixels);
    w.Write(0);
    for (var y = 0; y < height; ++y)
      for (var x = 0; x < width; ++x) {
        w.Write((byte)(x * 3 + r.Next(4)));
        w.Write((byte)(y * 5 + r.Next(4)));
        w.Write((byte)(x + y));
      }
    return ms.ToArray();
  }

  public static IEnumerable<TestCaseData> Sets() {
    yield return new TestCaseData(1, 11).SetName("Level 1 table pressure");
    yield return new TestCaseData(4, 12).SetName("Level 4 transforms and images");
  }

  [TestCaseSource(nameof(Sets))]
  public void GivenGeneratedInputs_WhenPaq8lAndWeCompress_ThenTheArchivesAreIdenticalAndCrossDecode(int level, int seed) {
    var tool = Tool();
    var r = new Random(seed);
    var noise = new byte[40000];
    r.NextBytes(noise);
    var files = new List<(string Name, byte[] Data)> {
      ("text.txt", Text(r, level == 1 ? 260000 : 60000)),
      ("calls.bin", DenseCalls(r, 70000)),
      ("image.tif", Tiff(r, 48, 40)),
      ("noise.bin", noise),
      ("sub/text2.txt", Text(r, 9000)),
    };
    var dir = Path.Combine(Path.GetTempPath(), "cwb-paq8l-" + Guid.NewGuid().ToString("N"));
    try {
      foreach (var (name, data) in files) {
        var path = Path.Combine(dir, "in", name.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, data);
      }
      // paq8l stores names relative to the arguments; "sub" expands to sub/text2.txt
      var inDir = FsInteropToolbox.WinToWsl(Path.Combine(dir, "in"));
      var run = FsInteropToolbox.RunWsl($"cd {inDir} && {tool} -{level} text.txt calls.bin image.tif noise.bin sub && echo done");
      TestContext.Out.WriteLine(run.StdOut);
      Assert.That(run.StdOut, Does.Contain("done"), run.StdErr);
      var theirs = File.ReadAllBytes(Path.Combine(dir, "in", "text.txt.paq8l"));

      using var ms = new MemoryStream();
      Paq8lArchive.Write(ms, files, level);
      var ours = ms.ToArray();
      Assert.That(ours.Length, Is.EqualTo(theirs.Length), "archive size");
      Assert.That(ours, Is.EqualTo(theirs), "archive bytes");

      var decoded = Paq8lArchive.Read(new MemoryStream(theirs));
      Assert.That(decoded.Select(d => d.Entry.Name), Is.EqualTo(files.Select(f => f.Name)));
      for (var i = 0; i < files.Count; ++i) Assert.That(decoded[i].Data, Is.EqualTo(files[i].Data), files[i].Name);

      // paq8l extracts our archive (written to a fresh directory, so it extracts rather than compares)
      var outDir = Path.Combine(dir, "out");
      Directory.CreateDirectory(outDir);
      File.WriteAllBytes(Path.Combine(outDir, "ours.paq8l"), ours);
      var extract = FsInteropToolbox.RunWsl($"cd {FsInteropToolbox.WinToWsl(outDir)} && {tool} -d ours.paq8l . > /dev/null && echo done");
      Assert.That(extract.StdOut, Does.Contain("done"), extract.StdErr);
      foreach (var (name, data) in files)
        Assert.That(File.ReadAllBytes(Path.Combine(outDir, name.Replace('/', Path.DirectorySeparatorChar))), Is.EqualTo(data), name);
    } finally {
      try { Directory.Delete(dir, true); } catch { /* best effort */ }
    }
  }
}
