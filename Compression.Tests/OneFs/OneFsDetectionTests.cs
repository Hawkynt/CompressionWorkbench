using System.Text;
using Compression.Registry;
using Compression.Registry.Streaming;
using Compression.Tests.Documentation;
using FileSystem.OneFs;

namespace Compression.Tests.OneFs;

/// <summary>
/// Acceptance tests for the conservative OneFS single-image inspection surface.
/// The tests intentionally do not invent an on-disk filesystem image: Dell's
/// public material documents cluster architecture and physical geometry, not a
/// complete standalone raw-drive serialization that this suite could author.
/// </summary>
[TestFixture]
public class OneFsDetectionTests {

  private static byte[] BuildOpaqueImage(int length = 512) {
    var image = new byte[length];
    for (var i = 0; i < image.Length; ++i)
      image[i] = unchecked((byte)(i * 37 + 11));
    return image;
  }

  [Test, Category("Regression")]
  public void Descriptor_DoesNotAdvertiseUnverifiedMagic() {
    var descriptor = new OneFsFormatDescriptor();

    Assert.Multiple(() => {
      Assert.That(descriptor.Id, Is.EqualTo("OneFs"));
      Assert.That(descriptor.Extensions, Does.Contain(".onefs"));
      Assert.That(descriptor.MagicSignatures, Is.Empty,
        "No Dell source was found defining 'OneFS' or 'ONEF' as an offset-zero raw-media signature.");
      Assert.That(descriptor.Description, Does.Contain("signature claim was therefore removed").IgnoreCase);
    });
  }

  [Test, Category("HappyPath")]
  public void List_ReturnsInspectionEntriesWithoutConsumingImage() {
    var descriptor = new OneFsFormatDescriptor();
    using var image = new MemoryStream(BuildOpaqueImage(1024));
    image.Position = 137;

    var entries = descriptor.List(image, password: null);

    Assert.Multiple(() => {
      Assert.That(entries.Select(entry => entry.Name), Is.EquivalentTo(new[] {
        OneFsReader.MetadataEntryName,
        OneFsReader.RawImageEntryName,
      }));
      Assert.That(entries.Single(entry => entry.Name == OneFsReader.RawImageEntryName).OriginalSize,
        Is.EqualTo(image.Length));
      Assert.That(image.Position, Is.EqualTo(137),
        "Listing an opaque multi-terabyte image must not copy or scan its payload.");
    });
  }

  [Test, Category("HappyPath")]
  public void OpenEntry_StreamsRawImageByteExact() {
    var expected = BuildOpaqueImage(4097);
    using var image = new MemoryStream(expected, writable: false);
    IArchiveFormatOperations operations = new OneFsFormatDescriptor();

    using var entry = operations.OpenEntry(image, OneFsReader.RawImageEntryName, password: null);
    using var copy = new MemoryStream();
    entry.CopyTo(copy);

    Assert.Multiple(() => {
      Assert.That(entry, Is.InstanceOf<BoundedEntryStream>());
      Assert.That(entry.Length, Is.EqualTo(expected.LongLength));
      Assert.That(copy.ToArray(), Is.EqualTo(expected));
      Assert.That(image.CanRead, Is.True, "The descriptor must not take ownership of the caller's archive stream.");
    });
  }

  [Test, Category("HappyPath")]
  public void Metadata_ReportsOnlyDocumentedArchitecture() {
    using var image = new MemoryStream(BuildOpaqueImage(777));
    using var reader = new OneFsReader(image);
    var metadata = reader.Entries.Single(entry => entry.Name == OneFsReader.MetadataEntryName);
    var text = Encoding.UTF8.GetString(reader.Extract(metadata));

    Assert.Multiple(() => {
      Assert.That(text, Does.Contain("parse_status=opaque-single-image"));
      Assert.That(text, Does.Contain("stage=0"));
      Assert.That(text, Does.Contain("authoritative_raw_magic=not_published"));
      Assert.That(text, Does.Contain("superblock_magic=known-to-exist-value-not-publicly-verified"));
      Assert.That(text, Does.Contain("superblock_locations=multiple-fixed-block-addresses-values-not-publicly-verified"));
      Assert.That(text, Does.Contain("superblock_role=references-LIN-master"));
      Assert.That(text, Does.Contain($"physical_block_size={OneFsReader.PhysicalBlockSize}"));
      Assert.That(text, Does.Contain($"cylinder_group_size={OneFsReader.CylinderGroupSize}"));
      Assert.That(text, Does.Contain($"blocks_per_cylinder_group={OneFsReader.BlocksPerCylinderGroup}"));
      Assert.That(text, Does.Contain("allocation_tracking=per-cylinder-group-bitmap-serialization-not-published"));
      Assert.That(text, Does.Contain("LIN B+ tree"));
      Assert.That(text, Does.Contain("protection groups"));
      Assert.That(text, Does.Contain("two-phase commit"));
      Assert.That(text, Does.Contain("rw_promotion=blocked"));
      Assert.That(text, Does.Contain("maintenance=blocked"));
      Assert.That(text, Does.Contain("FreeBSD-derived"));
    });
  }

