// HA's ASC method (LZ77 with adaptive arithmetic-coded tokens), decoder implemented from
// docs/HA-ON-DISK.md §3.

namespace Compression.Core.Dictionary.Ha;

/// <summary>A binary-indexed frequency tree with N leaves (docs/HA-ON-DISK.md §3.2).</summary>
internal sealed class HaFrequencyTree {
  private readonly int[] _nodes;
  private readonly int _leaves;
  private readonly int _limit;

  public HaFrequencyTree(int leaves, int limit, int initial) {
    this._leaves = leaves;
    this._limit = limit;
    this._nodes = new int[2 * leaves];
    Array.Fill(this._nodes, initial, leaves, leaves);
    this._Rebuild();
  }

  public int Leaves => this._leaves;

  public int Root => this._nodes[1];

  public int Leaf(int symbol) => this._nodes[this._leaves + symbol];

  private void _Rebuild() {
    for (var i = this._leaves - 1; i > 0; --i)
      this._nodes[i] = this._nodes[2 * i] + this._nodes[2 * i + 1];
  }

  public void Add(int symbol, int step) {
    for (var i = this._leaves + symbol; i > 0; i >>= 1)
      this._nodes[i] += step;
    if (this._nodes[1] >= this._limit)
      this.Rescale();
  }

  public void Rescale() {
    for (var i = this._leaves; i < 2 * this._leaves; ++i)
      if (this._nodes[i] > 1)
        this._nodes[i] >>= 1;
    this._Rebuild();
  }

  public void Zero(int symbol) {
    var value = this._nodes[this._leaves + symbol];
    for (var i = this._leaves + symbol; i > 0; i >>= 1)
      this._nodes[i] -= value;
  }

  /// <summary>The leaf whose cumulative range holds <paramref name="target"/>, and that range's start.</summary>
  public int Find(int target, out int cumulative) {
    var index = 2;
    cumulative = 0;
    while (true) {
      if (cumulative + this._nodes[index] <= target) {
        cumulative += this._nodes[index];
        ++index;
      }

      if (index >= this._leaves)
        return index - this._leaves;
      index <<= 1;
    }
  }
}

/// <summary>
/// Decoder for HA's ASC method: LZ77 over a 31 200-byte window whose literals, lengths and
/// distances are coded with adaptive frequency trees and HA's arithmetic coder.
/// </summary>
/// <remarks>Specification: docs/HA-ON-DISK.md §3, verified against HA 0.999's ha.exe.</remarks>
public static class HaAsc {
  private const int Window = 31200;
  private const int TypeStep = 40;
  private const int TypeLimit = 6000;

