#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Registry;
using FileFormat.Cso;

namespace Compression.Tests.Cso;

[TestFixture]
public class CsoInPlaceModifyTests {
  private const int BlockSize = 2048;

  [TestCase(null, TestName = "WriteBlock_CsoV1")]
  [TestCase("zso", TestName = "WriteBlock_Zso")]
  [TestCase("cso2", TestName = "WriteBlock_CsoV2")]
  [Category("RoundTrip")]
  public void WriteBlock_RoundTripsModifiedAndUnmodifiedBlocks(string? variant) {
    var payload = BuildPayload(3 * BlockSize, 7);
    var image = CreateImage(payload, variant);
    var replacement = new byte[BlockSize];
    new Random(1234).NextBytes(replacement);

    using var stream = Writable(image);
    CsoInPlaceModifier.WriteBlock(stream, 1, replacement);

    var expected = payload.ToArray();
    replacement.CopyTo(expected, BlockSize);
    Assert.That(ReadLogicalPayload(stream.ToArray()), Is.EqualTo(expected));
  }

  [Test, Category("Compatibility")]
  public void WriteBlock_NonZeroIndexShift_CanonicalizesAndPreservesContent() {
    var payload = BuildPayload(2 * BlockSize, 29);
    var image = BuildShiftedStoredImage(payload);
    var replacement = Enumerable.Repeat((byte)0xA5, BlockSize).ToArray();

    using var stream = Writable(image);
    CsoInPlaceModifier.WriteBlock(stream, 0, replacement);
    var after = stream.ToArray();

    Assert.That(after[21], Is.Zero, "transactional repack should canonicalize index_shift to zero");
    var expected = payload.ToArray();
    replacement.CopyTo(expected, 0);
    Assert.That(ReadLogicalPayload(after), Is.EqualTo(expected));
  }

  [Test, Category("RoundTrip")]
  public void Descriptor_Add_ReplacesSeveralBlocksInOneOperation() {
    var payload = BuildPayload(4 * BlockSize, 3);
    var image = CreateImage(payload, "zso");
    var block0 = Enumerable.Repeat((byte)0x11, BlockSize).ToArray();
    var block3 = Enumerable.Repeat((byte)0xCC, BlockSize).ToArray();

    using var stream = Writable(image);
    IArchiveModifiable modifier = new CsoFormatDescriptor();
    modifier.Add(stream, [
      ArchiveInputInfo.InMemory("blocks/block_00000.bin", block0),
      ArchiveInputInfo.InMemory("blocks/block_00003.bin", block3),
    ]);

    var expected = payload.ToArray();
    block0.CopyTo(expected, 0);
    block3.CopyTo(expected, 3 * BlockSize);
    Assert.That(ReadLogicalPayload(stream.ToArray()), Is.EqualTo(expected));
  }

  [Test, Category("ErrorHandling")]
  public void WriteBlock_BlockIndexOutOfRange_Throws() {
    using var stream = Writable(CreateImage(BuildPayload(2 * BlockSize, 9)));
    Assert.That(
      () => CsoInPlaceModifier.WriteBlock(stream, 99, new byte[BlockSize]),
      Throws.InstanceOf<ArgumentOutOfRangeException>());
  }

  [Test, Category("ErrorHandling")]
  public void WriteBlock_WrongPayloadSize_Throws() {
    using var stream = Writable(CreateImage(BuildPayload(2 * BlockSize, 9)));
    Assert.That(
      () => CsoInPlaceModifier.WriteBlock(stream, 0, new byte[BlockSize - 1]),
      Throws.InstanceOf<ArgumentException>());
  }

