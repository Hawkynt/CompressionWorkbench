using System.Buffers.Binary;
using System.Text;
using Compression.Registry;
using FileSystem.Tux2;

namespace Compression.Tests.Tux2;

[TestFixture]
public class Tux2Tests {
  [Test, Category("Spec")]
  public void Descriptor_DoesNotInventAStandaloneDiskFormatOrWriteSupport() {
    var descriptor = new Tux2FormatDescriptor();

    Assert.Multiple(() => {
      Assert.That(descriptor.Id, Is.EqualTo("Tux2"));
      Assert.That(descriptor.MagicSignatures, Is.Empty,
        "TUX2 has no stable independent magic; TUX2FS was a workbench-private invention.");
      Assert.That(descriptor.Capabilities.HasFlag(FormatCapabilities.CanCreate), Is.False);
      Assert.That(descriptor.Capabilities.HasFlag(FormatCapabilities.CanModify), Is.False);
      Assert.That(descriptor.Capabilities.HasFlag(FormatCapabilities.SupportsMultipleEntries), Is.False);
      Assert.That(descriptor is IArchiveCreatable, Is.False);
      Assert.That(descriptor is IArchiveModifiable, Is.False);
      Assert.That(descriptor.Description, Does.Contain("no stable standalone").IgnoreCase);
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

    Assert.That(reader.LooksLikeExt2, Is.True);
    Assert.That(reader.Entries.Select(entry => entry.Name),
      Is.EquivalentTo(new[] { "FULL.tux2", "metadata.ini" }));

    var full = reader.Entries.Single(entry => entry.Name == "FULL.tux2");
    Assert.That(reader.Extract(full), Is.EqualTo(image));

    var metadata = Encoding.UTF8.GetString(reader.Extract(
      reader.Entries.Single(entry => entry.Name == "metadata.ini")));
    Assert.Multiple(() => {
      Assert.That(metadata, Does.Contain("parse_status=opaque"));
      Assert.That(metadata, Does.Contain("self_identifying=false"));
      Assert.That(metadata, Does.Contain("ext2_superblock_magic=present"));
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
      Assert.That(reader.Entries, Has.Count.EqualTo(2));
      Assert.That(reader.Entries.Select(entry => entry.Name),
        Is.EquivalentTo(new[] { "FULL.tux2", "metadata.ini" }));
    });
  }

  [Test, Category("HappyPath")]
  public void Descriptor_ExtractsOnlyOpaqueImageAndMetadata() {
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
}
