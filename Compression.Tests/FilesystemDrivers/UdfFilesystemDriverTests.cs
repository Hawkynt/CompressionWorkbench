using System.Buffers.Binary;
using Compression.Lib;
using Compression.Registry;
using FileSystem.Udf;

namespace Compression.Tests.FilesystemDrivers;

[TestFixture]
public sealed class UdfFilesystemDriverTests {
  private const int SectorSize = 2048;

  [OneTimeSetUp]
  public void InitializeRegistry() => FormatRegistration.EnsureInitialized();

  [Test, Category("Driver")]
  public void GeneratedSidecar_IsRegisteredForUdf() {
    var driver = FormatRegistry.GetFilesystemDriver("Udf");
    var coverage = FormatRegistry.GetFilesystemDriverCoverage("Udf");

    Assert.Multiple(() => {
      Assert.That(driver, Is.TypeOf<UdfFilesystemDriverAdapter>());
      Assert.That(coverage.Binding, Is.EqualTo(FilesystemDriverBindingKind.SidecarNative));
      Assert.That(coverage.HasExtentMap, Is.True);
      Assert.That(coverage.HasBlockMover, Is.True);
      Assert.That(coverage.HasNativeReadinessProvider, Is.True);
    });
  }

  [Test, Category("Driver")]
  public void NativeUdfSession_ReadsNestedArbitraryOffsets() {
    var payload = Enumerable.Range(0, 10000).Select(static i => (byte)(i * 17 + 3)).ToArray();
    var imageBytes = BuildImage(("docs/api/reference.bin", payload), ("README.TXT", "root"u8.ToArray()));
    using var image = new MemoryStream(imageBytes, writable: false);

    var profile = FormatRegistry.ProbeFilesystem("Udf", image);
    Assert.Multiple(() => {
      Assert.That(profile.CanMount, Is.True, string.Join("; ", profile.Limitations));
      Assert.That(profile.CanMountWritable, Is.False);
      Assert.That(profile.Capabilities.HasFlag(FilesystemDriverCapabilities.RandomAccess), Is.True);
      Assert.That(profile.Capabilities.HasFlag(FilesystemDriverCapabilities.SparseFiles), Is.True);
    });

    using var session = FormatRegistry.OpenFilesystem(
      "Udf",
      image,
      new FilesystemOpenOptions(ReadOnly: true, LeaveOpen: true));

    var docs = session.Lookup(session.RootNodeId, "DOCS");
    Assert.That(docs, Is.Not.Null);
    var api = session.Lookup(docs!.Value, "api");
    Assert.That(api, Is.Not.Null);
    var file = session.Lookup(api!.Value, "REFERENCE.BIN");
    Assert.That(file, Is.Not.Null);

    using var handle = session.OpenFile(file!.Value, FileAccess.Read);
    var middle = new byte[4093];
    Assert.That(handle.Read(1701, middle), Is.EqualTo(middle.Length));
    Assert.That(middle, Is.EqualTo(payload.AsSpan(1701, middle.Length).ToArray()));

    var tail = new byte[100];
    Assert.That(handle.Read(payload.Length - 23, tail), Is.EqualTo(23));
    Assert.That(tail.AsSpan(0, 23).ToArray(), Is.EqualTo(payload[^23..]));
    Assert.That(handle.Read(payload.Length, tail), Is.Zero);
  }

  [Test, Category("Driver"), Category("Contract")]
  public void MultiExtentFile_ReadsAcrossAllocationDescriptors() {
    var payload = Enumerable.Range(0, 6000).Select(static i => (byte)(i * 29 + 11)).ToArray();
    var bytes = BuildImage(("split.bin", payload));
    RewriteSingleFileAsTwoRecordedExtents(bytes, payload.Length, firstLength: SectorSize);
    using var image = new MemoryStream(bytes, writable: false);

    var profile = new UdfFilesystemDriverAdapter().ProbeFilesystem(image);
    Assert.That(profile.CanMount, Is.True, string.Join("; ", profile.Limitations));

    image.Position = 0;
    using var session = new UdfFilesystemDriverAdapter().OpenFilesystem(
      image,
      new FilesystemOpenOptions(ReadOnly: true, LeaveOpen: true));
    var node = session.Lookup(session.RootNodeId, "split.bin");
    Assert.That(node, Is.Not.Null);

    using var handle = session.OpenFile(node!.Value, FileAccess.Read);
    var actual = new byte[3500];
    Assert.That(handle.Read(1500, actual), Is.EqualTo(actual.Length));
    Assert.That(actual, Is.EqualTo(payload.AsSpan(1500, actual.Length).ToArray()));
  }

