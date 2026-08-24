namespace Compression.Core.Dictionary.Lzms;

/// <summary>
/// One adaptive bit probability: the count of zeros in a sixty-four bit history.
/// </summary>
internal sealed class LzmsProbability {
  private ulong _recent = LzmsConstants.InitialRecentBits;
  private int _zeros = LzmsConstants.ProbDenominator - System.Numerics.BitOperations.PopCount(LzmsConstants.InitialRecentBits);

  /// <summary>The probability of a zero, never allowed to reach either end.</summary>
  public int Probability => this._zeros == 0 ? 1
    : this._zeros == LzmsConstants.ProbDenominator ? LzmsConstants.ProbDenominator - 1
    : this._zeros;

  /// <summary>Folds a coded bit into the history, dropping the oldest.</summary>
  public void Update(int bit) {
    var leaving = (int)((this._recent >> (LzmsConstants.ProbDenominator - 1)) & 1);
    this._zeros += leaving - bit;
    this._recent = (this._recent << 1) | (uint)bit;
  }
}
