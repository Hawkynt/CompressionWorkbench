#pragma warning disable CS1591
using Compression.Analysis;

namespace Compression.Tests.Analysis;

[TestFixture]
public class BlockMapTests {

  [Test]
  public void EmptyMap_AllBlocksUnmapped() {
    var map = new BlockMap(totalBytes: 4096, blockSize: 4096);
    Assert.That(map.BlockCount, Is.EqualTo(1));
    Assert.That(map.GetOwnerStack(0), Is.Empty);
    Assert.That(map.MaxDepth, Is.Zero);
    Assert.That(map.CountByFormat(), Is.Empty);
  }

  [Test]
  public void SingleHit_AllBlocksOwned() {
    var map = new BlockMap(totalBytes: 1 << 20, blockSize: 4096);  // 1 MB / 4 KB = 256 blocks
    map.Mark(0, 1 << 20, "Qcow2");
    Assert.That(map.BlockCount, Is.EqualTo(256));
    for (var i = 0; i < map.BlockCount; ++i) {
      var stk = map.GetOwnerStack(i);
      Assert.That(stk, Has.Count.EqualTo(1), $"block {i}");
      Assert.That(stk[0], Is.EqualTo("Qcow2"));
    }
  }

  [Test]
  public void NestedHits_OuterAndInnerOwnerStack() {
    // 1 MB outer Qcow2; 64 KB inner Fat at 100 KB.
    var map = new BlockMap(totalBytes: 1 << 20, blockSize: 4096);
    map.Mark(0, 1 << 20, "Qcow2");
    map.Mark(100 * 1024, 64 * 1024, "Fat");

    // Block 0 (offset 0) → only Qcow2.
    Assert.That(map.GetOwnerStack(0), Is.EqualTo(new[] { "Qcow2" }));

    // Block at offset 100K = block index 25 → ["Qcow2", "Fat"].
    var fatStartBlock = (100 * 1024) / 4096;
    var fatEndBlock = (100 * 1024 + 64 * 1024 - 1) / 4096;
    for (var i = fatStartBlock; i <= fatEndBlock; ++i) {
      Assert.That(map.GetOwnerStack(i), Is.EqualTo(new[] { "Qcow2", "Fat" }), $"block {i}");
    }

    // Block right after Fat: only Qcow2.
    Assert.That(map.GetOwnerStack(fatEndBlock + 1), Is.EqualTo(new[] { "Qcow2" }));

    Assert.That(map.MaxDepth, Is.EqualTo(2));
  }

  [Test]
  public void MarkRecursive_WalksChildren() {
    var inner = new NestedHit(
      ByteOffset: 100 * 1024, Length: 64 * 1024, FormatId: "Fat",
      Confidence: 0.9, Depth: 1,
      EnvelopeStack: ["Qcow2", "Fat"], Children: []);
    var outer = new NestedHit(
      ByteOffset: 0, Length: 1 << 20, FormatId: "Qcow2",
      Confidence: 0.95, Depth: 0,
      EnvelopeStack: ["Qcow2"], Children: [inner]);

    var map = new BlockMap(totalBytes: 1 << 20, blockSize: 4096);
    map.MarkRecursive([outer]);

    Assert.That(map.GetOwnerStack(0), Is.EqualTo(new[] { "Qcow2" }));
    var fatBlock = (100 * 1024) / 4096;
    Assert.That(map.GetOwnerStack(fatBlock), Is.EqualTo(new[] { "Qcow2", "Fat" }));
  }

  [Test]
  public void AsciiRenderProducesNonEmptyOutput() {
    var map = new BlockMap(totalBytes: 1 << 20, blockSize: 4096);
    map.Mark(0, 1 << 20, "Qcow2");
    map.Mark(100 * 1024, 64 * 1024, "Fat");

    var ascii = BlockMapRenderer.RenderAscii(map, columns: 80);
    Assert.That(ascii.Length, Is.GreaterThan(0));
    Assert.That(ascii, Does.Contain("Q"));
    Assert.That(ascii, Does.Contain("F"));
    Assert.That(ascii, Does.Contain("Qcow2"));
    Assert.That(ascii, Does.Contain("Fat"));
  }

  [Test]
  public void AsciiLayered_HasOneLinePerDepth() {
    var map = new BlockMap(totalBytes: 64 * 1024, blockSize: 4096);
    map.Mark(0, 64 * 1024, "Qcow2");
    map.Mark(8 * 1024, 16 * 1024, "Fat");

    var s = BlockMapRenderer.RenderAsciiLayered(map);
    Assert.That(s, Does.Contain("Depth 0:"));
    Assert.That(s, Does.Contain("Depth 1:"));
  }