  [Test, Category("Driver"), Category("Contract")]
  public void SparseExtent_ReadsAsZeroesWithoutTouchingBackingBytes() {
    var payload = Enumerable.Range(0, 5000).Select(static i => (byte)(i * 7 + 5)).ToArray();
    var bytes = BuildImage(("sparse.bin", payload));
    RewriteSingleFileWithSparsePrefix(bytes, payload.Length, sparseLength: SectorSize);
    using var image = new MemoryStream(bytes, writable: false);

    var profile = new UdfFilesystemDriverAdapter().ProbeFilesystem(image);
    Assert.That(profile.CanMount, Is.True, string.Join("; ", profile.Limitations));

    image.Position = 0;
    using var session = new UdfFilesystemDriverAdapter().OpenFilesystem(
      image,
      new FilesystemOpenOptions(ReadOnly: true, LeaveOpen: true));
    var node = session.Lookup(session.RootNodeId, "sparse.bin");
    Assert.That(node, Is.Not.Null);

    using var handle = session.OpenFile(node!.Value, FileAccess.Read);
    var actual = new byte[payload.Length];
    Assert.That(handle.Read(0, actual), Is.EqualTo(actual.Length));
    Assert.Multiple(() => {
      Assert.That(actual.AsSpan(0, SectorSize).ToArray(), Is.EqualTo(new byte[SectorSize]));
      Assert.That(actual.AsSpan(SectorSize).ToArray(), Is.EqualTo(payload[SectorSize..]));
    });
  }

  [Test, Category("Driver"), Category("Contract")]
  public void ExistingExtractorAlsoFollowsMultipleAllocationDescriptors() {
    var payload = Enumerable.Range(0, 7000).Select(static i => (byte)(i * 13 + 1)).ToArray();
    var bytes = BuildImage(("multi.bin", payload));
    RewriteSingleFileAsTwoRecordedExtents(bytes, payload.Length, firstLength: SectorSize * 2);
    using var image = new MemoryStream(bytes, writable: false);
    using var reader = new UdfReader(image, leaveOpen: true);
    var entry = reader.Entries.Single(static entry => entry.Name == "multi.bin");

    Assert.That(reader.Extract(entry), Is.EqualTo(payload));
  }

  [Test, Category("Driver"), Category("Corruption")]
  public void ContinuationAllocationDescriptorFailsMountClosed() {
    var payload = Enumerable.Repeat((byte)0xA5, 6000).ToArray();
    var bytes = BuildImage(("continued.bin", payload));
    var fe = FindRegularFileEntry(bytes, payload.Length);
    var adStart = FileAllocationDescriptorStart(bytes, fe);
    BinaryPrimitives.WriteUInt32LittleEndian(
      bytes.AsSpan(adStart),
      0xC0000000u | (uint)SectorSize);
    using var image = new MemoryStream(bytes, writable: false);

    var profile = new UdfFilesystemDriverAdapter().ProbeFilesystem(image);

    Assert.Multiple(() => {
      Assert.That(profile.CanMount, Is.False);
      Assert.That(profile.Limitations.Any(text => text.Contains("continuation", StringComparison.OrdinalIgnoreCase)), Is.True);
    });
  }

  [Test, Category("Driver"), Category("Contract")]
  public void ProbePreservesCallerStreamPosition() {
    using var image = new MemoryStream(BuildImage(("A.TXT", "abc"u8.ToArray())), writable: false) {
      Position = 777,
    };

    var profile = new UdfFilesystemDriverAdapter().ProbeFilesystem(image);

    Assert.Multiple(() => {
      Assert.That(profile.CanMount, Is.True);
      Assert.That(image.Position, Is.EqualTo(777));
    });
  }

