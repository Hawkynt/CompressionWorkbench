using System.Buffers.Binary;
using System.Text;
using Compression.Registry;
using FileSystem.Cxfs;
using FileSystem.Xfs;

namespace Compression.Tests.Cxfs;

[TestFixture]
public class CxfsDetectionTests {

  private static byte[] BuildMinimal(uint sbFeatures2 = 0x00000080, int sbSize = 512) {
    var image = new byte[sbSize];
    Encoding.ASCII.GetBytes("XFSB").CopyTo(image.AsSpan(0, 4));
    BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(CxfsReader.SbFeatures2Offset, 4), sbFeatures2);
    return image;
  }

  private static byte[] Extract(CxfsFormatDescriptor descriptor, Stream image, string name) {
    image.Position = 0;
    using var entry = ((IArchiveFormatOperations)descriptor).OpenEntry(image, name, password: null);
    using var output = new MemoryStream();
    entry.CopyTo(output);
    return output.ToArray();
  }

  private static string VolumeLabel(Stream image) {
    var bytes = image switch {
      MemoryStream ms => ms.ToArray(),
      _ => throw new ArgumentException("Test helper expects a memory stream.", nameof(image)),
    };
    var field = bytes.AsSpan(108, 12);
    var end = field.IndexOf((byte)0);
    if (end >= 0) field = field[..end];
    return Encoding.ASCII.GetString(field);
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
    Assert.That(CxfsReader.SbFeatures2Offset, Is.EqualTo(0xC8));
    Assert.That(r.SbFeatures2, Is.EqualTo(0x00010080u));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_AdvertisesOnlyBackedWritableMaintenanceSurface() {
    var d = new CxfsFormatDescriptor();
    var description = d.Description.ToLowerInvariant();

    Assert.Multiple(() => {
      Assert.That(description, Does.Contain("xfs on disk"));
      Assert.That(description, Does.Contain("xfs-v4"));
      Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanCreate), Is.True);
      Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanModify), Is.True);
      Assert.That(d, Is.InstanceOf<IArchiveCreatable>());
      Assert.That(d, Is.InstanceOf<IArchiveModifiable>());
      Assert.That(d, Is.InstanceOf<IArchivePurgeable>());
      Assert.That(d, Is.InstanceOf<IArchiveDefragmentable>());
      Assert.That(d, Is.InstanceOf<IArchiveShrinkable>());
      Assert.That(d, Is.Not.InstanceOf<IWipeEmpty>());
      Assert.That(d, Is.Not.InstanceOf<ILayoutOptimizable>());
      Assert.That(d, Is.Not.InstanceOf<IFilesystemExtentMap>());
      Assert.That(d, Is.Not.InstanceOf<IFilesystemBlockMover>());
      Assert.That(d.MaxTotalArchiveSize, Is.EqualTo(1L << 30));
    });
  }

  [Test, Category("HappyPath")]
  public void List_OnSyntheticSuperblock_FallsBackToMetadata() {
    var d = new CxfsFormatDescriptor();
    using var ms = new MemoryStream(BuildMinimal(sbSize: 1024));
    var names = d.List(ms, password: null).Select(e => e.Name).ToList();
    Assert.That(names, Is.EquivalentTo(new[] { "metadata.ini", "cxfs-volume.bin" }));
  }

  [Test, Category("HappyPath")]
  public void Reader_FallbackPath_FlagsDelegationFailure() {
    using var ms = new MemoryStream(BuildMinimal(sbSize: 1024));
    using var r = new CxfsReader(ms);
    Assert.That(r.DelegatedToXfs, Is.False);
    Assert.That(r.Entries.All(e => !e.FromXfsLayer), Is.True);
  }

  [Test, Category("RoundTrip")]
  public void Create_EmitsPreCrcXfsV4AndRoundTripsThroughXfsReader() {
    var d = new CxfsFormatDescriptor();
    var content = "created through CXFS v4"u8.ToArray();
    using var image = new MemoryStream();

    d.Create(image, [ArchiveInputInfo.InMemory("hello.txt", content)], new FormatCreateOptions());

    var bytes = image.ToArray();
    var version = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(100, 2));
    var rootIno = BinaryPrimitives.ReadUInt64BigEndian(bytes.AsSpan(56, 8));
    var blockSize = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(4, 4));
    var inodeSize = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(104, 2));
    var agBlkLog = bytes[124];
    var agBlocks = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(84, 4));
    var inoPbLog = System.Numerics.BitOperations.Log2(blockSize / inodeSize);
    var aginoLog = agBlkLog + inoPbLog;
    var agIno = rootIno & ((1UL << aginoLog) - 1);
    var rootOffset = (int)((agIno / (blockSize / inodeSize)) * blockSize + (agIno % (blockSize / inodeSize)) * inodeSize);

    Assert.Multiple(() => {
      Assert.That(version & 0xF, Is.EqualTo(4), "CXFS authoring must use the pre-CRC XFS-v4 family.");
      Assert.That(BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(CxfsReader.SbFeatures2Offset, 4)), Is.Zero);
      Assert.That(bytes[rootOffset + 4], Is.EqualTo(2), "XFS v4 uses the 100-byte v2 dinode core in this profile.");
      Assert.That(Extract(d, image, "hello.txt"), Is.EqualTo(content));
    });

    image.Position = 0;
    using var xfs = new XfsReader(image);
    var entry = xfs.Entries.Single(e => e.Name == "hello.txt");
    Assert.That(xfs.Extract(entry), Is.EqualTo(content));
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
  public void Modify_AddReplaceRemoveAndPurge_RebuildsV4Profile() {
    var d = new CxfsFormatDescriptor();
    using var image = new MemoryStream();
    d.Create(image, [ArchiveInputInfo.InMemory("a.txt", "A"u8.ToArray())], new FormatCreateOptions());

    d.Add(image, [ArchiveInputInfo.InMemory("b.txt", "B"u8.ToArray())]);
    d.Add(image, [ArchiveInputInfo.InMemory("a.txt", "A2"u8.ToArray())]);
    Assert.That(Extract(d, image, "a.txt"), Is.EqualTo("A2"u8.ToArray()));
    Assert.That(Extract(d, image, "b.txt"), Is.EqualTo("B"u8.ToArray()));

    d.Remove(image, ["a.txt"]);
    image.Position = 0;
    Assert.That(d.List(image, null).Where(e => !e.IsDirectory).Select(e => e.Name), Is.EquivalentTo(new[] { "b.txt" }));

    ((IArchivePurgeable)d).Purge(image);
    image.Position = 0;
    Assert.That(d.List(image, null).Where(e => !e.IsDirectory), Is.Empty);
    var version = BinaryPrimitives.ReadUInt16BigEndian(image.ToArray().AsSpan(100, 2));
    Assert.That(version & 0xF, Is.EqualTo(4));
  }

  [Test, Category("RoundTrip")]
  public void RebuildMaintenance_PreservesVolumeLabel() {
    var d = new CxfsFormatDescriptor();
    var options = new FormatCreateOptions();
    options.FormatSpecific["VolumeLabel"] = "CXFSVOL";
    using var image = new MemoryStream();
    d.Create(image, [ArchiveInputInfo.InMemory("a.txt", "A"u8.ToArray())], options);

    Assert.That(VolumeLabel(image), Is.EqualTo("CXFSVOL"));
    d.Add(image, [ArchiveInputInfo.InMemory("b.txt", "B"u8.ToArray())]);
    Assert.That(VolumeLabel(image), Is.EqualTo("CXFSVOL"));
    d.Remove(image, ["b.txt"]);
    Assert.That(VolumeLabel(image), Is.EqualTo("CXFSVOL"));
    d.Defragment(image);
    Assert.That(VolumeLabel(image), Is.EqualTo("CXFSVOL"));

    using var shrunk = new MemoryStream();
    d.Shrink(image, shrunk);
    Assert.That(VolumeLabel(shrunk), Is.EqualTo("CXFSVOL"));
  }

  [Test, Category("RoundTrip")]
  public void DefragAndShrink_PreservePayloadAndV4Profile() {
    var d = new CxfsFormatDescriptor();
    var content = Enumerable.Range(0, 128 * 1024).Select(i => (byte)(i * 37)).ToArray();
    using var image = new MemoryStream();
    d.Create(image, [ArchiveInputInfo.InMemory("payload.bin", content)], new FormatCreateOptions());

    d.Defragment(image);
    Assert.That(Extract(d, image, "payload.bin"), Is.EqualTo(content));

    using var shrunk = new MemoryStream();
    d.Shrink(image, shrunk);
    Assert.That(Extract(d, shrunk, "payload.bin"), Is.EqualTo(content));
    var version = BinaryPrimitives.ReadUInt16BigEndian(shrunk.ToArray().AsSpan(100, 2));
    Assert.That(version & 0xF, Is.EqualTo(4));
  }

  [Test, Category("ErrorHandling")]
  public void Mutation_RejectsModernXfsV5RatherThanSilentlyChangingCompatibility() {
    var cxfs = new CxfsFormatDescriptor();
    var xfs = new XfsFormatDescriptor();
    using var image = new MemoryStream();
    xfs.Create(image, [ArchiveInputInfo.InMemory("old.txt", "old"u8.ToArray())], new FormatCreateOptions());

    Assert.Throws<NotSupportedException>(() =>
      cxfs.Add(image, [ArchiveInputInfo.InMemory("new.txt", "new"u8.ToArray())]));
  }

  [Test, Category("ErrorHandling")]
  public void Create_RejectsNestedPathsAndDirectories() {
    var d = new CxfsFormatDescriptor();
    using var image = new MemoryStream();

    // An input the profile cannot place is a wrong argument rather than an unsupported operation,
    // which is the distinction the conversion matrix reads: it ignores the first and fails on the
    // second. BcacheFs, VobSub and Bik refuse the same way.
    Assert.Throws<ArgumentException>(() =>
      d.Create(image, [ArchiveInputInfo.InMemory("dir/file.txt", "x"u8.ToArray())], new FormatCreateOptions()));
    Assert.Throws<ArgumentException>(() =>
      d.Create(image, [new ArchiveInputInfo("", "dir/", true)], new FormatCreateOptions()));
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
