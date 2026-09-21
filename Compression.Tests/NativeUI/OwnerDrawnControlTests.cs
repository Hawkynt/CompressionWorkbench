using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using Compression.Analysis.Statistics;
using Compression.Core.Layout;
using Compression.NativeUI.Controls;
using Compression.NativeUI.Theming;
using Compression.Registry;
using Hawkynt.NativeForms;
using Hawkynt.NativeForms.Drawing;
using NUnit.Framework;

namespace Compression.Tests.NativeUI;

/// <summary>
/// Paints the shell's own-drawn controls into a buffer and checks what came out. None of these draw
/// through a widget — they compose pixels themselves — so a mistake in one produces a blank panel
/// rather than an exception, and only looking at the result catches it.
/// </summary>
[TestFixture]
[NonParallelizable]
public sealed class OwnerDrawnControlTests {
  [SetUp]
  public void SetUp() => HeadlessBackend.Install();

  /// <summary>Paints <paramref name="control"/> at the given size and returns what it drew.</summary>
  private static RecordingGraphics Paint(Control control, int width, int height) {
    control.Size = new(width, height);
    var graphics = new RecordingGraphics(width, height);

    var onPaint = control.GetType().GetMethod(
      "OnPaint",
      BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public,
      [typeof(PaintEventArgs)]);

    Assert.That(onPaint, Is.Not.Null, $"{control.GetType().Name} does not override OnPaint");
    onPaint!.Invoke(control, [new PaintEventArgs(graphics, new(0, 0, width, height))]);
    return graphics;
  }

  /// <summary>
  /// The shared expectations of anything that paints: it covers its bounds, it draws more than a
  /// flat wash, and it leaves the clip stack as it found it.
  /// </summary>
  private static void AssertRendered(RecordingGraphics graphics, string what) {
    var covered = 100.0 * graphics.PaintedPixels / (graphics.Width * (long)graphics.Height);

    Assert.Multiple(() => {
      Assert.That(covered, Is.GreaterThan(99.0), $"{what} left part of its bounds unpainted");
      Assert.That(graphics.DistinctColors, Is.GreaterThan(2), $"{what} drew a flat fill and nothing else");
      Assert.That(graphics.LeakedClips, Is.Zero, $"{what} pushed a clip it never popped");
      Assert.That(graphics.UnbalancedPops, Is.Zero, $"{what} popped a clip it never pushed");
    });
  }

  [Test]
  public void GivenByteData_WhenTheHistogramPaints_ThenItDrawsABarPerValue() {
    var control = new HistogramControl();
    // Every byte value present, so every one of the 256 bars has something to draw.
    var data = new byte[64 * 1024];
    for (var i = 0; i < data.Length; ++i) data[i] = (byte)i;
    control.SetData(data);

    var graphics = Paint(control, 420, 160);

    AssertRendered(graphics, "the histogram");
    Assert.That(graphics.Counts.GetValueOrDefault("FillRectangle"), Is.GreaterThanOrEqualTo(257),
      "one bar per byte value, plus the ground");
  }

  [Test]
  public void GivenRegionProfiles_WhenTheEntropyBarPaints_ThenItDrawsABandPerRegion() {
    var control = new EntropyBarControl();
    var regions = Enumerable.Range(0, 24)
      .Select(i => new RegionProfile(
        Offset: i * 4096L,
        Length: 4096,
        Entropy: 1.0 + 7.0 * (i % 8) / 7.0,
        ChiSquare: 100 + i,
        Mean: 96 + i,
        Classification: "test"))
      .ToList();
    control.SetRegions(regions, 24 * 4096L);

    var graphics = Paint(control, 520, 48);

    AssertRendered(graphics, "the entropy bar");
    Assert.That(graphics.Counts.GetValueOrDefault("FillRectangle"), Is.GreaterThanOrEqualTo(24));
  }

  [Test]
  public void GivenARatio_WhenTheRatioBarPaintsInItsOwnColumn_ThenItDrawsTheFilledBox() {
    // The properties window gives it a tall, narrow column.
    var graphics = Paint(new RatioBarControl { Ratio = 37 }, 52, 300);

    AssertRendered(graphics, "the ratio bar");
    Assert.That(graphics.Counts.GetValueOrDefault("DrawImage"), Is.EqualTo(1),
      "the box is rasterised once and blitted");
  }

  /// <summary>
  /// The isometric rise comes from the width, so in a cell wider than it is tall there is no room
  /// between the top and bottom faces and the box inverts. It has to decline instead.
  /// </summary>
  [Test]
  public void GivenACellTooShortForTheBox_WhenTheRatioBarPaints_ThenItDrawsOnlyItsGround() {
    var graphics = Paint(new RatioBarControl { Ratio = 37 }, 240, 24);

    Assert.Multiple(() => {
      Assert.That(graphics.Counts.GetValueOrDefault("DrawImage"), Is.Zero, "no box should be drawn");
      Assert.That(graphics.DistinctColors, Is.EqualTo(1), "just the ground");
      Assert.That(100.0 * graphics.PaintedPixels / (240 * 24), Is.GreaterThan(99.0));
    });
  }