  [Test, Category("Driver"), Category("Contract")]
  public void UdfReadinessSeparatesOfflineEditingFromMountedWrites() {
    using var image = new MemoryStream(BuildImage(("A.TXT", "abc"u8.ToArray())), writable: true);

    var report = FormatRegistry.AssessFilesystemDriver("Udf", image, FilesystemDriverTarget.ReadWrite);

    Assert.Multiple(() => {
      Assert.That(report.UsesNativeProvider, Is.True);
      Assert.That(report.Derivable, Is.False);
      Assert.That(report.AvailableLayers.HasFlag(FilesystemDriverReadinessLayer.AllocationMap), Is.True);
      Assert.That(report.AvailableLayers.HasFlag(FilesystemDriverReadinessLayer.WriteData), Is.False);
      Assert.That(report.Blockers.Any(text => text.Contains("offline", StringComparison.OrdinalIgnoreCase)), Is.True);
    });
  }

  [Test, Category("Driver"), Category("Contract")]
  public void NativeUdfSessionRejectsMutation() {
    using var image = new MemoryStream(BuildImage(("A.TXT", "abc"u8.ToArray())), writable: false);
    using var session = new UdfFilesystemDriverAdapter().OpenFilesystem(
      image,
      new FilesystemOpenOptions(ReadOnly: true, LeaveOpen: true));

    Assert.Throws<NotSupportedException>(() => session.CreateFile(session.RootNodeId, "NEW.TXT"));
    Assert.Throws<NotSupportedException>(() => session.OpenFile(
      session.Lookup(session.RootNodeId, "A.TXT")!.Value,
      FileAccess.Write));
  }

  private static byte[] BuildImage(params (string Name, byte[] Data)[] files) {
    var writer = new UdfWriter();
    foreach (var (name, data) in files)
      writer.AddFile(name, data);
    using var image = new MemoryStream();
    writer.WriteTo(image);
    return image.ToArray();
  }

  private static void RewriteSingleFileAsTwoRecordedExtents(byte[] image, int fileLength, int firstLength) {
    if (firstLength <= 0 || firstLength >= fileLength || firstLength % SectorSize != 0)
      throw new ArgumentOutOfRangeException(nameof(firstLength));

    var fe = FindRegularFileEntry(image, fileLength);
    var adStart = FileAllocationDescriptorStart(image, fe);
    var originalLbn = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(adStart + 4));
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(fe + 172), 16);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(adStart), (uint)firstLength);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(adStart + 4), originalLbn);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(adStart + 8), (uint)(fileLength - firstLength));
    BinaryPrimitives.WriteUInt32LittleEndian(
      image.AsSpan(adStart + 12),
      originalLbn + checked((uint)(firstLength / SectorSize)));
  }

  private static void RewriteSingleFileWithSparsePrefix(byte[] image, int fileLength, int sparseLength) {
    if (sparseLength <= 0 || sparseLength >= fileLength || sparseLength % SectorSize != 0)
      throw new ArgumentOutOfRangeException(nameof(sparseLength));

    var fe = FindRegularFileEntry(image, fileLength);
    var adStart = FileAllocationDescriptorStart(image, fe);
    var originalLbn = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(adStart + 4));
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(fe + 172), 16);
    BinaryPrimitives.WriteUInt32LittleEndian(
      image.AsSpan(adStart),
      0x80000000u | (uint)sparseLength); // extent type 2: unallocated and unrecorded
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(adStart + 4), 0);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(adStart + 8), (uint)(fileLength - sparseLength));
    BinaryPrimitives.WriteUInt32LittleEndian(
      image.AsSpan(adStart + 12),
      originalLbn + checked((uint)(sparseLength / SectorSize)));
  }

  private static int FindRegularFileEntry(byte[] image, int logicalLength) {
    for (var offset = 257 * SectorSize; offset <= image.Length - SectorSize; offset += SectorSize) {
      if (BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(offset)) != 261)
        continue;
      if (image[offset + 27] != 5)
        continue;
      if (BinaryPrimitives.ReadUInt64LittleEndian(image.AsSpan(offset + 56)) != (ulong)logicalLength)
        continue;
      return offset;
    }

    throw new InvalidDataException($"No regular UDF File Entry with logical length {logicalLength} was found.");
  }

  private static int FileAllocationDescriptorStart(byte[] image, int fileEntryOffset) {
    var extendedAttributesLength = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(fileEntryOffset + 168)));
    return checked(fileEntryOffset + 176 + extendedAttributesLength);
  }
}
