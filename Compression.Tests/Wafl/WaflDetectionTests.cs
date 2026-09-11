using System.Buffers.Binary;
using System.Text;
using Compression.Registry;
using FileSystem.Wafl;

namespace Compression.Tests.Wafl;

/// <summary>
/// Acceptance gates for the deliberately conservative WAFL reader. Detection is
/// grounded in NetApp's documented volinfo magic/VBNs; Stage 1 additionally
/// follows the disclosed classic 32-bit volinfo-to-fsinfo lookup-table relation.
/// </summary>
[TestFixture]
public class WaflDetectionTests {
  private const int BlockSize = 4096;
  private const int DefaultMagicOffset = 20;
  private const int VbnArrayOffset = 256;
  private const uint VolInfoMagic = 0xDAB8FBAB;
  private const uint SyntheticFsInfoMagic = 0xF51F0001;
  private const uint SyntheticFsInfoVersionTag = 0x80000004;

  private static byte[] BuildMinimal(
      uint version = 0x100,
      int payloadLen = 128,
      int magicOffset = DefaultMagicOffset,
      bool littleEndian = false,
      bool firstCopyValid = true,
      bool secondCopyValid = true) {
    var image = new byte[BlockSize * 3 + payloadLen];
    if (firstCopyValid) WriteVolInfo(image.AsSpan(BlockSize, BlockSize), version, magicOffset, littleEndian);
    if (secondCopyValid) WriteVolInfo(image.AsSpan(BlockSize * 2, BlockSize), version, magicOffset, littleEndian);
    for (var i = 0; i < payloadLen; ++i) image[BlockSize * 3 + i] = (byte)(i & 0xFF);
    return image;
  }

  /// <summary>
  /// Patent-shaped structural fixture, not a claim to reproduce any particular
  /// ONTAP release byte-for-byte. It uses only relationships disclosed publicly:
  /// volinfo carries the fsinfo compatibility magic and a 32-bit VBN table whose
  /// first entry references the active fsinfo block.
  /// </summary>
  private static byte[] BuildStructural(
      bool littleEndian = false,
      uint firstActiveFsInfoVbn = 4,
      uint secondActiveFsInfoVbn = 4,
      bool secondTable = false) {
    var image = new byte[BlockSize * 8];
    WriteStructuralVolInfo(image.AsSpan(BlockSize, BlockSize), 4, firstActiveFsInfoVbn, littleEndian, secondTable);
    WriteStructuralVolInfo(image.AsSpan(BlockSize * 2, BlockSize), 4, secondActiveFsInfoVbn, littleEndian, secondTable);

    foreach (var vbn in new uint[] { 4, 5, 6 })
      WriteFsInfo(image.AsSpan(checked((int)vbn * BlockSize), BlockSize), littleEndian, vbn);

    return image;
  }

  private static void WriteVolInfo(Span<byte> block, uint version, int magicOffset, bool littleEndian) {
    WriteUInt32(block.Slice(magicOffset, sizeof(uint)), VolInfoMagic, littleEndian);
    WriteUInt32(block.Slice(magicOffset + sizeof(uint), sizeof(uint)), version, littleEndian);
  }

  private static void WriteStructuralVolInfo(
      Span<byte> block,
      uint version,
      uint activeFsInfoVbn,
      bool littleEndian,
      bool secondTable) {
    WriteUInt32(block[..4], SyntheticFsInfoMagic, littleEndian);
    WriteUInt32(block.Slice(4, 4), SyntheticFsInfoVersionTag, littleEndian);
    WriteVolInfo(block, version, DefaultMagicOffset, littleEndian);

    WriteUInt32(block.Slice(VbnArrayOffset, 4), activeFsInfoVbn, littleEndian);
    WriteUInt32(block.Slice(VbnArrayOffset + 4, 4), 6, littleEndian); // one synthetic snapshot root
    WriteUInt32(block.Slice(VbnArrayOffset + 8, 4), 0, littleEndian);

    if (!secondTable) return;
    WriteUInt32(block.Slice(512, 4), 5, littleEndian);
    WriteUInt32(block.Slice(516, 4), 0, littleEndian);
  }

  private static void WriteFsInfo(Span<byte> block, bool littleEndian, uint marker) {
    WriteUInt32(block[..4], SyntheticFsInfoMagic, littleEndian);
    WriteUInt32(block.Slice(4, 4), 4, littleEndian);
    WriteUInt32(block.Slice(8, 4), marker, littleEndian);
  }

