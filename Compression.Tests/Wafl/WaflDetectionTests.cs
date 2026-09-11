using System.Buffers.Binary;
using System.Text;
using Compression.Registry;
using Compression.Registry.Streaming;
using FileSystem.Wafl;

namespace Compression.Tests.Wafl;

/// <summary>
/// Acceptance gates for the deliberately narrow WAFL implementation. Public
/// WAFL material is enough for header detection and fixed-block geometry, but
/// not for a safe modern ONTAP aggregate/FlexVol traversal or writer.
/// </summary>
[TestFixture]
public class WaflDetectionTests {

  private static byte[] BuildMinimal(uint version = 0x100, int payloadLen = 128) {
    var image = new byte[8 + payloadLen];
    Encoding.ASCII.GetBytes("wafd").CopyTo(image.AsSpan(0, 4));
    BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(4, 4), version);
    for (var i = 0; i < payloadLen; ++i) image[8 + i] = (byte)(i & 0xFF);
    return image;
  }

  [Test, Category("HappyPath")]
  public void Detector_IdentifiesByMagic() {
    var descriptor = new WaflFormatDescriptor();
    Assert.That(descriptor.Id, Is.EqualTo("Wafl"));
    Assert.That(descriptor.Extensions, Does.Contain(".wafl"));
    Assert.That(descriptor.MagicSignatures, Has.Count.EqualTo(1));
    Assert.That(descriptor.MagicSignatures[0].Offset, Is.EqualTo(0));
    Assert.That(descriptor.MagicSignatures[0].Bytes, Is.EqualTo("wafd"u8.ToArray()));
  }

  [Test, Category("HappyPath")]
  public void List_ReturnsMetadataAndOpaqueVolume() {
    var descriptor = new WaflFormatDescriptor();
    using var stream = new MemoryStream(BuildMinimal(version: 0x200, payloadLen: 256));
    var entries = descriptor.List(stream, password: null);
    Assert.That(entries.Select(entry => entry.Name), Is.EquivalentTo(new[] { "metadata.ini", "wafl-volume.bin" }));
  }

  [Test, Category("HappyPath")]
  public void Reader_ParsesVersion_WithoutDuplicatingRawImage() {
    var image = BuildMinimal(version: 0x300, payloadLen: 512);
    using var stream = new MemoryStream(image, writable: false);
    using var reader = new WaflReader(stream);

    Assert.Multiple(() => {
      Assert.That(reader.ValidHeader, Is.True);
      Assert.That(reader.Version, Is.EqualTo(0x300u));
      Assert.That(reader.ImageSize, Is.EqualTo(image.Length));
    });

    var volume = reader.Entries.Single(entry => entry.Name == "wafl-volume.bin");
    Assert.That(volume.Size, Is.EqualTo(image.Length));
    Assert.That(volume.Data, Is.Empty, "The raw image pseudo-entry must not duplicate a potentially huge WAFL image in RAM.");
    Assert.That(reader.Extract(volume), Is.EqualTo(image), "The buffered compatibility API still has to materialize correctly when explicitly requested.");
  }

  [Test, Category("HappyPath")]
  public void OpenEntry_StreamsOpaqueVolumeThroughBoundedView() {
    var image = BuildMinimal(version: 0x400, payloadLen: 1024);
    var descriptor = (IArchiveFormatOperations)new WaflFormatDescriptor();
    using var archive = new MemoryStream(image, writable: false);
    using var entry = descriptor.OpenEntry(archive, "wafl-volume.bin", password: null);

    Assert.That(entry, Is.TypeOf<BoundedEntryStream>());
    Assert.That(entry.Length, Is.EqualTo(image.Length));

    var roundTrip = new byte[image.Length];
    entry.ReadExactly(roundTrip);
    Assert.That(roundTrip, Is.EqualTo(image));
  }

  [Test, Category("HappyPath")]
  public void AnalyzeLayout_ReportsPublishedFixedBlockSize_WithoutClaimingRewrite() {
    var descriptor = new WaflFormatDescriptor();
    using var image = new MemoryStream(BuildMinimal(payloadLen: 4096));

    var analysis = descriptor.AnalyzeLayout(image);
    Assert.Multiple(() => {
      Assert.That(analysis.ImageSize, Is.EqualTo(image.Length));
      Assert.That(analysis.CurrentUnitSize, Is.EqualTo(WaflReader.BlockSize));
      Assert.That(analysis.OptimalUnitSize, Is.EqualTo(WaflReader.BlockSize));
      Assert.That(analysis.PotentialSavingsBytes, Is.Zero);
      Assert.That(analysis.Notes.Any(note => note.Contains("not computed", StringComparison.OrdinalIgnoreCase)), Is.True);
    });

    using var target = new MemoryStream();
    Assert.Throws<NotSupportedException>(() =>
      ((ILayoutOptimizable)descriptor).RebuildStreaming(image, target, new LayoutRebuildOptions()));
  }

  [Test, Category("Sad")]
  public void Reader_RejectsMissingMagic() {
    var image = new byte[64];
    image[0] = 0xDE;
    image[1] = 0xAD;
    image[2] = 0xBE;
    image[3] = 0xEF;
    using var stream = new MemoryStream(image);
    Assert.Throws<InvalidDataException>(() => _ = new WaflReader(stream));
  }

  [Test, Category("Sad")]
  public void Reader_RejectsTooSmall() {
    using var stream = new MemoryStream(new byte[4]);
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
      Assert.That(descriptor, Is.Not.InstanceOf<IArchivePurgeable>());
    });
  }

  [Test, Category("Stub")]
  public void Description_PinsStage0Confirmation_AndModernOntapBlockers() {
    var description = new WaflFormatDescriptor().Description.ToLowerInvariant();
    Assert.Multiple(() => {
      Assert.That(description, Does.Contain("stage-0 confirmed"));
      Assert.That(description, Does.Contain("opaque streaming"));
      Assert.That(description, Does.Contain("4 kib"));
      Assert.That(description, Does.Contain("flexvol"));
      Assert.That(description, Does.Contain("raid"));
      Assert.That(description, Does.Contain("snapshot"));
    });
  }

  [Test, Category("Stub")]
  public void Metadata_DocumentsGeometry_AndWhyMaintenanceStaysDisabled() {
    using var stream = new MemoryStream(BuildMinimal(version: 0x100, payloadLen: 64));
    using var reader = new WaflReader(stream);
    var metadata = reader.Entries.Single(entry => entry.Name == "metadata.ini");
    var text = Encoding.UTF8.GetString(metadata.Data);

    Assert.Multiple(() => {
      Assert.That(text, Does.Contain("parse_status=detection-only"));
      Assert.That(text, Does.Contain("stage=0"));
      Assert.That(text, Does.Contain("allocation_block_size=4096"));
      Assert.That(text, Does.Contain("layout_analysis=fixed-4k-block-size-only"));
      Assert.That(text, Does.Contain("maintenance_support=none"));
      Assert.That(text, Does.Contain("upgrade_blockers="));
      Assert.That(text, Does.Contain("flexvol"));
      Assert.That(text, Does.Contain("raid"));
      Assert.That(text, Does.Contain("snapshot"));
      Assert.That(text, Does.Contain("references="));
    });
  }
}
