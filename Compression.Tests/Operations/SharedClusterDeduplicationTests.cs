#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Registry;
using FileSystem.ExFat;
using FileSystem.Fat;

namespace Compression.Tests.Operations;

[TestFixture]
public class SharedClusterDeduplicationTests {
  [Test]
  public void Fat_AdvertisesReadOnlySharedData_NotNativeHardLinks() {
    var descriptor = new FatFormatDescriptor();
    var layout = (ILayoutOptimizable)descriptor;
    var features = FilesystemOptimization.GetSupportedFeatures(descriptor);

    Assert.Multiple(() => {
      Assert.That(features.HasFlag(FilesystemOptimizationFeatures.HardLinkDeduplication), Is.True);
      Assert.That(layout.ReclaimSupport.HasFlag(LayoutReclaim.HardLinks), Is.False,
        "FAT must never masquerade as a filesystem with native hard links");
      Assert.That(FilesystemOptimization.GetHardLinkDeduplicationSemantics(descriptor),
        Is.EqualTo(HardLinkDeduplicationSemantics.ReadOnlySharedData));
    });
  }

  [Test]
  public void ExFat_AdvertisesReadOnlySharedData_NotNativeHardLinks() {
    var descriptor = new ExFatFormatDescriptor();
    var layout = (ILayoutOptimizable)descriptor;
    var features = FilesystemOptimization.GetSupportedFeatures(descriptor);

    Assert.Multiple(() => {
      Assert.That(features.HasFlag(FilesystemOptimizationFeatures.HardLinkDeduplication), Is.True);
      Assert.That(layout.ReclaimSupport.HasFlag(LayoutReclaim.HardLinks), Is.False);
      Assert.That(FilesystemOptimization.GetHardLinkDeduplicationSemantics(descriptor),
        Is.EqualTo(HardLinkDeduplicationSemantics.ReadOnlySharedData));
    });
  }

  [Test]
  public void Fat_Optimize_SharesChainMarksReadOnly_AndRemovePreservesRemainingAlias() {
    var payload = DeterministicPayload(512 * 1024, 0xF47);
    var writer = new FatWriter();
    writer.AddFile("ONE.BIN", payload);
    writer.AddFile("TWO.BIN", (byte[])payload.Clone());
    writer.AddFile("UNIQUE.TXT", "different"u8.ToArray());
    using var source = new MemoryStream(writer.BuildAutoSized());
    var originalLength = source.Length;

    var descriptor = new FatFormatDescriptor();
    using var optimized = new MemoryStream();
    descriptor.Optimize(source, optimized, new FilesystemOptimizationOptions {
      DeduplicateWithHardLinks = true,
    });

    var one = FindFatRootEntry(optimized, "ONE.BIN");
    var two = FindFatRootEntry(optimized, "TWO.BIN");
    Assert.Multiple(() => {
      Assert.That(optimized.Length, Is.LessThan(originalLength));
      Assert.That(one.FirstCluster, Is.GreaterThanOrEqualTo(2));
      Assert.That(two.FirstCluster, Is.EqualTo(one.FirstCluster), "duplicates must point at the exact same FAT chain");
      Assert.That(one.Attributes & 0x01, Is.EqualTo(0x01), "canonical entry must be read-only");
      Assert.That(two.Attributes & 0x01, Is.EqualTo(0x01), "alias entry must be read-only");
    });

    optimized.Position = 0;
    descriptor.Remove(optimized, ["ONE.BIN"]);
    optimized.Position = 0;
    using var remaining = descriptor.OpenEntry(optimized, "TWO.BIN", null);
    Assert.That(ReadAll(remaining), Is.EqualTo(payload),
      "removing one shared FAT name must not zero/free clusters still referenced by the other name");
  }

  [Test]
  public void ExFat_Optimize_SharesChainMarksReadOnly_AndRemovePreservesRemainingAlias() {
    // exFAT auto-size has an 8 MiB floor; two 5 MiB duplicates ensure the
    // shared-data candidate is physically smaller than the ordinary source.
    var payload = DeterministicPayload(5 * 1024 * 1024, 0xEFA7);
    var writer = new ExFatWriter();
    writer.AddFile("ONE.BIN", payload);
    writer.AddFile("TWO.BIN", (byte[])payload.Clone());
    writer.AddFile("UNIQUE.TXT", "different"u8.ToArray());
    using var source = new MemoryStream(writer.BuildAutoSized());
    var originalLength = source.Length;

    var descriptor = new ExFatFormatDescriptor();
    using var optimized = new MemoryStream();
    descriptor.Optimize(source, optimized, new FilesystemOptimizationOptions {
      DeduplicateWithHardLinks = true,
    });

    var one = FindExFatRootEntry(optimized, "ONE.BIN");
    var two = FindExFatRootEntry(optimized, "TWO.BIN");
    Assert.Multiple(() => {
      Assert.That(optimized.Length, Is.LessThan(originalLength));
      Assert.That(one.FirstCluster, Is.GreaterThanOrEqualTo(2));
      Assert.That(two.FirstCluster, Is.EqualTo(one.FirstCluster), "duplicates must point at the exact same exFAT chain");
      Assert.That(one.Attributes & 0x0001, Is.EqualTo(0x0001), "canonical entry must be read-only");
      Assert.That(two.Attributes & 0x0001, Is.EqualTo(0x0001), "alias entry must be read-only");
    });

    optimized.Position = 0;
    descriptor.Remove(optimized, ["ONE.BIN"]);
    optimized.Position = 0;
    using var remaining = descriptor.OpenEntry(optimized, "TWO.BIN", null);
    Assert.That(ReadAll(remaining), Is.EqualTo(payload),
      "removing one shared exFAT name must leave the shared FAT chain and Allocation Bitmap allocation intact");
  }

