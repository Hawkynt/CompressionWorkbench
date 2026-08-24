using Compression.Core.Dictionary.Quantum;

namespace Compression.Tests.Dictionary;

/// <summary>
/// Quantum, as a cabinet carries it.
/// </summary>
/// <remarks>
/// The numbers checked here were measured against libmspack rather than chosen, so a
/// change that breaks one of them is a change that stops us reading and writing what
/// the reference reader does. The derivation is in <c>docs/QUANTUM-ON-DISK.md</c>.
/// </remarks>
[TestFixture]
public class QuantumTests {

  // ---------------------------------------------------------------------------
  // The tables
  // ---------------------------------------------------------------------------

  [Category("ThemVsUs")]
  [Test]
  public void PositionSlots_AreTheDoublingTable() {
    Assert.Multiple(() => {
      Assert.That(QuantumConstants.PositionExtraBits, Has.Length.EqualTo(42));
      Assert.That(QuantumConstants.PositionExtraBits[..4], Is.EqualTo(new[] { 0, 0, 0, 0 }).AsCollection);
      Assert.That(QuantumConstants.PositionBases[..10],
        Is.EqualTo(new[] { 0, 1, 2, 3, 4, 6, 8, 12, 16, 24 }).AsCollection);
    });
  }

  [Category("ThemVsUs")]
  [Test]
  public void LengthSlots_AreSixFlatThenFourAtEachWidth() {
    Assert.Multiple(() => {
      Assert.That(QuantumConstants.LengthExtraBits, Has.Length.EqualTo(27));
      Assert.That(QuantumConstants.LengthExtraBits[..6], Is.All.Zero);
      Assert.That(QuantumConstants.LengthBases[..8],
        Is.EqualTo(new[] { 0, 1, 2, 3, 4, 5, 6, 8 }).AsCollection);
    });
  }

  /// <summary>
  /// A distance model follows the window, but each selector has a ceiling of its own.
  /// Measured by sweeping each model's width against cabextract: at a 2 MB window the
  /// three answers are 24, 36 and 42.
  /// </summary>
  [Category("ThemVsUs")]
  [TestCase(21, 24, 36, 42)]
  [TestCase(18, 24, 36, 36)]
  [TestCase(13, 24, 26, 26)]
  [TestCase(10, 20, 20, 20)]
  public void PositionSlotCount_IsCappedPerSelector(int windowBits, int three, int four, int longer) {
    Assert.Multiple(() => {
      Assert.That(QuantumConstants.PositionSlots(4, windowBits), Is.EqualTo(three));
      Assert.That(QuantumConstants.PositionSlots(5, windowBits), Is.EqualTo(four));
      Assert.That(QuantumConstants.PositionSlots(6, windowBits), Is.EqualTo(longer));
    });
  }

  [Category("HappyPath")]
  [Test]
  public void SlotLookups_InvertTheTables() {
    Assert.Multiple(() => {
      for (var distance = 1; distance <= 5000; ++distance) {
        var (slot, extra) = QuantumConstants.PositionSlot(distance);
        Assert.That(QuantumConstants.PositionBases[slot] + extra + 1, Is.EqualTo(distance));
        Assert.That(extra, Is.LessThan(1 << QuantumConstants.PositionExtraBits[slot]));
      }

      for (var length = 5; length <= QuantumConstants.MaxMatch; ++length) {
        var (slot, extra) = QuantumConstants.LengthSlot(length);
        Assert.That(5 + QuantumConstants.LengthBases[slot] + extra, Is.EqualTo(length));
        Assert.That(extra, Is.LessThan(1 << QuantumConstants.LengthExtraBits[slot]));
      }
    });
  }

  // ---------------------------------------------------------------------------
  // The model
  // ---------------------------------------------------------------------------

