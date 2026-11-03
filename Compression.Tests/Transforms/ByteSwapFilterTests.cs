using Compression.Core.Transforms;
using FileFormat.SevenZip;

namespace Compression.Tests.Transforms;

[TestFixture]
public class ByteSwapFilterTests {

  [Test, Category("Spec")]
  public void Swap2_SwapsPairs_LeavesOddTail()
    => Assert.That(ByteSwapFilter.Swap2([1, 2, 3, 4, 5]), Is.EqualTo(new byte[] { 2, 1, 4, 3, 5 }));

  [Test, Category("Spec")]
  public void Swap4_ReversesQuads_LeavesPartialTail()
    => Assert.That(ByteSwapFilter.Swap4([1, 2, 3, 4, 5, 6, 7]), Is.EqualTo(new byte[] { 4, 3, 2, 1, 5, 6, 7 }));

  [TestCase(0)]
  [TestCase(1)]
  [TestCase(2)]
  [TestCase(3)]
  [TestCase(4)]
  [TestCase(5)]
  [TestCase(1023)]
  [Category("Boundary")]
  public void BothFilters_AreSelfInverse(int length) {
    var data = new byte[length];
    new Random(length).NextBytes(data);
    Assert.Multiple(() => {
      Assert.That(ByteSwapFilter.Swap2(ByteSwapFilter.Swap2(data)), Is.EqualTo(data));
      Assert.That(ByteSwapFilter.Swap4(ByteSwapFilter.Swap4(data)), Is.EqualTo(data));
    });
  }

  [TestCase(SevenZipFilter.Swap2)]
  [TestCase(SevenZipFilter.Swap4)]
  [Category("RoundTrip")]
  public void SevenZip_SwapFilter_RoundTrips(SevenZipFilter filter) {
    var data = new byte[4099];
    new Random(9).NextBytes(data);
    var ms = new MemoryStream();
    using (var writer = new SevenZipWriter(ms, SevenZipCodec.Lzma2, leaveOpen: true, filter: filter))
      writer.AddEntry(new SevenZipEntry { Name = "samples.raw" }, data);
    ms.Position = 0;
    using var reader = new SevenZipReader(ms);
    Assert.That(reader.Extract(0), Is.EqualTo(data));
  }
}

/// <summary>7-Zip owns SWAP2/SWAP4: it must extract ours, and we must extract its.</summary>
[TestFixture]
[Category("ExternalInterop")]
public class ByteSwapFilterSevenZipInteropTests {
  private string _tmpDir = null!;

  [SetUp]
  public void Setup() {
    this._tmpDir = Path.Combine(Path.GetTempPath(), $"cwb_swap_{Guid.NewGuid():N}");
    Directory.CreateDirectory(this._tmpDir);
  }

  [TearDown]
  public void Teardown() {
    try { Directory.Delete(this._tmpDir, true); } catch { /* best effort */ }
  }

  private static byte[] Samples() {
    var data = new byte[10_007];
    for (var i = 0; i < data.Length; ++i) data[i] = (byte)(i * 7 ^ (i >> 3));
    return data;
  }

  [TestCase(SevenZipFilter.Swap2)]
  [TestCase(SevenZipFilter.Swap4)]
  public void OurSwapArchive_IsExtractedBy7z(SevenZipFilter filter) {
    FsInteropToolbox.Require7z();
    var data = Samples();
    var archive = Path.Combine(this._tmpDir, "ours.7z");
    using (var fs = File.Create(archive))
    using (var writer = new SevenZipWriter(fs, SevenZipCodec.Lzma2, filter: filter))
      writer.AddEntry(new SevenZipEntry { Name = "samples.raw" }, data);

    var outDir = Path.Combine(this._tmpDir, "x");
    var r = FsInteropToolbox.Run7z($"x -y \"-o{outDir}\" \"{archive}\"");
    Assert.That(r.ExitCode, Is.EqualTo(0), r.StdOut + r.StdErr);
    Assert.That(File.ReadAllBytes(Path.Combine(outDir, "samples.raw")), Is.EqualTo(data));
    var list = FsInteropToolbox.Run7z($"l -slt \"{archive}\"");
    Assert.That(list.StdOut, Does.Contain(filter == SevenZipFilter.Swap2 ? "Swap2" : "Swap4").IgnoreCase);
  }

  [TestCase("SWAP2")]
  [TestCase("SWAP4")]
  public void SevenZipSwapArchive_IsExtractedByUs(string filter) {
    FsInteropToolbox.Require7z();
    var data = Samples();
    var input = Path.Combine(this._tmpDir, "samples.raw");
    File.WriteAllBytes(input, data);
    var archive = Path.Combine(this._tmpDir, "theirs.7z");
    var r = FsInteropToolbox.Run7z($"a -y -mf={filter} \"{archive}\" \"{input}\"");
    Assert.That(r.ExitCode, Is.EqualTo(0), r.StdOut + r.StdErr);

    using var reader = new SevenZipReader(File.OpenRead(archive));
    Assert.That(reader.Extract(0), Is.EqualTo(data));
  }
}