  private readonly record struct FatRawEntry(byte Attributes, int FirstCluster);
  private readonly record struct ExFatRawEntry(ushort Attributes, uint FirstCluster);

  private static FatRawEntry FindFatRootEntry(Stream stream, string name) {
    var image = Snapshot(stream);
    var bps = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(11));
    var reserved = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(14));
    var fatCount = image[16];
    var rootEntries = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(17));
    var fat16 = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(22));
    var fatSize = fat16 != 0 ? fat16 : BinaryPrimitives.ReadInt32LittleEndian(image.AsSpan(36));
    var rootOffset = (reserved + fatCount * fatSize) * bps;
    if (rootEntries == 0) {
      var spc = image[13];
      var firstDataSector = reserved + fatCount * fatSize;
      var rootCluster = BinaryPrimitives.ReadInt32LittleEndian(image.AsSpan(44));
      rootOffset = (firstDataSector + (rootCluster - 2) * spc) * bps;
      rootEntries = checked((ushort)(spc * bps / 32));
    }

    for (var i = 0; i < rootEntries; ++i) {
      var off = rootOffset + i * 32;
      if (image[off] == 0x00) break;
      if (image[off] == 0xE5 || (image[off + 11] & 0x3F) == 0x0F || (image[off + 11] & 0x08) != 0) continue;
      var shortName = DecodeFatShortName(image.AsSpan(off, 11));
      if (!shortName.Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
      var low = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(off + 26));
      var high = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(off + 20));
      return new FatRawEntry(image[off + 11], (high << 16) | low);
    }
    throw new AssertionException($"FAT root entry '{name}' not found.");
  }

  private static string DecodeFatShortName(ReadOnlySpan<byte> entry) {
    var stem = Encoding.ASCII.GetString(entry[..8]).TrimEnd(' ');
    var ext = Encoding.ASCII.GetString(entry[8..11]).TrimEnd(' ');
    return ext.Length == 0 ? stem : $"{stem}.{ext}";
  }

  private static ExFatRawEntry FindExFatRootEntry(Stream stream, string name) {
    var image = Snapshot(stream);
    var bps = 1 << image[108];
    var clusterSize = bps << image[109];
    var heapOffset = (long)BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(88)) * bps;
    var rootCluster = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(96));
    var rootOffset = heapOffset + (rootCluster - 2L) * clusterSize;

    for (var off = rootOffset; off + 64 <= rootOffset + clusterSize; off += 32) {
      var index = checked((int)off);
      if (image[index] == 0x00) break;
      if (image[index] != 0x85) continue;
      var secondaryCount = image[index + 1];
      var setBytes = 32 * (1 + secondaryCount);
      var set = image.AsSpan(index, setBytes);
      if (set[32] != 0xC0) continue;
      var candidate = DecodeExFatName(set, set[35]);
      if (!candidate.Equals(name, StringComparison.OrdinalIgnoreCase)) {
        off += secondaryCount * 32L;
        continue;
      }
      return new ExFatRawEntry(
        BinaryPrimitives.ReadUInt16LittleEndian(set[4..]),
        BinaryPrimitives.ReadUInt32LittleEndian(set[52..]));
    }
    throw new AssertionException($"exFAT root entry '{name}' not found.");
  }

  private static string DecodeExFatName(ReadOnlySpan<byte> set, int length) {
    var result = new StringBuilder(length);
    var remaining = length;
    for (var off = 64; off + 32 <= set.Length && remaining > 0; off += 32) {
      if (set[off] != 0xC1) break;
      var count = Math.Min(15, remaining);
      for (var i = 0; i < count; ++i)
        result.Append((char)BinaryPrimitives.ReadUInt16LittleEndian(set[(off + 2 + i * 2)..]));
      remaining -= count;
    }
    return result.ToString();
  }

  private static byte[] DeterministicPayload(int length, int seed) {
    var result = new byte[length];
    new Random(seed).NextBytes(result);
    return result;
  }

  private static byte[] Snapshot(Stream stream) {
    var old = stream.Position;
    stream.Position = 0;
    using var copy = new MemoryStream();
    stream.CopyTo(copy);
    stream.Position = old;
    return copy.ToArray();
  }

  private static byte[] ReadAll(Stream stream) {
    using var result = new MemoryStream();
    stream.CopyTo(result);
    return result.ToArray();
  }
}
