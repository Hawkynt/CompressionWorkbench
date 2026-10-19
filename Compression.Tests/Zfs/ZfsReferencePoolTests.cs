using System.Buffers.Binary;
using FileSystem.Zfs;
using static Compression.Tests.Zfs.ZfsReferencePools;

namespace Compression.Tests.Zfs;

/// <summary>
/// Reads pools that OpenZFS 2.2.2 wrote and compares every file with the SHA-256 the
/// mounted pool gave (<c>ReferenceVectors/README.md</c>).
/// </summary>
[TestFixture]
public class ZfsReferencePoolTests {

  private static readonly string[] CompressionDatasets =
    ["c-off", "c-lzjb", "c-zle", "c-gzip-1", "c-gzip-9", "c-lz4", "c-zstd", "c-zstd-19", "c-zstd-fast"];

  [Test, Category("HappyPath"), Category("Oracle")]
  public void FeaturePool_EveryDatasetSnapshotAndFile_MatchesTheMountedPool() {
    // Given: one ashift=12 vdev with every compression, checksum, record size, dnode size
    // When
    using var reader = new ZfsReader(Open("cwbmain"));
    // Then
    Assert.That(reader.PoolName, Is.EqualTo("cwbmain"));
    Assert.That(reader.PoolVersion, Is.EqualTo(5000UL));
    Assert.That(reader.Warnings, Is.Empty);
    AssertMatchesManifest(reader, "cwbmain");
  }

  [Test, Category("Spec"), Category("Oracle")]
  public void FeaturePool_ListsEveryDatasetAndTheSnapshot() {
    using var reader = new ZfsReader(Open("cwbmain"));
    var names = reader.Datasets.Where(d => d.Refusal == null).Select(d => d.Name).ToHashSet();
    var expected = CompressionDatasets.Concat(["rs4k", "rs1m", "largedn", "copies2", "dedup", "zpl4", "nest", "nest/child",
        "k-fletcher2", "k-sha256", "k-sha512", "k-skein", "k-edonr", "k-blake3"])
      .Select(d => "cwbmain/" + d).Append("cwbmain").Append("cwbmain/c-lz4@before");
    Assert.That(names, Is.SupersetOf(expected));
    Assert.That(reader.Datasets.Where(d => d.Refusal != null), Is.Empty);
  }

  [Test, Category("Spec"), Category("Oracle")]
  public void FeaturePool_ExercisesEveryCompressionAndBlockShape() {
    using var reader = new ZfsReader(Open("cwbmain"));
    foreach (var e in reader.Entries.Where(e => !e.IsDirectory)) reader.Extract(e);
    var stats = reader.Statistics;
    Assert.Multiple(() => {
      Assert.That(stats.Compressions, Is.SupersetOf(new byte[] {
        ZfsConstants.CompressOff, ZfsConstants.CompressLzjb, ZfsConstants.CompressZle,
        ZfsConstants.CompressGzip1, ZfsConstants.CompressGzip9, ZfsConstants.CompressLz4, ZfsConstants.CompressZstd,
      }));
      Assert.That(stats.Checksums, Is.SupersetOf(new byte[] {
        ZfsConstants.ChecksumFletcher2, ZfsConstants.ChecksumFletcher4, ZfsConstants.ChecksumSha256,
        ZfsConstants.ChecksumSha512, ZfsConstants.ChecksumSkein, ZfsConstants.ChecksumEdonR, ZfsConstants.ChecksumBlake3,
      }));
      Assert.That(stats.EmbeddedBlocks, Is.GreaterThan(0), "tiny files are embedded in their block pointers");
      Assert.That(stats.Holes, Is.GreaterThan(0), "sparse.bin and far.bin have holes");
      // Salted checksums are read unverified, and the reader says so.
      Assert.That(reader.UnverifiedChecksums, Is.EquivalentTo(new[] { "blake3", "edonr", "skein" }));
    });
  }

