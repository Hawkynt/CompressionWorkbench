using System.Text;
using Compression.Registry;
using FileFormat.StuffIt;

namespace Compression.Tests.StuffIt;

/// <summary>
/// StuffIt method 13 (LZ+Huffman) writing, in the classic and StuffIt 5 containers. Our decoder is
/// checked on archives StuffIt itself wrote (StuffItCorpusTests); The Unarchiver's unar is the
/// oracle for what we write here.
/// </summary>
[TestFixture]
public class StuffIt13EncoderTests {

  private static byte[] Text(int length, int seed = 1) {
    var words = "the quick brown fox jumps over the lazy dog while seven wizards box nimbly and jovial frogs hum".Split(' ');
    var r = new Random(seed);
    var sb = new StringBuilder();
    while (sb.Length < length) sb.Append(words[r.Next(words.Length)]).Append(r.Next(9) == 0 ? ".\n" : " ");
    return Encoding.ASCII.GetBytes(sb.ToString(0, length));
  }

  private static byte[] Noise(int length, int seed = 2) {
    var b = new byte[length];
    new Random(seed).NextBytes(b);
    return b;
  }

  /// <summary>Equivalence classes: tiny, one symbol, runs past both length escapes, text, noise, distances across the whole window.</summary>
  public static IEnumerable<TestCaseData> Inputs() {
    yield return new TestCaseData(new byte[] { 42 }).SetName("OneByte");
    yield return new TestCaseData(new byte[] { 1, 2 }).SetName("TwoBytes");
    yield return new TestCaseData(Enumerable.Repeat((byte)'a', 3).ToArray()).SetName("ShortestMatchLength");
    yield return new TestCaseData(Enumerable.Repeat((byte)'a', 64 + 1).ToArray()).SetName("RunOf64");
    yield return new TestCaseData(Enumerable.Repeat((byte)0, 1089 + 1).ToArray()).SetName("RunPast10BitEscape");
    yield return new TestCaseData(Enumerable.Repeat((byte)7, 100_000).ToArray()).SetName("RunPast15BitEscape");
    yield return new TestCaseData(Enumerable.Range(0, 256).Select(i => (byte)i).ToArray()).SetName("EveryByteOnce");
    yield return new TestCaseData(Text(20_000)).SetName("Text20K");
    yield return new TestCaseData(Noise(5_000)).SetName("Noise5K");
    var far = Noise(70_000, 5);
    Text(2_000, 9).CopyTo(far, 0);
    Text(2_000, 9).CopyTo(far, 65_500); // a repeat 65,500 bytes back, near the window's edge
    yield return new TestCaseData(far).SetName("RepeatNearWindowEdge");
  }

  [TestCaseSource(nameof(Inputs)), Category("RoundTrip")]
  public void GivenInput_WhenEncodedAndDecoded_ThenBytesAreIdentical(byte[] data) {
    var packed = StuffIt13Encoder.Encode(data);
    Assert.That(StuffIt13Decoder.Decode(packed, data.Length), Is.EqualTo(data));
  }

  [Test, Category("HappyPath")]
  public void GivenText_WhenEncoded_ThenItShrinks() {
    var data = Text(50_000);
    Assert.That(StuffIt13Encoder.Encode(data).Length, Is.LessThan(data.Length / 2));
  }

  [Test, Category("Boundary")]
  public void GivenSkewedFrequencies_WhenLengthsAreBuilt_ThenNoneExceeds15AndKraftHolds() {
    // Fibonacci frequencies give the deepest possible Huffman tree (39 levels for 40 symbols).
    var freq = new int[321];
    long a = 1, b = 1;
    for (var i = 0; i < 40; ++i) {
      freq[i] = (int)a;
      (a, b) = (b, a + b);
    }
    var lengths = StuffIt13Encoder.CodeLengths(freq);
    Assert.That(lengths.Max(), Is.LessThanOrEqualTo(15));
    Assert.That(lengths.Where(l => l > 0).Sum(l => Math.Pow(2, -l)), Is.LessThanOrEqualTo(1.0));
  }

  [TestCase(false), TestCase(true), Category("RoundTrip")]
  public void GivenWriter_WhenArchiveIsReadBack_ThenForksMatchAndMethod13IsUsed(bool stuffIt5) {
    var data = Text(30_000);
    var resource = Text(5_000, 3);
    var archive = Write(stuffIt5, ("notes.txt", data, resource), ("noise.bin", Noise(3_000), null));
    using var r = new StuffItReader(new MemoryStream(archive));
    var notes = r.Entries.Single(e => e.FileName == "notes.txt");
    Assert.That(notes.DataMethod, Is.EqualTo(13));
    Assert.That(notes.ResourceMethod, Is.EqualTo(13));
    Assert.That(r.Extract(notes), Is.EqualTo(data));
    Assert.That(r.ExtractResourceFork(notes), Is.EqualTo(resource));
    var noise = r.Entries.Single(e => e.FileName == "noise.bin");
    Assert.That(noise.DataMethod, Is.EqualTo(0), "incompressible data is stored");
    Assert.That(r.Extract(noise), Is.EqualTo(Noise(3_000)));
  }

