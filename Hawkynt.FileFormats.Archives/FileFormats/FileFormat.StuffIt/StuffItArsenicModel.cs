namespace FileFormat.StuffIt;

/// <summary>
/// Adaptive frequency model of StuffIt's Arsenic method, shared by its decoder and encoder.
/// Symbols first..last start with equal counts of <c>increment</c>. Each coded symbol gains
/// <c>increment</c>, and once the total passes <c>limit</c> every count is halved, rounding up.
/// </summary>
/// <remarks>Converted from XADMaster's XADStuffItArsenicHandle (LGPL-2.1-or-later).</remarks>
internal sealed class StuffItArsenicModel {
  private readonly int _increment, _limit;
  private readonly int[] _freq;

  public StuffItArsenicModel(int first, int last, int increment, int limit) {
    this.First = first;
    this._increment = increment;
    this._limit = limit;
    this._freq = new int[last - first + 1];
    this.Reset();
  }

  public int First { get; }
  public int Total { get; private set; }
  public int Count => this._freq.Length;
  public int Freq(int index) => this._freq[index];
  public int Symbol(int index) => this.First + index;

  public void Reset() {
    this.Total = this._increment * this._freq.Length;
    Array.Fill(this._freq, this._increment);
  }

  public void Increase(int index) {
    this._freq[index] += this._increment;
    this.Total += this._increment;
    if (this.Total <= this._limit) return;
    this.Total = 0;
    for (var i = 0; i < this._freq.Length; ++i) {
      this._freq[i] = (this._freq[i] + 1) >> 1;
      this.Total += this._freq[i];
    }
  }
}