  [Test, Category("Capabilities")]
  public void Descriptor_AdvertisesReadWriteAndMaintenanceInterfaces() {
    var descriptor = new CsoFormatDescriptor();
    Assert.That(descriptor.Capabilities.HasFlag(FormatCapabilities.CanCreate), Is.True);
    Assert.That(descriptor.Capabilities.HasFlag(FormatCapabilities.CanModify), Is.True);
    Assert.That(descriptor, Is.InstanceOf<IArchiveCreatable>());
    Assert.That(descriptor, Is.InstanceOf<IArchiveModifiable>());
    Assert.That(descriptor, Is.InstanceOf<IArchiveDefragmentable>());
    Assert.That(descriptor, Is.InstanceOf<IWipeEmpty>());
    Assert.That(descriptor, Is.InstanceOf<IArchiveShrinkable>());
    Assert.That(descriptor, Is.InstanceOf<IArchiveLayoutMap>());
    Assert.That(descriptor, Is.InstanceOf<ILayoutOptimizable>());
    Assert.That(descriptor, Is.InstanceOf<IArchivePurgeable>());
  }

  [Test, Category("Maintenance")]
  public void Defragment_DropsUnindexedTailAndPreservesLogicalIso() {
    var payload = BuildPayload(3 * BlockSize, 41);
    var image = CreateImage(payload, "cso2");
    using var stream = Writable(AppendGarbage(image, 193));
    var oldLength = stream.Length;

    ((IArchiveDefragmentable)new CsoFormatDescriptor()).Defragment(stream);

    Assert.That(stream.Length, Is.LessThan(oldLength));
    Assert.That(ReadLogicalPayload(stream.ToArray()), Is.EqualTo(payload));
    Assert.That(stream.ToArray()[21], Is.Zero);
  }

  [Test, Category("Maintenance")]
  public void WipeUnusedSpace_ZeroesCanonicalizedTailWithoutChangingLength() {
    var payload = BuildPayload(2 * BlockSize, 55);
    var core = CreateImage(payload, "zso");
    using var stream = Writable(AppendGarbage(core, 257));
    var originalLength = stream.Length;

    var wiped = ((IWipeEmpty)new CsoFormatDescriptor()).WipeUnusedSpace(stream);

    Assert.That(wiped, Is.EqualTo(257));
    Assert.That(stream.Length, Is.EqualTo(originalLength));
    Assert.That(stream.ToArray().AsSpan(core.Length).ToArray(), Is.All.Zero);
    Assert.That(ReadLogicalPayload(stream.ToArray()), Is.EqualTo(payload));
  }

  [Test, Category("Maintenance")]
  public void Shrink_DropsUnindexedTailAndNeverGrows() {
    var payload = BuildPayload(2 * BlockSize, 71);
    var sourceBytes = AppendGarbage(CreateImage(payload), 311);
    using var source = new MemoryStream(sourceBytes, writable: false);
    using var target = new MemoryStream();

    ((IArchiveShrinkable)new CsoFormatDescriptor()).Shrink(source, target);

    Assert.That(target.Length, Is.LessThan(sourceBytes.Length));
    Assert.That(ReadLogicalPayload(target.ToArray()), Is.EqualTo(payload));
  }

