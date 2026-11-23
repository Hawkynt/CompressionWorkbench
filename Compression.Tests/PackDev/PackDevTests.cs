using System.Buffers.Binary;
using FileFormat.PackDev;

namespace Compression.Tests.PackDev;

[TestFixture]
public class PackDevTests {
  // ── Builder: a stored "PKD\x13" image ────────────────────────────────────────

  private static byte[] Build(int blocks, int reserved, int trackBlocks, bool[]? free, Func<int, byte> fill, bool old = false, uint packer = 0) {
    using var ms = new MemoryStream();
    var header = new byte[old ? 32 : 52];
    header[0] = (byte)'P'; header[1] = (byte)'K'; header[2] = (byte)'D'; header[3] = (byte)(old ? 0x11 : 0x13);
    BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), (uint)blocks);
    BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(8), 512);
    BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(12), (uint)reserved);
    BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(16), (uint)(trackBlocks * 512));
    BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(24), packer);
    BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(header.Length - 2), (ushort)(free is null ? 0 : 1));
    ms.Write(header);
    if (free is not null) {
      var map = new byte[blocks >> 3];
      for (var i = 0; i < blocks - reserved; ++i)
        if (free[i + reserved]) {
          var at = (i >> 5) << 2;
          var w = BinaryPrimitives.ReadUInt32BigEndian(map.AsSpan(at)) | (1u << (i & 31));
          BinaryPrimitives.WriteUInt32BigEndian(map.AsSpan(at), w);
        }
      ms.Write(map);
    }
    var stored = 0;
    for (var b = 0; b < blocks; ++b) {
      if (free is null || !free[b]) {
        var sector = new byte[512];
        Array.Fill(sector, fill(b));
        ms.Write(sector);
        ++stored;
      }
      if (b % trackBlocks == trackBlocks - 1) {
        ms.Write(new byte[16 * stored]);
        stored = 0;
      }
    }
    return ms.ToArray();
  }

  [Test, Category("HappyPath")]
  public void AllBlocksStored_RebuildInOrder() {
    var data = Build(64, 0, 8, null, b => (byte)(b + 1));
    var r = new PackDevReader(new MemoryStream(data));
    var image = r.ExtractImage();
    Assert.That(image, Has.Length.EqualTo(64 * 512));
    for (var b = 0; b < 64; ++b)
      Assert.That(image[b * 512], Is.EqualTo((byte)(b + 1)));
    Assert.That(r.HasFreeBlockMap, Is.False);
  }

  [Test, Category("HappyPath")]
  public void FreeBlocks_AreZero_AndMapStartsAfterReserved() {
    // 64 blocks, 2 reserved: map bit i is block i + 2. Blocks 2, 33 and 63 (the last one) are free.
    var free = new bool[64];
    free[2] = free[33] = free[63] = true;
    var image = new PackDevReader(new MemoryStream(Build(64, 2, 8, free, b => (byte)(b + 1)))).ExtractImage();
    for (var b = 0; b < 64; ++b)
      Assert.That(image.AsSpan(b * 512, 512).ToArray(), Is.All.EqualTo(free[b] ? 0 : b + 1), $"block {b}");
  }

  [Test, Category("HappyPath")]
  public void OldHeader_IsRead() {
    var r = new PackDevReader(new MemoryStream(Build(16, 0, 4, null, b => 7, old: true)));
    Assert.That(r.IsOldFormat, Is.True);
    Assert.That(r.ExtractImage(), Is.All.EqualTo(7));
  }

  [Test, Category("Boundary")]
  public void PartialLastTrack_KeepsItsBlocks() {
    var image = new PackDevReader(new MemoryStream(Build(10, 0, 4, null, b => (byte)b))).ExtractImage();
    Assert.That(image[9 * 512], Is.EqualTo(9));
  }

  [Test, Category("Exception")]
  public void Truncated_Throws() {
    var data = Build(16, 0, 4, null, b => 1);
    Assert.Throws<InvalidDataException>(() => new PackDevReader(new MemoryStream(data[..^100])).ExtractImage());
  }

  [Test, Category("Exception")]
  public void TrailingBytes_Throw() {
    var data = Build(16, 0, 4, null, b => 1);
    Assert.Throws<InvalidDataException>(() => new PackDevReader(new MemoryStream([.. data, 0])).ExtractImage());
  }

  [Test, Category("Exception")]
  [TestCase(0, 4)]
  [TestCase(16, 0)]
  public void ImpossibleGeometry_Throws(int blocks, int trackBlocks) {
    var data = Build(16, 0, 4, null, b => 1);
    BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(4), (uint)blocks);
    BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(16), (uint)(trackBlocks * 512));
    Assert.Throws<InvalidDataException>(() => _ = new PackDevReader(new MemoryStream(data)));
  }

  [Test, Category("Exception")]
  public void XpkPacked_WithoutBuffers_IsRejected() {
    // A packer id over stored data: the first "buffer" size runs past the end.
    var r = new PackDevReader(new MemoryStream(Build(16, 0, 4, null, b => 1, packer: 0x53515348)));
    Assert.Throws<InvalidDataException>(() => r.ExtractImage());
  }

  // ── XPK images: synthetic buffers, then PackDev 1.9's own output ─────────────

  private static byte[] XpkImage(byte[] stored, int bufferSize, bool old = false, bool breakChecksum = false) {
    using var ms = new MemoryStream();
    var header = Build(16, 0, 4, null, b => 0, old, packer: 0x524C454E)[..(old ? 32 : 52)];
    ms.Write(header);
    var be = new byte[4];
    for (var at = 0; at < stored.Length; at += bufferSize) {
      var xpk = FileFormat.Xpk.XpkStream.Encode(stored.AsSpan(at, Math.Min(bufferSize, stored.Length - at)));
      BinaryPrimitives.WriteUInt32BigEndian(be, (uint)xpk.Length);
      ms.Write(be);
      ms.Write(xpk);
      if (!old) {
        var sum = 0;
        foreach (var b in xpk) sum += (sbyte)b;
        BinaryPrimitives.WriteUInt32BigEndian(be, (uint)(sum + (breakChecksum ? 1 : 0)));
        ms.Write(be);
      }
    }
    return ms.ToArray();
  }

  private static byte[] StoredLayout(int blocks, int trackBlocks) {
    // Every block stored (no map): blocks of a track, then a label per block.
    using var ms = new MemoryStream();
    for (var t = 0; t < blocks / trackBlocks; ++t) {
      for (var b = 0; b < trackBlocks; ++b) ms.Write(Enumerable.Repeat((byte)(t * trackBlocks + b + 1), 512).ToArray());
      ms.Write(new byte[16 * trackBlocks]);
    }
    return ms.ToArray();
  }

  [Test, Category("HappyPath")]
  [TestCase(false)]
  [TestCase(true)]
  public void XpkBuffers_SplitAcrossBlocksAndLabels_Rebuild(bool old) {
    // 700-byte buffers cut blocks and labels at arbitrary points.
    var image = new PackDevReader(new MemoryStream(XpkImage(StoredLayout(16, 4), 700, old))).ExtractImage();
    for (var b = 0; b < 16; ++b)
      Assert.That(image.AsSpan(b * 512, 512).ToArray(), Is.All.EqualTo(b + 1), $"block {b}");
  }

  [Test, Category("Exception")]
  public void XpkBuffer_ChecksumMismatch_Throws() {
    var data = XpkImage(StoredLayout(16, 4), 700, breakChecksum: true);
    Assert.Throws<InvalidDataException>(() => new PackDevReader(new MemoryStream(data)).ExtractImage());
  }

  private const string FloppySha256 = "59c11eab0538310e808ccf254ab4c69d8aef25e6d515685e83676d771e9ff5c9";

  private static string VectorPath(string name)
    => Path.Combine(Documentation.FilesystemReadmeIsCurrentTests.RepositoryRoot(), "Compression.Tests", "PackDev", "ReferenceVectors", name);

  /// <summary>
  /// Images PackDev 1.9 wrote from one test floppy (see ReferenceVectors/README.md). Images with a
  /// free-block map leave free blocks out, so they rebuild the floppy's used blocks with zeros
  /// elsewhere; the ALL image keeps every block and rebuilds the floppy byte for byte.
  /// </summary>
  [Test, Category("RealFiles")]
  [TestCase("rlenall.pkd", "RLEN", false)]
  [TestCase("nuke.pkd", "NUKE", true)]
  [TestCase("sqsh.pkd", "SQSH", true)]
  [TestCase("stored.pkd", "", true)]
  public void ProducerImage_RebuildsTheFloppy(string file, string packer, bool freeMap) {
    var r = new PackDevReader(File.OpenRead(VectorPath(file)));
    Assert.That(r.PackerId, Is.EqualTo(packer));
    Assert.That(r.HasFreeBlockMap, Is.EqualTo(freeMap));
    var image = r.ExtractImage();
    var full = new PackDevReader(File.OpenRead(VectorPath("rlenall.pkd"))).ExtractImage();
    Assert.That(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(full)), Is.EqualTo(FloppySha256));
    for (var b = 0; b < 1760; ++b) {
      var block = image.AsSpan(b * 512, 512);
      if (block.ContainsAnyExcept((byte)0))
        Assert.That(block.SequenceEqual(full.AsSpan(b * 512, 512)), Is.True, $"{file}: block {b}");
    }
    // Every file of the volume survives, so no used block was left out.
    using var adf = new FileSystem.Adf.AdfReader(new MemoryStream(image));
    using var src = new FileSystem.Adf.AdfReader(new MemoryStream(full));
    Assert.That(adf.Entries.Select(e => e.FullPath), Is.EquivalentTo(src.Entries.Select(e => e.FullPath)));
    foreach (var entry in adf.Entries)
      Assert.That(adf.Extract(entry), Is.EqualTo(src.Extract(src.Entries.Single(e => e.FullPath == entry.FullPath))), entry.FullPath);
  }

  [Test, Category("Exception")]
  public void NotPackDev_ListFallsBack() {
    byte[] hunk = [0x00, 0x00, 0x03, 0xF3, 0, 0, 0, 0];
    Assert.Throws<InvalidDataException>(() => _ = new PackDevReader(new MemoryStream(hunk)));
    Assert.That(new PackDevFormatDescriptor().List(new MemoryStream(hunk), null).Single().Name, Is.EqualTo("FULL.pkd"));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_NamesFloppyImagesAdf() {
    var list = new PackDevFormatDescriptor().List(new MemoryStream(Build(1760, 2, 11, null, b => 0)), null);
    Assert.That(list.Single().Name, Is.EqualTo("disk.adf"));
    Assert.That(list[0].OriginalSize, Is.EqualTo(901120));
  }

  [Test, Category("HappyPath")]
  [TestCase(false)]
  [TestCase(true)]
  public void Magic_IsDetectedAsPackDev(bool old) {
    var header = Build(16, 0, 4, null, b => 0, old)[..64];
    Assert.That(Compression.Lib.FormatDetector.DetectByMagic(header).ToString(), Is.EqualTo("PackDev"));
  }

  // ── Real images (gated: test-corpus/packdev, not committed) ──────────────────

  private static uint BlockSum(byte[] image, int block) {
    var sum = 0u;
    for (var i = 0; i < 512; i += 4) sum += BinaryPrimitives.ReadUInt32BigEndian(image.AsSpan(block * 512 + i));
    return sum;
  }

  /// <summary>
  /// Real PackDev images: the reader disks of the Amiga Format CD collection ("Image.dsq",
  /// fetched from discmaster.textfiles.com, "format=packDevDSQ"). Each must consume its file exactly
  /// and yield an AmigaDOS volume with an intact root block and file headers whose sizes our ADF
  /// reader reproduces.
  /// </summary>
  [Test, Category("RealFiles")]
  public void RealImages_InTestCorpus_DecodeToIntactVolumes() {
    var corpus = Path.GetFullPath(Path.Combine(TestContext.CurrentContext.TestDirectory, "..", "..", "..", "..", "test-corpus", "packdev"));
    var files = Directory.Exists(corpus) ? Directory.EnumerateFiles(corpus).Where(f => PackDevReader.IsPackDev(File.ReadAllBytes(f))).Order().ToList() : [];
    if (files.Count == 0)
      Assert.Ignore($"No real PackDev images under {corpus}.");

    foreach (var file in files) {
      var image = new PackDevReader(File.OpenRead(file)).ExtractImage();
      Assert.That(image, Has.Length.EqualTo(901120), file);
      Assert.That(BinaryPrimitives.ReadUInt32BigEndian(image.AsSpan(880 * 512)), Is.EqualTo(2u), $"{file}: root type");
      Assert.That(BlockSum(image, 880), Is.Zero, $"{file}: root checksum");
      using var adf = new FileSystem.Adf.AdfReader(new MemoryStream(image));
      foreach (var entry in adf.Entries) {
        Assert.That(BlockSum(image, entry.HeaderBlock), Is.Zero, $"{Path.GetFileName(file)}: header of {entry.FullPath}");
        if (!entry.IsDirectory)
          Assert.That(adf.Extract(entry), Has.Length.EqualTo(entry.Size), $"{Path.GetFileName(file)}: {entry.FullPath}");
      }
    }
  }
}
