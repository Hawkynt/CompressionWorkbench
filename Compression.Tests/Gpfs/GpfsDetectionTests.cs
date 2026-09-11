using System.Buffers.Binary;
using System.Text;
using Compression.Core.DiskImage;
using Compression.Registry;
using FileSystem.Gpfs;

namespace Compression.Tests.Gpfs;

/// <summary>
/// Stage-0 structural-inspection acceptance gate for <see cref="GpfsFormatDescriptor"/>.
/// </summary>
[TestFixture]
public class GpfsDetectionTests {

  private const int SectorSize = 512;

  private static byte[] BuildLegacyMinimal(int payloadLength = 128) {
    var image = new byte[8 + payloadLength];
    GpfsReader.NsdMagic.CopyTo(image, 0);
    for (var i = 0; i < payloadLength; ++i)
      image[8 + i] = (byte)(i & 0xFF);
    return image;
  }

  private static byte[] BuildNsdV2Gpt(bool gpfsPartition = true, int entryIndex = 0) {
    var image = new byte[128 * 1024];
    var header = image.AsSpan(SectorSize, SectorSize);
    "EFI PART"u8.CopyTo(header);
    BinaryPrimitives.WriteUInt32LittleEndian(header[8..], 0x00010000);
    BinaryPrimitives.WriteUInt32LittleEndian(header[12..], 92);
    BinaryPrimitives.WriteUInt64LittleEndian(header[24..], 1);
    BinaryPrimitives.WriteUInt64LittleEndian(header[32..], (ulong)(image.Length / SectorSize - 1));
    BinaryPrimitives.WriteUInt64LittleEndian(header[40..], 34);
    BinaryPrimitives.WriteUInt64LittleEndian(header[48..], (ulong)(image.Length / SectorSize - 34));
    new Guid("00112233-4455-6677-8899-AABBCCDDEEFF").TryWriteBytes(header[56..72]);
    BinaryPrimitives.WriteUInt64LittleEndian(header[72..], 2);
    BinaryPrimitives.WriteUInt32LittleEndian(header[80..], 128);
    BinaryPrimitives.WriteUInt32LittleEndian(header[84..], 128);

    var entry = image.AsSpan(2 * SectorSize + entryIndex * 128, 128);
    var type = gpfsPartition
      ? GpfsReader.GpfsPartitionTypeGuid
      : new Guid("0FC63DAF-8483-4772-8E79-3D69D8477DE4");
    type.TryWriteBytes(entry[..16]);
    new Guid("10213243-5465-7687-98A9-BACBDCEDFE0F").TryWriteBytes(entry[16..32]);
    BinaryPrimitives.WriteUInt64LittleEndian(entry[32..], 34);
    BinaryPrimitives.WriteUInt64LittleEndian(entry[40..], (ulong)(image.Length / SectorSize - 34));
    Encoding.Unicode.GetBytes(gpfsPartition ? "gpfs" : "linux").AsSpan().CopyTo(entry[56..]);
    return image;
  }

  [Test, Category("HappyPath")]
  public void Descriptor_UsesCanonicalGpfsGptPartitionSignature() {
    var d = new GpfsFormatDescriptor();
    Assert.That(d.Id, Is.EqualTo("Gpfs"));
    Assert.That(d.Extensions, Does.Contain(".gpfs"));
    Assert.That(d.MagicSignatures, Has.Count.EqualTo(1));
    Assert.Multiple(() => {
      Assert.That(d.MagicSignatures[0].Bytes, Is.EqualTo(GpfsReader.GpfsPartitionTypeGuidBytes));
      Assert.That(d.MagicSignatures[0].Offset, Is.EqualTo(2 * SectorSize));
      Assert.That(d, Is.InstanceOf<ILayoutOptimizable>());
      Assert.That(d, Is.Not.InstanceOf<IArchiveCreatable>());
      Assert.That(d, Is.Not.InstanceOf<IArchiveModifiable>());
      Assert.That(d, Is.Not.InstanceOf<IArchiveDefragmentable>());
      Assert.That(d, Is.Not.InstanceOf<IArchiveShrinkable>());
      Assert.That(d, Is.Not.InstanceOf<IArchivePurgeable>());
      Assert.That(d, Is.Not.InstanceOf<IWipeEmpty>());
    });
  }

