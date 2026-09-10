#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Registry;
using FileSystem.BcacheFs;
using static FileSystem.BcacheFs.BcacheFsFormat;

namespace Compression.Tests.BcacheFs;

[TestFixture]
public sealed class BcacheFsBucketGenerationTests {

  [Test, Category("RoundTrip")]
  public void RemoveThenReuseBucket_CarriesAdvancedGenerationEverywhere() {
    var descriptor = new BcacheFsFormatDescriptor();
    var firstPayload = Payload(12_345, 17);
    var replacementPayload = Payload(23_456, 29);

    using var image = new MemoryStream();
    descriptor.Create(image,
      [ArchiveInputInfo.InMemory("first.bin", firstPayload)],
      new FormatCreateOptions());

    var firstSector = Entry(image, "first.bin").FirstSector;
    var reusedBucket = firstSector / BucketSectors;
    Assert.That(firstSector % BucketSectors, Is.Zero,
      "the package mutation profile allocates one regular extent from a bucket boundary");

    image.Position = 0;
    descriptor.Remove(image, ["first.bin"]);

    var freed = BucketMetadata(image, reusedBucket);
    Assert.Multiple(() => {
      Assert.That(freed.AllocGeneration, Is.EqualTo(1),
        "live -> free must advance the bucket generation so stale pointers cannot revive");
      Assert.That(freed.OldestGeneration, Is.EqualTo(1));
      Assert.That(freed.DataType, Is.EqualTo(DataFree));
      Assert.That(freed.BucketGensGeneration, Is.EqualTo(1));
    });

    image.Position = 0;
    descriptor.Add(image, [ArchiveInputInfo.InMemory("replacement.bin", replacementPayload)]);

    var replacement = Entry(image, "replacement.bin");
    Assert.That(replacement.FirstSector, Is.EqualTo(firstSector),
      "the first free data bucket should be reused, exercising a nonzero pointer generation");
    Assert.That(ReadPayload(image, replacement), Is.EqualTo(replacementPayload));

    var live = BucketMetadata(image, reusedBucket);
    var pointerGeneration = ExtentPointerGeneration(image, replacement.Inode, replacement.FirstSector);
    var backpointerGeneration = BackpointerGeneration(image, replacement.FirstSector);
    Assert.Multiple(() => {
      Assert.That(live.AllocGeneration, Is.EqualTo(1));
      Assert.That(live.OldestGeneration, Is.EqualTo(1));
      Assert.That(live.DataType, Is.EqualTo(DataUser));
      Assert.That(live.BucketGensGeneration, Is.EqualTo(1));
      Assert.That(pointerGeneration, Is.EqualTo(1));
      Assert.That(backpointerGeneration, Is.EqualTo(1));
    });

    // A later unrelated metadata commit must preserve the nonzero generation and
    // must not move or reinterpret the already-live extent.
    image.Position = 0;
    descriptor.Add(image, [ArchiveInputInfo.InMemory("other.bin", Payload(4_321, 41))]);
    var after = Entry(image, "replacement.bin");
    Assert.Multiple(() => {
      Assert.That(after.FirstSector, Is.EqualTo(firstSector));
      Assert.That(ReadPayload(image, after), Is.EqualTo(replacementPayload));
      Assert.That(ExtentPointerGeneration(image, after.Inode, after.FirstSector), Is.EqualTo(1));
      Assert.That(BucketMetadata(image, reusedBucket).AllocGeneration, Is.EqualTo(1));
    });
  }

  private static BcacheFsReader.Entry Entry(MemoryStream image, string name) {
    image.Position = 0;
    using var reader = new BcacheFsReader(image, leaveOpen: true);
    Assert.That(reader.Valid, Is.True, reader.Status);
    return reader.Entries.Single(entry => entry.Name == name);
  }

  private static byte[] ReadPayload(MemoryStream image, BcacheFsReader.Entry entry) {
    image.Position = 0;
    using var reader = new BcacheFsReader(image, leaveOpen: true);
    return reader.Extract(entry);
  }

  private static (byte AllocGeneration, byte OldestGeneration, byte DataType, byte BucketGensGeneration)
      BucketMetadata(MemoryStream image, long bucket) {
    image.Position = 0;
    var volume = BcacheFsVolume.Open(image);
    Assert.That(volume.Valid, Is.True, volume.Status);

    var alloc = volume.Keys(BtreeAlloc).Single(key => key.Position.Offset == (ulong)bucket);
    Assert.That(alloc.Type, Is.EqualTo(KeyAllocV4));
    Assert.That(alloc.Value, Has.Length.GreaterThanOrEqualTo(20));

    var gens = volume.Keys(BtreeBucketGens)
      .Single(key => key.Position.Offset == (ulong)(bucket / BucketGensNr));
    Assert.That(gens.Type, Is.EqualTo(KeyBucketGens));
    Assert.That(gens.Value, Has.Length.EqualTo(BucketGensNr));

    return (
      alloc.Value[12],
      alloc.Value[13],
      alloc.Value[14],
      gens.Value[(int)(bucket % BucketGensNr)]);
  }

  private static byte ExtentPointerGeneration(MemoryStream image, ulong inode, long expectedSector) {
    image.Position = 0;
    var volume = BcacheFsVolume.Open(image);
    var extent = volume.Keys(BtreeExtents).Single(key => key.Position.Inode == inode);
    for (var i = 0; i + sizeof(ulong) <= extent.Value.Length; i += sizeof(ulong)) {
      var word = BinaryPrimitives.ReadUInt64LittleEndian(extent.Value.AsSpan(i));
      if (!IsPointer(word) || PointerSector(word) != expectedSector) continue;
      return (byte)(word >> 56);
    }
    Assert.Fail($"extent for inode {inode} contains no pointer to sector {expectedSector}");
    return 0;
  }

  private static byte BackpointerGeneration(MemoryStream image, long sector) {
    image.Position = 0;
    var volume = BcacheFsVolume.Open(image);
    var position = (ulong)sector << ExtentBpShift;
    var backpointer = volume.Keys(BtreeBackpointers)
      .Single(key => key.Position.Inode == 0 && key.Position.Offset == position);
    Assert.That(backpointer.Value, Has.Length.GreaterThan(3));
    Assert.Multiple(() => {
      Assert.That(backpointer.Value[0], Is.EqualTo(BtreeExtents));
      Assert.That(backpointer.Value[2], Is.EqualTo(DataUser));
    });
    return backpointer.Value[3];
  }

  private static byte[] Payload(int length, int seed) {
    var result = new byte[length];
    for (var i = 0; i < result.Length; ++i)
      result[i] = (byte)(i * 37 + seed * 19 + i / 257);
    return result;
  }
}