  private static void WriteUInt32(Span<byte> target, uint value, bool littleEndian) {
    if (littleEndian)
      BinaryPrimitives.WriteUInt32LittleEndian(target, value);
    else
      BinaryPrimitives.WriteUInt32BigEndian(target, value);
  }

  [Test, Category("HappyPath")]
  public void Descriptor_DoesNotPublishTheFormerInventedFixedSignature() {
    var descriptor = new WaflFormatDescriptor();
    Assert.Multiple(() => {
      Assert.That(descriptor.Id, Is.EqualTo("Wafl"));
      Assert.That(descriptor.Extensions, Does.Contain(".wafl"));
      Assert.That(descriptor.MagicSignatures, Is.Empty,
        "The volinfo magic is documented, but its byte offset inside all ONTAP volinfo generations is not a stable published detector contract.");
    });
  }

  [Test, Category("HappyPath")]
  public void Reader_RecognizesDocumentedVolInfoCopies_AndParsesFollowingVersion() {
    var image = BuildMinimal(version: 0x300, payloadLen: 512, magicOffset: 124);
    using var stream = new MemoryStream(image, writable: false);
    using var reader = new WaflReader(stream);

    Assert.Multiple(() => {
      Assert.That(reader.ValidHeader, Is.True);
      Assert.That(reader.Version, Is.EqualTo(0x300u));
      Assert.That(reader.Stage, Is.Zero);
    });

    var volume = reader.Entries.Single(entry => entry.Name == "wafl-volume.bin");
    Assert.That(volume.Size, Is.EqualTo(image.Length));
    Assert.That(volume.Data, Is.Empty, "The raw image pseudo-entry must not duplicate a potentially huge WAFL image in RAM.");
    Assert.That(reader.Extract(volume), Is.EqualTo(image));
  }

  [Test, Category("HappyPath")]
  public void Reader_AcceptsLittleEndianVolInfoRepresentation() {
    using var stream = new MemoryStream(BuildMinimal(version: 0x10203040, littleEndian: true));
    using var reader = new WaflReader(stream);
    Assert.That(reader.Version, Is.EqualTo(0x10203040u));
  }

  [Test, Category("HappyPath")]
  public void Reader_CanRecoverDetectionFromSecondVolInfoCopy() {
    using var stream = new MemoryStream(BuildMinimal(version: 7, firstCopyValid: false));
    using var reader = new WaflReader(stream);
    Assert.Multiple(() => {
      Assert.That(reader.ValidHeader, Is.True);
      Assert.That(reader.Version, Is.EqualTo(7u));
    });

    var metadata = Encoding.UTF8.GetString(reader.Entries.Single(entry => entry.Name == "metadata.ini").Data);
    Assert.Multiple(() => {
      Assert.That(metadata, Does.Contain("volinfo_valid_copies=1"));
      Assert.That(metadata, Does.Contain("volinfo_vbn1=invalid"));
      Assert.That(metadata, Does.Contain("volinfo_vbn2=valid"));
    });
  }

  [Test, Category("HappyPath")]
  public void Reader_Stage1TraversesVolInfoToVerifiedFsInfoBlocks() {
    var image = BuildStructural();
    using var stream = new MemoryStream(image, writable: false);
    using var reader = new WaflReader(stream);

    Assert.Multiple(() => {
      Assert.That(reader.Stage, Is.EqualTo(1));
      Assert.That(reader.ActiveFsInfoVbn, Is.EqualTo(4u));
      Assert.That(reader.FsInfoVbns, Is.EqualTo(new uint[] { 4, 6 }));
    });

    var names = reader.Entries.Select(entry => entry.Name).ToArray();
    Assert.Multiple(() => {
      Assert.That(names, Does.Contain("fsinfo/vbn-4.bin"));
      Assert.That(names, Does.Contain("fsinfo/vbn-6.bin"));
    });

    var active = reader.Entries.Single(entry => entry.Name == "fsinfo/vbn-4.bin");
    Assert.That(BinaryPrimitives.ReadUInt32BigEndian(active.Data.AsSpan(0, 4)), Is.EqualTo(SyntheticFsInfoMagic));
  }