  [Test, Category("HappyPath")]
  public void Reader_RecognizesNsdV2GptEnvelope() {
    var image = BuildNsdV2Gpt();
    using var ms = new MemoryStream(image);
    using var reader = new GpfsReader(ms);

    Assert.Multiple(() => {
      Assert.That(reader.ValidHeader, Is.True);
      Assert.That(reader.IsNsdV2Gpt, Is.True);
      Assert.That(reader.UsesLegacyDescriptorSignature, Is.False);
      Assert.That(reader.GpfsPartitionOffset, Is.EqualTo(34L * SectorSize));
      Assert.That(reader.GpfsPartitionSize, Is.GreaterThan(0));
      Assert.That(reader.GpfsPartitionName, Is.EqualTo("gpfs"));
      Assert.That(reader.Entries.Select(e => e.Name), Is.EquivalentTo(new[] { "metadata.ini", "gpfs-nsd.bin" }));
    });

    var metadata = Encoding.UTF8.GetString(reader.Extract(reader.Entries.Single(e => e.Name == "metadata.ini")));
    Assert.Multiple(() => {
      Assert.That(metadata, Does.Contain("parse_status=structural-inspection-only"));
      Assert.That(metadata, Does.Contain("nsd_surface=v2-gpt"));
      Assert.That(metadata, Does.Contain($"gpfs_partition_type={GpfsReader.GpfsPartitionTypeGuid:D}"));
      Assert.That(metadata, Does.Contain("layout_analysis=available"));
      Assert.That(metadata, Does.Contain("layout_rebuild=false"));
      Assert.That(metadata, Does.Contain("maintenance_support=none"));
    });
  }

  [Test, Category("HappyPath")]
  public void StructuralDetector_FindsRelocatedGpfsEntryAndRejectsOtherGpt() {
    var detector = new GpfsDetectionSource();
    var gpfs = BuildNsdV2Gpt(entryIndex: 3);
    var other = BuildNsdV2Gpt(gpfsPartition: false, entryIndex: 3);

    Assert.Multiple(() => {
      Assert.That(detector.DetectHeader(gpfs)?.FormatId, Is.EqualTo("Gpfs"));
      Assert.That(detector.DetectHeader(other), Is.Null);
    });
  }

  [Test, Category("HappyPath")]
  public void PartitionTypeDatabase_RecognizesGpfsGuid() {
    Assert.That(
      PartitionTypeDatabase.GetGptTypeName(GpfsReader.GpfsPartitionTypeGuid),
      Is.EqualTo("IBM GPFS"));
  }

  [Test, Category("HappyPath")]
  public void List_ReturnsStructuralEntriesForNsdV2() {
    var d = new GpfsFormatDescriptor();
    using var ms = new MemoryStream(BuildNsdV2Gpt());
    var names = d.List(ms, password: null).Select(e => e.Name).ToList();
    Assert.That(names, Is.EquivalentTo(new[] { "metadata.ini", "gpfs-nsd.bin" }));
  }

  [Test, Category("HappyPath")]
  public void AnalyzeLayout_ReportsEnvelopeWithoutClaimingFilesystemGeometry() {
    var d = new GpfsFormatDescriptor();
    using var ms = new MemoryStream(BuildNsdV2Gpt());
    ms.Position = 17;

    var analysis = ((ILayoutOptimizable)d).AnalyzeLayout(ms);

    Assert.Multiple(() => {
      Assert.That(analysis.ImageSize, Is.EqualTo(ms.Length));
      Assert.That(analysis.CurrentUnitSize, Is.Zero);
      Assert.That(analysis.CurrentSlackBytes, Is.Zero);
      Assert.That(analysis.OptimalUnitSize, Is.Zero);
      Assert.That(analysis.PotentialSavingsBytes, Is.Zero);
      Assert.That(analysis.Notes.Any(note => note.Contains("NSD v2 GPT envelope", StringComparison.Ordinal)), Is.True);
      Assert.That(analysis.Notes.Any(note => note.Contains("remain disabled", StringComparison.Ordinal)), Is.True);
      Assert.That(ms.Position, Is.EqualTo(17));
    });
  }

