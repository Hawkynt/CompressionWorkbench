using System.Buffers.Binary;
using System.Text;
using Compression.Registry;
using FileSystem.Wafl;

namespace Compression.Tests.Wafl;

/// <summary>
/// Acceptance gates for the deliberately narrow WAFL implementation. Detection
/// is grounded in NetApp's documented volinfo magic/VBNs; filesystem traversal
/// and mutation stay disabled until the modern mapping layers can be proven.
/// </summary>
[TestFixture]
public class WaflDetectionTests {
  private const int BlockSize = 4096;
  private const int DefaultMagicOffset = 20;
  private const uint VolInfoMagic = 0xDAB8FBAB;

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

  private static void WriteVolInfo(Span<byte> block, uint version, int magicOffset, bool littleEndian) {
    var magic = block.Slice(magicOffset, sizeof(uint));
    var versionField = block.Slice(magicOffset + sizeof(uint), sizeof(uint));
    if (littleEndian) {
      BinaryPrimitives.WriteUInt32LittleEndian(magic, VolInfoMagic);
      BinaryPrimitives.WriteUInt32LittleEndian(versionField, version);
    } else {
      BinaryPrimitives.WriteUInt32BigEndian(magic, VolInfoMagic);
      BinaryPrimitives.WriteUInt32BigEndian(versionField, version);
    }
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
  public void List_ReturnsMetadataAndOpaqueVolume() {
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
}