  [Test, Category("HappyPath")]
  public void GivenDescriptorWithStuffIt5Target_WhenCreating_ThenAStuffIt5ArchiveComesOut() {
    var d = new StuffItFormatDescriptor();
    using var ms = new MemoryStream();
    var options = new FormatCreateOptions { FormatSpecific = new() { [FormatOptionKeys.TargetCompatibility] = "StuffIt5" } };
    d.Create(ms, [new ArchiveInputInfo("a.txt", "a.txt", false, Text(2_000))], options);
    Assert.That(Encoding.ASCII.GetString(ms.ToArray(), 0, 11), Is.EqualTo("StuffIt (c)"));
  }

  private static byte[] Write(bool stuffIt5, params (string Name, byte[] Data, byte[]? Resource)[] files) {
    using var ms = new MemoryStream();
    if (stuffIt5) {
      using var w = new StuffIt5Writer(ms, leaveOpen: true);
      foreach (var f in files) w.AddFile(f.Name, f.Data, f.Resource, lastModified: new DateTime(2001, 2, 3, 4, 5, 6, DateTimeKind.Utc));
    } else {
      using var w = new StuffItWriter(ms, leaveOpen: true);
      foreach (var f in files) w.AddFileWithResourceFork(f.Name, f.Data, f.Resource ?? [], lastModified: new DateTime(2001, 2, 3, 4, 5, 6, DateTimeKind.Utc));
    }
    return ms.ToArray();
  }

  // ── The Unarchiver ───────────────────────────────────────────────────────

  [TestCase(false), TestCase(true), Category("ExternalInterop")]
  public void GivenOurArchive_WhenUnarExtractsIt_ThenEveryForkIsByteIdentical(bool stuffIt5) {
    if (OperatingSystem.IsWindows() && !FsInteropToolbox.WslAvailable) Assert.Ignore("WSL is not available.");
    if (!FsInteropToolbox.WslHasTool("unar")) Assert.Ignore("unar (The Unarchiver) is not installed.");
    var files = new (string Name, byte[] Data, byte[]? Resource)[] {
      ("readme.txt", Text(40_000), Text(3_000, 4)),
      ("far.bin", Inputs().Select(c => (byte[])c.Arguments[0]!).Last(), null),
      ("run.bin", Enumerable.Repeat((byte)7, 100_000).ToArray(), null),
      ("tiny.txt", "hi"u8.ToArray(), null),
    };
    var archive = Write(stuffIt5, files);
    using (var r = new StuffItReader(new MemoryStream(archive)))
      Assert.That(r.Entries.Count(e => e.DataMethod == 13), Is.GreaterThanOrEqualTo(3), "the test must exercise method 13");

    var tmp = Path.Combine(Path.GetTempPath(), $"cwb_sit13_{Guid.NewGuid():N}");
    try {
      Directory.CreateDirectory(tmp);
      var path = Path.Combine(tmp, stuffIt5 ? "ours5.sit" : "ours.sit");
      File.WriteAllBytes(path, archive);
      var outDir = Path.Combine(tmp, "out");
      var run = FsInteropToolbox.RunWsl($"unar -q -f -k visible -D -o '{FsInteropToolbox.WinToWsl(outDir)}' '{FsInteropToolbox.WinToWsl(path)}'");
      Assert.That(run.ExitCode, Is.EqualTo(0), run.StdErr);
      foreach (var f in files) {
        Assert.That(File.ReadAllBytes(Path.Combine(outDir, f.Name)), Is.EqualTo(f.Data), f.Name);
        if (f.Resource is null) continue;
        var fork = Directory.GetFiles(outDir, "*", SearchOption.AllDirectories).Where(p => Path.GetFileName(p).Contains(f.Name) && !Path.GetFileName(p).Equals(f.Name)).ToList();
        Assert.That(fork, Has.Count.EqualTo(1), $"unar wrote no single resource-fork file for {f.Name}: {string.Join(", ", Directory.GetFiles(outDir))}");
        var bytes = File.ReadAllBytes(fork[0]);
        Assert.That(bytes.AsSpan().IndexOf(f.Resource) >= 0, Is.True, $"{f.Name}: unar's resource fork does not contain ours");
      }
    } finally {
      try { Directory.Delete(tmp, true); } catch { /* best effort */ }
    }
  }
}