  [Test, Category("HappyPath")]
  public void Reader_Stage1WorksWithLittleEndianClassicLookupTable() {
    using var stream = new MemoryStream(BuildStructural(littleEndian: true), writable: false);
    using var reader = new WaflReader(stream);

    Assert.Multiple(() => {
      Assert.That(reader.Stage, Is.EqualTo(1));
      Assert.That(reader.ActiveFsInfoVbn, Is.EqualTo(4u));
      Assert.That(reader.FsInfoVbns, Is.EqualTo(new uint[] { 4, 6 }));
    });
  }

  [Test, Category("HappyPath")]
  public void Reader_Stage1ReportsBothRootsWithoutInventingConsensus() {
    using var stream = new MemoryStream(BuildStructural(firstActiveFsInfoVbn: 4, secondActiveFsInfoVbn: 5));
    using var reader = new WaflReader(stream);

    Assert.Multiple(() => {
      Assert.That(reader.Stage, Is.EqualTo(1));
      Assert.That(reader.ActiveFsInfoVbn, Is.Null);
      Assert.That(reader.FsInfoVbns, Is.EqualTo(new uint[] { 4, 5, 6 }));
    });

    var metadata = Encoding.UTF8.GetString(reader.Entries.Single(entry => entry.Name == "metadata.ini").Data);
    Assert.Multiple(() => {
      Assert.That(metadata, Does.Contain("volinfo_vbn1_active_fsinfo_vbn=4"));
      Assert.That(metadata, Does.Contain("volinfo_vbn2_active_fsinfo_vbn=5"));
      Assert.That(metadata, Does.Contain("active_fsinfo_vbn=unknown"));
    });
  }

  [Test, Category("Sad")]
  public void Reader_AmbiguousFsInfoTableFailsClosedAtStage0() {
    using var stream = new MemoryStream(BuildStructural(secondTable: true));
    using var reader = new WaflReader(stream);

    Assert.Multiple(() => {
      Assert.That(reader.ValidHeader, Is.True);
      Assert.That(reader.Stage, Is.Zero);
      Assert.That(reader.ActiveFsInfoVbn, Is.Null);
      Assert.That(reader.FsInfoVbns, Is.Empty);
    });

    var metadata = Encoding.UTF8.GetString(reader.Entries.Single(entry => entry.Name == "metadata.ini").Data);
    Assert.That(metadata, Does.Contain("fsinfo_table=ambiguous"));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_OpenEntryReadsVerifiedFsInfoPseudoEntry() {
    var image = BuildStructural();
    var descriptor = (IArchiveFormatOperations)new WaflFormatDescriptor();
    using var archive = new MemoryStream(image, writable: false);
    using var entry = descriptor.OpenEntry(archive, "fsinfo/vbn-4.bin", password: null);

    var data = new byte[BlockSize];
    entry.ReadExactly(data);
    Assert.That(BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(0, 4)), Is.EqualTo(SyntheticFsInfoMagic));
    Assert.That(entry.ReadByte(), Is.EqualTo(-1));
  }

  [Test, Category("HappyPath")]
  public void List_ReturnsMetadataAndOpaqueVolumeForStage0Image() {
    var descriptor = new WaflFormatDescriptor();
    using var stream = new MemoryStream(BuildMinimal(version: 0x200, payloadLen: 256));
    var entries = descriptor.List(stream, password: null);
    Assert.That(entries.Select(entry => entry.Name), Is.EquivalentTo(new[] { "metadata.ini", "wafl-volume.bin" }));
  }

  [Test, Category("HappyPath")]
  public void OpenEntry_StreamsOpaqueVolumeWithoutSecondFullImageAllocation() {
    var image = BuildMinimal(version: 0x400, payloadLen: 1024);
    var descriptor = (IArchiveFormatOperations)new WaflFormatDescriptor();
    using var archive = new MemoryStream(image, writable: false);
    using var entry = descriptor.OpenEntry(archive, "wafl-volume.bin", password: null);

    Assert.Multiple(() => {
      Assert.That(entry.CanRead, Is.True);
      Assert.That(entry.CanSeek, Is.True);
      Assert.That(entry.Length, Is.EqualTo(image.Length));
    });

    var roundTrip = new byte[image.Length];
    entry.ReadExactly(roundTrip);
    Assert.That(roundTrip, Is.EqualTo(image));
  }

