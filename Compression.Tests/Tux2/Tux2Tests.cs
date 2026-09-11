using System.Buffers.Binary;
using System.Text;
using Compression.Registry;
using FileSystem.Tux2;

namespace Compression.Tests.Tux2;

[TestFixture]
public class Tux2Tests {
  [Test, Category("Spec")]
  public void Descriptor_DoesNotInventStandaloneMagic_ButExposesExt2CompatibilityOperations() {
    var descriptor = new Tux2FormatDescriptor();

    Assert.Multiple(() => {
      Assert.That(descriptor.Id, Is.EqualTo("Tux2"));
      Assert.That(descriptor.MagicSignatures, Is.Empty,
        "The Ext2 magic is not a TUX2 identity signature.");
      Assert.That(descriptor.Capabilities.HasFlag(FormatCapabilities.CanCreate), Is.True);
      Assert.That(descriptor.Capabilities.HasFlag(FormatCapabilities.CanModify), Is.True);
      Assert.That(descriptor.Capabilities.HasFlag(FormatCapabilities.SupportsMultipleEntries), Is.True);
      Assert.That(descriptor, Is.InstanceOf<IArchiveCreatable>());
      Assert.That(descriptor, Is.InstanceOf<IArchiveModifiable>());
      Assert.That(descriptor, Is.InstanceOf<IArchiveDefragmentable>());
      Assert.That(descriptor, Is.InstanceOf<IArchiveShrinkable>());
      Assert.That(descriptor, Is.InstanceOf<IFilesystemExtentMap>());
      Assert.That(descriptor, Is.InstanceOf<IFilesystemBlockMover>());
      Assert.That(descriptor, Is.InstanceOf<IWipeEmpty>());
      Assert.That(descriptor, Is.InstanceOf<ILayoutOptimizable>());
      Assert.That(descriptor.Description, Does.Contain("Ext2-compatible").IgnoreCase);
    });
  }

  [Test, Category("Spec")]
  public void Reader_SurfacesImageOpaque_AndTreatsExt2MagicOnlyAsACompatibilityHint() {
    var image = new byte[4096];
    for (var i = 0; i < image.Length; ++i)
      image[i] = (byte)(i * 17 + i / 31);
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(1024 + 56, 2), 0xEF53);

    using var stream = new MemoryStream(image, writable: false);
    using var reader = new Tux2Reader(stream);

    Assert.Multiple(() => {
      Assert.That(reader.LooksLikeExt2, Is.True);
      Assert.That(reader.IsSupportedExt2CompatibilityProfile, Is.False,
        "Magic alone must not promote arbitrary bytes into the writable compatibility profile.");
      Assert.That(reader.Entries.Select(entry => entry.Name),
        Is.EquivalentTo(new[] { "FULL.tux2", "metadata.ini" }));
    });

    var full = reader.Entries.Single(entry => entry.Name == "FULL.tux2");
    Assert.That(reader.Extract(full), Is.EqualTo(image));

