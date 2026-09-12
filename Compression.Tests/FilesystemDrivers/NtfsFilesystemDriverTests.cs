using System.Buffers.Binary;
using System.Text;
using Compression.Lib;
using Compression.Registry;
using FileSystem.Ntfs;

namespace Compression.Tests.FilesystemDrivers;

[TestFixture]
public sealed class NtfsFilesystemDriverTests {
  [OneTimeSetUp]
  public void Init() => FormatRegistration.EnsureInitialized();

  [Test]
  public void RegistryUsesNativeNtfsSidecar() {
    var coverage = FormatRegistry.GetFilesystemDriverCoverage("Ntfs");
    Assert.That(coverage.Binding, Is.EqualTo(FilesystemDriverBindingKind.SidecarNative));
    Assert.That(coverage.HasExtentMap, Is.True);
    Assert.That(coverage.HasBlockMover, Is.True);
  }

  [Test]
  public void NativeSessionUsesCompleteMftFileReferenceAndSupportsPositionalReads() {
    var payload = Enumerable.Range(0, 200 * 1024).Select(i => (byte)(i * 29 + 7)).ToArray();
    var writer = new NtfsWriter();
    writer.AddFile("dir/data.bin", payload);
    var image = writer.Build(8 * 1024 * 1024);

    using var stream = new MemoryStream(image, writable: false);
    var profile = FormatRegistry.ProbeFilesystem("Ntfs", stream);
    Assert.That(profile.CanMount, Is.True, string.Join("; ", profile.Limitations));
    Assert.That(profile.CanMountWritable, Is.False);

    stream.Position = 0;
    using var session = FormatRegistry.OpenFilesystem(
      "Ntfs", stream, new FilesystemOpenOptions(ReadOnly: true, LeaveOpen: true));

    var dir = session.Lookup(session.RootNodeId, "dir");
    Assert.That(dir, Is.Not.Null);
    var file = session.Lookup(dir!.Value, "data.bin");
    Assert.That(file, Is.Not.Null);
    Assert.Multiple(() => {
      Assert.That(file!.Value.Value, Is.GreaterThan(15), "user object id should be its non-reserved MFT record");
      Assert.That(session.RootNodeId.Generation, Is.EqualTo(ReadMftSequence(image, session.RootNodeId.Value)));
      Assert.That(dir.Value.Generation, Is.EqualTo(ReadMftSequence(image, dir.Value.Value)));
      Assert.That(file.Value.Generation, Is.EqualTo(ReadMftSequence(image, file.Value.Value)));
      Assert.That(file.Value.Generation, Is.Not.Zero,
        "the MFT sequence number is the stale-file-reference generation, not an optional decoration");
    });

    using var handle = session.OpenFile(file.Value, FileAccess.Read);
    var slice = new byte[2049];
    var read = handle.Read(73_333, slice);
    Assert.That(read, Is.EqualTo(slice.Length));
    Assert.That(slice, Is.EqualTo(payload.AsSpan(73_333, slice.Length).ToArray()));
  }