  [TestCase("c-lzjb/text.txt", ZfsConstants.CompressLzjb)]
  [TestCase("c-zle/sparse.bin", ZfsConstants.CompressZle)]
  [Category("Oracle")]
  public void OurCompressor_ReproducesTheBlockOpenZfsWrote(string path, byte compression) {
    // Given: a record OpenZFS compressed, physical bytes as stored and logical bytes decoded
    using var reader = new ZfsReader(Open("cwbmain"));
    var entry = reader.Entries.Single(e => e.Name == path);
    var pool = entry.ObjectSet!.Pool;
    var bp = FirstLevel0Pointer(entry, b => b.Compression == compression);
    var physical = pool.ReadPhysical(bp);
    var logical = pool.ReadBlock(bp, cache: false);
    // When
    var ours = compression == ZfsConstants.CompressLzjb
      ? Compression.Core.Dictionary.Lzjb.LzjbCompressor.Compress(logical)
      : Compression.Core.Transforms.ZleEncoding.Encode(logical);
    // Then: byte for byte, the rest of the sector is zero padding
    Assert.That(physical.AsSpan(0, ours.Length).ToArray(), Is.EqualTo(ours));
    Assert.That(physical.AsSpan(ours.Length).ContainsAnyExcept((byte)0), Is.False);
  }

  private static ZfsBlockPointer FirstLevel0Pointer(ZfsEntry entry, Func<ZfsBlockPointer, bool> wanted) {
    var dnode = entry.Dnode!;
    var os = entry.ObjectSet!;
    var pointers = new List<ZfsBlockPointer>(dnode.BlockPointers);
    for (var level = dnode.Levels - 1; level > 0; --level) {
      var next = new List<ZfsBlockPointer>();
      foreach (var bp in pointers.Where(p => !p.IsHole)) {
        var indirect = os.Pool.ReadBlock(bp);
        for (var at = 0; at + 128 <= indirect.Length; at += 128)
          next.Add(ZfsBlockPointer.Read(indirect.AsSpan(at)));
      }
      pointers = next;
    }
    return pointers.First(p => !p.IsHole && !p.IsEmbedded && wanted(p));
  }

  [Test, Category("Spec"), Category("Oracle")]
  public void FeaturePool_SnapshotKeepsTheOldContents() {
    using var reader = new ZfsReader(Open("cwbmain"));
    var current = reader.Entries.Single(e => e.Name == "c-lz4/text.txt");
    var before = reader.Entries.Single(e => e.Name == "c-lz4/.zfs/snapshot/before/text.txt");
    Assert.That(before.Size, Is.LessThan(current.Size));
    Assert.That(reader.Entries.Any(e => e.Name == "c-lz4/tiny.txt"), Is.False);
    Assert.That(reader.Entries.Any(e => e.Name == "c-lz4/.zfs/snapshot/before/tiny.txt"), Is.True);
  }

  [Test, Category("Spec"), Category("Oracle")]
  public void FeaturePool_ThreeLevelIndirectTreeWithHoles() {
    using var reader = new ZfsReader(Open("cwbmain"));
    var far = reader.Entries.Single(e => e.Name == "rs4k/far.bin");
    Assert.That(far.Dnode!.Levels, Is.EqualTo(3));
    var data = reader.Extract(far);
    Assert.That(data.Length, Is.EqualTo(4096 * 4096 + 3));
    Assert.That(data.AsSpan(0, 4096 * 4096).ContainsAnyExcept((byte)0), Is.False);
    Assert.That(data[^3..], Is.EqualTo("far"u8.ToArray()));
  }

  [Test, Category("Spec"), Category("Oracle")]
  public void FeaturePool_HardLinkSharesTheObject() {
    using var reader = new ZfsReader(Open("cwbmain"));
    var text = reader.Entries.Single(e => e.Name == "text.txt");
    var link = reader.Entries.Single(e => e.Name == "hardlink.txt");
    Assert.That(link.ObjectId, Is.EqualTo(text.ObjectId));
  }

  [Test, Category("Spec"), Category("Oracle")]
  public void FeaturePool_FatZapDirectoryListsAllSixHundredNames() {
    using var reader = new ZfsReader(Open("cwbmain"));
    Assert.That(reader.Entries.Count(e => e.Name.StartsWith("many/", StringComparison.Ordinal)), Is.EqualTo(600));
  }

