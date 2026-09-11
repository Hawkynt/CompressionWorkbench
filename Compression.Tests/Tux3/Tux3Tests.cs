using System.Buffers.Binary;
using System.Text;
using Compression.Registry;
using FileSystem.Tux3;

namespace Compression.Tests.Tux3;

[TestFixture]
public class Tux3Tests {
  private static byte[] BuildNativeImage(
    bool legacy2012 = false,
    ulong volBlocks = 0x1234,
    ushort blockBits = 12,
    int imageLength = 16 * 1024) {
    var image = new byte[imageLength];
    var super = image.AsSpan(Tux3Reader.SuperblockOffset, Tux3Reader.DiskSuperSize);
    (legacy2012 ? Tux3Reader.Legacy2012Magic : Tux3Reader.Magic).CopyTo(super);

    BinaryPrimitives.WriteUInt64BigEndian(super.Slice(0x08, 8), 0x0123_4567_89AB_CDEFUL);
    BinaryPrimitives.WriteUInt64BigEndian(super.Slice(0x10, 8), 0x1020_3040_5060_7080UL);
    BinaryPrimitives.WriteUInt16BigEndian(super.Slice(0x18, 2), blockBits);
    BinaryPrimitives.WriteUInt64BigEndian(super.Slice(0x20, 8), volBlocks);
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
  public void Descriptor_AdvertisesNativeMetadataAndSafeMaintenanceSurface() {
    var descriptor = new Tux3FormatDescriptor();

    Assert.Multiple(() => {
      Assert.That(descriptor.MagicSignatures, Has.Count.EqualTo(2));
      Assert.That(descriptor.MagicSignatures.All(signature => signature.Offset == Tux3Reader.SuperblockOffset), Is.True);
      Assert.That(descriptor.MagicSignatures[0].Bytes, Is.EqualTo(Tux3Reader.Magic));
      Assert.That(descriptor.Capabilities.HasFlag(FormatCapabilities.CanCreate), Is.False);
      Assert.That(descriptor.Capabilities.HasFlag(FormatCapabilities.CanModify), Is.False);
      Assert.That(descriptor.Capabilities.HasFlag(FormatCapabilities.SupportsMultipleEntries), Is.False);
      Assert.That(descriptor, Is.InstanceOf<IFilesystemExtentMap>());
      Assert.That(descriptor, Is.InstanceOf<IWipeEmpty>());
      Assert.That(descriptor, Is.InstanceOf<IArchiveShrinkable>());
      Assert.That(descriptor, Is.Not.InstanceOf<IArchiveCreatable>());
      Assert.That(descriptor, Is.Not.InstanceOf<IArchiveModifiable>());
      Assert.That(descriptor, Is.Not.InstanceOf<IArchiveDefragmentable>());
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

  [Test, Category("Spec")]
  public void ExtentMap_ReservesUndecodedVolumeAndMarksOnlyExternalTailFree() {
    const long declaredLength = 3L << 12;
    var image = BuildNativeImage(volBlocks: 3);
    using var stream = new MemoryStream(image, writable: false);

    var extents = new Tux3FormatDescriptor().EnumerateExtents(stream).ToArray();

    Assert.That(extents, Has.Length.EqualTo(2));
    Assert.Multiple(() => {
      Assert.That(extents[0].Offset, Is.Zero);
      Assert.That(extents[0].Length, Is.EqualTo(declaredLength));
      Assert.That(extents[0].Kind, Is.EqualTo(DefragBlockKind.MetadataReserved));
      Assert.That(extents[1].Offset, Is.EqualTo(declaredLength));
      Assert.That(extents[1].Length, Is.EqualTo(image.LongLength - declaredLength));
      Assert.That(extents[1].Kind, Is.EqualTo(DefragBlockKind.Free));
    });
  }

  [Test, Category("HappyPath")]
  public void WipeUnusedSpace_ZeroesOnlyExternalTail() {
    const int declaredLength = 3 << 12;
    var image = BuildNativeImage(volBlocks: 3);
    image[declaredLength - 1] = 0x5A;
    Array.Fill(image, (byte)0xA5, declaredLength, image.Length - declaredLength);
    using var stream = new MemoryStream(image, writable: true);
    var descriptor = new Tux3FormatDescriptor();

    var wiped = ((IWipeEmpty)descriptor).WipeUnusedSpace(stream, wipeClusterTips: false, wipeDeletedEntries: false);

    Assert.Multiple(() => {
      Assert.That(wiped, Is.EqualTo(image.Length - declaredLength));
      Assert.That(image[declaredLength - 1], Is.EqualTo(0x5A));
      Assert.That(image.AsSpan(declaredLength).ToArray(), Is.All.Zero);
    });
  }

  [Test, Category("HappyPath")]
  public void Shrink_RemovesOnlyExternalTail() {
    const int declaredLength = 3 << 12;
    var image = BuildNativeImage(volBlocks: 3);
    Array.Fill(image, (byte)0xA5, declaredLength, image.Length - declaredLength);
    using var input = new MemoryStream(image, writable: false);
    using var output = new MemoryStream();

    new Tux3FormatDescriptor().Shrink(input, output);

    Assert.Multiple(() => {
      Assert.That(output.Length, Is.EqualTo(declaredLength));
      Assert.That(output.ToArray(), Is.EqualTo(image[..declaredLength]));
    });
  }

  [Test, Category("Regression")]
  public void TruncatedDeclaredVolume_IsNeverTreatedAsFreeOrShrunk() {
    var image = BuildNativeImage(volBlocks: 5);
    image[^1] = 0xA5;
    var descriptor = new Tux3FormatDescriptor();

    using var layoutStream = new MemoryStream(image, writable: false);
    var extents = descriptor.EnumerateExtents(layoutStream).ToArray();
    Assert.That(extents, Has.Length.EqualTo(1));
    Assert.Multiple(() => {
      Assert.That(extents[0].Offset, Is.Zero);
      Assert.That(extents[0].Length, Is.EqualTo(image.LongLength));
      Assert.That(extents[0].Kind, Is.EqualTo(DefragBlockKind.MetadataReserved));
    });

    var wipeCopy = image.ToArray();
    using var wipeStream = new MemoryStream(wipeCopy, writable: true);
    var wiped = ((IWipeEmpty)descriptor).WipeUnusedSpace(wipeStream, wipeClusterTips: false, wipeDeletedEntries: false);
    Assert.Multiple(() => {
      Assert.That(wiped, Is.Zero);
      Assert.That(wipeCopy, Is.EqualTo(image));
    });

    using var shrinkInput = new MemoryStream(image, writable: false);
    using var shrinkOutput = new MemoryStream();
    descriptor.Shrink(shrinkInput, shrinkOutput);
    Assert.That(shrinkOutput.ToArray(), Is.EqualTo(image));
  }

  [Test, Category("Regression")]
  public void OverflowingDeclaredVolume_FailsClosed() {
    var image = BuildNativeImage(volBlocks: ulong.MaxValue, blockBits: 62);
    var descriptor = new Tux3FormatDescriptor();

    using var layoutStream = new MemoryStream(image, writable: false);
    Assert.That(descriptor.EnumerateExtents(layoutStream), Is.Empty);

    using var input = new MemoryStream(image, writable: false);
    using var output = new MemoryStream();
    descriptor.Shrink(input, output);
    Assert.That(output.ToArray(), Is.EqualTo(image));
  }
}
