using System.IO.Compression;
using System.Security.Cryptography;
using FileSystem.SquashFs;

namespace Compression.Tests.SquashFs;

/// <summary>
/// SquashFS 1.x, 2.x and 3.x images written by mksquashfs 1.3, 2.2, 3.0 and 3.4 (both byte
/// orders, with and without the check byte, compression and fragments), checked against what
/// unsquashfs 4.6.1 extracts from them (LegacyVectors/oracle.tsv; see README.md there).
/// </summary>
[TestFixture]
public class SquashFsLegacyReferenceTests {

  private static readonly string[] Images = [
    "v1.0-le.sqfs", "v1.0-be.sqfs", "v1.0-le-check-uncompressed.sqfs",
    "v2.0-le.sqfs", "v2.1-le.sqfs", "v2.1-be.sqfs",
    "v3.0-le.sqfs", "v3.1-le.sqfs", "v3.1-be.sqfs", "v3.1-le-check-nofrag.sqfs", "v3.1-le-uncompressed.sqfs",
    "v3.1-lzma-le.sqfs", "v3.1-lzma-be.sqfs",
  ];

  private sealed record Row(string Kind, long Size, int Mode, uint Uid, uint Gid, long MTime, string Tail);

  private static byte[] Load(string name) {
    using var resource = typeof(SquashFsLegacyReferenceTests).Assembly.GetManifestResourceStream("SquashFsLegacyVectors." + name + ".gz")
      ?? throw new InvalidOperationException($"Missing embedded fixture {name}.");
    using var gz = new GZipStream(resource, CompressionMode.Decompress); // BCL as the test-side oracle
    using var ms = new MemoryStream();
    gz.CopyTo(ms);
    return ms.ToArray();
  }

  private static Dictionary<string, Row> Oracle(string image) {
    using var resource = typeof(SquashFsLegacyReferenceTests).Assembly.GetManifestResourceStream("SquashFsLegacyVectors.oracle.tsv")!;
    using var reader = new StreamReader(resource);
    var result = new Dictionary<string, Row>(StringComparer.Ordinal);
    while (reader.ReadLine() is { } line) {
      if (line.StartsWith('#') || line.Length == 0)
        continue;
      var f = line.Split('\t');
      if (f[0] != image)
        continue;
      var inv = System.Globalization.CultureInfo.InvariantCulture;
      result[f[1]] = new Row(f[2], long.Parse(f[3], inv), Convert.ToInt32(f[4], 8), uint.Parse(f[5], inv), uint.Parse(f[6], inv),
        long.Parse(f[7], inv), f[8]);
    }
    return result;
  }

  [TestCaseSource(nameof(Images))]
  public void GivenLegacyImage_WhenRead_ThenEveryEntryMatchesUnsquashfs(string image) {
    var oracle = Oracle(image);
    Assert.That(oracle, Has.Count.EqualTo(314));
    using var reader = new SquashFsReader(new MemoryStream(Load(image)));

    Assert.That(reader.Entries.Select(e => e.FullPath), Is.EquivalentTo(oracle.Keys));
    Assert.Multiple(() => {
      foreach (var e in reader.Entries) {
        var row = oracle[e.FullPath];
        Assert.That(e.IsDirectory, Is.EqualTo(row.Kind == "d"), e.FullPath);
        Assert.That(e.IsSymlink, Is.EqualTo(row.Kind == "l"), e.FullPath);
        Assert.That(e.Permissions, Is.EqualTo(row.Mode), e.FullPath);
        Assert.That((e.Uid, e.Gid), Is.EqualTo((row.Uid, row.Gid)), e.FullPath);
        // 1.x and 2.x symlink inodes carry no time, and the sqlzma unsquashfs (3.3) never sets a
        // symlink's time; either way the oracle holds the time of the extraction.
        if (row.Kind != "l" || image.StartsWith("v3", StringComparison.Ordinal) && !image.Contains("lzma", StringComparison.Ordinal))
          Assert.That(new DateTimeOffset(e.ModifiedTime).ToUnixTimeSeconds(), Is.EqualTo(row.MTime), e.FullPath);
        if (row.Kind == "l")
          Assert.That(e.SymlinkTarget, Is.EqualTo(row.Tail), e.FullPath);
        if (row.Kind == "f") {
          Assert.That(e.Size, Is.EqualTo(row.Size), e.FullPath);
          Assert.That(Convert.ToHexStringLower(SHA256.HashData(reader.Extract(e))), Is.EqualTo(row.Tail), e.FullPath);
        }
      }
    });
  }

  [TestCase("v1.0-be.sqfs", "1.0")]
  [TestCase("v2.0-le.sqfs", "2.0")]
  [TestCase("v2.1-be.sqfs", "2.1")]
  [TestCase("v3.0-le.sqfs", "3.0")]
  [TestCase("v3.1-le.sqfs", "3.1")]
  public void GivenLegacyImage_WhenOpened_ThenItsVersionIsReported(string image, string version) {
    using var reader = new SquashFsReader(new MemoryStream(Load(image)));
    Assert.That(reader.LegacyVersion, Is.EqualTo(version));
  }

  [Test]
  public void GivenLegacyImage_WhenListedThroughTheDescriptor_ThenFilesAndFoldersAreThere() {
    var entries = new SquashFsFormatDescriptor().List(new MemoryStream(Load("v2.1-be.sqfs")), null);

    Assert.That(entries, Has.Count.EqualTo(314));
    Assert.That(entries.Single(e => e.Name == "docs/lorem.txt").OriginalSize, Is.EqualTo(85500));
    Assert.That(entries.Single(e => e.Name == "emptydir").IsDirectory, Is.True);
  }

