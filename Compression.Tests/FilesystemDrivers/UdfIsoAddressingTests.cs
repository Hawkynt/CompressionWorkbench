using System.Buffers.Binary;
using System.Text;
using Compression.Lib;
using Compression.Registry;
using FileSystem.Iso;
using FileSystem.Udf;

namespace Compression.Tests.FilesystemDrivers;

[TestFixture]
public sealed class UdfIsoAddressingTests {
  private const int IsoSectorSize = 2048;

  [OneTimeSetUp]
  public void InitializeRegistry() => FormatRegistration.EnsureInitialized();

  [Test, Category("Driver"), Category("Contract")]
  public void IsoMultiExtentFile_IsOneLogicalMountedFile() {
    var payload = Enumerable.Range(0, 7000).Select(static i => (byte)(i * 43 + 9)).ToArray();
    var writer = new IsoWriter();
    writer.AddFile("SPLIT.BIN", payload);
    var bytes = writer.Build();
    RewriteJolietFileAsTwoExtents(bytes, "SPLIT.BIN", payload.Length, IsoSectorSize * 2);

    using var image = new MemoryStream(bytes, writable: false);
    var profile = new IsoFilesystemDriverAdapter().ProbeFilesystem(image);
    Assert.That(profile.CanMount, Is.True, string.Join("; ", profile.Limitations));

    image.Position = 0;
    using var session = new IsoFilesystemDriverAdapter().OpenFilesystem(
      image,
      new FilesystemOpenOptions(ReadOnly: true, LeaveOpen: true));
    var node = session.Lookup(session.RootNodeId, "split.bin");
    Assert.That(node, Is.Not.Null);

    using var handle = session.OpenFile(node!.Value, FileAccess.Read);
    var actual = new byte[3079];
    Assert.That(handle.Read(IsoSectorSize * 2 - 731, actual), Is.EqualTo(actual.Length));
    Assert.That(actual, Is.EqualTo(payload.AsSpan(IsoSectorSize * 2 - 731, actual.Length).ToArray()));

    image.Position = 0;
    using var reader = new IsoReader(image, leaveOpen: true);
    var entry = reader.Entries.Single(static entry => entry.Name.Equals("SPLIT.BIN", StringComparison.OrdinalIgnoreCase));
    Assert.Multiple(() => {
      Assert.That(entry.Size, Is.EqualTo(payload.Length));
      Assert.That(reader.Extract(entry), Is.EqualTo(payload));
    });
  }

  [Test, Category("Driver"), Category("Contract")]
  public void IsoBlockDeviceProvider_UsesNativeFileSectionReader() {
    var writer = new IsoWriter();
    writer.AddFile("HELLO.TXT", "block-device-iso"u8.ToArray());
    using var image = new MemoryStream(writer.Build(), writable: false);
    using var device = new StreamBlockDevice(image, 512, writable: false, leaveOpen: true);
    var adapter = FormatRegistry.GetFilesystemDriver("Iso");
    Assert.That(adapter, Is.AssignableTo<IBlockDeviceFilesystemDriverProvider>());

    var blockProvider = (IBlockDeviceFilesystemDriverProvider)adapter!;
    var profile = blockProvider.ProbeFilesystem(device);
    Assert.That(profile.CanMount, Is.True, string.Join("; ", profile.Limitations));

    using var session = blockProvider.OpenFilesystem(
      device,
      new FilesystemOpenOptions(ReadOnly: true, LeaveOpen: true));
    var node = session.Lookup(session.RootNodeId, "hello.txt");
    Assert.That(node, Is.Not.Null);
    using var handle = session.OpenFile(node!.Value, FileAccess.Read);
    var actual = new byte[32];
    var count = handle.Read(0, actual);
    Assert.That(actual.AsSpan(0, count).ToArray(), Is.EqualTo("block-device-iso"u8.ToArray()));
  }