  [Test, Category("Sad")]
  public void Reader_RejectsFormerAsciiWafdPseudoSignature() {
    var image = new byte[BlockSize * 3];
    "wafd"u8.CopyTo(image);
    BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(4, 4), 0x100);
    using var stream = new MemoryStream(image);
    var exception = Assert.Throws<InvalidDataException>(() => _ = new WaflReader(stream));
    Assert.That(exception!.Message, Does.Contain("0xdab8fbab"));
  }

  [Test, Category("Sad")]
  public void Reader_RejectsWhenNeitherVolInfoCopyHasDocumentedMagic() {
    using var stream = new MemoryStream(new byte[BlockSize * 3]);
    Assert.Throws<InvalidDataException>(() => _ = new WaflReader(stream));
  }

  [Test, Category("Sad")]
  public void Reader_RejectsTooSmallForBothDocumentedVolInfoLocations() {
    using var stream = new MemoryStream(new byte[BlockSize * 2]);
    Assert.Throws<InvalidDataException>(() => _ = new WaflReader(stream));
  }

  [Test, Category("Stub")]
  public void Descriptor_DoesNotAdvertiseUnsafeWriteOrMaintenanceVerbs() {
    var descriptor = new WaflFormatDescriptor();

    Assert.Multiple(() => {
      Assert.That(descriptor.Capabilities.HasFlag(FormatCapabilities.CanCreate), Is.False);
      Assert.That(descriptor.Capabilities.HasFlag(FormatCapabilities.CanModify), Is.False);
      Assert.That(descriptor, Is.Not.InstanceOf<IArchiveCreatable>());
      Assert.That(descriptor, Is.Not.InstanceOf<IArchiveModifiable>());
      Assert.That(descriptor, Is.Not.InstanceOf<IArchiveDefragmentable>());
      Assert.That(descriptor, Is.Not.InstanceOf<IArchiveShrinkable>());
      Assert.That(descriptor, Is.Not.InstanceOf<IWipeEmpty>());
      Assert.That(descriptor, Is.Not.InstanceOf<ILayoutOptimizable>());
      Assert.That(descriptor, Is.Not.InstanceOf<IArchivePurgeable>());
    });
  }

  [Test, Category("Stub")]
  public void Metadata_RecordsDocumentedDetectorAndModernOntapBlockers() {
    using var stream = new MemoryStream(BuildMinimal(version: 0x100, payloadLen: 64));
    using var reader = new WaflReader(stream);
    var metadata = Encoding.UTF8.GetString(reader.Entries.Single(entry => entry.Name == "metadata.ini").Data);

    Assert.Multiple(() => {
      Assert.That(metadata, Does.Contain("parse_status=detection-only"));
      Assert.That(metadata, Does.Contain("stage=0"));
      Assert.That(metadata, Does.Contain("volinfo_magic=0xdab8fbab"));
      Assert.That(metadata, Does.Contain("volinfo_vbns=1,2"));
      Assert.That(metadata, Does.Contain("volinfo_valid_copies=2"));
      Assert.That(metadata, Does.Contain("allocation_block_size=4096"));
      Assert.That(metadata, Does.Contain("maintenance_support=none"));
      Assert.That(metadata, Does.Contain("upgrade_blockers="));
      Assert.That(metadata, Does.Contain("flexvol"));
      Assert.That(metadata, Does.Contain("raid"));
      Assert.That(metadata, Does.Contain("snapshot"));
      Assert.That(metadata, Does.Contain("references="));
    });
  }

  [Test, Category("HappyPath")]
  public void Metadata_RecordsStage1StructuralEvidence() {
    using var stream = new MemoryStream(BuildStructural());
    using var reader = new WaflReader(stream);
    var metadata = Encoding.UTF8.GetString(reader.Entries.Single(entry => entry.Name == "metadata.ini").Data);

    Assert.Multiple(() => {
      Assert.That(metadata, Does.Contain("parse_status=structural-read-only"));
      Assert.That(metadata, Does.Contain("stage=1"));
      Assert.That(metadata, Does.Contain("structural_profile=classic-32bit-direct-fsinfo"));
      Assert.That(metadata, Does.Contain("volinfo_vbn1_fsinfo_table=verified"));
      Assert.That(metadata, Does.Contain($"volinfo_vbn1_fsinfo_table_offset={VbnArrayOffset}"));
      Assert.That(metadata, Does.Contain("active_fsinfo_vbn=4"));
      Assert.That(metadata, Does.Contain("fsinfo_reference_count=2"));
    });
  }
}
