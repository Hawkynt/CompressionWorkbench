using System.Buffers.Binary;
using System.Text;
using Compression.Registry;
using FileSystem.MooseFs;

namespace Compression.Tests.MooseFs;

/// <summary>
/// Tests for the MooseFS master-metadata envelope and the maintenance operations
/// that are safe on a standalone metadata.mfs image. Synthetic images contain
/// only specification-defined framing; NODE/EDGE/CHNK payloads remain opaque.
/// </summary>
[TestFixture]
public class MooseFsTests {

  private static byte[] BuildModernHeader(
      string signature = "MFSM 2.0",
      ulong metadataVersion = 42,
      ulong metaId = 100) {
    if (signature.Length != 8)
      throw new ArgumentException("MooseFS signature must be exactly 8 ASCII bytes.", nameof(signature));

    var result = new byte[24];
    Encoding.ASCII.GetBytes(signature).CopyTo(result.AsSpan(0, 8));
    BinaryPrimitives.WriteUInt64BigEndian(result.AsSpan(8, 8), metadataVersion);
    BinaryPrimitives.WriteUInt64BigEndian(result.AsSpan(16, 8), metaId);
    return result;
  }

  private static byte[] BuildPre20Header(
      string signature = "MFSM 1.9",
      uint maxNodeId = 123,
      ulong metadataVersion = 42,
      uint nextSessionId = 7) {
    if (signature.Length != 8)
      throw new ArgumentException("MooseFS signature must be exactly 8 ASCII bytes.", nameof(signature));

    var result = new byte[24];
    Encoding.ASCII.GetBytes(signature).CopyTo(result.AsSpan(0, 8));
    BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(8, 4), maxNodeId);
    BinaryPrimitives.WriteUInt64BigEndian(result.AsSpan(12, 8), metadataVersion);
    BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(20, 4), nextSessionId);
    return result;
  }

  private static byte[] BuildSection(string tag, byte[] payload) {
    if (tag.Length != 8)
      throw new ArgumentException("MooseFS section tag must be exactly 8 ASCII bytes.", nameof(tag));

    var result = new byte[16 + payload.Length];
    Encoding.ASCII.GetBytes(tag).CopyTo(result.AsSpan(0, 8));
    BinaryPrimitives.WriteUInt64BigEndian(result.AsSpan(8, 8), (ulong)payload.Length);
    payload.CopyTo(result.AsSpan(16));
    return result;
  }

  private static byte[] EofMarker() => "[MFS EOF MARKER]"u8.ToArray();

  private static byte[] Concat(params byte[][] parts) {
    var total = parts.Sum(static p => p.Length);
    var result = new byte[total];
    var offset = 0;
    foreach (var part in parts) {
      part.CopyTo(result.AsSpan(offset));
      offset += part.Length;
    }
    return result;
  }

  private static byte[] BuildModernImage(params (string Tag, int PayloadLength)[] sections) {
    var parts = new List<byte[]> { BuildModernHeader() };
    foreach (var (tag, length) in sections) {
      var payload = new byte[length];
      for (var i = 0; i < payload.Length; ++i)
        payload[i] = (byte)i;
      parts.Add(BuildSection(tag, payload));
    }
    parts.Add(EofMarker());
    return Concat(parts.ToArray());
  }

  [Test, Category("HappyPath")]
  public void Descriptor_IdentifiesByMagic() {
    var descriptor = new MooseFsFormatDescriptor();
    Assert.That(descriptor.Id, Is.EqualTo("MooseFs"));
    Assert.That(descriptor.Extensions, Does.Contain(".mfsm"));
    Assert.That(descriptor.Extensions, Does.Not.Contain(".mfs"),
      "MooseFS must not claim .mfs because it collides with Macintosh File System.");
    Assert.That(descriptor.MagicSignatures, Has.Count.EqualTo(1));
    Assert.That(descriptor.MagicSignatures[0].Bytes, Is.EqualTo("MFSM"u8.ToArray()));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_AdvertisesOnlyHonestStandaloneCapabilities() {
    var descriptor = new MooseFsFormatDescriptor();
    Assert.Multiple(() => {
      Assert.That(descriptor.Capabilities.HasFlag(FormatCapabilities.CanCreate), Is.False);
      Assert.That(descriptor.Capabilities.HasFlag(FormatCapabilities.CanModify), Is.False);
      Assert.That(descriptor.Capabilities.HasFlag(FormatCapabilities.CanList), Is.True);
      Assert.That(descriptor.Capabilities.HasFlag(FormatCapabilities.CanExtract), Is.True);
      Assert.That(descriptor, Is.InstanceOf<IArchiveLayoutMap>());
      Assert.That(descriptor, Is.InstanceOf<IWipeEmpty>());
      Assert.That(descriptor, Is.InstanceOf<IArchivePurgeable>());
      Assert.That(descriptor, Is.Not.InstanceOf<IArchiveShrinkable>());
      Assert.That(descriptor, Is.Not.InstanceOf<IArchiveDefragmentable>());
    });
  }

  [Test, Category("HappyPath")]
  public void Descriptor_DescriptionStatesChunkServerLimitation() {
    var description = new MooseFsFormatDescriptor().Description.ToLowerInvariant();
    Assert.That(description, Does.Contain("chunk server"));
    Assert.That(description, Does.Contain("not claimed"));
  }

  [Test, Category("Boundary")]
  public void Reader_RejectsImageSmallerThanSignature() {
    using var stream = new MemoryStream("MFSM"u8.ToArray());
    Assert.Throws<InvalidDataException>(() => _ = new MooseFsReader(stream));
  }

  [Test, Category("Exception")]
  public void Reader_RejectsWrongMagic() {
    using var stream = new MemoryStream("NOPENOPE"u8.ToArray());
    Assert.Throws<InvalidDataException>(() => _ = new MooseFsReader(stream));
  }

  [Test, Category("HappyPath")]
  public void Reader_ModernHeader_ParsesMetadataVersionAndMetaId() {
    var image = Concat(BuildModernHeader(metadataVersion: 42, metaId: 100), EofMarker());
    using var stream = new MemoryStream(image);
    using var reader = new MooseFsReader(stream);

    Assert.Multiple(() => {
      Assert.That(reader.ValidHeader, Is.True);
      Assert.That(reader.Signature, Is.EqualTo("MFSM 2.0"));
      Assert.That(reader.FileFormatVersion, Is.EqualTo(0x20));
      Assert.That(reader.MetadataVersion, Is.EqualTo(42UL));
      Assert.That(reader.MetaId, Is.EqualTo(100UL));
      Assert.That(reader.MaxNodeId, Is.Null);
      Assert.That(reader.NextSessionId, Is.Null);
      Assert.That(reader.ParseStatus, Is.EqualTo("ok"));
      Assert.That(reader.Sections, Is.Empty);
    });
  }

  [Test, Category("Boundary")]
  public void Reader_TooShortForMetadataHeader_StaysHeaderOnly() {
    using var stream = new MemoryStream("MFSM 2.0"u8.ToArray());
    using var reader = new MooseFsReader(stream);
    Assert.Multiple(() => {
      Assert.That(reader.FileFormatVersion, Is.EqualTo(0x20));
      Assert.That(reader.MetadataVersion, Is.Null);
      Assert.That(reader.MetaId, Is.Null);
      Assert.That(reader.ParseStatus, Is.EqualTo("header-only"));
    });
  }

  [Test, Category("HappyPath")]
  public void Reader_MfsmNew_IsOfficialEmptyBootstrap() {
    using var stream = new MemoryStream("MFSM NEW"u8.ToArray());
    using var reader = new MooseFsReader(stream);
    Assert.Multiple(() => {
      Assert.That(reader.ValidHeader, Is.True);
      Assert.That(reader.IsEmptyBootstrap, Is.True);
      Assert.That(reader.FileFormatVersion, Is.Null);
      Assert.That(reader.ParseStatus, Is.EqualTo("ok"));
      Assert.That(reader.Sections, Is.Empty);
    });
  }

  [Test, Category("Exception")]
  public void Reader_MfsmNewWithTrailingData_IsRejectedAsCanonicalImage() {
    var image = Concat("MFSM NEW"u8.ToArray(), [0x42]);
    using var stream = new MemoryStream(image);
    using var reader = new MooseFsReader(stream);
    Assert.That(reader.ParseStatus, Is.EqualTo("trailing-data"));
  }

  [Test, Category("HappyPath")]
  public void Reader_WalksModernSections_AndStopsAtEofMarker() {
    var image = BuildModernImage(
      ("SESS 1.0", 32),
      ("NODE 1.0", 256),
      ("EDGE 1.0", 128),
      ("CHNK 1.0", 64));
    using var stream = new MemoryStream(image);
    using var reader = new MooseFsReader(stream);

    Assert.That(reader.ParseStatus, Is.EqualTo("ok"));
    Assert.That(reader.Sections.Select(static s => s.Tag),
      Is.EqualTo(new[] { "SESS 1.0", "NODE 1.0", "EDGE 1.0", "CHNK 1.0" }));
    Assert.That(reader.Sections.Select(static s => s.Length),
      Is.EqualTo(new long[] { 32, 256, 128, 64 }));
  }

  [Test, Category("HappyPath")]
  public void Reader_SectionedPre20Format_UsesLegacyHeaderShape() {
    var image = Concat(
      BuildPre20Header("MFSM 1.9", maxNodeId: 321, metadataVersion: 88, nextSessionId: 9),
      BuildSection("NODE 1.0", new byte[16]),
      EofMarker());
    using var stream = new MemoryStream(image);
    using var reader = new MooseFsReader(stream);

    Assert.Multiple(() => {
      Assert.That(reader.FileFormatVersion, Is.EqualTo(0x19));
      Assert.That(reader.MaxNodeId, Is.EqualTo(321U));
      Assert.That(reader.MetadataVersion, Is.EqualTo(88UL));
      Assert.That(reader.NextSessionId, Is.EqualTo(9U));
      Assert.That(reader.MetaId, Is.Null);
      Assert.That(reader.ParseStatus, Is.EqualTo("ok"));
      Assert.That(reader.Sections, Has.Count.EqualTo(1));
    });
  }

  [Test, Category("HappyPath")]
  public void Reader_Pre16Format_RemainsOpaqueAndUsesZeroEofMarker() {
    var opaqueLegacyBody = new byte[] { 0x01, 0xA5, 0x7F, 0x00, 0xCC };
    var image = Concat(
      BuildPre20Header("MFSM 1.5", maxNodeId: 456, metadataVersion: 99, nextSessionId: 11),
      opaqueLegacyBody,
      new byte[16]);
    using var stream = new MemoryStream(image);
    using var reader = new MooseFsReader(stream);

    Assert.Multiple(() => {
      Assert.That(reader.FileFormatVersion, Is.EqualTo(0x15));
      Assert.That(reader.MaxNodeId, Is.EqualTo(456U));
      Assert.That(reader.MetadataVersion, Is.EqualTo(99UL));
      Assert.That(reader.NextSessionId, Is.EqualTo(11U));
      Assert.That(reader.ParseStatus, Is.EqualTo("ok"));
      Assert.That(reader.Sections, Is.Empty,
        "Pre-1.6 bodies are not section-framed and must not be guessed from payload bytes.");
    });
  }

  [Test, Category("HappyPath")]
  public void Reader_SurfacesPerSectionPayloadAsSyntheticEntry() {
    var image = BuildModernImage(("NODE 1.0", 100), ("EDGE 1.0", 50));
    using var stream = new MemoryStream(image);
    using var reader = new MooseFsReader(stream);

    var names = reader.Entries.Select(static e => e.Name).ToArray();
    Assert.That(names, Does.Contain("metadata.ini"));
    Assert.That(names, Does.Contain("moosefs-master.bin"));
    Assert.That(names, Does.Contain("section_NODE_1_0.bin"));
    Assert.That(names, Does.Contain("section_EDGE_1_0.bin"));

    var node = reader.Entries.First(static e => e.Name == "section_NODE_1_0.bin");
    Assert.That(node.Data[99], Is.EqualTo(99));
  }

  [Test, Category("Exception")]
  public void Reader_TruncatedSectionLength_MarksTruncated() {
    var length = new byte[8];
    BinaryPrimitives.WriteUInt64BigEndian(length, 10_000UL);
    var image = Concat(BuildModernHeader(), "NODE 1.0"u8.ToArray(), length, new byte[100]);
    using var stream = new MemoryStream(image);
    using var reader = new MooseFsReader(stream);

    Assert.That(reader.ParseStatus, Is.EqualTo("truncated"));
    Assert.That(reader.Sections, Is.Empty);
  }

  [Test, Category("Exception")]
  public void Reader_NoEofMarker_MarksTruncated() {
    var image = Concat(BuildModernHeader(), BuildSection("NODE 1.0", new byte[64]));
    using var stream = new MemoryStream(image);
    using var reader = new MooseFsReader(stream);

    Assert.That(reader.ParseStatus, Is.EqualTo("truncated"));
    Assert.That(reader.Sections, Has.Count.EqualTo(1));
  }

  [Test, Category("Exception")]
  public void Reader_EofMarkerMustBeLastBytes() {
    var image = Concat(BuildModernHeader(), EofMarker(), [0xDE, 0xAD]);
    using var stream = new MemoryStream(image);
    using var reader = new MooseFsReader(stream);
    Assert.That(reader.ParseStatus, Is.EqualTo("trailing-data"));
  }

  [Test, Category("Exception")]
  public void Reader_InvalidSectionTag_MarksTruncated() {
    var invalidTag = new byte[] { 0, 1, 2, 3, 4, 5, 6, 7 };
    var image = Concat(BuildModernHeader(), invalidTag, new byte[8], EofMarker());
    using var stream = new MemoryStream(image);
    using var reader = new MooseFsReader(stream);

    Assert.That(reader.ParseStatus, Is.EqualTo("truncated"));
    Assert.That(reader.Sections, Is.Empty);
  }

  [Test, Category("HappyPath")]
  public void MetadataIni_ReportsCorrectVersionedHeaderAndSectionTable() {
    var image = BuildModernImage(("NODE 1.0", 100), ("EDGE 1.0", 50));
    using var stream = new MemoryStream(image);
    using var reader = new MooseFsReader(stream);
    var text = Encoding.UTF8.GetString(reader.Entries.First(static e => e.Name == "metadata.ini").Data);

    Assert.That(text, Does.Contain("parse_status=ok"));
    Assert.That(text, Does.Contain("signature=MFSM 2.0"));
    Assert.That(text, Does.Contain("file_format_version=2.0"));
    Assert.That(text, Does.Contain("metadata_version=42"));
    Assert.That(text, Does.Contain("meta_id=100"));
    Assert.That(text, Does.Not.Contain("file_id_counter"));
    Assert.That(text, Does.Contain("section_count=2"));
    Assert.That(text, Does.Contain("NODE 1.0"));
    Assert.That(text.ToLowerInvariant(), Does.Contain("chunk servers"));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_List_ReturnsMetadataPlusSections() {
    var descriptor = new MooseFsFormatDescriptor();
    using var stream = new MemoryStream(BuildModernImage(("NODE 1.0", 32)));
    var names = descriptor.List(stream, password: null).Select(static e => e.Name).ToArray();

    Assert.That(names, Does.Contain("metadata.ini"));
    Assert.That(names, Does.Contain("moosefs-master.bin"));
    Assert.That(names, Does.Contain("section_NODE_1_0.bin"));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_OpenEntry_ReturnsExactlySectionPayload() {
    var operations = (IArchiveFormatOperations)new MooseFsFormatDescriptor();
    using var archive = new MemoryStream(BuildModernImage(("NODE 1.0", 100)));
    using var entry = operations.OpenEntry(archive, "section_NODE_1_0.bin", password: null);

    Assert.That(entry.Length, Is.EqualTo(100));
    var data = new byte[200];
    Assert.That(entry.Read(data, 0, data.Length), Is.EqualTo(100));
  }

  [Test, Category("Exception")]
  public void Descriptor_OpenEntry_UnknownName_Throws() {
    var operations = (IArchiveFormatOperations)new MooseFsFormatDescriptor();
    using var archive = new MemoryStream(BuildModernImage(("NODE 1.0", 32)));
    Assert.Throws<FileNotFoundException>(() => operations.OpenEntry(archive, "does-not-exist.bin", null));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Layout_CoversEveryByteWithoutInventingFreeSpace() {
    var image = BuildModernImage(("NODE 1.0", 32), ("EDGE 1.0", 17));
    var layout = (IArchiveLayoutMap)new MooseFsFormatDescriptor();
    using var archive = new MemoryStream(image);
    var blocks = layout.EnumerateLayout(archive).ToArray();

    Assert.Multiple(() => {
      Assert.That(blocks, Is.Not.Empty);
      Assert.That(blocks.Sum(static block => block.Length), Is.EqualTo(image.LongLength));
      Assert.That(blocks.Any(static block => block.Kind == DefragBlockKind.Free), Is.False);
      Assert.That(blocks[0].Offset, Is.Zero);
      Assert.That(blocks[^1].Offset + blocks[^1].Length, Is.EqualTo(image.LongLength));
    });
  }

  [Test, Category("HappyPath")]
  public void Descriptor_LegacyLayout_IsConservativelyAllMetadata() {
    var image = Concat(BuildPre20Header("MFSM 1.5"), [0x01, 0x02, 0x03], new byte[16]);
    var layout = (IArchiveLayoutMap)new MooseFsFormatDescriptor();
    using var archive = new MemoryStream(image);
    var blocks = layout.EnumerateLayout(archive).ToArray();

    Assert.That(blocks, Has.Length.EqualTo(1));
    Assert.That(blocks[0].Offset, Is.Zero);
    Assert.That(blocks[0].Length, Is.EqualTo(image.LongLength));
    Assert.That(blocks[0].Kind, Is.EqualTo(DefragBlockKind.MetadataReserved));
  }

  [Test, Category("Exception")]
  public void Descriptor_Layout_FailsClosedForTruncatedImage() {
    var image = Concat(BuildModernHeader(), BuildSection("NODE 1.0", new byte[8]));
    var layout = (IArchiveLayoutMap)new MooseFsFormatDescriptor();
    using var archive = new MemoryStream(image);
    Assert.That(layout.EnumerateLayout(archive), Is.Empty);
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Wipe_IsNoOpForPackedMetadata() {
    var original = BuildModernImage(("NODE 1.0", 64), ("EDGE 1.0", 23));
    using var archive = new MemoryStream();
    archive.Write(original);
    archive.Position = 0;

    var wiped = ((IWipeEmpty)new MooseFsFormatDescriptor()).WipeUnusedSpace(archive);

    Assert.That(wiped, Is.Zero);
    Assert.That(archive.ToArray(), Is.EqualTo(original));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Purge_ResetsToOfficialEmptyBootstrap() {
    using var archive = new MemoryStream();
    archive.Write(BuildModernImage(("NODE 1.0", 64), ("EDGE 1.0", 23)));
    archive.Position = 0;

    ((IArchivePurgeable)new MooseFsFormatDescriptor()).Purge(archive);

    Assert.That(archive.ToArray(), Is.EqualTo("MFSM NEW"u8.ToArray()));
    archive.Position = 0;
    using var reader = new MooseFsReader(archive);
    Assert.That(reader.IsEmptyBootstrap, Is.True);
    Assert.That(reader.ParseStatus, Is.EqualTo("ok"));
  }

  [Test, Category("Exception")]
  public void Descriptor_Purge_RejectsStructurallyInvalidMetadata() {
    using var archive = new MemoryStream();
    archive.Write(Concat(BuildModernHeader(), BuildSection("NODE 1.0", new byte[8])));
    archive.Position = 0;

    Assert.Throws<InvalidDataException>(() =>
      ((IArchivePurgeable)new MooseFsFormatDescriptor()).Purge(archive));
  }
}