  /// <summary>
  /// The legend's swatches are sampled from the same palette the bar paints with, so a change to
  /// one shows up in the other. This checks they still agree at the ends of the ramp.
  /// </summary>
  [Test]
  public void GivenTheEntropyLegend_WhenItPaints_ThenItsSwatchesComeFromTheBarsPalette() {
    var control = new EntropyLegendControl();
    var graphics = Paint(control, control.PreferredWidth, 20);

    Assert.Multiple(() => {
      Assert.That(graphics.Counts.GetValueOrDefault("FillRoundedRectangle"), Is.EqualTo(5),
        "one swatch per entropy band");
      Assert.That(graphics.Counts.GetValueOrDefault("DrawText"), Is.EqualTo(5),
        "each swatch is labelled");
    });

    // The plain and random ends of the ramp have to be present as actual pixels.
    var painted = graphics.Pixels.Select(Color.FromArgb).ToHashSet();
    Assert.Multiple(() => {
      Assert.That(painted, Does.Contain(EntropyPalette.ToColor(0.0)), "the plaintext swatch");
      Assert.That(painted, Does.Contain(EntropyPalette.ToColor(8.0)), "the random/encrypted swatch");
    });
  }

  [Test]
  public void GivenTheEntropyLegend_WhenItIsNarrowerThanItNeeds_ThenItClipsInsteadOfOverflowing() {
    var control = new EntropyLegendControl();
    var graphics = Paint(control, 60, 20);

    Assert.Multiple(() => {
      Assert.That(graphics.Counts.GetValueOrDefault("FillRoundedRectangle"), Is.LessThan(5),
        "a 60px strip cannot show all five bands");
      Assert.That(graphics.Counts.GetValueOrDefault("FillRoundedRectangle"), Is.GreaterThan(0),
        "but it should still show what fits");
    });
  }

  [TestCaseSource(nameof(BlockMapViews))]
  public void GivenABlockMap_WhenItPaints_ThenEveryProjectionRenders(BlockMapView view) {
    var control = new BlockMapControl {
      BlockMap = BuildBlockMap(),
      ImageSize = 64L * 1024 * 1024,
      ViewMode = view,
      Geometry = new MediaGeometry(BytesPerSector: 512, SectorsPerTrack: 63, Heads: 8, TotalSectors: 131072),
      ReadHead = 12L * 1024 * 1024,
      WriteHead = 40L * 1024 * 1024,
    };

    var graphics = Paint(control, 520, 300);

    AssertRendered(graphics, $"the {view} block map");
    Assert.Multiple(() => {
      Assert.That(graphics.Counts.GetValueOrDefault("DrawImage"), Is.EqualTo(1),
        "the map is composed into one buffer and blitted once");
      Assert.That(graphics.DistinctColors, Is.GreaterThan(20),
        "block kinds and classifications should produce many colours");
    });
  }

  public static IEnumerable<BlockMapView> BlockMapViews => Enum.GetValues<BlockMapView>();

  [Test]
  public void GivenRows_WhenTheVirtualRowViewPaints_ThenItDrawsOnlyTheVisibleOnes() {
    var painted = new List<int>();
    var control = new VirtualRowView { RowCount = 5000, RowHeight = 20, ContentWidth = 400 };
    control.RenderRow = context => {
      painted.Add(context.Index);
      context.Graphics.FillRectangle(context.Index % 2 == 0 ? Color.White : Color.Gainsboro, context.Bounds);
      context.Graphics.DrawText(
        $"row {context.Index}", DefaultTheme.Instance.DefaultFont, Color.Black,
        context.Bounds, ContentAlignment.MiddleLeft);
    };

    var graphics = Paint(control, 480, 200);

    Assert.Multiple(() => {
      Assert.That(painted, Is.Not.Empty, "nothing was rendered");
      Assert.That(painted.Count, Is.LessThan(30),
        "a 200px viewport over 20px rows must not render all 5000");
      Assert.That(painted, Is.Ordered, "rows should be rendered top to bottom");
      Assert.That(graphics.LeakedClips, Is.Zero);
    });
  }

  [Test]
  public void GivenMixedEntropyData_WhenTheHeatmapPaints_ThenTilesTakeTheirColourFromTheirContent() {
    var grid = new HeatmapGridControl();
    var data = new byte[512 * 1024];
    for (var i = 0; i < data.Length; ++i)
      data[i] = i < data.Length / 2 ? (byte)(i % 17) : (byte)(i * 2654435761u >> 13);
    grid.OpenStream(new MemoryStream(data), "sample.bin");

    // The grid itself only paints its ground; the tiles are a nested control.
    var tiles = (Control)grid.GetType()
      .GetField("_tiles", BindingFlags.Instance | BindingFlags.NonPublic)!
      .GetValue(grid)!;

    var graphics = Paint(tiles, 520, 300);

    AssertRendered(graphics, "the heatmap tiles");
    Assert.That(graphics.Counts.GetValueOrDefault("FillRectangle"), Is.GreaterThan(100),
      "one fill per tile");
  }

  private static List<DefragBlockInfo> BuildBlockMap() {
    var blocks = new List<DefragBlockInfo>();
    var random = new Random(1234);

    for (var offset = 0L; offset < 64L * 1024 * 1024;) {
      var size = random.Next(4096, 256 * 1024);
      var kind = random.Next(10) switch {
        < 6 => DefragBlockKind.Used,
        < 8 => DefragBlockKind.Free,
        8 => DefragBlockKind.MetadataReserved,
        _ => DefragBlockKind.InProgress,
      };

      blocks.Add(new(offset, size, kind, FileName: kind == DefragBlockKind.Used ? $"file{blocks.Count}.bin" : null));
      offset += size;
    }

    return blocks;
  }
}