  [Test, Category("Compatibility")]
  public void LegacyDescriptorFixture_RemainsReadableButIsNotAdvertisedAsCanonicalMagic() {
    var d = new GpfsFormatDescriptor();
    using var ms = new MemoryStream(BuildLegacyMinimal());
    using var reader = new GpfsReader(ms);

    Assert.Multiple(() => {
      Assert.That(reader.ValidHeader, Is.True);
      Assert.That(reader.IsNsdV2Gpt, Is.False);
      Assert.That(reader.UsesLegacyDescriptorSignature, Is.True);
      Assert.That(d.MagicSignatures.Any(s => s.Bytes.SequenceEqual(GpfsReader.NsdMagic)), Is.False);
    });

    var metadata = Encoding.UTF8.GetString(reader.Extract(reader.Entries.Single(e => e.Name == "metadata.ini")));
    Assert.That(metadata, Does.Contain("legacy_signature_authoritative=false"));
  }

  [Test, Category("Stub")]
  public void Description_DocumentsWhyMutationRemainsDeferred() {
    var d = new GpfsFormatDescriptor();
    var desc = d.Description.ToLowerInvariant();
    Assert.Multiple(() => {
      Assert.That(desc, Does.Contain("stage-0"));
      Assert.That(desc, Does.Contain("proprietary"));
      Assert.That(desc, Does.Contain("deferred"));
      Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanCreate), Is.False);
      Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanModify), Is.False);
    });
  }

  [Test, Category("Stub")]
  public void Metadata_DocumentsPromotionBlockedReason() {
    var d = new GpfsFormatDescriptor();
    using var ms = new MemoryStream(BuildNsdV2Gpt());
    d.Extract(ms, TestContext.CurrentContext.WorkDirectory, password: null, files: ["metadata.ini"]);
    var metaPath = Path.Combine(TestContext.CurrentContext.WorkDirectory, "metadata.ini");
    try {
      Assert.That(File.Exists(metaPath), Is.True, "metadata.ini must be written by Extract");
      var text = File.ReadAllText(metaPath);
      Assert.Multiple(() => {
        Assert.That(text, Does.Contain("stage=0"));
        Assert.That(text, Does.Contain("parse_status=structural-inspection-only"));
        Assert.That(text, Does.Contain("promotion_blocked_reason="));
      });
    } finally {
      File.Delete(metaPath);
    }
  }

  [Test, Category("ExceptionalCase")]
  public void Reader_RejectsGptWithoutGpfsPartition() {
    using var ms = new MemoryStream(BuildNsdV2Gpt(gpfsPartition: false));
    Assert.That(() => new GpfsReader(ms), Throws.InstanceOf<InvalidDataException>());
  }

  [Test, Category("ExceptionalCase")]
  public void Reader_RejectsBadMagicAndNonGptInput() {
    var image = new byte[64];
    image[0] = 0xDE; image[1] = 0xAD; image[2] = 0xBE; image[3] = 0xEF;
    using var ms = new MemoryStream(image);
    Assert.That(() => new GpfsReader(ms), Throws.InstanceOf<InvalidDataException>());
  }

  [Test, Category("BoundaryCase")]
  public void Reader_RejectsTruncatedInput() {
    using var ms = new MemoryStream([0x43, 0x47, 0x46]);
    Assert.That(() => new GpfsReader(ms), Throws.InstanceOf<InvalidDataException>());
  }
}