  [Test]
  public void GivenLegacyImage_WhenExtractedThroughTheDescriptor_ThenTheBytesMatchUnsquashfs() {
    var oracle = Oracle("v3.1-be.sqfs");
    var dir = Path.Combine(Path.GetTempPath(), "cwb-sqfs-legacy-" + Guid.NewGuid().ToString("N"));
    try {
      new SquashFsFormatDescriptor().Extract(new MemoryStream(Load("v3.1-be.sqfs")), dir, null, ["docs/lorem.txt", "readme.txt"]);
      Assert.That(Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(dir, "docs", "lorem.txt")))),
        Is.EqualTo(oracle["docs/lorem.txt"].Tail));
      Assert.That(Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(dir, "readme.txt")))),
        Is.EqualTo(oracle["readme.txt"].Tail));
    } finally {
      if (Directory.Exists(dir))
        Directory.Delete(dir, recursive: true);
    }
  }

  [Test]
  public void GivenLegacyImage_WhenDefragmented_ThenItIsRefusedAndLeftUnchanged() {
    var original = Load("v3.1-le.sqfs");
    var image = new MemoryStream(original.ToArray());

    Assert.Throws<NotSupportedException>(() => new SquashFsFormatDescriptor().Defragment(image));
    Assert.That(image.ToArray(), Is.EqualTo(original));
  }

  [Test]
  public void GivenADamagedCompressedDataBlock_WhenTheFileIsExtracted_ThenItIsRefusedNotReturned() {
    var bytes = Load("v1.0-le.sqfs");
    using (var probe = new SquashFsReader(new MemoryStream(bytes))) {
      // 1.x has no fragments: lorem.txt's first block starts the data area right after the superblock.
      Assert.That(probe.Entries.Any(e => e.FullPath == "docs/lorem.txt"), Is.True);
    }
    var damaged = bytes.ToArray();
    // Corrupt the whole data area (everything between the superblock and the inode table) a
    // little: every compressed block now fails its Adler-32 or its Deflate decoding.
    var inodeTable = BitConverter.ToUInt32(bytes, 0x14);
    for (var i = 0x100; i < inodeTable; i += 97)
      damaged[i] ^= 0x5A;

    using var reader = new SquashFsReader(new MemoryStream(damaged));
    var lorem = reader.Entries.Single(e => e.FullPath == "docs/lorem.txt");
    Assert.Throws<InvalidDataException>(() => reader.Extract(lorem));
  }

  [TestCase("v3.1-lzma-le.sqfs", "shsq")]
  [TestCase("v3.1-lzma-be.sqfs", "qshs")]
  public void GivenAnSqlzmaImage_WhenRead_ThenItsLzmaBlocksDecode(string image, string magic) {
    var bytes = Load(image);
    Assert.That(System.Text.Encoding.ASCII.GetString(bytes, 0, 4), Is.EqualTo(magic));
    Assert.That(LzmaHeaders(bytes), Is.Not.Empty, "the image holds LZMA blocks, not only zlib ones");
    using var reader = new SquashFsReader(new MemoryStream(bytes));
    var lorem = reader.Entries.Single(e => e.FullPath == "docs/lorem.txt");
    Assert.That(reader.Extract(lorem), Has.Length.EqualTo(lorem.Size));
  }

  /// <summary>Offsets of the sqlzma LZMA block headers: 0x5D, a 64 KiB-or-smaller power-of-two dictionary, a length up to 128 KiB.</summary>
  private static List<int> LzmaHeaders(byte[] image) {
    var found = new List<int>();
    for (var i = 0x77; i + 13 <= image.Length; ++i)
      if (image[i] == 0x5D && BitConverter.ToUInt32(image, i + 1) is var dict && dict != 0 && (dict & (dict - 1)) == 0 && dict <= 1u << 24
          && BitConverter.ToUInt64(image, i + 5) is > 0 and <= 1u << 17)
        found.Add(i);
    return found;
  }

  [Test]
  public void GivenLzmaBlocksUnderThePlainMagic_WhenOpened_ThenTheyAreRefusedAsAnUnknownLzmaBuild() {
    var bytes = Load("v3.1-lzma-le.sqfs");
    "hsqs"u8.CopyTo(bytes);

    Assert.Throws<NotSupportedException>(() => _ = new SquashFsReader(new MemoryStream(bytes)));
  }

  [Test]
  public void GivenAnLzmaBlockDeclaringTooMuch_WhenRead_ThenItIsRefusedNotReturned() {
    var damaged = Load("v3.1-lzma-le.sqfs");
    var headers = LzmaHeaders(damaged);
    Assert.That(headers, Is.Not.Empty);
    foreach (var at in headers)
      damaged[at + 8] = 0x7F; // the declared length now exceeds any block

    Assert.That(() => {
      using var reader = new SquashFsReader(new MemoryStream(damaged));
      foreach (var e in reader.Entries.Where(static e => !e.IsDirectory && !e.IsSymlink))
        _ = reader.Extract(e);
    }, Throws.InstanceOf<InvalidDataException>());
  }

  [Test]
  public void GivenALegacyImageCutShort_WhenOpened_ThenInvalidData() {
    var bytes = Load("v2.1-le.sqfs");
    var cut = bytes.AsSpan(0, 8192).ToArray();

    Assert.Throws<InvalidDataException>(() => _ = new SquashFsReader(new MemoryStream(cut)));
  }
}
