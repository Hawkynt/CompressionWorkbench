using System.Buffers.Binary;
using System.Text;
using Compression.Registry;
using FileSystem.Cxfs;
using FileSystem.Xfs;

namespace Compression.Tests.Cxfs;

[TestFixture]
public class CxfsDetectionTests {

  /// <summary>
  /// Build a minimal XFS superblock carrying an arbitrary legacy sb_features2
  /// value but no inode/directory body. CXFS uses the XFS filesystem structure;
  /// this deliberately incomplete image therefore exercises only the descriptor's
  /// detection fallback.
  /// </summary>
  private static byte[] BuildMinimal(uint sbFeatures2 = 0x00000080, int sbSize = 512) {
    var image = new byte[sbSize];
    Encoding.ASCII.GetBytes("XFSB").CopyTo(image.AsSpan(0, 4));
    BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(CxfsReader.SbFeatures2Offset, 4), sbFeatures2);
    return image;
  }

  /// <summary>
  /// Build a real XFS filesystem body suitable for CXFS. Layout is identical to
  /// the FileSystem.Xfs reader's happy-path test fixture: 1 MB, 1 AG, 256 blocks
  /// @ 4 KiB, v4 superblock, short-form root directory with inline file inodes.
  /// </summary>
  private static byte[] BuildCxfsWithFiles(uint features2, params (string Name, byte[] Data)[] files) {
    const uint blockSize = 4096;
    const ushort inodeSize = 256;
    const uint agBlocks = 256;
    const int inoPerBlock = (int)(blockSize / inodeSize); // 16
    const int agBlkLog = 8; // log2(256)

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
    img[124] = agBlkLog;

    // Ordinary XFS sb_features2 field. CXFS does not assign a separate magic bit.
    BinaryPrimitives.WriteUInt32BigEndian(img.AsSpan(CxfsReader.SbFeatures2Offset, 4), features2);

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
    for (var i = 0; i < files.Length; i++) {
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

  private static byte[] Extract(CxfsFormatDescriptor descriptor, Stream image, string name) {
    image.Position = 0;
    using var entry = ((IArchiveFormatOperations)descriptor).OpenEntry(image, name, password: null);
    using var output = new MemoryStream();
    entry.CopyTo(output);
    return output.ToArray();
  }

  [Test, Category("HappyPath")]
  public void Detector_UsesExtensionBecauseCxfsHasNoDistinctFilesystemMagic() {
    var d = new CxfsFormatDescriptor();
    Assert.That(d.Id, Is.EqualTo("Cxfs"));
    Assert.That(d.Extensions, Does.Contain(".cxfs"));
    Assert.That(d.MagicSignatures, Has.Count.EqualTo(0));

    using var ms = new MemoryStream(BuildMinimal(sbFeatures2: 0x00010080));
    using var r = new CxfsReader(ms);
    Assert.That(r.ValidHeader, Is.True);
    Assert.That(r.XfsMagic, Is.EqualTo(0x58465342u));
    Assert.That(r.SbFeatures2, Is.EqualTo(0x00010080u),
      "sb_features2 remains visible as ordinary XFS diagnostics, not as a CXFS discriminator.");
  }

  [Test, Category("HappyPath")]
  public void Descriptor_AdvertisesXfsBackedRwAndMaintenanceSurface() {
    var d = new CxfsFormatDescriptor();
    var description = d.Description.ToLowerInvariant();

    Assert.Multiple(() => {
      Assert.That(description, Does.Contain("r/w"));
      Assert.That(description, Does.Contain("same filesystem structure as xfs"));
      Assert.That(description, Does.Contain("cluster database"));
      Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanCreate), Is.True);
      Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanModify), Is.True);
      Assert.That(d, Is.InstanceOf<IArchiveCreatable>());
      Assert.That(d, Is.InstanceOf<IArchiveModifiable>());
      Assert.That(d, Is.InstanceOf<IArchivePurgeable>());
      Assert.That(d, Is.InstanceOf<IArchiveDefragmentable>());
      Assert.That(d, Is.InstanceOf<IArchiveShrinkable>());
      Assert.That(d, Is.InstanceOf<IWipeEmpty>());
      Assert.That(d, Is.InstanceOf<ILayoutOptimizable>());
      Assert.That(d, Is.InstanceOf<IFilesystemExtentMap>());
      Assert.That(d, Is.InstanceOf<IFilesystemBlockMover>());
    });
  }

  [Test, Category("HappyPath")]
  public void List_OnSyntheticSuperblock_FallsBackToMetadata() {
    var d = new CxfsFormatDescriptor();
    using var ms = new MemoryStream(BuildMinimal(sbSize: 1024));
    var entries = d.List(ms, password: null);
    var names = entries.Select(e => e.Name).ToList();
    Assert.That(names, Is.EquivalentTo(new[] { "metadata.ini", "cxfs-volume.bin" }));
  }

  [Test, Category("HappyPath")]
  public void Reader_FallbackPath_FlagsDelegationFailure() {
    using var ms = new MemoryStream(BuildMinimal(sbSize: 1024));
    using var r = new CxfsReader(ms);
    Assert.That(r.DelegatedToXfs, Is.False,
      "Synthetic superblock has no XFS root inode; reader must fall back to detection metadata.");
    Assert.That(r.Entries.All(e => !e.FromXfsLayer), Is.True);
  }

  [Test, Category("HappyPath")]
  public void Read_DelegatesToXfs_SingleFile() {
    var img = BuildCxfsWithFiles(features2: 0x00010080, ("hello.txt", "Hi CXFS"u8.ToArray()));
    using var ms = new MemoryStream(img);
    using var r = new CxfsReader(ms);

    Assert.That(r.DelegatedToXfs, Is.True);
    Assert.That(r.SbFeatures2, Is.EqualTo(0x00010080u));
    Assert.That(r.Entries.Count(e => e.FromXfsLayer), Is.EqualTo(1));
    var entry = r.Entries.Single(e => e.Name == "hello.txt");
    Assert.That(entry.IsDirectory, Is.False);
    Assert.That(entry.FromXfsLayer, Is.True);
  }

  [Test, Category("HappyPath")]
  public void Extract_DelegatesToXfs_RoundTrip() {
    var content = "CXFS via XFS delegation"u8.ToArray();
    var img = BuildCxfsWithFiles(features2: 0x80, ("greet.txt", content));
    using var ms = new MemoryStream(img);
    using var r = new CxfsReader(ms);

    var entry = r.Entries.Single(e => e.Name == "greet.txt");
    Assert.That(r.Extract(entry), Is.EqualTo(content));
  }

  [Test, Category("HappyPath")]
  public void List_OnRealXfsLayer_SurfacesRealEntries() {
    var d = new CxfsFormatDescriptor();
    var img = BuildCxfsWithFiles(
      features2: 0x80,
      ("a.txt", "A"u8.ToArray()),
      ("b.txt", "B"u8.ToArray()));
    using var ms = new MemoryStream(img);
    var entries = d.List(ms, password: null);
    var names = entries.Select(e => e.Name).ToHashSet();
    Assert.That(names, Does.Contain("a.txt"));
    Assert.That(names, Does.Contain("b.txt"));
    Assert.That(names, Does.Not.Contain("metadata.ini"),
      "Detection fallback must not surface when the XFS layer walk succeeds.");
  }

  [Test, Category("HappyPath")]
  public void OpenEntry_DelegatesToXfsAndBoundedToLogicalSize() {
    var d = new CxfsFormatDescriptor();
    var content = "Bounded CXFS content"u8.ToArray();
    var img = BuildCxfsWithFiles(0x80, ("doc.txt", content));
    using var ms = new MemoryStream(img);
    Assert.That(Extract(d, ms, "doc.txt"), Is.EqualTo(content));
  }

  [Test, Category("RoundTrip")]
  public void Create_RoundTripsThroughCxfsAndXfsReaders() {
    var d = new CxfsFormatDescriptor();
    var content = "created through CXFS"u8.ToArray();
    using var image = new MemoryStream();

    d.Create(image, [ArchiveInputInfo.InMemory("hello.txt", content)], new FormatCreateOptions());

    Assert.That(Extract(d, image, "hello.txt"), Is.EqualTo(content));

    image.Position = 0;
    using var xfs = new XfsReader(image);
    var entry = xfs.Entries.Single(e => e.Name == "hello.txt");
    Assert.That(xfs.Extract(entry), Is.EqualTo(content),
      "A CXFS filesystem image is an XFS filesystem image; the XFS backend must read authored output directly.");
  }

  [Test, Category("RoundTrip")]
  public void Create_EmptyFilesystem_RemainsARealFilesystemInsteadOfFallbackMetadata() {
    var d = new CxfsFormatDescriptor();
    using var image = new MemoryStream();

    d.Create(image, [], new FormatCreateOptions());
    image.Position = 0;
    using var reader = new CxfsReader(image);

    Assert.Multiple(() => {
      Assert.That(reader.DelegatedToXfs, Is.True);
      Assert.That(reader.Entries, Is.Empty);
    });
  }

  [Test, Category("RoundTrip")]
  public void Modify_AddRemoveAndPurge_RoundTrips() {
    var d = new CxfsFormatDescriptor();
    using var image = new MemoryStream();
    d.Create(image, [ArchiveInputInfo.InMemory("a.txt", "A"u8.ToArray())], new FormatCreateOptions());

    image.Position = 0;
    d.Add(image, [ArchiveInputInfo.InMemory("b.txt", "B"u8.ToArray())]);
    Assert.That(Extract(d, image, "a.txt"), Is.EqualTo("A"u8.ToArray()));
    Assert.That(Extract(d, image, "b.txt"), Is.EqualTo("B"u8.ToArray()));

    image.Position = 0;
    d.Remove(image, ["a.txt"]);
    image.Position = 0;
    Assert.That(d.List(image, null).Where(e => !e.IsDirectory).Select(e => e.Name), Is.EquivalentTo(new[] { "b.txt" }));

    image.Position = 0;
    ((IArchivePurgeable)d).Purge(image);
    image.Position = 0;
    Assert.That(d.List(image, null).Where(e => !e.IsDirectory), Is.Empty);
  }

  [Test, Category("RoundTrip")]
  public void MaintenanceOperations_DelegateToXfsAndPreservePayload() {
    var d = new CxfsFormatDescriptor();
    var content = Enumerable.Range(0, 32 * 1024).Select(i => (byte)(i * 37)).ToArray();
    using var image = new MemoryStream();
    d.Create(image, [ArchiveInputInfo.InMemory("payload.bin", content)], new FormatCreateOptions());

    image.Position = 0;
    var wiped = d.WipeUnusedSpace(image);
    Assert.That(wiped, Is.GreaterThanOrEqualTo(0));
    Assert.That(Extract(d, image, "payload.bin"), Is.EqualTo(content));

    image.Position = 0;
    d.Defragment(image);
    Assert.That(Extract(d, image, "payload.bin"), Is.EqualTo(content));

    using var shrunk = new MemoryStream();
    image.Position = 0;
    d.Shrink(image, shrunk);
    Assert.That(Extract(d, shrunk, "payload.bin"), Is.EqualTo(content));

    using var relaid = new MemoryStream();
    shrunk.Position = 0;
    d.RebuildStreaming(shrunk, relaid, new LayoutRebuildOptions());
    Assert.That(Extract(d, relaid, "payload.bin"), Is.EqualTo(content));
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
