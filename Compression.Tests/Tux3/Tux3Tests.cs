using System.Buffers.Binary;
using System.Text;
using Compression.Registry;
using FileSystem.Tux3;

namespace Compression.Tests.Tux3;

[TestFixture]
public class Tux3Tests {
  private static byte[] BuildNativeImage(bool legacy2012 = false) {
    var image = new byte[16 * 1024];
    var super = image.AsSpan(Tux3Reader.SuperblockOffset, Tux3Reader.DiskSuperSize);
    (legacy2012 ? Tux3Reader.Legacy2012Magic : Tux3Reader.Magic).CopyTo(super);

    BinaryPrimitives.WriteUInt64BigEndian(super.Slice(0x08, 8), 0x0123_4567_89AB_CDEFUL);
    BinaryPrimitives.WriteUInt64BigEndian(super.Slice(0x10, 8), 0x1020_3040_5060_7080UL);
    BinaryPrimitives.WriteUInt16BigEndian(super.Slice(0x18, 2), 12);
    BinaryPrimitives.WriteUInt64BigEndian(super.Slice(0x20, 8), 0x0000_0000_0000_1234UL);
    BinaryPrimitives.WriteUInt64BigEndian(super.Slice(0x28, 8), 0x0001_0000_0000_0042UL);
    BinaryPrimitives.WriteUInt64BigEndian(super.Slice(0x30, 8), 0x8002_0000_0000_0043UL);
    BinaryPrimitives.WriteUInt64BigEndian(super.Slice(0x38, 8), 0x0000_0000_0000_0040UL);
    BinaryPrimitives.WriteUInt64BigEndian(super.Slice(0x40, 8), 0x0000_0000_0000_0080UL);
    BinaryPrimitives.WriteUInt64BigEndian(super.Slice(0x48, 8), 0x0000_0000_0000_0100UL);
    BinaryPrimitives.WriteUInt32BigEndian(super.Slice(0x50, 4), 0x1020_3040U);
    BinaryPrimitives.WriteUInt32BigEndian(super.Slice(0x54, 4), 0x5060_7080U);
    BinaryPrimitives.WriteUInt64BigEndian(super.Slice(0x58, 8), 0x0000_0000_0000_2222UL);
    BinaryPrimitives.WriteUInt32BigEndian(super.Slice(0x60, 4), 0x0000_0003U);
    return image;
  }

  [Test, Category("Spec")]
  public void Reader_ParsesCanonicalPackedBigEndianDiskSuper() {
    var image = BuildNativeImage();
    using var stream = new MemoryStream(image, writable: false);
    using var reader = new Tux3Reader(stream);

    Assert.Multiple(() => {
      Assert.That(reader.ValidSuperblock, Is.True);
      Assert.That(reader.Revision, Is.EqualTo("2014-05-06"));
      Assert.That(reader.Birthday, Is.EqualTo(0x0123_4567_89AB_CDEFUL));
      Assert.That(reader.Flags, Is.EqualTo(0x1020_3040_5060_7080UL));
      Assert.That(reader.BlockBits, Is.EqualTo(12));
      Assert.That(reader.VolBlocks, Is.EqualTo(0x1234UL));
      Assert.That(reader.IRoot, Is.EqualTo(0x0001_0000_0000_0042UL));
      Assert.That(reader.ORoot, Is.EqualTo(0x8002_0000_0000_0043UL));
      Assert.That(reader.UsedInodes, Is.EqualTo(0x40UL));
      Assert.That(reader.NextBlock, Is.EqualTo(0x80UL));
      Assert.That(reader.AtomDictionarySize, Is.EqualTo(0x100UL));
      Assert.That(reader.FreeAtom, Is.EqualTo(0x1020_3040U));
      Assert.That(reader.AtomGeneration, Is.EqualTo(0x5060_7080U));
      Assert.That(reader.LogChain, Is.EqualTo(0x2222UL));
      Assert.That(reader.LogCount, Is.EqualTo(3U));
    });

    Assert.That(reader.Entries.Select(entry => entry.Name),
      Is.EquivalentTo(new[] { "FULL.tux3", "metadata.ini", "superblock.bin" }));
    var superblock = reader.Extract(reader.Entries.Single(entry => entry.Name == "superblock.bin"));
    Assert.That(superblock, Is.EqualTo(image.AsSpan(Tux3Reader.SuperblockOffset, Tux3Reader.DiskSuperSize).ToArray()));
  }

  [Test, Category("Spec")]
  public void Reader_AcceptsKnown2012DiskRevision() {
    using var stream = new MemoryStream(BuildNativeImage(legacy2012: true), writable: false);
    using var reader = new Tux3Reader(stream);
    Assert.That(reader.Revision, Is.EqualTo("2012-12-20"));
  }

  [Test, Category("Regression")]
  public void FormerPrivateTux3SuprMagic_IsRejected() {
    var image = new byte[16 * 1024];
    "TUX3SUPR"u8.CopyTo(image.AsSpan(Tux3Reader.SuperblockOffset));
    using var stream = new MemoryStream(image, writable: false);

    Assert.Throws<InvalidDataException>(() => _ = new Tux3Reader(stream));
  }

  [Test, Category("Spec")]
  public void Descriptor_AdvertisesOnlyTheNativeMetadataSurface() {
    var descriptor = new Tux3FormatDescriptor();

    Assert.Multiple(() => {
      Assert.That(descriptor.MagicSignatures, Has.Count.EqualTo(2));
      Assert.That(descriptor.MagicSignatures.All(signature => signature.Offset == Tux3Reader.SuperblockOffset), Is.True);
      Assert.That(descriptor.MagicSignatures[0].Bytes, Is.EqualTo(Tux3Reader.Magic));
      Assert.That(descriptor.Capabilities.HasFlag(FormatCapabilities.CanCreate), Is.False);
      Assert.That(descriptor.Capabilities.HasFlag(FormatCapabilities.CanModify), Is.False);
      Assert.That(descriptor.Capabilities.HasFlag(FormatCapabilities.SupportsMultipleEntries), Is.False);
      Assert.That(descriptor is IArchiveCreatable, Is.False);
      Assert.That(descriptor is IArchiveModifiable, Is.False);
      Assert.That(descriptor.Description, Does.Contain("native big-endian superblock"));
    });
  }

  [Test, Category("HappyPath")]
  public void Descriptor_ExtractsNativeMetadataWithoutInventedFileTable() {
    var image = BuildNativeImage();
    using var stream = new MemoryStream(image, writable: false);
    var descriptor = new Tux3FormatDescriptor();
    var output = Path.Combine(Path.GetTempPath(), $"tux3-native-{Guid.NewGuid():N}");
    Directory.CreateDirectory(output);

    try {
      descriptor.Extract(stream, output, null, null);
      var metadata = File.ReadAllText(Path.Combine(output, "metadata.ini"));
      Assert.Multiple(() => {
        Assert.That(metadata, Does.Contain("parse_status=superblock-only"));
        Assert.That(metadata, Does.Contain("revision=2014-05-06"));
        Assert.That(metadata, Does.Contain("blockbits=12"));
        Assert.That(metadata, Does.Not.Contain("TUX3WORM"));
        Assert.That(Directory.EnumerateFiles(output).Select(Path.GetFileName),
          Is.EquivalentTo(new[] { "FULL.tux3", "metadata.ini", "superblock.bin" }));
      });
    } finally {
      Directory.Delete(output, recursive: true);
    }
  }
}