  [Test, Category("Driver"), Category("Contract")]
  public void UdfBlockDeviceProvider_UsesNativeAllocationDescriptorReader() {
    var writer = new UdfWriter();
    writer.AddFile("DIR/PAYLOAD.BIN", Enumerable.Range(0, 5000).Select(static i => (byte)(i * 11)).ToArray());
    using var image = new MemoryStream();
    writer.WriteTo(image);
    image.Position = 0;
    using var device = new StreamBlockDevice(image, 512, writable: false, leaveOpen: true);
    var adapter = FormatRegistry.GetFilesystemDriver("Udf");
    Assert.That(adapter, Is.AssignableTo<IBlockDeviceFilesystemDriverProvider>());

    var blockProvider = (IBlockDeviceFilesystemDriverProvider)adapter!;
    var profile = blockProvider.ProbeFilesystem(device);
    Assert.That(profile.CanMount, Is.True, string.Join("; ", profile.Limitations));

    using var session = blockProvider.OpenFilesystem(
      device,
      new FilesystemOpenOptions(ReadOnly: true, LeaveOpen: true));
    var directory = session.Lookup(session.RootNodeId, "dir");
    Assert.That(directory, Is.Not.Null);
    var file = session.Lookup(directory!.Value, "payload.bin");
    Assert.That(file, Is.Not.Null);
    using var handle = session.OpenFile(file!.Value, FileAccess.Read);
    var actual = new byte[777];
    Assert.That(handle.Read(2039, actual), Is.EqualTo(actual.Length));
  }