    var metadata = Encoding.UTF8.GetString(reader.Extract(
      reader.Entries.Single(entry => entry.Name == "metadata.ini")));
    Assert.Multiple(() => {
      Assert.That(metadata, Does.Contain("parse_status=opaque"));
      Assert.That(metadata, Does.Contain("self_identifying=false"));
      Assert.That(metadata, Does.Contain("ext2_superblock_magic=present"));
      Assert.That(metadata, Does.Contain("ext2_compatibility_profile=unsupported"));
    });
  }

  [Test, Category("Regression")]
  public void FormerPrivateTux2FsMagic_DoesNotCreateSyntheticFiles() {
    var image = new byte[128];
    "TUX2FS\0\0"u8.CopyTo(image);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(8, 4), 1);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(12, 4), 1);

    using var stream = new MemoryStream(image, writable: false);
    using var reader = new Tux2Reader(stream);

    Assert.Multiple(() => {
      Assert.That(reader.LooksLikeExt2, Is.False);
      Assert.That(reader.IsSupportedExt2CompatibilityProfile, Is.False);
      Assert.That(reader.Entries, Has.Count.EqualTo(2));
      Assert.That(reader.Entries.Select(entry => entry.Name),
        Is.EquivalentTo(new[] { "FULL.tux2", "metadata.ini" }));
    });
  }

  [Test, Category("HappyPath")]
  public void Descriptor_ExtractsOnlyOpaqueImageAndMetadata_WhenNoSupportedExt2ProfileExists() {
    var image = Enumerable.Range(0, 257).Select(i => (byte)i).ToArray();
    using var stream = new MemoryStream(image, writable: false);
    var descriptor = new Tux2FormatDescriptor();
    var output = Path.Combine(Path.GetTempPath(), $"tux2-opaque-{Guid.NewGuid():N}");
    Directory.CreateDirectory(output);

    try {
      descriptor.Extract(stream, output, null, null);
      Assert.Multiple(() => {
        Assert.That(File.ReadAllBytes(Path.Combine(output, "FULL.tux2")), Is.EqualTo(image));
        Assert.That(File.ReadAllText(Path.Combine(output, "metadata.ini")),
          Does.Contain("no stable standalone TUX2 disk signature/layout"));
      });
    } finally {
      Directory.Delete(output, recursive: true);
    }
  }

  [Test, Category("HappyPath")]
  public void CreateAddRemove_RoundTripsThroughSupportedExt2Profile() {
    var descriptor = new Tux2FormatDescriptor();
    using var image = new MemoryStream();
    descriptor.Create(image,
      [ArchiveInputInfo.InMemory("alpha.txt", "alpha"u8.ToArray())],
      new FormatCreateOptions());

    image.Position = 0;
    using (var reader = new Tux2Reader(image)) {
      Assert.Multiple(() => {
        Assert.That(reader.LooksLikeExt2, Is.True);
        Assert.That(reader.IsSupportedExt2CompatibilityProfile, Is.True);
      });
    }

    descriptor.Add(image, [ArchiveInputInfo.InMemory("beta.bin", [1, 3, 3, 7])]);
    descriptor.Remove(image, ["alpha.txt"]);

    image.Position = 0;
    var listed = descriptor.List(image, null).Where(entry => !entry.IsDirectory).ToList();
    Assert.Multiple(() => {
      Assert.That(listed.Select(entry => entry.Name), Is.EquivalentTo(new[] { "beta.bin" }));
      Assert.That(listed.Select(entry => entry.Name), Does.Not.Contain("FULL.tux2"));
    });
    Assert.That(Extract(descriptor, image, "beta.bin"), Is.EqualTo(new byte[] { 1, 3, 3, 7 }));
  }

  [Test, Category("Maintenance")]
  public void MaintenanceVerbs_PreserveLiveBytes_AndPurgeLeavesValidEmptyImage() {
    var descriptor = new Tux2FormatDescriptor();
    var payload = Enumerable.Range(0, 1537).Select(i => (byte)(i * 29)).ToArray();
    using var image = new MemoryStream();
    descriptor.Create(image,
      [ArchiveInputInfo.InMemory("payload.bin", payload)],
      new FormatCreateOptions());

    var layout = ((ILayoutOptimizable)descriptor).AnalyzeLayout(image);
    Assert.That(layout.CurrentUnitSize, Is.AnyOf(1024, 2048, 4096));

    var wiped = ((IWipeEmpty)descriptor).WipeUnusedSpace(image);
    Assert.That(wiped, Is.GreaterThanOrEqualTo(0));
    Assert.That(Extract(descriptor, image, "payload.bin"), Is.EqualTo(payload));

    ((IArchiveDefragmentable)descriptor).Defragment(image);
    Assert.That(Extract(descriptor, image, "payload.bin"), Is.EqualTo(payload));

    using var relaid = new MemoryStream();
    ((ILayoutOptimizable)descriptor).RebuildStreaming(image, relaid, new LayoutRebuildOptions { UnitSize = 2048 });
    Assert.That(Extract(descriptor, relaid, "payload.bin"), Is.EqualTo(payload));

    using var shrunk = new MemoryStream();
    ((IArchiveShrinkable)descriptor).Shrink(relaid, shrunk);
    Assert.That(Extract(descriptor, shrunk, "payload.bin"), Is.EqualTo(payload));

    ((IArchivePurgeable)descriptor).Purge(shrunk);
    shrunk.Position = 0;
    Assert.That(descriptor.List(shrunk, null).Where(entry => !entry.IsDirectory), Is.Empty);
  }

  [Test, Category("Safety")]
  public void OpaqueInput_RefusesMutationAsInvalidData_NotAsAnInventedCapability() {
    var descriptor = new Tux2FormatDescriptor();
    using var opaque = new MemoryStream(new byte[4096]);

    Assert.Multiple(() => {
      Assert.That(
        () => descriptor.Add(opaque, [ArchiveInputInfo.InMemory("x", [1])]),
        Throws.TypeOf<InvalidDataException>());
      Assert.That(
        () => ((IWipeEmpty)descriptor).WipeUnusedSpace(opaque),
        Throws.TypeOf<InvalidDataException>());
      Assert.That(
        () => ((IArchiveDefragmentable)descriptor).Defragment(opaque),
        Throws.TypeOf<InvalidDataException>());
    });
  }

  private static byte[] Extract(Tux2FormatDescriptor descriptor, Stream image, string entryName) {
    var output = Path.Combine(Path.GetTempPath(), $"tux2-entry-{Guid.NewGuid():N}");
    Directory.CreateDirectory(output);
    try {
      image.Position = 0;
      descriptor.Extract(image, output, null, [entryName]);
      return File.ReadAllBytes(Path.Combine(output, entryName));
    } finally {
      Directory.Delete(output, recursive: true);
    }
  }
}
