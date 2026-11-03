using System.IO.Compression;
using System.Security.Cryptography;
using Compression.Registry;
using FileSystem.Iso;

namespace Compression.Tests.Iso;

/// <summary>
/// Interleaved file sections (ECMA-119 6.4.3): images whose DATA.BIN is recorded in file units
/// separated by interleave gaps of filler, with the original contiguous copy overwritten, read back
/// by Windows' CDFS driver (ReferenceVectors/mkinterleaved.py, manifest-interleave.txt).
/// </summary>
[TestFixture]
public sealed class IsoInterleaveTests {
  private sealed record Row(string Image, int Unit, int Gap, string SourceSha, string WindowsSha, long Size);

  private static readonly Lazy<Row[]> Manifest = new(() => {
    using var resource = typeof(IsoInterleaveTests).Assembly.GetManifestResourceStream("IsoVectors.manifest-interleave.txt")!;
    using var text = new StreamReader(resource);
    return text.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries)
      .Where(l => !l.StartsWith('#'))
      .Select(l => l.TrimEnd('\r').Split(' '))
      .Select(f => new Row(f[0], int.Parse(f[1]), int.Parse(f[2]), f[3], f[4], long.Parse(f[5])))
      .ToArray();
  });

  private static MemoryStream Image(string name) {
    using var resource = typeof(IsoInterleaveTests).Assembly.GetManifestResourceStream($"IsoVectors.{name}.iso.gz")
      ?? throw new FileNotFoundException($"embedded reference vector {name} is missing");
    using var gzip = new GZipStream(resource, CompressionMode.Decompress);
    var image = new MemoryStream();
    gzip.CopyTo(image);
    image.Position = 0;
    return image;
  }

  [TestCase("interleave-1-1", true)]
  [TestCase("interleave-1-1", false)]
  [TestCase("interleave-2-3", true)]
  [TestCase("interleave-2-3", false)]
  public void GivenAnInterleavedFile_WhenExtracted_ThenItMatchesWhatWindowsRead(string image, bool joliet) {
    var row = Manifest.Value.Single(r => r.Image == image);
    Assert.That(row.WindowsSha, Is.EqualTo(row.SourceSha), "the oracle itself disagreed with the source");
    using var reader = new IsoReader(Image(image), useJoliet: joliet);
    var entry = reader.Entries.Single(e => string.Equals(e.Name, "DATA.BIN", StringComparison.OrdinalIgnoreCase));
    Assert.That(entry.IsInterleaved, Is.True);
    Assert.That(entry.Size, Is.EqualTo(row.Size));
    Assert.That(Convert.ToHexStringLower(SHA256.HashData(reader.Extract(entry))), Is.EqualTo(row.WindowsSha));
  }

  [Test]
  public void GivenAnInterleavedFile_WhenTheDescriptorExtractsIt_ThenItMatchesWhatWindowsRead() {
    var row = Manifest.Value.Single(r => r.Image == "interleave-2-3");
    var data = new IsoFormatDescriptor().ExtractEntryToMemory(Image("interleave-2-3"), "DATA.BIN", null);
    Assert.That(Convert.ToHexStringLower(SHA256.HashData(data)), Is.EqualTo(row.WindowsSha));
  }

  [Test]
  public void GivenAnInterleavedFile_WhenItsExtentsAreMapped_ThenEachUnitIsItsOwnRun() {
    using var image = Image("interleave-2-3");
    var runs = new IsoFormatDescriptor().EnumerateExtents(image)
      .Where(e => e.FileName == "DATA.BIN" && e.Kind == DefragBlockKind.Used).OrderBy(e => e.Offset).ToArray();
    // 10940 bytes in 2-block units with 3-block gaps: 4096, 4096, 2748.
    Assert.That(runs.Select(r => r.Length), Is.EqualTo(new long[] { 4096, 4096, 2748 }));
    Assert.That(runs[1].Offset - runs[0].Offset, Is.EqualTo(5 * 2048));
    Assert.That(runs[2].Offset - runs[1].Offset, Is.EqualTo(5 * 2048));
  }

  private static void AssertRefusedAndUntouched(Action<MemoryStream> edit) {
    using var image = Image("interleave-1-1");
    var before = image.ToArray();
    Assert.Throws<NotSupportedException>(() => edit(image));
    Assert.That(image.ToArray(), Is.EqualTo(before));
  }

  [Test]
  public void GivenAnImageWithAnInterleavedFile_WhenWiped_ThenItIsRefusedAndUntouched()
    => AssertRefusedAndUntouched(image => new IsoFormatDescriptor().WipeUnusedSpace(image));

  [Test]
  public void GivenAnImageWithAnInterleavedFile_WhenDefragmented_ThenItIsRefusedAndUntouched()
    => AssertRefusedAndUntouched(image => new IsoFormatDescriptor().Defragment(image));

  [Test]
  public void GivenAnImageWithAnInterleavedFile_WhenAFileIsRemoved_ThenItIsRefusedAndUntouched()
    => AssertRefusedAndUntouched(image => new IsoFormatDescriptor().Remove(image, ["DATA.BIN"]));

  [Test]
  public void GivenAGapWithoutAFileUnit_WhenExtracted_ThenItIsRefused() {
    using var image = Image("interleave-1-1");
    var bytes = image.ToArray();
    PatchUnit(bytes, unit: 0);
    using var reader = new IsoReader(new MemoryStream(bytes), useJoliet: false);
    var entry = reader.Entries.Single(e => e.Name == "DATA.BIN");
    Assert.Throws<NotSupportedException>(() => reader.Extract(entry));
  }

  /// <summary>Sets byte 26 (File Unit Size) of DATA.BIN's primary-tree record.</summary>
  private static void PatchUnit(byte[] image, byte unit) {
    var rootLba = BitConverter.ToInt32(image, 16 * 2048 + 158);
    var pos = rootLba * 2048;
    while (image[pos] != 0) {
      var nameLen = image[pos + 32];
      if (System.Text.Encoding.ASCII.GetString(image, pos + 33, nameLen).StartsWith("DATA.BIN", StringComparison.Ordinal)) {
        image[pos + 26] = unit;
        return;
      }
      pos += image[pos];
    }
    Assert.Fail("DATA.BIN record not found");
  }
}
