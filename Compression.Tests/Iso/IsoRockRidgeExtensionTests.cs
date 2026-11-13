#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using FileSystem.Iso;

namespace Compression.Tests.Iso;

/// <summary>
/// The Rock Ridge extensions the writer records: symbolic links (SL), directories deeper than
/// ECMA-119's eight levels relocated to rr_moved (RE, CL, PL), and zisofs-compressed files (ZF)
/// — read back by our reader here; <see cref="IsoRockRidgeExtensionOracleTests"/> has the
/// kernel's and xorriso's word on the same images.
/// </summary>
[TestFixture]
[Category("RoundTrip")]
public sealed class IsoRockRidgeExtensionTests {
  private static IsoReader Read(byte[] image) => new(new MemoryStream(image));

  private static byte[] Text(int bytes) => Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("zisofs compresses this line well. ", bytes / 34 + 1)))[..bytes];

  [TestCase("/usr/bin/bash")]
  [TestCase("../lib/libc.so.6")]
  [TestCase("./here")]
  [TestCase("plain")]
  [TestCase("/")]
  [TestCase("a/../b/./c")]
  [Category("EquivalenceClass")]
  public void GivenASymbolicLink_WhenWrittenAndRead_ThenTheTargetComesBack(string target) {
    var w = new IsoWriter { EnableRockRidge = true };
    w.AddFile("bin/real", "x"u8.ToArray());
    w.AddSymlink("bin/link", target);

    var link = Read(w.Build()).Entries.Single(e => e.Name == "bin/link");
    Assert.Multiple(() => {
      Assert.That(link.IsSymlink, Is.True);
      Assert.That(link.LinkTarget, Is.EqualTo(target == "a/../b/./c" ? "a/../b/./c" : target));
    });
  }

  [Test, Category("Boundary")]
  public void GivenALinkTargetLongerThanOneEntry_WhenWritten_ThenItIsSplitAndStillReadsBack() {
    var target = "/" + string.Join('/', Enumerable.Range(0, 8).Select(i => new string((char)('a' + i), 120))) + "/" + new string('z', 300);
    var w = new IsoWriter { EnableRockRidge = true };
    w.AddSymlink("long-link", target);
    Assert.That(Read(w.Build()).Entries.Single().LinkTarget, Is.EqualTo(target));
  }

  [Test, Category("ErrorHandling")]
  public void GivenNoRockRidge_WhenALinkOrZisofsIsAsked_ThenItIsRefused() {
    var links = new IsoWriter { EnableRockRidge = false };
    links.AddSymlink("l", "t");
    Assert.Throws<NotSupportedException>(() => links.Build());
    var zf = new IsoWriter { EnableRockRidge = false, EnableZisofs = true };
    zf.AddFile("f", Text(100_000));
    Assert.Throws<NotSupportedException>(() => zf.Build());
  }

  [TestCase(8)]
  [TestCase(9)]
  [TestCase(20)]
  [Category("Boundary")]
  public void GivenADirectoryTreeThatDeep_WhenWrittenAndRead_ThenEveryPathIsWhereItBelongs(int levels) {
    var w = new IsoWriter { EnableRockRidge = true, EnableJoliet = true };
    var path = "";
    var expected = new List<string>();
    for (var i = 1; i <= levels; i++) {
      path += (i == 1 ? "" : "/") + $"level{i:D2}";
      w.AddFile($"{path}/file{i:D2}.txt", Encoding.ASCII.GetBytes($"at level {i}"));
      expected.Add(path);
      expected.Add($"{path}/file{i:D2}.txt");
    }
    var image = w.Build();
    var reader = Read(image);

    Assert.Multiple(() => {
      Assert.That(reader.Entries.Select(e => e.Name), Is.EquivalentTo(expected), "no rr_moved, nothing missing");
      var deepest = reader.Entries.Single(e => e.Name.EndsWith($"file{levels:D2}.txt", StringComparison.Ordinal));
      Assert.That(Encoding.ASCII.GetString(reader.Extract(deepest)), Is.EqualTo($"at level {levels}"));
      Assert.That(PrimaryPathTableDepth(image), Is.LessThanOrEqualTo(8), "ECMA-119 6.8.2.1: at most eight levels");
    });
  }

  [Test, Category("HappyPath")]
  public void GivenZisofs_WhenCompressibleFilesAreWritten_ThenTheyAreStoredSmallerAndReadBackWhole() {
    var text = Text(300_000);
    var zeros = new byte[100_000];
    var random = new byte[70_000];
    new Random(5).NextBytes(random);
    var w = new IsoWriter { EnableRockRidge = true, EnableZisofs = true };
    w.AddFile("docs/text.txt", text);
    w.AddFile("zeros.bin", zeros);
    w.AddFile("random.bin", random);
    var image = w.Build();
    var reader = Read(image);

    Assert.Multiple(() => {
      Assert.That(reader.Extract(reader.Entries.Single(e => e.Name == "docs/text.txt")), Is.EqualTo(text));
      Assert.That(reader.Extract(reader.Entries.Single(e => e.Name == "zeros.bin")), Is.EqualTo(zeros));
      Assert.That(reader.Extract(reader.Entries.Single(e => e.Name == "random.bin")), Is.EqualTo(random));
      Assert.That(reader.Entries.Single(e => e.Name == "docs/text.txt").StoredSize, Is.LessThan(text.Length / 10), "the text is stored compressed");      Assert.That(image.AsSpan().IndexOf(random.AsSpan(0, 64)), Is.GreaterThan(0), "incompressible data stays plain");
    });
  }

  [TestCase(1)]
  [TestCase(32768)]
  [TestCase(32769)]
  [TestCase(200_000)]
  [Category("Boundary")]
  public void GivenZisofsCompression_WhenDecodedByTheReader_ThenEveryBlockSizeRoundTrips(int size) {
    var data = Text(size);
    var packed = Zisofs.Compress(data)!;
    Assert.Multiple(() => {
      Assert.That(packed.AsSpan(0, 8).ToArray(), Is.EqualTo(new byte[] { 0x37, 0xE4, 0x53, 0x96, 0xC9, 0xDB, 0xD6, 0x07 }), "zisofs magic");
      Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(packed.AsSpan(8)), Is.EqualTo((uint)size));
      Assert.That(IsoRockRidge.DecodeZisofs(packed, new ZisofsInfo(16, 15, size), "t"), Is.EqualTo(data));
    });
  }

  [Test, Category("HappyPath")]
  public void GivenAllFeatures_WhenTheWriterStreams_ThenTheImageEqualsTheBufferedOne() {
    IsoWriter Make() {
      var w = new IsoWriter { EnableRockRidge = true, EnableZisofs = true };
      w.AddFile("a/b/c/d/e/f/g/h/i/j/deep.txt", Text(50_000));
      w.AddSymlink("a/link", "b/c");
      return w;
    }
    using var streamed = new MemoryStream();
    Make().BuildToStreaming(streamed);
    var reader = Read(streamed.ToArray());
    Assert.That(reader.Extract(reader.Entries.Single(e => e.Name == "a/b/c/d/e/f/g/h/i/j/deep.txt")), Is.EqualTo(Text(50_000)));
    Assert.That(reader.Entries.Single(e => e.Name == "a/link").LinkTarget, Is.EqualTo("b/c"));
  }

  /// <summary>The deepest level the primary L path table records (the root is level 1).</summary>
  private static int PrimaryPathTableDepth(byte[] image) {
    var pvd = 16 * 2048;
    var size = BinaryPrimitives.ReadInt32LittleEndian(image.AsSpan(pvd + 132));
    var at = BinaryPrimitives.ReadInt32LittleEndian(image.AsSpan(pvd + 140)) * 2048;
    var levels = new List<int>();
    for (var pos = at; pos < at + size;) {
      var len = image[pos];
      var parent = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(pos + 6));
      levels.Add(levels.Count == 0 ? 1 : levels[parent - 1] + 1);
      pos += 8 + len + (len & 1);
    }
    return levels.Max();
  }
}