  [Category("HappyPath")]
  [Test]
  public void Model_StartsUniformAndInOrder() {
    var model = new QuantumModel(4, 64);
    Assert.Multiple(() => {
      Assert.That(model.SymbolCount, Is.EqualTo(4));
      Assert.That(model.TotalFrequency, Is.EqualTo(4));
      Assert.That(model.SymbolAt(0), Is.EqualTo(64));
      Assert.That(model.SymbolAt(3), Is.EqualTo(67));
      Assert.That(model.IndexOf(66), Is.EqualTo(2));
      Assert.That(model.CumulativeFrom(0), Is.EqualTo(4));
      Assert.That(model.CumulativeFrom(4), Is.EqualTo(0));
    });
  }

  [Category("HappyPath")]
  [Test]
  public void Model_ObservingASymbol_AddsEight() {
    var model = new QuantumModel(4);
    model.Update(1);
    Assert.Multiple(() => {
      Assert.That(model.FrequencyAt(1), Is.EqualTo(9));
      Assert.That(model.TotalFrequency, Is.EqualTo(12));
    });
  }

  /// <summary>
  /// The rescale halves the cumulative counts, rounding down, and then walks the
  /// result back to strictly decreasing. This exact table came off libmspack: counts
  /// of 817, 1017, 945, 1025 and three ones become 408, 509, 472, 511 and three ones.
  /// Halving each frequency on its own gives 409, 509, 473, 513 instead, which is
  /// close enough to look right and wrong enough to part company at the first rescale.
  /// </summary>
  [Category("ThemVsUs")]
  [Test]
  public void Model_Rescale_HalvesTheCumulativeCountsAndRepairsThem() {
    var model = new QuantumModel(QuantumConstants.SelectorSymbols);

    // walk to 817, 1017, 945, 1017 and three ones, which totals 3799 — one update
    // short of the limit, so the walk itself never rescales
    int[] onTheWayUp = [817, 1017, 945, 1017, 1, 1, 1];
    for (var symbol = 0; symbol < onTheWayUp.Length; ++symbol)
      for (var step = (onTheWayUp[symbol] - 1) / QuantumConstants.ModelIncrement; step > 0; --step)
        model.Update(symbol);

    Assume.That(model.Rescales, Is.Zero, "the walk itself must not rescale");
    Assume.That(model.TotalFrequency, Is.EqualTo(3799));

    model.Update(3); // the fourth count reaches 1025 and the total 3807

    Assert.Multiple(() => {
      Assert.That(model.Rescales, Is.EqualTo(1));
      Assert.That(model.TotalFrequency, Is.EqualTo(1903));
      int[] got = [.. Enumerable.Range(0, model.SymbolCount).Select(model.FrequencyAt)];
      Assert.That(got, Is.EqualTo(new[] { 408, 509, 472, 511, 1, 1, 1 }).AsCollection);
    });
  }

  [Category("EdgeCase")]
  [Test]
  public void Model_NeverLeavesASymbolUncodeable() {
    var model = new QuantumModel(64);
    for (var round = 0; round < 600; ++round)
      model.Update(round % 3); // three busy symbols, sixty-one idle ones

    Assume.That(model.Rescales, Is.GreaterThan(0));
    Assert.Multiple(() => {
      for (var i = 0; i < model.SymbolCount; ++i)
        Assert.That(model.FrequencyAt(i), Is.GreaterThan(0), $"symbol at {i} fell to zero");
    });
  }

  /// <summary>
  /// A model's fourth rescale sorts it into descending order of count, and every
  /// fiftieth rescale after that sorts it again. Between them the order stands.
  /// </summary>
  [Category("ThemVsUs")]
  [Test]
  public void Model_SortsItselfAtTheFourthRescaleAndEveryFiftiethAfter() {
    var model = new QuantumModel(QuantumConstants.SelectorSymbols);
    var sorted = new List<int>();
    var seen = 0;
    for (var round = 0; round < 200_000; ++round) {
      // an uneven diet, so the counts are worth sorting
      model.Update(round % 3 == 0 ? 0 : round % QuantumConstants.SelectorSymbols);
      if (model.Rescales == seen) continue;

      seen = model.Rescales;
      var descending = true;
      for (var i = 1; i < model.SymbolCount; ++i)
        if (model.FrequencyAt(i) > model.FrequencyAt(i - 1)) descending = false;
      if (descending) sorted.Add(seen);
    }

    Assert.Multiple(() => {
      Assert.That(sorted, Does.Contain(QuantumConstants.RescalesBeforeSort));
      Assert.That(sorted, Does.Contain(QuantumConstants.RescalesBeforeSort + QuantumConstants.RescalesBetweenSorts));
    });
  }

