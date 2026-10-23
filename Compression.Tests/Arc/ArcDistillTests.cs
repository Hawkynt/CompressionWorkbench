using Compression.Core.Dictionary.ArcDistill;
using FileFormat.Arc;

namespace Compression.Tests.Arc;

[TestFixture]
public class ArcDistillTests {

  private static byte[] Text(int n) {
    var s = "PAK distill packs a dictionary coder behind one Huffman tree. "u8;
    var d = new byte[n];
    for (var i = 0; i < n; ++i) d[i] = s[i % s.Length];
    return d;
  }

  private static byte[] Random(int n, int seed) {
    var d = new byte[n];
    new Random(seed).NextBytes(d);
    return d;
  }

  public static IEnumerable<TestCaseData> Payloads() {
    foreach (var n in new[] { 0, 1, 2, 3, 4, 60, 64, 200, 4035, 4036, 8192, 8193, 30000 }) {
      yield return new TestCaseData(Text(n)).SetName($"Text_{n}");
      yield return new TestCaseData(Random(n, n)).SetName($"Random_{n}");
    }
    yield return new TestCaseData(new byte[20000]).SetName("Zeros_20000");
  }

  [TestCaseSource(nameof(Payloads)), Category("RoundTrip")]
  public void BuildingBlock_RoundTrips(byte[] data)
    => Assert.That(ArcDistillBuildingBlock.Decode(ArcDistillBuildingBlock.Encode(data), int.MaxValue), Is.EqualTo(data));

  [Test, Category("HappyPath")]
  public void Encode_CompressesText() {
    var data = Text(40_000);
    Assert.That(ArcDistillBuildingBlock.Encode(data).Length, Is.LessThan(data.Length / 5));
  }

  [Test, Category("Boundary")]
  public void Decode_StopsAtTheDeclaredLength() {
    var data = Text(1000);
    Assert.That(ArcDistillBuildingBlock.Decode(ArcDistillBuildingBlock.Encode(data), 300), Is.EqualTo(data[..300]));
  }

  [Test, Category("Exception")]
  public void Decode_NodeCountOutOfRange_Throws() {
    Assert.Throws<InvalidDataException>(() => ArcDistillBuildingBlock.Decode([1, 0, 9, 0, 0], 10));      // 1 node
    Assert.Throws<InvalidDataException>(() => ArcDistillBuildingBlock.Decode([0x75, 0x02, 9, 0, 0], 10)); // 0x275 nodes
  }

  [Test, Category("Exception")]
  public void Decode_TruncatedStream_Throws() {
    var encoded = ArcDistillBuildingBlock.Encode(Text(2000));
    Assert.Throws<InvalidDataException>(() => ArcDistillBuildingBlock.Decode(encoded[..(encoded.Length / 2)], int.MaxValue));
  }

  [Test, Category("Spec")]
  public void Decode_TwoLeafTree_EmitsLiteralThenEnds() {
    // Nodes N = 2, width 9: root pair (2 + 'A', 2 + 256). Code "0" = 'A', "1" = end.
    // Bits LSB first: 16-bit N, 8-bit width, two 9-bit entries, then 0, 0, 1.
    var bits = new List<int>();
    void Put(int v, int n) { for (var i = 0; i < n; ++i) bits.Add((v >> i) & 1); }
    Put(2, 16);
    Put(9, 8);
    Put(2 + 'A', 9);
    Put(2 + 256, 9);
    Put(0, 1);
    Put(0, 1);
    Put(1, 1);
    var bytes = new byte[(bits.Count + 7) / 8];
    for (var i = 0; i < bits.Count; ++i) bytes[i >> 3] |= (byte)(bits[i] << (i & 7));
    Assert.That(ArcDistillBuildingBlock.Decode(bytes, int.MaxValue), Is.EqualTo("AA"u8.ToArray()));
  }

  [Test, Category("RoundTrip")]
  public void ArcArchive_DistilledEntry_RoundTrips() {
    var data = Text(12_345);
    using var ms = new MemoryStream();
    using (var writer = new ArcWriter(ms, leaveOpen: true)) {
      writer.AddEntry("DISTILL.TXT", data, ArcCompressionMethod.Distilled);
      writer.Finish();
    }
    Assert.That(ms.ToArray()[1], Is.EqualTo(11), "header carries method 11");
    ms.Position = 0;
    using var reader = new ArcReader(ms, leaveOpen: true);
    var entry = reader.GetNextEntry();
    Assert.That(entry, Is.Not.Null);
    Assert.That(reader.ReadEntryData(), Is.EqualTo(data));
  }

  [Test, Category("Exception")]
  public void ArcWriter_DistilledOutsideExtendedProfile_IsRefused() {
    using var ms = new MemoryStream();
    using var writer = new ArcWriter(ms, leaveOpen: true, compatibilityProfile: ArcCompatibilityProfile.PkArc);
    Assert.Throws<NotSupportedException>(() => writer.AddEntry("X.TXT", Text(4096), ArcCompressionMethod.Distilled));
  }
}

/// <summary>The Unarchiver (unar) reads PAK method 11; it must extract what we write.</summary>
[TestFixture]
[Category("ExternalInterop")]
public class ArcDistillUnarInteropTests {
  [Test]
  public void OurDistilledArchive_IsExtractedByUnar() {
    if (!FsInteropToolbox.WslAvailable || !FsInteropToolbox.WslHasTool("unar"))
      Assert.Ignore("unar not installed. Run: sudo apt install -y unar");
    var dir = Path.Combine(Path.GetTempPath(), $"cwb_distill_{Guid.NewGuid():N}");
    Directory.CreateDirectory(dir);
    try {
      var text = new byte[30_000];
      var phrase = "Distilled by CompressionWorkbench, read back by The Unarchiver. "u8;
      for (var i = 0; i < text.Length; ++i) text[i] = phrase[i % phrase.Length];
      var random = new byte[3_000];
      new Random(1).NextBytes(random);
      var archive = Path.Combine(dir, "ours.pak");
      using (var fs = File.Create(archive))
      using (var writer = new ArcWriter(fs)) {
        writer.AddEntry("TEXT.TXT", text, ArcCompressionMethod.Distilled);
        writer.AddEntry("RANDOM.BIN", random, ArcCompressionMethod.Distilled);
        writer.Finish();
      }
      var outDir = Path.Combine(dir, "x");
      var r = FsInteropToolbox.RunWsl($"unar -q -f -o {FsInteropToolbox.WinToWsl(outDir)} {FsInteropToolbox.WinToWsl(archive)}");
      Assert.That(r.ExitCode, Is.EqualTo(0), $"unar failed:\n{r.StdOut}\n{r.StdErr}");
      var textOut = FsInteropToolbox.FindFile(outDir, "TEXT.TXT");
      var randomOut = FsInteropToolbox.FindFile(outDir, "RANDOM.BIN");
      Assert.That(textOut, Is.Not.Null, r.StdOut);
      Assert.Multiple(() => {
        Assert.That(File.ReadAllBytes(textOut!), Is.EqualTo(text));
        Assert.That(File.ReadAllBytes(randomOut!), Is.EqualTo(random));
      });
    } finally {
      try { Directory.Delete(dir, true); } catch { /* best effort */ }
    }
  }
}