  [Test, Category("HappyPath"), Category("Oracle")]
  public void LegacyPool_Version28WithZpl3Znodes_MatchesTheMountedPool() {
    using var reader = new ZfsReader(Open("cwbv28"));
    Assert.That(reader.PoolVersion, Is.EqualTo(28UL));
    Assert.That(reader.Warnings, Is.Empty);
    AssertMatchesManifest(reader, "cwbv28");
    var zpl3 = reader.Entries.Single(e => e.Name == "zpl3/text.txt");
    Assert.That(zpl3.Dnode!.BonusType, Is.EqualTo(ZfsConstants.OtZnode), "version 3 stores znode_phys_t in the bonus");
  }

  [Test, Category("HappyPath"), Category("Oracle")]
  public void GangPool_ReassemblesGangBlocks() {
    using var reader = new ZfsReader(Open("cwbgang"));
    AssertMatchesManifest(reader, "cwbgang");
    Assert.That(reader.Statistics.GangBlocks, Is.GreaterThan(0), "metaslab_force_ganging made the data ganged");
  }

  [TestCase("cwbmirror-0,cwbmirror-1")]
  [TestCase("cwbmirror-0")]
  [TestCase("cwbmirror-1")]
  [Category("HappyPath"), Category("Oracle")]
  public void Mirror_AnySideAloneOrBoth_MatchesTheMountedPool(string list) {
    var devices = list.Split(',');
    using var reader = new ZfsReader(devices.Select(d => (Stream)Open(d)).ToArray());
    Assert.That(reader.Topology.Single(), Does.StartWith("mirror("));
    AssertMatchesManifest(reader, "cwbmirror");
  }

  [TestCase("cwbraidz1-0,cwbraidz1-1,cwbraidz1-2")]
  [TestCase("cwbraidz1-1,cwbraidz1-2")]
  [TestCase("cwbraidz1-0,cwbraidz1-2")]
  [TestCase("cwbraidz1-0,cwbraidz1-1")]
  [Category("HappyPath"), Category("Oracle")]
  public void RaidZ1_AllDevicesOrAnyOneMissing_MatchesTheMountedPool(string list) {
    var devices = list.Split(',');
    using var reader = new ZfsReader(devices.Select(d => (Stream)Open(d)).ToArray());
    Assert.That(reader.Topology.Single(), Does.StartWith("raidz1("));
    AssertMatchesManifest(reader, "cwbraidz1");
  }

  [Test, Category("HappyPath"), Category("Oracle")]
  public void RaidZ2_AllDevices_MatchesTheMountedPool() {
    using var reader = new ZfsReader(Enumerable.Range(0, 4).Select(i => (Stream)Open($"cwbraidz2-{i}")).ToArray());
    Assert.That(reader.Topology.Single(), Does.StartWith("raidz2("));
    AssertMatchesManifest(reader, "cwbraidz2");
  }

  [Test, Category("HappyPath"), Category("Oracle")]
  public void Stripe_BothDevices_MatchesTheMountedPool() {
    using var reader = new ZfsReader([Open("cwbstripe-0"), Open("cwbstripe-1")]);
    Assert.That(reader.Topology, Has.Count.EqualTo(2));
    AssertMatchesManifest(reader, "cwbstripe");
  }