  // ---------------------------------------------------------------------------
  // The coder
  // ---------------------------------------------------------------------------

  [Category("HappyPath")]
  [Test]
  public void Coder_CarriesSymbolsAndRawBitsTogether() {
    var encoder = new QuantumRangeEncoder();
    var writeModels = new QuantumModels(15);
    int[] symbols = [3, 1, 0, 2, 2, 1, 3, 0];
    foreach (var symbol in symbols) {
      encoder.Encode(writeModels.Selector, writeModels.Selector.IndexOf(symbol));
      encoder.EncodeRaw(symbol, 2);
    }

    var stream = encoder.Finish();
    var decoder = new QuantumRangeDecoder(stream);
    var readModels = new QuantumModels(15);
    Assert.Multiple(() => {
      foreach (var symbol in symbols) {
        Assert.That(decoder.Decode(readModels.Selector), Is.EqualTo(symbol));
        Assert.That(decoder.DecodeRaw(2), Is.EqualTo(symbol));
      }
    });
  }

  [Category("EdgeCase")]
  [Test]
  public void Coder_LeavesTheReaderTwoBytesItNeverUses() {
    var encoder = new QuantumRangeEncoder();
    var models = new QuantumModels(15);
    encoder.Encode(models.Selector, 0);
    Assert.That(encoder.Finish(), Has.Length.GreaterThanOrEqualTo(QuantumConstants.TrailingSlackBytes));
  }

  // ---------------------------------------------------------------------------
  // Round trips
  // ---------------------------------------------------------------------------

  [Category("HappyPath")]
  [Test]
  public void RoundTrip_Text() => AssertRoundTrips(Repeat("the quick brown fox jumps over the lazy dog. ", 200));

  [Category("HappyPath")]
  [Test]
  public void RoundTrip_Source() => AssertRoundTrips(Repeat("int main(void) { return 0; }\n", 200));

  [Category("EdgeCase")]
  [Test]
  public void RoundTrip_Zeros() => AssertRoundTrips(new byte[20_000]);

  [Category("EdgeCase")]
  [Test]
  public void RoundTrip_SingleByte() => AssertRoundTrips([0x51]);

  [Category("EdgeCase")]
  [Test]
  public void RoundTrip_TwoBytes() => AssertRoundTrips([0x51, 0x51]);

  [Category("HappyPath")]
  [Test]
  public void RoundTrip_Runs() {
    var data = new byte[16_000];
    for (var i = 0; i < data.Length; ++i)
      data[i] = (byte)(i / 400);

    AssertRoundTrips(data);
  }

  [Category("HappyPath")]
  [Test]
  public void RoundTrip_Random() {
    var data = new byte[600];
    new Random(1234).NextBytes(data);
    AssertRoundTrips(data);
  }

  [Category("HappyPath")]
  [TestCase(10)]
  [TestCase(15)]
  [TestCase(21)]
  public void RoundTrip_EveryWindow(int windowBits) {
    var data = Repeat("cabinets carry quantum. ", 40);
    var folder = QuantumCompressor.CompressFolder(data, 0, windowBits);
    Assume.That(folder.Consumed, Is.EqualTo(data.Length));
    Assert.That(QuantumDecompressor.Decompress(folder.Compressed, folder.Consumed, windowBits),
      Is.EqualTo(data).AsCollection);
  }