  [Test, Category("HappyPath")]
  public void LayoutAnalysis_ReportsDocumentedFixedGeometryWithoutReadingImage() {
    var descriptor = new OneFsFormatDescriptor();
    using var image = new MemoryStream(BuildOpaqueImage(4096));
    image.Position = 73;

    var layout = (ILayoutOptimizable)descriptor;
    var analysis = layout.AnalyzeLayout(image);

    Assert.Multiple(() => {
      Assert.That(analysis.ImageSize, Is.EqualTo(image.Length));
      Assert.That(analysis.CurrentUnitSize, Is.EqualTo(OneFsReader.PhysicalBlockSize));
      Assert.That(analysis.OptimalUnitSize, Is.EqualTo(OneFsReader.PhysicalBlockSize));
      Assert.That(analysis.PotentialSavingsBytes, Is.Zero,
        "Unknown allocation/slack must not be turned into invented savings.");
      Assert.That(analysis.Notes, Has.Some.Contains("33554432"));
      Assert.That(analysis.Notes, Has.Some.Contains("4096 blocks"));
      Assert.That(image.Position, Is.EqualTo(73),
        "Geometry analysis is documentary and must not scan the opaque image.");
      Assert.That(FilesystemSupportMatrix.RelaysOut(descriptor), Is.False,
        "Analysis-only ILayoutOptimizable must not advertise a working Layout rebuild.");
    });

    using var target = new MemoryStream();
    Assert.That(
      () => layout.RebuildStreaming(image, target, new LayoutRebuildOptions()),
      Throws.TypeOf<NotSupportedException>(),
      "No creator exists, so the default layout rebuild must fail closed.");
  }

  [Test, Category("Regression")]
  public void LegacyMagicLookingBytes_AreTreatedAsOpaquePayload() {
    var imageBytes = BuildOpaqueImage(256);
    "OneFSONEF"u8.CopyTo(imageBytes);
    using var image = new MemoryStream(imageBytes, writable: false);
    using var reader = new OneFsReader(image);

    Assert.Multiple(() => {
      Assert.That(reader.Tag, Is.Empty);
      Assert.That(reader.TrailingWord, Is.Zero);
      Assert.That(reader.ValidHeader, Is.False);
    });

    using var raw = reader.OpenEntry(reader.Entries.Single(entry => entry.Name == OneFsReader.RawImageEntryName));
    var prefix = new byte[9];
    raw.ReadExactly(prefix);
    Assert.That(prefix, Is.EqualTo("OneFSONEF"u8.ToArray()));
  }

  [Test, Category("Stub")]
  public void WriteAndDestructiveMaintenanceCapabilities_RemainBlocked() {
    var descriptor = new OneFsFormatDescriptor();

    Assert.Multiple(() => {
      Assert.That(descriptor.Capabilities.HasFlag(FormatCapabilities.CanCreate), Is.False);
      Assert.That(descriptor.Capabilities.HasFlag(FormatCapabilities.CanModify), Is.False);
      Assert.That(descriptor, Is.Not.InstanceOf<IArchiveCreatable>());
      Assert.That(descriptor, Is.Not.InstanceOf<IArchiveModifiable>());
      Assert.That(descriptor, Is.Not.InstanceOf<IArchivePurgeable>());
      Assert.That(descriptor, Is.Not.InstanceOf<IArchiveDefragmentable>());
      Assert.That(descriptor, Is.Not.InstanceOf<IArchiveShrinkable>());
      Assert.That(descriptor, Is.InstanceOf<ILayoutOptimizable>(),
        "Documented fixed geometry is safe to analyze even though rewriting it is not.");
      Assert.That(descriptor, Is.Not.InstanceOf<IWipeEmpty>());
    });
  }

  [Test, Category("Malformed")]
  public void Reader_RejectsEmptyImage() {
    using var image = new MemoryStream();
    Assert.That(() => new OneFsReader(image), Throws.TypeOf<InvalidDataException>());
  }
}
