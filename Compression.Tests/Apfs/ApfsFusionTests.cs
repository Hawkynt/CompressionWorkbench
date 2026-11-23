using System.Buffers.Binary;
using System.IO.Compression;
using FileSystem.Apfs;

namespace Compression.Tests.Apfs;

/// <summary>
/// Fusion containers: the main device of one mkapfs -F made (<c>ReferenceVectors/generate-fusion.sh</c>),
/// read like apfs-fuse -f reads the pair, and a tier-2 address refused instead of read
/// from the wrong device.
/// </summary>
[TestFixture]
public sealed class ApfsFusionTests {

  private static byte[] MainDevice() {
    using var resource = typeof(ApfsFusionTests).Assembly.GetManifestResourceStream("ApfsVectors.apfs-fusion-main.img.gz")!;
    using var gzip = new GZipStream(resource, CompressionMode.Decompress);
    using var image = new MemoryStream();
    gzip.CopyTo(image);
    return image.ToArray();
  }

  [Test, Category("HappyPath")]
  public void GivenAFusionContainer_WhenItsMainDeviceIsRead_ThenItListsWhatApfsFuseListed() {
    using var reader = new ApfsReader(new MemoryStream(MainDevice(), writable: false));
    var expected = DissectApfsSamples.Manifest("apfs-fusion.manifest").Select(m => m.Value).ToList();
    Assert.Multiple(() => {
      Assert.That(reader.IsFusion, Is.True);
      Assert.That(reader.Entries.Select(e => e.Name), Is.EquivalentTo(expected), "apfs-fuse lists an empty root");
    });
  }

  [Test, Category("Exception")]
  public void GivenAnObjectOnTheTier2Device_WhenReached_ThenItIsRefusedRatherThanReadFromTheMainDevice() {
    var image = MainDevice();
    var blockSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(36));
    var tier2 = 0x4000000000000000UL >> System.Numerics.BitOperations.TrailingZeroCount((uint)blockSize);
    // The volume superblock's block, and the container object-map node that maps to it:
    // move that mapping onto the tier-2 device.
    var apsb = Enumerable.Range(1, image.Length / blockSize - 1)
      .First(b => BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(b * blockSize + 32)) == 0x42535041);
    var patched = 0;
    for (var b = 1; b < image.Length / blockSize; b++) {
      var node = image.AsSpan(b * blockSize, blockSize);
      var type = BinaryPrimitives.ReadUInt32LittleEndian(node[24..]) & 0xFFFF;
      var subtype = BinaryPrimitives.ReadUInt32LittleEndian(node[28..]);
      if (type is not (2 or 3) || subtype != 0x0B) continue; // B-tree (node) of an object map
      for (var i = 56; i + 8 <= blockSize; i += 8)
        if (BinaryPrimitives.ReadUInt64LittleEndian(node[i..]) == (ulong)apsb) {
          BinaryPrimitives.WriteUInt64LittleEndian(node[i..], (ulong)(uint)apsb | tier2);
          patched++;
        }
      StampFletcher(node);
    }
    Assume.That(patched, Is.GreaterThan(0), "the container object map was located");
    Assert.Throws<NotSupportedException>(() => _ = new ApfsReader(new MemoryStream(image, writable: false)));
  }

  private static void StampFletcher(Span<byte> block) {
    // APFS object checksum: Fletcher-64 over everything after the 8-byte checksum field.
    ulong lo = 0, hi = 0;
    const ulong mod = 0xFFFFFFFF;
    for (var i = 8; i < block.Length; i += 4) {
      lo = (lo + BinaryPrimitives.ReadUInt32LittleEndian(block[i..])) % mod;
      hi = (hi + lo) % mod;
    }
    var c = mod - ((lo + hi) % mod);
    var d = mod - ((lo + c) % mod);
    BinaryPrimitives.WriteUInt64LittleEndian(block, (d << 32) | c);
  }
}