  [Category("EdgeCase")]
  [TestCase(9)]
  [TestCase(22)]
  public void Compressor_RefusesAWindowNoCabinetNames(int windowBits) {
    Assert.Throws<ArgumentOutOfRangeException>(() => QuantumCompressor.CompressFolder([1, 2, 3], 0, windowBits));
  }

  /// <summary>
  /// A folder of several data blocks is one stream in the models and a fresh one in
  /// the coder, and a reader that restarts the models reads the second block as noise.
  /// </summary>
  [Category("HappyPath")]
  [Test]
  public void Compress_CarriesItsModelsFromOneBlockToTheNext() {
    var data = new byte[80_000];
    new Random(99).NextBytes(data);

    var blocks = QuantumCompressor.CompressBlocks(data, 15);
    var reader = new QuantumDecompressor.FolderReader(15);
    var rebuilt = new List<byte>();
    foreach (var block in blocks)
      rebuilt.AddRange(reader.ReadBlock(block.Compressed, block.Consumed));

    Assert.Multiple(() => {
      Assert.That(blocks, Has.Count.GreaterThan(1), "80 KB does not fit in one data block");
      Assert.That(blocks.Sum(b => b.Consumed), Is.EqualTo(data.Length));
      Assert.That(rebuilt, Is.EqualTo(data).AsCollection);
    });
  }

  /// <summary>
  /// A block carries a whole block's worth, whatever the data, now that the rescale
  /// that sorts a model is measured rather than avoided.
  /// </summary>
  [Category("ThemVsUs")]
  [Test]
  public void Compress_FillsAWholeBlockWithDataThatResistsIt() {
    var data = new byte[40_000];
    new Random(1234).NextBytes(data);

    var first = QuantumCompressor.CompressFolder(data, 0, 15);
    Assert.Multiple(() => {
      Assert.That(first.Consumed, Is.EqualTo(QuantumConstants.MaxBlockSize));
      Assert.That(QuantumDecompressor.Decompress(first.Compressed, first.Consumed, 15),
        Is.EqualTo(data[..first.Consumed]).AsCollection);
    });
  }

  [Category("EdgeCase")]
  [Test]
  public void Decompress_NothingWanted_GivesNothingBack() {
    Assert.That(QuantumDecompressor.Decompress(default, 0, 15), Is.Empty);
  }

  [Category("EdgeCase")]
  [TestCase(9)]
  [TestCase(22)]
  public void Decompress_RefusesAWindowNoCabinetNames(int windowBits) {
    Assert.Throws<ArgumentOutOfRangeException>(() => QuantumDecompressor.Decompress(default, 1, windowBits));
  }

  // ---------------------------------------------------------------------------
  // The building block
  // ---------------------------------------------------------------------------

  [Category("HappyPath")]
  [Test]
  public void BuildingBlock_RoundTripsWhatTheCompressorSplits() {
    var block = new QuantumBuildingBlock();
    var data = new byte[6_000];
    new Random(7).NextBytes(data);

    Assert.That(block.Decompress(block.Compress(data)), Is.EqualTo(data).AsCollection);
  }

  [Category("EdgeCase")]
  [Test]
  public void BuildingBlock_Empty() {
    var block = new QuantumBuildingBlock();
    Assert.That(block.Compress([]), Is.Empty);
  }

  private static byte[] Repeat(string phrase, int times) {
    var one = System.Text.Encoding.ASCII.GetBytes(phrase);
    var data = new byte[one.Length * times];
    for (var i = 0; i < data.Length; ++i)
      data[i] = one[i % one.Length];

    return data;
  }

  private static void AssertRoundTrips(byte[] data) {
    var folders = QuantumCompressor.Compress(data, 15);
    var rebuilt = new List<byte>();
    foreach (var folder in folders)
      rebuilt.AddRange(QuantumDecompressor.Decompress(folder.Compressed, folder.Consumed, 15));

    Assert.That(rebuilt, Is.EqualTo(data).AsCollection);
  }
}
