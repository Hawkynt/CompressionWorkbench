using System.Buffers.Binary;
using System.Text;
using Compression.Registry;
using FileSystem.Cxfs;

namespace Compression.Tests.Cxfs;

[TestFixture]
public class CxfsDetectionTests {

  /// <summary>
  /// Build a minimal CXFS image: synthetic 512-byte XFS superblock with the
  /// CXFS-ish bit set in sb_features2 but no inode/directory body. The XFS
  /// reader will fail to walk this image (no root inode) and the CxfsReader
  /// will fall back to the Stage-0 metadata.ini + cxfs-volume.bin surface.
  /// </summary>
  private static byte[] BuildMinimal(uint sbFeatures2 = 0x00000080, int sbSize = 512) {
    var image = new byte[sbSize];
    Encoding.ASCII.GetBytes("XFSB").CopyTo(image.AsSpan(0, 4));
    BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(CxfsReader.SbFeatures2Offset, 4), sbFeatures2);
    return image;
  }

  /// <summary>
  /// Build a real XFS image that also carries a CXFS feature-bit flag in
  /// sb_features2. Layout is identical to the FileSystem.Xfs reader's own
  /// happy-path test fixture: 1 MB, 1 AG, 256 blocks @ 4 KiB, v4 superblock,
  /// short-form root directory with inline-format file inodes.
  /// </summary>
  private static byte[] BuildCxfsWithFiles(uint cxfsFeatureBit, params (string Name, byte[] Data)[] files) {
    const uint blockSize = 4096;
    const ushort inodeSize = 256;
    const uint agBlocks = 256;
    const int inoPerBlock = (int)(blockSize / inodeSize); // 16
    var agBlkLog = 8; // log2(256)

    var imageSize = (int)(agBlocks * blockSize);
    var img = new byte[imageSize];

    // Superblock — canonical XFS v4 layout matches the XFS reader's expectations.
    BinaryPrimitives.WriteUInt32BigEndian(img.AsSpan(0), 0x58465342);    // sb_magicnum = XFSB
    BinaryPrimitives.WriteUInt32BigEndian(img.AsSpan(4), blockSize);     // sb_blocksize
    BinaryPrimitives.WriteUInt64BigEndian(img.AsSpan(8), agBlocks);      // sb_dblocks
    var rootIno = (ulong)(4 * inoPerBlock);
    BinaryPrimitives.WriteUInt64BigEndian(img.AsSpan(56), rootIno);
    BinaryPrimitives.WriteUInt32BigEndian(img.AsSpan(84), agBlocks);     // sb_agblocks
    BinaryPrimitives.WriteUInt32BigEndian(img.AsSpan(88), 1);            // sb_agcount
    BinaryPrimitives.WriteUInt16BigEndian(img.AsSpan(100), 4);           // sb_versionnum = v4
    BinaryPrimitives.WriteUInt16BigEndian(img.AsSpan(104), inodeSize);
    BinaryPrimitives.WriteUInt16BigEndian(img.AsSpan(106), inoPerBlock);
    img[124] = (byte)agBlkLog;

    // CXFS feature flag in sb_features2 (the field the CxfsReader reads at 0x82).
    BinaryPrimitives.WriteUInt32BigEndian(img.AsSpan(CxfsReader.SbFeatures2Offset, 4), cxfsFeatureBit);

    // Root inode at block 4: v2 dinode, short-form directory.
    var rootOff = 4 * (int)blockSize;
    BinaryPrimitives.WriteUInt16BigEndian(img.AsSpan(rootOff), 0x494E);   // IN
    BinaryPrimitives.WriteUInt16BigEndian(img.AsSpan(rootOff + 2), 0x41ED); // S_IFDIR | 0755
    img[rootOff + 4] = 2; // di_version
    img[rootOff + 5] = 1; // di_format = local (short-form)

    var sfOff = rootOff + 100;
    img[sfOff] = (byte)files.Length;
    img[sfOff + 1] = 0;
    BinaryPrimitives.WriteUInt32BigEndian(img.AsSpan(sfOff + 2), (uint)rootIno);
    var entryPos = sfOff + 6;

    var nextBlock = 5;
    for (int i = 0; i < files.Length; i++) {
      var (name, data) = files[i];
      var nameBytes = Encoding.UTF8.GetBytes(name);

      var fileIno = (ulong)(nextBlock * inoPerBlock);

      img[entryPos] = (byte)nameBytes.Length;
      BinaryPrimitives.WriteUInt16BigEndian(img.AsSpan(entryPos + 1), (ushort)(i + 3));
      nameBytes.CopyTo(img, entryPos + 3);
      BinaryPrimitives.WriteUInt32BigEndian(img.AsSpan(entryPos + 3 + nameBytes.Length), (uint)fileIno);
      entryPos += 3 + nameBytes.Length + 4;

      var fInodeOff = nextBlock * (int)blockSize;
      BinaryPrimitives.WriteUInt16BigEndian(img.AsSpan(fInodeOff), 0x494E);
      BinaryPrimitives.WriteUInt16BigEndian(img.AsSpan(fInodeOff + 2), 0x81A4); // S_IFREG
      img[fInodeOff + 4] = 2;
      img[fInodeOff + 5] = 1; // local/inline (data <= inodeSize - 100)
      BinaryPrimitives.WriteUInt64BigEndian(img.AsSpan(fInodeOff + 56), (ulong)data.Length);
      data.CopyTo(img, fInodeOff + 100);
      nextBlock++;
    }

    var sfSize = entryPos - sfOff;
    BinaryPrimitives.WriteUInt64BigEndian(img.AsSpan(rootOff + 56), (ulong)sfSize);

    return img;
  }

  [Test, Category("HappyPath")]
  public void Detector_IdentifiesByMagic() {
    var d = new CxfsFormatDescriptor();
    Assert.That(d.Id, Is.EqualTo("Cxfs"));
    Assert.That(d.Extensions, Does.Contain(".cxfs"));
    // CXFS shares the XFS magic — extension-only detection to avoid
    // FormatDetector first-match collision with FileSystem.Xfs.
    Assert.That(d.MagicSignatures, Has.Count.EqualTo(0));
    Assert.That(d, Is.Not.InstanceOf<IArchiveCreatable>());

    // Reader still verifies the XFS magic internally.
    using var ms = new MemoryStream(BuildMinimal(sbFeatures2: 0x00010080));
    var r = new CxfsReader(ms);
    Assert.That(r.ValidHeader, Is.True);
    Assert.That(r.XfsMagic, Is.EqualTo(0x58465342u));
    Assert.That(r.SbFeatures2, Is.EqualTo(0x00010080u));
  }

  [Test, Category("HappyPath")]
  public void List_OnSyntheticSuperblock_FallsBackToMetadata() {
    var d = new CxfsFormatDescriptor();
    using var ms = new MemoryStream(BuildMinimal(sbSize: 1024));
    var entries = d.List(ms, password: null);
    var names = entries.Select(e => e.Name).ToList();
    // 512-byte sb-only image — no walkable XFS root inode — Stage-0 fallback.
    Assert.That(names, Is.EquivalentTo(new[] { "metadata.ini", "cxfs-volume.bin" }));
  }

  [Test, Category("HappyPath")]
  public void Reader_FallbackPath_FlagsDelegationFailure() {
    using var ms = new MemoryStream(BuildMinimal(sbSize: 1024));
    var r = new CxfsReader(ms);
    Assert.That(r.DelegatedToXfs, Is.False,
      "Synthetic 1 KiB superblock has no XFS inode table; reader must fall back to Stage-0 metadata.");
    Assert.That(r.Entries.All(e => !e.FromXfsLayer), Is.True);
  }

  [Test, Category("HappyPath")]
  public void Description_FlagsRoDelegation() {
    var d = new CxfsFormatDescriptor();
    var description = d.Description.ToLowerInvariant();
    Assert.That(description, Does.Contain("xfs reader delegation"));
    Assert.That(description, Does.Contain("cluster metadata"));
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanList), Is.True);
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanExtract), Is.True);
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanCreate), Is.False,
      "CXFS R/O via XFS delegation — writing back a cluster-aware volume is out of scope.");
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanModify), Is.False);
  }

  // ── R/O delegation: real XFS layer walks through CxfsReader ──────────────

  [Test, Category("HappyPath")]
  public void Read_DelegatesToXfs_SingleFile() {
    var img = BuildCxfsWithFiles(cxfsFeatureBit: 0x00010080, ("hello.txt", "Hi CXFS"u8.ToArray()));
    using var ms = new MemoryStream(img);
    var r = new CxfsReader(ms);

    Assert.That(r.DelegatedToXfs, Is.True,
      "Real XFS-layer image with CXFS feature bit — reader must delegate to XfsReader.");
    Assert.That(r.SbFeatures2, Is.EqualTo(0x00010080u),
      "CXFS-specific feature bits in sb_features2 must still be exposed for diagnostics.");
    Assert.That(r.Entries.Count(e => e.FromXfsLayer), Is.EqualTo(1));
    var entry = r.Entries.Single(e => e.Name == "hello.txt");
    Assert.That(entry.IsDirectory, Is.False);
    Assert.That(entry.FromXfsLayer, Is.True);
  }

  [Test, Category("HappyPath")]
  public void Extract_DelegatesToXfs_RoundTrip() {
    var content = "CXFS via XFS delegation"u8.ToArray();
    var img = BuildCxfsWithFiles(cxfsFeatureBit: 0x80, ("greet.txt", content));
    using var ms = new MemoryStream(img);
    var r = new CxfsReader(ms);

    var entry = r.Entries.Single(e => e.Name == "greet.txt");
    var got = r.Extract(entry);
    Assert.That(got, Is.EqualTo(content));
  }

  [Test, Category("HappyPath")]
  public void List_OnRealXfsLayer_SurfacesRealEntries() {
    var d = new CxfsFormatDescriptor();
    var img = BuildCxfsWithFiles(
      cxfsFeatureBit: 0x80,
      ("a.txt", "A"u8.ToArray()),
      ("b.txt", "B"u8.ToArray()));
    using var ms = new MemoryStream(img);
    var entries = d.List(ms, password: null);
    var names = entries.Select(e => e.Name).ToHashSet();
    Assert.That(names, Does.Contain("a.txt"));
    Assert.That(names, Does.Contain("b.txt"));
    Assert.That(names, Does.Not.Contain("metadata.ini"),
      "Stage-0 fallback metadata must not surface when the XFS layer walk succeeds.");
  }

  [Test, Category("HappyPath")]
  public void OpenEntry_DelegatesToXfsAndBoundedToLogicalSize() {
    var d = new CxfsFormatDescriptor();
    var content = "Bounded CXFS content"u8.ToArray();
    var img = BuildCxfsWithFiles(0x80, ("doc.txt", content));
    using var ms = new MemoryStream(img);
    using var entryStream = ((IArchiveFormatOperations)d).OpenEntry(ms, "doc.txt", password: null);
    using var buf = new MemoryStream();
    entryStream.CopyTo(buf);
    Assert.That(buf.ToArray(), Is.EqualTo(content));
  }

  [Test, Category("ErrorHandling")]
  public void Reader_BadMagic_Throws() {
    var bad = new byte[1024];
    bad[0] = (byte)'X'; bad[1] = (byte)'X'; bad[2] = (byte)'X'; bad[3] = (byte)'X';
    using var ms = new MemoryStream(bad);
    Assert.Throws<InvalidDataException>(() => _ = new CxfsReader(ms));
  }

  [Test, Category("ErrorHandling")]
  public void Reader_TooSmall_Throws() {
    using var ms = new MemoryStream(new byte[10]);
    Assert.Throws<InvalidDataException>(() => _ = new CxfsReader(ms));
  }
}