  [Test, Category("Driver"), Category("Contract")]
  public void UdfType1Map_PartitionNumberIsResolvedThroughMapNotAssumedZero() {
    var writer = new UdfWriter();
    writer.AddFile("MAPPED.TXT", "partition-map"u8.ToArray());
    using var output = new MemoryStream();
    writer.WriteTo(output);
    var bytes = output.ToArray();

    var (partitionDescriptor, logicalVolumeDescriptor) = FindUdfVolumeDescriptors(bytes);
    const ushort nonZeroPartitionNumber = 37;
    BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(partitionDescriptor + 22), nonZeroPartitionNumber);
    Assert.That(bytes[logicalVolumeDescriptor + 440], Is.EqualTo(1), "writer fixture should use an ECMA-167 Type 1 partition map");
    Assert.That(bytes[logicalVolumeDescriptor + 441], Is.EqualTo(6));
    BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(logicalVolumeDescriptor + 444), nonZeroPartitionNumber);

    using var image = new MemoryStream(bytes, writable: false);
    using var reader = new UdfReader(image, leaveOpen: true);
    var entry = reader.Entries.Single(static entry => entry.Name == "MAPPED.TXT");
    Assert.That(reader.Extract(entry), Is.EqualTo("partition-map"u8.ToArray()));
  }

  [Test, Category("Driver"), Category("Corruption")]
  public void UdfReferencedType2PartitionMap_FailsClosedInsteadOfUsingPhysicalPartitionDirectly() {
    var writer = new UdfWriter();
    writer.AddFile("A.TXT", "abc"u8.ToArray());
    using var output = new MemoryStream();
    writer.WriteTo(output);
    var bytes = output.ToArray();
    var (_, logicalVolumeDescriptor) = FindUdfVolumeDescriptors(bytes);

    // Retype the sole map as Type 2 while retaining its compact body. The map is
    // intentionally malformed as Type 2; the relevant contract is that a referenced
    // non-Type-1 map is never silently interpreted as the physical partition.
    bytes[logicalVolumeDescriptor + 440] = 2;

    using var image = new MemoryStream(bytes, writable: false);
    var profile = new UdfFilesystemDriverAdapter().ProbeFilesystem(image);
    Assert.Multiple(() => {
      Assert.That(profile.CanMount, Is.False);
      Assert.That(profile.Limitations.Any(static text => text.Contains("Type 2", StringComparison.OrdinalIgnoreCase)), Is.True,
        string.Join("; ", profile.Limitations));
    });
  }

  private static void RewriteJolietFileAsTwoExtents(byte[] image, string wantedName, int totalLength, int firstLength) {
    if (firstLength <= 0 || firstLength >= totalLength || firstLength % IsoSectorSize != 0)
      throw new ArgumentOutOfRangeException(nameof(firstLength));

    var rootOffset = FindJolietRootDirectoryOffset(image);
    var recordOffset = FindJolietDirectoryRecord(image, rootOffset, wantedName);
    var recordLength = image[recordOffset];
    var secondOffset = recordOffset + recordLength;
    if (secondOffset + recordLength > rootOffset + IsoSectorSize || image[secondOffset] != 0)
      throw new AssertionException("fixture has no room for a second multi-extent directory record");

    var originalLba = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(recordOffset + 2));
    image.AsSpan(recordOffset, recordLength).CopyTo(image.AsSpan(secondOffset, recordLength));

    WriteBothEndianUInt32(image.AsSpan(recordOffset + 10, 8), checked((uint)firstLength));
    image[recordOffset + 25] |= 0x80;

    var secondLba = checked(originalLba + (uint)(firstLength / IsoSectorSize));
    WriteBothEndianUInt32(image.AsSpan(secondOffset + 2, 8), secondLba);
    WriteBothEndianUInt32(image.AsSpan(secondOffset + 10, 8), checked((uint)(totalLength - firstLength)));
    image[secondOffset + 25] &= 0x7F;
  }

  private static int FindJolietRootDirectoryOffset(byte[] image) {
    for (var sector = 16; sector < 64; ++sector) {
      var offset = sector * IsoSectorSize;
      if (offset + IsoSectorSize > image.Length) break;
      if (image[offset] == 0xFF) break;
      if (image[offset] != 2 || Encoding.ASCII.GetString(image, offset + 1, 5) != "CD001") continue;
      if (image[offset + 88] != 0x25 || image[offset + 89] != 0x2F || image[offset + 90] is not (0x40 or 0x43 or 0x45)) continue;
      var rootRecord = offset + 156;
      var lba = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(rootRecord + 2));
      var extendedAttributeBlocks = image[rootRecord + 1];
      return checked((int)((lba + extendedAttributeBlocks) * IsoSectorSize));
    }
    throw new AssertionException("Joliet supplementary volume descriptor was not found");
  }

  private static int FindJolietDirectoryRecord(byte[] image, int rootOffset, string wantedName) {
    for (var offset = rootOffset; offset < rootOffset + IsoSectorSize;) {
      var length = image[offset];
      if (length == 0) break;
      var nameLength = image[offset + 32];
      if (nameLength == 1 && image[offset + 33] is 0 or 1) {
        offset += length;
        continue;
      }
      var name = Encoding.BigEndianUnicode.GetString(image, offset + 33, nameLength);
      var semicolon = name.IndexOf(';');
      if (semicolon >= 0) name = name[..semicolon];
      if (string.Equals(name.TrimEnd('.'), wantedName, StringComparison.OrdinalIgnoreCase))
        return offset;
      offset += length;
    }
    throw new AssertionException($"Joliet directory record '{wantedName}' was not found");
  }

  private static void WriteBothEndianUInt32(Span<byte> destination, uint value) {
    BinaryPrimitives.WriteUInt32LittleEndian(destination, value);
    BinaryPrimitives.WriteUInt32BigEndian(destination[4..], value);
  }

  private static (int PartitionDescriptor, int LogicalVolumeDescriptor) FindUdfVolumeDescriptors(byte[] image) {
    const int blockSize = 2048;
    var anchor = 256 * blockSize;
    var sequenceLength = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(anchor + 16));
    var sequenceLocation = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(anchor + 20));
    var partitionDescriptor = -1;
    var logicalVolumeDescriptor = -1;
    var count = checked((int)(sequenceLength / blockSize));
    for (var i = 0; i < count; ++i) {
      var offset = checked((int)(sequenceLocation + (uint)i) * blockSize);
      var tag = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(offset));
      if (tag == 5) partitionDescriptor = offset;
      if (tag == 6) logicalVolumeDescriptor = offset;
      if (tag == 8) break;
    }
    if (partitionDescriptor < 0 || logicalVolumeDescriptor < 0)
      throw new AssertionException("UDF writer fixture did not emit PD/LVD descriptors");
    return (partitionDescriptor, logicalVolumeDescriptor);
  }
}
