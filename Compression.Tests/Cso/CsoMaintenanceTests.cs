#pragma warning disable CS1591
using System.Buffers.Binary;
using System.IO.Compression;
using Compression.Registry;
using FileFormat.Cso;

namespace Compression.Tests.Cso;

[TestFixture]
public sealed class CsoMaintenanceTests {
  private const int BlockSize = CsoWriter.DefaultBlockSize;
  private const uint Flag = 0x8000_0000u;
  private const uint OffsetMask = 0x7FFF_FFFFu;

  [Test, Category("RoundTrip"), Category("Regression")]
  public void GrowMutationThenDefrag_ShrinksAndPreservesLogicalDisk() {
    var original = new byte[3 * BlockSize];
    var image = CsoWriter.Build(original);
    using var stream = new MemoryStream();
    stream.Write(image);

    var random = new byte[BlockSize];
    new Random(0xC50).NextBytes(random);
    CsoInPlaceModifier.WriteBlock(stream, 0, random);

    var indexAfterGrow = ReadIndex(stream.ToArray());
    Assert.That((indexAfterGrow[0] & Flag) != 0, Is.True,
      "an incompressible replacement must remain marked stored after the tail is rebuilt");

    var logicalBefore = DecodeLogicalDisk(stream.ToArray());
    var grownLength = stream.Length;

    ((IArchiveDefragmentable)new CsoFormatDescriptor()).Defragment(stream);

    var compacted = stream.ToArray();
    Assert.That(compacted.LongLength, Is.LessThan(grownLength));
    Assert.That(DecodeLogicalDisk(compacted), Is.EqualTo(logicalBefore));

    var index = ReadIndex(compacted);
    var align = compacted[21];
    var previous = -1L;
    foreach (var raw in index) {
      var offset = (long)(raw & OffsetMask) << align;
      Assert.That(offset, Is.GreaterThanOrEqualTo(previous));
      previous = offset;
    }
    Assert.That(previous, Is.EqualTo(compacted.LongLength), "sentinel must point at compacted EOF");
  }

  [Test, Category("RoundTrip")]
  public void Shrink_RemovesTrailingJunkAndPreservesLogicalDisk() {
    var disk = new byte[5 * BlockSize];
    new Random(42).NextBytes(disk.AsSpan(BlockSize, BlockSize));
    var image = CsoWriter.Build(disk);

    using var source = new MemoryStream();
    source.Write(image);
    source.Write(new byte[8192]);
    source.Position = 0;

    using var target = new MemoryStream();
    ((IArchiveShrinkable)new CsoFormatDescriptor()).Shrink(source, target);

    Assert.That(target.Length, Is.LessThan(source.Length));
    Assert.That(DecodeLogicalDisk(target.ToArray()), Is.EqualTo(disk));
  }

  [Test, Category("RoundTrip")]
  public void ZsoShrink_PreservesStoredBlocksAndDropsTrailingJunk() {
    var first = Enumerable.Repeat((byte)0x11, BlockSize).ToArray();
    var second = Enumerable.Repeat((byte)0xA7, BlockSize).ToArray();
    var zso = BuildStoredZso(first, second, trailingJunk: 4096);

    using var source = new MemoryStream(zso, writable: false);
    using var target = new MemoryStream();
    ((IArchiveShrinkable)new CsoFormatDescriptor()).Shrink(source, target);

    var compacted = target.ToArray();
    Assert.That(compacted.LongLength, Is.LessThan(zso.LongLength));
    Assert.That(compacted.AsSpan(0, 4).SequenceEqual("ZISO"u8), Is.True);
    Assert.That(DecodeStoredZso(compacted), Is.EqualTo(first.Concat(second).ToArray()));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_AdvertisesNativeMaintenanceInterfaces() {
    var descriptor = new CsoFormatDescriptor();
    Assert.That(descriptor, Is.InstanceOf<IArchiveDefragmentable>());
    Assert.That(descriptor, Is.InstanceOf<IArchiveShrinkable>());
  }

  private static byte[] DecodeLogicalDisk(byte[] image) {
    var blockSize = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(16, 4)));
    var logicalSize = checked((int)BinaryPrimitives.ReadUInt64LittleEndian(image.AsSpan(8, 8)));
    var align = image[21];
    var blockCount = (logicalSize + blockSize - 1) / blockSize;
    var index = ReadIndex(image);
    var result = new byte[logicalSize];

    for (var block = 0; block < blockCount; ++block) {
      var raw = index[block];
      var start = checked((int)((raw & OffsetMask) << align));
      var end = checked((int)((index[block + 1] & OffsetMask) << align));
      var logicalOffset = block * blockSize;
      var logicalLength = Math.Min(blockSize, logicalSize - logicalOffset);

      if ((raw & Flag) != 0) {
        image.AsSpan(start, logicalLength).CopyTo(result.AsSpan(logicalOffset));
        continue;
      }

      using var compressed = new MemoryStream(image, start, end - start, writable: false);
      using var inflater = new DeflateStream(compressed, CompressionMode.Decompress);
      var slab = new byte[blockSize];
      var written = 0;
      while (written < slab.Length) {
        var read = inflater.Read(slab, written, slab.Length - written);
        if (read <= 0) break;
        written += read;
      }
      Assert.That(written, Is.GreaterThanOrEqualTo(logicalLength), $"block {block} did not fully decode");
      slab.AsSpan(0, logicalLength).CopyTo(result.AsSpan(logicalOffset));
    }

    return result;
  }

  private static uint[] ReadIndex(byte[] image) {
    var blockSize = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(16, 4));
    var logicalSize = BinaryPrimitives.ReadUInt64LittleEndian(image.AsSpan(8, 8));
    var blockCount = checked((int)((logicalSize + blockSize - 1) / blockSize));
    var result = new uint[blockCount + 1];
    for (var i = 0; i < result.Length; ++i)
      result[i] = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(24 + i * 4, 4));
    return result;
  }

  private static byte[] BuildStoredZso(byte[] first, byte[] second, int trailingJunk) {
    var blockCount = 2;
    var dataStart = 24 + (blockCount + 1) * 4;
    var logicalSize = first.Length + second.Length;
    var payloadEnd = dataStart + logicalSize;
    var image = new byte[payloadEnd + trailingJunk];

    "ZISO"u8.CopyTo(image);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(4), 24);
    BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(8), (ulong)logicalSize);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(16), BlockSize);
    image[20] = 1;
    image[21] = 0;

    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(24), (uint)dataStart | Flag);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(28), (uint)(dataStart + first.Length) | Flag);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(32), (uint)payloadEnd);
    first.CopyTo(image, dataStart);
    second.CopyTo(image, dataStart + first.Length);
    image.AsSpan(payloadEnd).Fill(0xCC);
    return image;
  }

  private static byte[] DecodeStoredZso(byte[] image) {
    var index = ReadIndex(image);
    var align = image[21];
    var output = new List<byte>();
    for (var block = 0; block + 1 < index.Length; ++block) {
      Assert.That((index[block] & Flag) != 0, Is.True, "test fixture expects stored ZSO blocks");
      var start = checked((int)((index[block] & OffsetMask) << align));
      var end = checked((int)((index[block + 1] & OffsetMask) << align));
      output.AddRange(image.AsSpan(start, Math.Min(BlockSize, end - start)).ToArray());
    }
    return output.ToArray();
  }
}
