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

  // ── In-place editing ───────────────────────────────────────────────────

  private static readonly IsoFormatDescriptor Iso = new();

  private static byte[] Pattern(int size, int seed) {
    var d = new byte[size];
    new Random(seed).NextBytes(d);
    return d;
  }

  /// <summary>DATA.BIN's primary record: first sector, unit and gap sizes, and the sectors from its first unit to its last.</summary>
  private static (int Lba, int Unit, int Gap, int Span) Layout(byte[] image) {
    var rootLba = BitConverter.ToInt32(image, 16 * 2048 + 158);
    for (var pos = rootLba * 2048; image[pos] != 0; pos += image[pos]) {
      if (!System.Text.Encoding.ASCII.GetString(image, pos + 33, image[pos + 32]).StartsWith("DATA.BIN", StringComparison.Ordinal)) continue;
      var lba = BitConverter.ToInt32(image, pos + 2);
      var sectors = (BitConverter.ToInt32(image, pos + 10) + 2047) / 2048;
      int unit = image[pos + 26], gap = image[pos + 27];
      var units = (sectors + unit - 1) / unit;
      return (lba, unit, gap, sectors + (units - 1) * gap);
    }
    throw new AssertionException("DATA.BIN record not found");
  }

  /// <summary>The gap sectors between DATA.BIN's file units.</summary>
  private static IEnumerable<int> GapSectors((int Lba, int Unit, int Gap, int Span) layout) {
    for (var at = layout.Lba + layout.Unit; at < layout.Lba + layout.Span; at += layout.Unit + layout.Gap)
      for (var g = 0; g < layout.Gap; ++g) yield return at + g;
  }

  private static byte[] Sector(byte[] image, int lba) => image.AsSpan(lba * 2048, 2048).ToArray();

  private static string Sha(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

  [TestCase("interleave-1-1")]
  [TestCase("interleave-2-3")]
  public void GivenAnImageWithAnInterleavedFile_WhenFilesAreAdded_ThenItsUnitsAndGapsStayAndTheNewFilesAreContiguous(string name) {
    var row = Manifest.Value.Single(r => r.Image == name);
    using var image = Image(name);
    var before = image.ToArray();
    var layout = Layout(before);
    var oldEnd = (int)(before.Length / 2048);
    var added = Pattern(9000, 7);

    Iso.Add(image, [ArchiveInputInfo.InMemory("NEW.BIN", added), ArchiveInputInfo.InMemory("SUB/INNER.TXT", "inner\n"u8.ToArray())]);

    var after = image.ToArray();
    var at = after.AsSpan(oldEnd * 2048).IndexOf(added.AsSpan(0, 64));
    Assert.Multiple(() => {
      Assert.That(after.AsSpan(layout.Lba * 2048, layout.Span * 2048).ToArray(),
        Is.EqualTo(before.AsSpan(layout.Lba * 2048, layout.Span * 2048).ToArray()), "units and gaps stay byte for byte");
      Assert.That(Layout(after), Is.EqualTo(layout), "DATA.BIN's record keeps its interleave");
      Assert.That(Sha(Iso.ExtractEntryToMemory(image, "DATA.BIN", null)), Is.EqualTo(row.WindowsSha));
      Assert.That(Iso.ExtractEntryToMemory(image, "NEW.BIN", null), Is.EqualTo(added));
      Assert.That(Iso.ExtractEntryToMemory(image, "SUB/INNER.TXT", null), Is.EqualTo("inner\n"u8.ToArray()));
      Assert.That(at, Is.GreaterThanOrEqualTo(0), "new data after the old volume space");
      Assert.That(after.AsSpan(oldEnd * 2048 + at, added.Length).ToArray(), Is.EqualTo(added), "in one contiguous run");
    });
    image.Position = 0;
    using var reader = new IsoReader(image, leaveOpen: true, useJoliet: false);
    Assert.That(reader.Entries.Single(e => e.Name == "NEW.BIN").IsInterleaved, Is.False);
  }

  [TestCase("interleave-1-1")]
  [TestCase("interleave-2-3")]
  public void GivenAnImageWithAnInterleavedFile_WhenItIsRemoved_ThenItsUnitsAreZeroedAndItsGapsKept(string name) {
    using var image = Image(name);
    var before = image.ToArray();
    var layout = Layout(before);
    var gaps = GapSectors(layout).ToList();
    Assert.That(gaps, Is.Not.Empty);

    Iso.Remove(image, ["DATA.BIN"]);

    var after = image.ToArray();
    var units = Enumerable.Range(layout.Lba, layout.Span).Except(gaps).ToList();
    Assert.Multiple(() => {
      Assert.That(Iso.List(image, null).Where(e => !e.IsDirectory), Is.Empty);
      Assert.That(units.All(lba => !Sector(after, lba).AsSpan().ContainsAnyExcept((byte)0)), Is.True, "every file unit is zeroed");
      Assert.That(gaps.All(lba => Sector(after, lba).SequenceEqual(Sector(before, lba))), Is.True, "the interleave gaps are not the file's");
      Assert.That(gaps.All(lba => Sector(after, lba).All(b => b == 0xA5)), Is.True);
    });
  }

  [Test]
  public void GivenAFileWhoseDataLiesInAnInterleaveGap_WhenTheInterleavedFileIsRemoved_ThenThatFileStaysReadable() {
    // A second file recorded inside DATA.BIN's first gap: the gap is another file's, as in a
    // disc that interleaves two streams.
    using var image = Image("interleave-2-3");
    var layout = Layout(image.ToArray());
    Iso.Add(image, [ArchiveInputInfo.InMemory("OTHER.BIN", new byte[2048])]);
    var bytes = image.ToArray();
    var rootLba = BitConverter.ToInt32(bytes, 16 * 2048 + 158);
    for (var pos = rootLba * 2048; bytes[pos] != 0; pos += bytes[pos]) {
      if (!System.Text.Encoding.ASCII.GetString(bytes, pos + 33, bytes[pos + 32]).StartsWith("OTHER.BIN", StringComparison.Ordinal)) continue;
      var gapLba = layout.Lba + layout.Unit;
      BitConverter.GetBytes(gapLba).CopyTo(bytes, pos + 2);
      BitConverter.GetBytes(System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(gapLba)).CopyTo(bytes, pos + 6);
    }
    using var patched = new MemoryStream();
    patched.Write(bytes);

    Iso.Remove(patched, ["DATA.BIN"]);

    Assert.That(Iso.ExtractEntryToMemory(patched, "OTHER.BIN", null), Is.EqualTo(Enumerable.Repeat((byte)0xA5, 2048).ToArray()));
  }

  [TestCase("interleave-1-1")]
  [TestCase("interleave-2-3")]
  public void GivenAnImageWithAnInterleavedFile_WhenItIsReplaced_ThenTheNewContentIsContiguousAndTheGapsKept(string name) {
    using var image = Image(name);
    var before = image.ToArray();
    var gaps = GapSectors(Layout(before)).ToList();
    var replacement = Pattern(12000, 9);

    Iso.Add(image, [ArchiveInputInfo.InMemory("DATA.BIN", replacement)]);

    var after = image.ToArray();
    image.Position = 0;
    using var reader = new IsoReader(image, leaveOpen: true, useJoliet: false);
    var entry = reader.Entries.Single(e => e.Name == "DATA.BIN");
    Assert.Multiple(() => {
      Assert.That(entry.IsInterleaved, Is.False);
      Assert.That(reader.Extract(entry), Is.EqualTo(replacement));
      Assert.That(after.AsSpan().IndexOf(replacement), Is.GreaterThanOrEqualTo(before.Length), "written contiguously after the old volume space");
      Assert.That(gaps.All(lba => Sector(after, lba).SequenceEqual(Sector(before, lba))), Is.True);
    });
  }

  [Test]
  public void GivenAnImageWithAnInterleavedFileAndJoliet_WhenAFileIsRemoved_ThenBothTreesLoseIt() {
    using var image = Image("interleave-1-1");
    Iso.Add(image, [ArchiveInputInfo.InMemory("Second File.txt", "second"u8.ToArray())]);
    Iso.Remove(image, ["DATA.BIN"]);
    foreach (var joliet in new[] { true, false }) {
      image.Position = 0;
      using var reader = new IsoReader(image, leaveOpen: true, useJoliet: joliet);
      Assert.That(reader.Entries.Where(e => !e.IsDirectory).Select(e => e.Name).Single(), Does.StartWith("SECOND").IgnoreCase.Or.EqualTo("Second File.txt"));
    }
  }

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