  /// <summary>Decodes one ASC stream.</summary>
  /// <param name="source">The member's data (the stream terminates itself).</param>
  /// <param name="expectedLength">The member's original length; producing more is refused.</param>
  /// <returns>The decoded bytes.</returns>
  /// <exception cref="InvalidDataException">The stream is corrupt or longer than <paramref name="expectedLength"/>.</exception>
  public static byte[] Decode(ReadOnlyMemory<byte> source, int expectedLength) {
    ArgumentOutOfRangeException.ThrowIfNegative(expectedLength);

    var decoder = new HaArithmeticDecoder(source);
    var output = new byte[expectedLength];
    var produced = 0;

    var chars = new HaFrequencyTree(256, 1000, 0);
    var charEscapes = new HaFrequencyTree(256, 1000, 1);
    var lengths = new HaFrequencyTree(64, 6000, 0);
    var lengthEscapes = new HaFrequencyTree(64, 6000, 1);
    var classes = new HaFrequencyTree(16, 6000, 0);
    classes.Add(0, 24);
    var charEscape = 1;
    var lengthEscape = 8;
    Span<int> charWeight = [TypeStep, TypeStep, TypeStep, TypeStep];
    Span<int> pairWeight = [TypeStep, TypeStep, TypeStep, TypeStep];
    var context = 0;
    var seen = 0;
    var enabledClasses = 1;
    var classLimit = 1;

    while (true) {
      var a = charWeight[context];
      var both = a + pairWeight[context];
      var target = decoder.Threshold(both + 1);

      if (target < a) {
        decoder.Consume(0, a, both + 1);
        charWeight[context] += TypeStep;
        if (both >= TypeLimit)
          HalveWeights(charWeight, pairWeight, context);
        context = (context << 1) & 3;

        var c = DecodeSymbol(decoder, chars, charEscapes, ref charEscape, escapeStep: 1, radius: 8, step: 1);
        if (chars.Leaf(c) == 3)
          charEscape = charEscape > 1 ? charEscape - 1 : 1;

        if (produced == expectedLength)
          throw new InvalidDataException("ASC stream is longer than the member's original length.");
        output[produced++] = (byte)c;
        if (seen < Window)
          ++seen;
        continue;
      }

      if (target >= both) {
        decoder.Consume(both, both + 1, both + 1);
        break;
      }

      decoder.Consume(a, both, both + 1);
      pairWeight[context] += TypeStep;
      if (both >= TypeLimit)
        HalveWeights(charWeight, pairWeight, context);
      context = ((context << 1) | 1) & 3;

      while (seen > classLimit) {
        classes.Add(enabledClasses++, 24);
        classLimit <<= 1;
      }

      var distanceClass = classes.Find(decoder.Threshold(classes.Root), out var start);
      decoder.Consume(start, start + classes.Leaf(distanceClass), classes.Root);
      classes.Add(distanceClass, 24);

      var distance = distanceClass;
      if (distanceClass > 1) {
        var top = 1 << (distanceClass - 1);
        var span = top == classLimit >> 1 ? seen - (classLimit >> 1) : top;
        if (span <= 0)
          throw new InvalidDataException("ASC distance class is not reachable yet.");
        var low = decoder.Threshold(span);
        decoder.Consume(low, low + 1, span);
        distance = top + low;
      }

      var slot = DecodeSymbol(decoder, lengths, lengthEscapes, ref lengthEscape, escapeStep: 8, radius: 4, step: 8);
      if (lengths.Leaf(slot) == 24)
        lengthEscape = lengthEscape > 8 ? lengthEscape - 8 : 1;

      int length;
      if (slot == 15)
        length = 783;
      else if (slot >= 16) {
        var extra = decoder.Threshold(16);
        decoder.Consume(extra, extra + 1, 16);
        length = ((slot - 16) << 4) + extra + 15;
      } else
        length = slot;
      length += 3;

      if (seen < Window)
        seen = Math.Min(Window, seen + length);

      if (distance >= produced)
        throw new InvalidDataException("ASC match reaches before the start of the member.");
      if (length > expectedLength - produced)
        throw new InvalidDataException("ASC stream is longer than the member's original length.");
      for (var from = produced - distance - 1; length > 0; --length)
        output[produced++] = output[from++];
    }

    if (produced != expectedLength)
      throw new InvalidDataException($"ASC stream ended after {produced} of {expectedLength} bytes.");
    return output;
  }

  private static void HalveWeights(Span<int> charWeight, Span<int> pairWeight, int context) {
    charWeight[context] = Math.Max(1, charWeight[context] >> 1);
    pairWeight[context] = Math.Max(1, pairWeight[context] >> 1);
  }

  // A literal or a length slot: a known symbol from the main tree, or an escape followed by a new
  // symbol from the escape tree, whose neighbours then become likelier (§3.4).
  private static int DecodeSymbol(HaArithmeticDecoder decoder, HaFrequencyTree main, HaFrequencyTree escapes,
    ref int escapeWeight, int escapeStep, int radius, int step) {
    var total = main.Root + escapeWeight;
    var target = decoder.Threshold(total);
    int symbol;
    if (target >= main.Root) {
      decoder.Consume(main.Root, total, total);
      symbol = escapes.Find(decoder.Threshold(escapes.Root), out var start);
      decoder.Consume(start, start + escapes.Leaf(symbol), escapes.Root);
      escapes.Zero(symbol);
      escapeWeight = escapes.Root != 0 ? escapeWeight + escapeStep : 0;

      var end = Math.Min(escapes.Leaves - 1, symbol + radius);
      for (var k = Math.Max(0, symbol - radius); k < end; ++k)
        if (escapes.Leaf(k) != 0)
          escapes.Add(k, 1);
    } else {
      symbol = main.Find(target, out var start);
      decoder.Consume(start, start + main.Leaf(symbol), total);
    }

    main.Add(symbol, step);
    return symbol;
  }
}