  [Test]
  public void SvgRenderHasCorrectBlockCount() {
    var map = new BlockMap(totalBytes: 64 * 1024, blockSize: 4096);  // 16 blocks
    map.Mark(0, 64 * 1024, "Qcow2");
    map.Mark(16 * 1024, 16 * 1024, "Fat");
    // depth=2, blocks=16 → max 32 rects, but only blocks with an owner at that depth.

    var svg = BlockMapRenderer.RenderSvg(map);
    Assert.That(svg, Does.StartWith("<svg"));
    Assert.That(svg, Does.EndWith("</svg>"));

    // Count rect occurrences (excluding the background rect).
    var rectCount = CountOccurrences(svg, "<rect");
    // 1 background + (depth0: 16 cells of Qcow2) + (depth1: 4 cells of Fat) = 21
    Assert.That(rectCount, Is.EqualTo(1 + 16 + 4));
  }

  [Test]
  public void SvgRenderDownsamplesWhenAboveMaxColumns() {
    var map = new BlockMap(totalBytes: 1L << 24, blockSize: 4096);  // 4096 blocks
    map.Mark(0, 1L << 24, "Foo");

    var opts = new BlockMapRenderOptions(MaxColumns: 64);
    var svg = BlockMapRenderer.RenderSvg(map, opts);
    var rectCount = CountOccurrences(svg, "<rect");
    // 1 background + 64 cells (downsampled) = 65
    Assert.That(rectCount, Is.EqualTo(1 + 64));
  }

  [Test]
  public void HtmlRenderHasLegendForEachFormat() {
    var map = new BlockMap(totalBytes: 1 << 20, blockSize: 4096);
    map.Mark(0, 1 << 20, "Qcow2");
    map.Mark(100 * 1024, 64 * 1024, "Fat");

    var inner = new NestedHit(100 * 1024, 64 * 1024, "Fat", 0.9, 1, ["Qcow2", "Fat"], []);
    var outer = new NestedHit(0, 1 << 20, "Qcow2", 0.95, 0, ["Qcow2"], [inner]);
    var html = BlockMapRenderer.RenderHtml(map, [outer]);

    Assert.That(html, Does.Contain("<!doctype html"));
    Assert.That(html, Does.Contain("Qcow2"));
    Assert.That(html, Does.Contain("Fat"));
    Assert.That(html, Does.Contain("<svg"));
    Assert.That(html, Does.Contain("Hits"));
    Assert.That(html, Does.Contain("Legend"));
  }

  [Test]
  public void ColorFor_IsDeterministic() {
    var c1 = BlockMapRenderer.ColorFor("WeirdFormatXYZ");
    var c2 = BlockMapRenderer.ColorFor("WeirdFormatXYZ");
    Assert.That(c1, Is.EqualTo(c2));
    Assert.That(c1, Does.StartWith("#"));
    Assert.That(c1, Has.Length.EqualTo(7));
  }

  [Test]
  public void ColorFor_KnownFormatsHaveCuratedColor() {
    Assert.That(BlockMapRenderer.ColorFor("Fat"), Is.EqualTo("#ff9800"));
    Assert.That(BlockMapRenderer.ColorFor("Ext4"), Is.EqualTo("#4caf50"));
    Assert.That(BlockMapRenderer.ColorFor("Ntfs"), Is.EqualTo("#1976d2"));
    Assert.That(BlockMapRenderer.ColorFor("Qcow2"), Is.EqualTo("#673ab7"));
  }

  [Test]
  public void CountByFormat_ReportsBlocksPerFormat() {
    var map = new BlockMap(totalBytes: 32 * 1024, blockSize: 4096);  // 8 blocks
    map.Mark(0, 32 * 1024, "Outer");
    map.Mark(0, 16 * 1024, "Inner");
    var counts = map.CountByFormat();
    Assert.That(counts["Outer"], Is.EqualTo(8));
    Assert.That(counts["Inner"], Is.EqualTo(4));
  }

  [Test]
  public void Mark_ClampsBeyondTotalBytes() {
    var map = new BlockMap(totalBytes: 8192, blockSize: 4096);
    map.Mark(0, 1 << 30, "Big"); // huge length
    for (var i = 0; i < map.BlockCount; ++i)
      Assert.That(map.GetOwnerStack(i), Is.EqualTo(new[] { "Big" }));
  }

  [Test]
  public void Mark_IgnoresNegativeOffset() {
    var map = new BlockMap(totalBytes: 8192, blockSize: 4096);
    map.Mark(-100, 8192, "Foo"); // starts before zero — clamp to 0
    Assert.That(map.GetOwnerStack(0), Is.EqualTo(new[] { "Foo" }));
    Assert.That(map.GetOwnerStack(1), Is.EqualTo(new[] { "Foo" }));
  }

  private static int CountOccurrences(string haystack, string needle) {
    var n = 0;
    var i = 0;
    while ((i = haystack.IndexOf(needle, i, StringComparison.Ordinal)) >= 0) {
      ++n;
      i += needle.Length;
    }
    return n;
  }
}