  [Test, Category("Maintenance")]
  public void RebuildStreaming_ReblocksWithoutChangingLogicalIso() {
    var payload = BuildPayload(5 * BlockSize, 87);
    var sourceBytes = CreateImage(payload, "cso2");
    using var source = new MemoryStream(sourceBytes, writable: false);
    using var target = new MemoryStream();

    ((ILayoutOptimizable)new CsoFormatDescriptor()).RebuildStreaming(
      source, target, new LayoutRebuildOptions { UnitSize = 4096 });

    var rebuilt = target.ToArray();
    Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(rebuilt.AsSpan(16, 4)), Is.EqualTo(4096));
    Assert.That(ReadLogicalPayload(rebuilt), Is.EqualTo(payload));
  }

  [Test, Category("Maintenance")]
  public void LayoutMap_DeclaresOnlyTrailingUnindexedBytesFree() {
    var image = AppendGarbage(CreateImage(BuildPayload(BlockSize, 101)), 64);
    using var stream = new MemoryStream(image, writable: false);

    var layout = ((IArchiveLayoutMap)new CsoFormatDescriptor()).EnumerateLayout(stream).ToArray();

    Assert.That(layout[0].Kind, Is.EqualTo(DefragBlockKind.MetadataReserved));
    Assert.That(layout.Any(e => e.Kind == DefragBlockKind.Used), Is.True);
    var free = layout.Single(e => e.Kind == DefragBlockKind.Free);
    Assert.That(free.Length, Is.EqualTo(64));
    Assert.That(free.Offset + free.Length, Is.EqualTo(image.Length));
  }

  [Test, Category("Maintenance")]
  public void Purge_LeavesValidEmptyContainerOfSameVariant() {
    using var stream = Writable(CreateImage(BuildPayload(2 * BlockSize, 111), "zso"));

    ((IArchivePurgeable)new CsoFormatDescriptor()).Purge(stream);

    var bytes = stream.ToArray();
    Assert.That(System.Text.Encoding.ASCII.GetString(bytes, 0, 4), Is.EqualTo("ZISO"));
    Assert.That(BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(8, 8)), Is.Zero);
    stream.Position = 0;
    var entries = new CsoFormatDescriptor().List(stream, null);
    Assert.That(entries.Count(e => e.Name.StartsWith("blocks/block_", StringComparison.Ordinal)), Is.Zero);
  }

  private static MemoryStream Writable(byte[] bytes) {
    var stream = new MemoryStream();
    stream.Write(bytes);
    stream.Position = 0;
    return stream;
  }

  private static byte[] CreateImage(byte[] payload, string? variant = null) {
    var creator = (IArchiveCreatable)new CsoFormatDescriptor();
    using var stream = new MemoryStream();
    creator.Create(stream, [ArchiveInputInfo.InMemory("disc.iso", payload)], new FormatCreateOptions(variant));
    return stream.ToArray();
  }

  private static byte[] ReadLogicalPayload(byte[] image) {
    var descriptor = new CsoFormatDescriptor();
    using var stream = new MemoryStream(image, writable: false);
    var names = descriptor.List(stream, null)
      .Where(e => !e.IsDirectory && e.Name.StartsWith("blocks/block_", StringComparison.Ordinal))
      .OrderBy(e => e.Index)
      .Select(e => e.Name)
      .ToArray();
    using var result = new MemoryStream();
    foreach (var name in names) {
      stream.Position = 0;
      result.Write(((IArchiveFormatOperations)descriptor).ExtractEntryToMemory(stream, name, null));
    }
    return result.ToArray();
  }

  private static byte[] AppendGarbage(byte[] image, int count) {
    var result = new byte[image.Length + count];
    image.CopyTo(result, 0);
    result.AsSpan(image.Length).Fill(0xD7);
    return result;
  }

  private static byte[] BuildShiftedStoredImage(byte[] payload) {
    Assert.That(payload.Length, Is.EqualTo(2 * BlockSize));
    const byte shift = 1;
    const int indexEnd = 24 + 3 * 4;
    const int dataStart = 40; // even and deliberately padded beyond the index table.
    var result = new byte[dataStart + payload.Length];
    "CISO"u8.CopyTo(result);
    BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(4, 4), 24);
    BinaryPrimitives.WriteUInt64LittleEndian(result.AsSpan(8, 8), checked((ulong)payload.Length));
    BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(16, 4), BlockSize);
    result[20] = 1;
    result[21] = shift;

    var first = checked((uint)(dataStart >> shift)) | 0x8000_0000u;
    var second = checked((uint)((dataStart + BlockSize) >> shift)) | 0x8000_0000u;
    var end = checked((uint)((dataStart + payload.Length) >> shift));
    BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(24, 4), first);
    BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(28, 4), second);
    BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(32, 4), end);
    Assert.That(indexEnd, Is.EqualTo(36));
    payload.CopyTo(result, dataStart);
    return result;
  }

  private static byte[] BuildPayload(int length, byte seed) {
    var data = new byte[length];
    for (var i = 0; i < data.Length; ++i)
      data[i] = (byte)(seed + i * 13 + i / 97);
    return data;
  }
}
