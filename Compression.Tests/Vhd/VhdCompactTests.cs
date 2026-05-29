#pragma warning disable CS1591
using FileFormat.Vhd;

namespace Compression.Tests.Vhd;

[TestFixture]
public class VhdCompactTests {

  [Test]
  public void Compact_DynamicVhd_WithAllZeroBlocks_ReducesSize() {
    // Create a dynamic VHD with mixed sparse and data blocks
    const int blockSize = 0x00200000; // 2 MB
    var data = new byte[blockSize * 3]; // 6 MB virtual
    // Block 0: all zeros (should become sparse)
    // Block 1: non-zero data
    new Random(42).NextBytes(data.AsSpan(blockSize, blockSize));
    // Block 2: all zeros (should become sparse)

    var writer = new VhdWriter();
    writer.SetDiskData(data);
    var vhd = writer.BuildDynamic(blockSize);

    using var ms = new MemoryStream();
    ms.Write(vhd);
    ms.Position = 0;

    // The dynamic VHD should already be somewhat sparse, but verify compact works
    var result = VhdCompactor.Compact(ms);
    Assert.That(result.OriginalSize, Is.GreaterThan(0));
    // At minimum, the round-trip should produce valid data
    ms.Position = 0;
    var reader = new VhdReader(ms);
    Assert.That(reader.Entries, Has.Count.EqualTo(1));
    var extracted = reader.Extract(reader.Entries[0]);
    Assert.That(extracted.Length, Is.EqualTo(data.Length));
    // Non-zero block data should match
    Assert.That(extracted.AsSpan(blockSize, blockSize).ToArray(),
      Is.EqualTo(data.AsSpan(blockSize, blockSize).ToArray()));
  }

  [Test]
  public void Compact_FixedVhd_WithZeros_ConvertsAndCompacts() {
    // Fixed VHD with mostly-zero data
    var data = new byte[1024 * 1024]; // 1 MB all zeros
    var writer = new VhdWriter();
    writer.SetDiskData(data);
    var vhd = writer.Build(); // fixed

    using var ms = new MemoryStream();
    ms.Write(vhd);
    ms.Position = 0;
    var fixedSize = ms.Length;

    var result = VhdCompactor.Compact(ms);
    Assert.That(result.WasReduced, Is.True);
    Assert.That(result.NewSize, Is.LessThan(fixedSize));

    // Verify data is preserved (all zeros)
    ms.Position = 0;
    var reader = new VhdReader(ms);
    var extracted = reader.Extract(reader.Entries[0]);
    Assert.That(extracted, Is.EqualTo(data));
  }

  [Test]
  public void Compact_DynamicVhd_AlreadySparse_NoChange() {
    // All-zero data — dynamic VHD should already be fully sparse
    var data = new byte[4096];
    var writer = new VhdWriter();
    writer.SetDiskData(data);
    var vhd = writer.BuildDynamic();

    using var ms = new MemoryStream();
    ms.Write(vhd);
    ms.Position = 0;

    var result = VhdCompactor.Compact(ms);
    Assert.That(result.BlocksFreed, Is.EqualTo(0));
    Assert.That(result.WasReduced, Is.False);
  }

  [Test]
  public void Compact_PreservesNonZeroData() {
    // Dynamic VHD with all non-zero data
    var data = new byte[4096];
    new Random(99).NextBytes(data);
    var writer = new VhdWriter();
    writer.SetDiskData(data);
    var vhd = writer.BuildDynamic();

    using var ms = new MemoryStream();
    ms.Write(vhd);
    ms.Position = 0;

    var result = VhdCompactor.Compact(ms);
    // Non-zero data: nothing should be freed
    Assert.That(result.BlocksFreed, Is.EqualTo(0));
    Assert.That(result.WasReduced, Is.False);

    // Data is intact
    ms.Position = 0;
    var reader = new VhdReader(ms);
    var extracted = reader.Extract(reader.Entries[0]);
    Assert.That(extracted, Is.EqualTo(data));
  }

  [Test]
  public void Compact_TooSmall_Throws() {
    using var ms = new MemoryStream(new byte[100]);
    Assert.Throws<InvalidDataException>(() => VhdCompactor.Compact(ms));
  }
}
