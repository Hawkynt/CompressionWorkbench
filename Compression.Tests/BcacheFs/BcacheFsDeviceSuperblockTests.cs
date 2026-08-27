#pragma warning disable CS1591
using System.Buffers.Binary;
using FileSystem.BcacheFs;

namespace Compression.Tests.BcacheFs;

[TestFixture]
public class BcacheFsDeviceSuperblockTests {
  [Test, Category("Superblock")]
  public void Discovery_FollowsEveryAdvertisedCopyAndFindsJournalV2Range() {
    var writer = new BcacheFsWriter();
    using var image = new MemoryStream();
    writer.WriteTo(image);

    image.Position = 0;
    var discovered = BcacheFsDeviceSuperblocks.Read(image);

    Assert.That(discovered.Current, Is.Not.Null,
      string.Join(Environment.NewLine, discovered.Diagnostics));
    Assert.Multiple(() => {
      Assert.That(discovered.AdvertisedSectors, Has.Count.EqualTo(3));
      Assert.That(discovered.Copies, Has.Count.EqualTo(3));
      Assert.That(discovered.Copies.All(c => c.Checksum.Valid), Is.True);
      Assert.That(discovered.Current!.Version, Is.EqualTo(BcacheFsOnDiskCatalog.MetadataVersion));
      Assert.That(discovered.Current.DeviceCount, Is.EqualTo(1));
      Assert.That(discovered.Current.BtreeNodeSectors, Is.EqualTo(BcacheFsFormat.BucketSectors));
      Assert.That(discovered.Current.Fields.Any(f =>
        f.KnownType == BcacheFsSuperblockFieldType.MembersV2), Is.True);
      Assert.That(discovered.Current.Fields.Any(f =>
        f.KnownType == BcacheFsSuperblockFieldType.Clean), Is.True);
    });

    var journal = discovered.Current!.JournalRanges();
    Assert.That(journal, Has.Count.EqualTo(1));
    Assert.Multiple(() => {
      Assert.That(journal[0].FirstBucket, Is.EqualTo(33));
      Assert.That(journal[0].Count, Is.EqualTo(16));
      Assert.That(journal[0].Buckets(), Is.EqualTo(Enumerable.Range(33, 16).Select(x => (long)x)));
    });
  }

  [Test, Category("Superblock")]
  public void Discovery_SelectsHighestChecksumValidSequenceCopy() {
    var writer = new BcacheFsWriter();
    using var image = new MemoryStream();
    writer.WriteTo(image);

    var first = BcacheFsDeviceSuperblocks.Read(image);
    var sectors = first.AdvertisedSectors.ToArray();
    Assert.That(sectors, Has.Length.EqualTo(3));

    var middle = first.Copies.Single(c => c.Sector == sectors[1]);
    RewriteSuperblock(image, middle, raw =>
      BinaryPrimitives.WriteUInt64LittleEndian(raw.AsSpan(112), 7));

    image.Position = 0;
    var discovered = BcacheFsDeviceSuperblocks.Read(image);
    Assert.That(discovered.Current!.Sector, Is.EqualTo(sectors[1]));
    Assert.That(discovered.Current.Sequence, Is.EqualTo(7));
  }

  [Test, Category("Superblock")]
  public void Discovery_IgnoresTornHigherSequenceCopy() {
    var writer = new BcacheFsWriter();
    using var image = new MemoryStream();
    writer.WriteTo(image);

    var first = BcacheFsDeviceSuperblocks.Read(image);
    var middle = first.Copies.OrderBy(c => c.Sector).Skip(1).First();

    // Simulate a torn superblock write: the fixed header contains a newer seq,
    // but its checksum still belongs to the old bytes.
    image.Position = middle.Sector * BcacheFsFormat.SectorSize + 112;
    Span<byte> seq = stackalloc byte[8];
    BinaryPrimitives.WriteUInt64LittleEndian(seq, 99);
    image.Write(seq);

    image.Position = 0;
    var discovered = BcacheFsDeviceSuperblocks.Read(image);
    var torn = discovered.Copies.Single(c => c.Sector == middle.Sector);
    Assert.Multiple(() => {
      Assert.That(torn.Sequence, Is.EqualTo(99));
      Assert.That(torn.Checksum.Valid, Is.False);
      Assert.That(discovered.Current, Is.Not.Null);
      Assert.That(discovered.Current!.Sector, Is.Not.EqualTo(middle.Sector));
      Assert.That(discovered.Current.Sequence, Is.EqualTo(1));
    });
  }

  [Test, Category("Superblock")]
  public void UnknownVariableField_IsPreservedLosslessly() {
    var field = new byte[16];
    BinaryPrimitives.WriteUInt32LittleEndian(field, 2);
    BinaryPrimitives.WriteUInt32LittleEndian(field.AsSpan(4), 0xDEADBEEF);
    for (var i = 8; i < field.Length; ++i) field[i] = (byte)(i * 13);

    var raw = new BcacheFsSuperblockField(0xDEADBEEF, field);
    Assert.That(raw.KnownType, Is.Null);
    Assert.That(raw.RawBytes, Is.EqualTo(field).AsCollection);
  }

  private static void RewriteSuperblock(
      Stream image,
      BcacheFsSuperblockRecord source,
      Action<byte[]> mutate) {
    var raw = source.RawBytes.ToArray();
    mutate(raw);
    Assert.That(BcacheFsChecksumCodec.TryCompute(source.ChecksumType, raw.AsSpan(16), out var checksum), Is.True);
    BinaryPrimitives.WriteUInt64LittleEndian(raw, checksum.Lo);
    BinaryPrimitives.WriteUInt64LittleEndian(raw.AsSpan(8), checksum.Hi);
    image.Position = source.Sector * BcacheFsFormat.SectorSize;
    image.Write(raw);
  }
}