  [TestCase("cwbraidz2-0,cwbraidz2-1,cwbraidz2-2")]
  [TestCase("cwbstripe-0")]
  [TestCase("cwbstripe-1")]
  [Category("ErrorHandling"), Category("Oracle")]
  public void MissingDevice_NeverYieldsWrongBytes(string list) {
    var devices = list.Split(',');
    // Given: a device the layout cannot do without is absent
    ZfsReader reader;
    try {
      reader = new ZfsReader(devices.Select(d => (Stream)Open(d)).ToArray());
    } catch (InvalidDataException) {
      Assert.Pass("the pool refused to open without the device");
      return;
    }
    // Then: whatever is listed and extracts must be exactly right
    using (reader) {
      var expected = Manifest(devices[0][..devices[0].LastIndexOf('-')])
        .Where(m => m.Kind == 'F').ToDictionary(m => m.Path, m => m.Sha256);
      foreach (var e in reader.Entries.Where(e => !e.IsDirectory && !e.IsSymlink)) {
        byte[] data;
        try {
          data = reader.Extract(e);
        } catch (Exception x) when (x is InvalidDataException or IOException or NotSupportedException) {
          continue;
        }
        Assert.That(Sha256Of(data), Is.EqualTo(expected[e.Name]), e.Name);
      }
    }
  }

  [Test, Category("Spec"), Category("Oracle")]
  public void EncryptedDataset_IsRefusedAndThePlainOneStillReads() {
    using var reader = new ZfsReader(Open("cwbenc"));
    var secret = reader.Datasets.Single(d => d.Name == "cwbenc/secret");
    Assert.That(secret.Refusal, Does.Contain("encryption"));
    Assert.That(reader.Entries.Any(e => e.Name.StartsWith("secret/", StringComparison.Ordinal)), Is.False);
    AssertMatchesManifest(reader, "cwbenc");
  }

  [Test, Category("ErrorHandling")]
  public void CorruptedDataBlock_FailsItsChecksumInsteadOfReturningGarbage() {
    // Given: one byte of c-off/text.txt's first record flipped (a single copy)
    using var image = Copy("cwbmain");
    using (var reader = new ZfsReader(image)) {
      var entry = reader.Entries.Single(e => e.Name == "c-off/text.txt");
      FlipFirstDataByte(image, entry);
    }
    // When / Then
    image.Position = 0;
    using var corrupted = new ZfsReader(image);
    var broken = corrupted.Entries.Single(e => e.Name == "c-off/text.txt");
    Assert.Throws<InvalidDataException>(() => corrupted.Extract(broken));
  }

  [Test, Category("ErrorHandling")]
  public void CorruptedFirstCopy_ReadsTheDittoCopy() {
    // Given: copies=2, the first copy of text.txt's first record damaged
    using var image = Copy("cwbmain");
    string expected;
    using (var reader = new ZfsReader(image)) {
      var entry = reader.Entries.Single(e => e.Name == "copies2/text.txt");
      expected = Sha256Of(reader.Extract(entry));
      FlipFirstDataByte(image, entry);
    }
    // When
    image.Position = 0;
    using var corrupted = new ZfsReader(image);
    var data = corrupted.Extract(corrupted.Entries.Single(e => e.Name == "copies2/text.txt"));
    // Then
    Assert.That(Sha256Of(data), Is.EqualTo(expected));
    Assert.That(corrupted.Statistics.CopiesRecovered, Is.GreaterThan(0));
  }

  [Test, Category("ErrorHandling")]
  public void BigEndianPool_IsRefused() {
    using var image = Copy("cwbenc");
    var bytes = image.GetBuffer();
    // Byte-swap the magic of every uberblock in L0.
    for (var at = 128 * 1024; at < 256 * 1024; at += 1024)
      if (BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(at)) == ZfsConstants.UberblockMagic)
        BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(at), ZfsConstants.UberblockMagic);
    Assert.Throws<NotSupportedException>(() => _ = new ZfsReader(image));
  }

  /// <summary>Flips the first byte of the first copy of the file's first data block.</summary>
  private static void FlipFirstDataByte(MemoryStream image, ZfsEntry entry) {
    var dnode = entry.Dnode!;
    var bp = dnode.BlockPointers[0];
    for (var level = dnode.Levels - 1; level > 0; --level) {
      var indirect = entry.ObjectSet!.Pool.ReadBlock(bp);
      bp = ZfsBlockPointer.Read(indirect);
    }
    Assert.That(bp.IsEmbedded || bp.IsHole, Is.False);
    var at = ZfsConstants.DataStart + bp.Dvas[0].Offset;
    image.GetBuffer()[at] ^= 0xFF;
  }
}
