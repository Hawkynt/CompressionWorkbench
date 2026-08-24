namespace Compression.Core.Dictionary.Quantum;

/// <summary>
/// The set of models a Quantum folder codes against, kept together because they are
/// created and carried as one and because their sizes depend on the window.
/// </summary>
internal sealed class QuantumModels {

  /// <summary>What kind of item comes next.</summary>
  public QuantumModel Selector { get; }

  /// <summary>One model per quarter of the byte range.</summary>
  public QuantumModel[] Literals { get; }

  /// <summary>One distance model each for selectors 4, 5 and 6.</summary>
  public QuantumModel[] Positions { get; }

  /// <summary>Lengths, for matches of five bytes or more.</summary>
  public QuantumModel Lengths { get; }

  /// <summary>Builds the models a folder with the given window needs.</summary>
  /// <param name="windowBits">The window, as a power of two.</param>
  public QuantumModels(int windowBits) {
    this.Selector = new(QuantumConstants.SelectorSymbols);
    this.Literals = new QuantumModel[4];
    for (var i = 0; i < 4; ++i)
      this.Literals[i] = new(QuantumConstants.LiteralSymbolsPerModel, i * QuantumConstants.LiteralSymbolsPerModel);

    this.Positions = new QuantumModel[3];
    for (var selector = 4; selector <= 6; ++selector)
      this.Positions[selector - 4] = new(QuantumConstants.PositionSlots(selector, windowBits));

    this.Lengths = new(QuantumConstants.LengthSymbols);
  }

  /// <summary>
  /// Whether writing a literal of this value would carry a model into the rescale
  /// that sorts it, which is where a folder has to end.
  /// </summary>
  /// <param name="value">The byte about to be written.</param>
  /// <returns><see langword="true"/> if the folder must close first.</returns>
  public bool LiteralWouldSort(byte value)
    => this.Selector.NextUpdateWouldSort || this.Literals[value >> 6].NextUpdateWouldSort;

  /// <summary>
  /// Whether writing this match would carry a model into the rescale that sorts it.
  /// </summary>
  /// <param name="length">The match length.</param>
  /// <returns><see langword="true"/> if the folder must close first.</returns>
  public bool MatchWouldSort(int length) {
    var selector = QuantumConstants.SelectorForLength(length);
    if (this.Selector.NextUpdateWouldSort || this.Positions[selector - 4].NextUpdateWouldSort)
      return true;

    return selector == 6 && this.Lengths.NextUpdateWouldSort;
  }

  /// <summary>Whether a match of this length and distance can be written at all.</summary>
  /// <param name="length">The match length.</param>
  /// <param name="distance">The match distance.</param>
  /// <returns><see langword="true"/> if the selector's distance model reaches that far.</returns>
  public bool CanCode(int length, int distance) {
    var selector = QuantumConstants.SelectorForLength(length);
    var (slot, _) = QuantumConstants.PositionSlot(distance);
    return slot < this.Positions[selector - 4].SymbolCount;
  }
}