  [Test]
  public void ProbeRejectsStaleFileNameParentReference() {
    var writer = new NtfsWriter();
    writer.AddFile("dir/data.bin", "payload"u8.ToArray());
    var image = writer.Build(8 * 1024 * 1024);

    var parentReferenceOffset = FindFileNameParentReference(image, "data.bin");
    var originalSequence = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(parentReferenceOffset + 6, 2));
    var staleSequence = originalSequence == ushort.MaxValue ? (ushort)1 : (ushort)(originalSequence + 1);
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(parentReferenceOffset + 6, 2), staleSequence);

    using var stream = new MemoryStream(image, writable: false);
    var profile = new NtfsFilesystemDriverAdapter().ProbeFilesystem(stream);

    Assert.Multiple(() => {
      Assert.That(profile.CanMount, Is.False);
      Assert.That(profile.Limitations.Any(static text => text.Contains("stale", StringComparison.OrdinalIgnoreCase)), Is.True,
        string.Join("; ", profile.Limitations));
    });
  }

  [Test]
  public void ReadinessClaimsNativeStableIdentityOnlyAfterSequenceValidation() {
    var writer = new NtfsWriter();
    writer.AddFile("x.txt", "x"u8.ToArray());
    using var stream = new MemoryStream(writer.Build(), writable: false);

    var report = new NtfsFilesystemDriverAdapter().DescribeFilesystemDriverReadiness(
      stream,
      FilesystemDriverTarget.ReadOnly);

    Assert.Multiple(() => {
      Assert.That(report.Derivable, Is.True, string.Join("; ", report.Blockers));
      Assert.That(report.AvailableLayers.HasFlag(FilesystemDriverReadinessLayer.NativeStableNodeIds), Is.True);
    });
  }

  [Test]
  public void WritableMountStaysFailClosed() {
    var writer = new NtfsWriter();
    writer.AddFile("x.txt", "x"u8.ToArray());
    using var stream = new MemoryStream(writer.Build(), writable: true);

    Assert.Throws<NotSupportedException>(() =>
      FormatRegistry.OpenFilesystem(
        "Ntfs", stream, new FilesystemOpenOptions(ReadOnly: false, LeaveOpen: true)));
  }

  private static ushort ReadMftSequence(byte[] image, ulong recordNumber) {
    var (mftOffset, recordSize, _) = ReadWriterGeometry(image);
    var offset = checked(mftOffset + (long)recordNumber * recordSize);
    if (offset < 0 || offset > image.LongLength - recordSize)
      throw new InvalidDataException($"MFT record {recordNumber} is outside the writer image.");
    return BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(checked((int)offset + 16), 2));
  }

  private static int FindFileNameParentReference(byte[] image, string leafName) {
    var (mftOffset, recordSize, bytesPerSector) = ReadWriterGeometry(image);
    var maxRecords = Math.Min(128, checked((image.Length - (int)mftOffset) / recordSize));
    for (var recordNumber = 16; recordNumber < maxRecords; ++recordNumber) {
      var physical = checked((int)(mftOffset + (long)recordNumber * recordSize));
      var record = image.AsSpan(physical, recordSize).ToArray();
      if (!record.AsSpan(0, 4).SequenceEqual("FILE"u8))
        continue;
      RestoreUsa(record, bytesPerSector);

      var firstAttribute = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(20));
      var used = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(24)));
      if (firstAttribute < 24 || used > record.Length)
        continue;

      for (var position = (int)firstAttribute; position <= used - 8;) {
        var type = BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(position));
        if (type == 0xFFFFFFFF)
          break;
        var length = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(position + 4)));
        if (length < 24 || position > used - length)
          break;

        if (type == 0x30 && record[position + 8] == 0) {
          var valueLength = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(position + 16)));
          var valueOffset = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(position + 20));
          if (valueLength >= 66 && valueOffset >= 24 && valueOffset <= length - valueLength) {
            var value = record.AsSpan(position + valueOffset, valueLength);
            var nameLength = value[64];
            if (66 + nameLength * 2 <= value.Length) {
              var name = Encoding.Unicode.GetString(value.Slice(66, nameLength * 2));
              if (string.Equals(name, leafName, StringComparison.OrdinalIgnoreCase))
                return checked(physical + position + valueOffset);
            }
          }
        }

        position += length;
      }
    }

    throw new InvalidDataException($"No resident $FILE_NAME named '{leafName}' was found in the writer MFT.");
  }

  private static (long MftOffset, int RecordSize, int BytesPerSector) ReadWriterGeometry(byte[] image) {
    var bytesPerSector = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(11, 2));
    var sectorsPerCluster = image[13];
    var clusterSize = checked(bytesPerSector * sectorsPerCluster);
    var mftCluster = checked((long)BinaryPrimitives.ReadUInt64LittleEndian(image.AsSpan(48, 8)));
    var encodedRecordSize = unchecked((sbyte)image[64]);
    var recordSize = encodedRecordSize < 0
      ? 1 << -encodedRecordSize
      : checked(encodedRecordSize * clusterSize);
    return (checked(mftCluster * clusterSize), recordSize, bytesPerSector);
  }

  private static void RestoreUsa(byte[] record, int bytesPerSector) {
    var usaOffset = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(4));
    var usaCount = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(6));
    if (usaOffset + usaCount * 2 > record.Length)
      throw new InvalidDataException("Test fixture FILE record has invalid USA bounds.");
    for (var sector = 1; sector < usaCount; ++sector) {
      var trailer = checked(sector * bytesPerSector - 2);
      record.AsSpan(usaOffset + sector * 2, 2).CopyTo(record.AsSpan(trailer, 2));
    }
  }
}
