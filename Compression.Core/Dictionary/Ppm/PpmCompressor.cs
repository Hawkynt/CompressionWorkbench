using System.Buffers.Binary;

namespace Compression.Core.Dictionary.Ppm;

/// <summary>
/// A clean-room implementation of PPM (Prediction by Partial Matching): a
/// finite-context statistical model whose symbol predictions are driven into an
/// adaptive arithmetic coder, so that a symbol which the model considers likely
/// costs a fraction of a bit rather than a whole byte.
/// </summary>
/// <remarks>
/// <para>
/// Implemented from the published descriptions — not ported or paraphrased from
/// any third-party source code:
/// </para>
/// <list type="bullet">
///   <item><description>
///     J. G. Cleary and I. H. Witten, "Data Compression Using Adaptive Coding
///     and Partial String Matching", IEEE Transactions on Communications 32(4),
///     1984, 396-402 — the blending-by-escape model: predict from the longest
///     context seen so far, and fall back to shorter contexts through an
///     explicit escape symbol.
///   </description></item>
///   <item><description>
///     A. Moffat, "Implementing the PPM Data Compression Scheme", IEEE
///     Transactions on Communications 38(11), 1990, 1917-1921 — escape method C
///     (the escape is given a count equal to the number of distinct symbols the
///     context has ever predicted) and full exclusion.
///   </description></item>
///   <item><description>
///     I. H. Witten, R. M. Neal and J. G. Cleary, "Arithmetic Coding for Data
///     Compression", Communications of the ACM 30(6), 1987, 520-540 — the
///     16-bit incremental arithmetic coder with underflow (bits-to-follow)
///     handling used here.
///   </description></item>
/// </list>
/// <para>
/// <b>Model.</b> Contexts of order 0 through <see cref="MaxOrder"/> are kept,
/// each a symbol-to-count table in first-seen order. A symbol is coded from the
/// longest context that both exists and predicts it. Where the longest context
/// does not predict it, an escape is coded in that context and coding drops to
/// the next shorter one; a context that has never been seen costs nothing at
/// all, since its escape probability is one. Below order 0 sits a fixed order
/// -1 context giving every byte value equal probability, which guarantees any
/// symbol can always be coded.
/// </para>
/// <para>
/// <b>Escape method C.</b> The escape is allotted a frequency equal to the
/// number of distinct symbols the context predicts, so a context that has been
/// surprising in the past is cheaper to escape out of.
/// </para>
/// <para>
/// <b>Full exclusion.</b> Escaping from a context proves the symbol is none of
/// the ones that context predicts, so those symbols are removed from
/// consideration in every shorter context, and their probability mass is
/// redistributed over what remains.
/// </para>
/// <para>
/// <b>Update.</b> After a symbol is coded, every context of order 0 through
/// <see cref="MaxOrder"/> that applies at that position has its count for the
/// symbol incremented. Counts are halved when a context's frequency total would
/// exceed what the arithmetic coder's 16-bit registers can carry, which also
/// makes the model adapt to drifting statistics.
/// </para>
/// <para>
/// <b>Wire format.</b> A one-byte maximum order, a four-byte little-endian
/// original length, then the arithmetic-coded symbol stream. The length header
/// terminates decoding, so no end-of-stream symbol is coded.
/// </para>
/// </remarks>
public static class PpmCompressor {
  /// <summary>The longest context the model keeps.</summary>
  public const int MaxOrder = 3;

  private const int NumSymbols = 256;

  // Witten-Neal-Cleary register layout: 16-bit code values, so the largest
  // frequency total that cannot overflow the narrowing arithmetic is 2^14 - 1.
  private const int CodeBits = 16;
  private const uint TopValue = (1u << CodeBits) - 1;
  private const uint FirstQuarter = (TopValue >> 2) + 1;
  private const uint Half = 2 * FirstQuarter;
  private const uint ThirdQuarter = 3 * FirstQuarter;
  private const int MaxFrequency = (1 << (CodeBits - 2)) - 1;

  /// <summary>Compresses data with an order-<see cref="MaxOrder"/> PPM model driving an arithmetic coder.</summary>
  /// <param name="data">The data to compress.</param>
  /// <returns>The compressed data.</returns>
  public static byte[] Compress(ReadOnlySpan<byte> data) {
    var header = new byte[5];
    header[0] = MaxOrder;
    BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(1), data.Length);
    if (data.Length == 0)
      return header;

    var model = new Model();
    var encoder = new ArithmeticEncoder(header);
    var excluded = new bool[NumSymbols];

    for (var i = 0; i < data.Length; ++i) {
      int symbol = data[i];
      Array.Clear(excluded);
      var excludedCount = 0;

      var coded = false;
      var highestOrder = Math.Min(MaxOrder, i);
      for (var order = highestOrder; order >= 0 && !coded; --order) {
        var context = model.Find(order, data, i);
        if (context == null)
          continue;

        var (escapeFrequency, symbolTotal) = context.EffectiveTotals(excluded);
        if (escapeFrequency == 0)
          continue;

        var total = symbolTotal + escapeFrequency;
        var cumulative = context.CumulativeBefore(symbol, excluded, out var frequency);
        if (frequency > 0) {
          encoder.Encode(cumulative, cumulative + frequency, total);
          coded = true;
          break;
        }

        // Escape occupies the top of the range, above every predicted symbol.
        encoder.Encode(symbolTotal, total, total);
        context.Exclude(excluded, ref excludedCount);
      }

      if (!coded) {
        // Order -1: every byte value the shorter contexts have not ruled out.
        var total = NumSymbols - excludedCount;
        var cumulative = 0;
        for (var s = 0; s < symbol; ++s)
          if (!excluded[s])
            ++cumulative;
        encoder.Encode(cumulative, cumulative + 1, total);
      }

      model.Update(data, i, symbol);
    }

    return encoder.Finish();
  }

  /// <summary>Decompresses data previously produced by <see cref="Compress"/>.</summary>
  /// <param name="data">The compressed data.</param>
  /// <returns>The original data.</returns>
  public static byte[] Decompress(ReadOnlySpan<byte> data) {
    if (data.Length < 5)
      throw new InvalidDataException("PPM: truncated header.");

    var maxOrder = data[0];
    if (maxOrder != MaxOrder)
      throw new InvalidDataException($"PPM: stream declares order {maxOrder}, this model is order {MaxOrder}.");

    var originalSize = BinaryPrimitives.ReadInt32LittleEndian(data[1..]);
    if (originalSize < 0)
      throw new InvalidDataException("PPM: negative original length.");
    if (originalSize == 0)
      return [];

    var model = new Model();
    var decoder = new ArithmeticDecoder(data, 5);
    var excluded = new bool[NumSymbols];
    var result = new byte[originalSize];

    for (var i = 0; i < originalSize; ++i) {
      Array.Clear(excluded);
      var excludedCount = 0;
      var symbol = -1;

      var highestOrder = Math.Min(maxOrder, i);
      for (var order = highestOrder; order >= 0 && symbol < 0; --order) {
        var context = model.Find(order, result, i);
        if (context == null)
          continue;

        var (escapeFrequency, symbolTotal) = context.EffectiveTotals(excluded);
        if (escapeFrequency == 0)
          continue;

        var total = symbolTotal + escapeFrequency;
        var target = decoder.Target(total);
        if (target >= symbolTotal) {
          decoder.Update(symbolTotal, total, total);
          context.Exclude(excluded, ref excludedCount);
          continue;
        }

        symbol = context.SymbolAt(target, excluded, out var cumulative, out var frequency);
        if (symbol < 0)
          throw new InvalidDataException("PPM: corrupt arithmetic-coded stream.");
        decoder.Update(cumulative, cumulative + frequency, total);
      }

      if (symbol < 0) {
        var total = NumSymbols - excludedCount;
        var target = decoder.Target(total);
        var cumulative = 0;
        for (var s = 0; s < NumSymbols; ++s) {
          if (excluded[s])
            continue;
          if (cumulative == target) {
            symbol = s;
            break;
          }
          ++cumulative;
        }
        if (symbol < 0)
          throw new InvalidDataException("PPM: corrupt arithmetic-coded stream.");
        decoder.Update(cumulative, cumulative + 1, total);
      }

      result[i] = (byte)symbol;
      model.Update(result, i, symbol);
    }

    return result;
  }

  /// <summary>
  /// One finite context: the symbols seen after it, in first-seen order, with
  /// their occurrence counts. First-seen order is part of the wire format,
  /// because it fixes where each symbol sits in the coder's frequency range.
  /// </summary>
  private sealed class Context {
    private int[] _symbols = new int[4];
    private int[] _counts = new int[4];

    public int Size;
    public int Total;

    /// <summary>Splits the context's frequency mass into (escape frequency, sum of symbol frequencies) under the current exclusion set.</summary>
    public (int Escape, int SymbolTotal) EffectiveTotals(bool[] excluded) {
      var escape = 0;
      var sum = 0;
      for (var k = 0; k < this.Size; ++k) {
        if (excluded[this._symbols[k]])
          continue;
        sum += this._counts[k];
        ++escape;
      }

      return (escape, sum);
    }

    /// <summary>Sums the frequencies of the non-excluded symbols preceding <paramref name="symbol"/>, and reports its own frequency (0 when absent or excluded).</summary>
    public int CumulativeBefore(int symbol, bool[] excluded, out int frequency) {
      var cumulative = 0;
      for (var k = 0; k < this.Size; ++k) {
        var s = this._symbols[k];
        if (excluded[s])
          continue;
        if (s == symbol) {
          frequency = this._counts[k];
          return cumulative;
        }

        cumulative += this._counts[k];
      }

      frequency = 0;
      return 0;
    }

    /// <summary>Finds the non-excluded symbol whose frequency range contains <paramref name="target"/>, or -1 when none does.</summary>
    public int SymbolAt(int target, bool[] excluded, out int cumulative, out int frequency) {
      var running = 0;
      for (var k = 0; k < this.Size; ++k) {
        var s = this._symbols[k];
        if (excluded[s])
          continue;
        var count = this._counts[k];
        if (target < running + count) {
          cumulative = running;
          frequency = count;
          return s;
        }

        running += count;
      }

      cumulative = 0;
      frequency = 0;
      return -1;
    }

    /// <summary>Rules out every symbol this context predicts, because escaping from it proved the symbol is none of them.</summary>
    public void Exclude(bool[] excluded, ref int excludedCount) {
      for (var k = 0; k < this.Size; ++k) {
        var s = this._symbols[k];
        if (excluded[s])
          continue;
        excluded[s] = true;
        ++excludedCount;
      }
    }

    /// <summary>Increments the count for <paramref name="symbol"/>, appending it when first seen, and halves the table when it would outgrow the coder.</summary>
    public void Increment(int symbol) {
      for (var k = 0; k < this.Size; ++k) {
        if (this._symbols[k] != symbol)
          continue;
        ++this._counts[k];
        ++this.Total;
        this.RescaleIfNeeded();
        return;
      }

      if (this.Size == this._symbols.Length) {
        Array.Resize(ref this._symbols, this.Size * 2);
        Array.Resize(ref this._counts, this.Size * 2);
      }

      this._symbols[this.Size] = symbol;
      this._counts[this.Size] = 1;
      ++this.Size;
      ++this.Total;
      this.RescaleIfNeeded();
    }

    private void RescaleIfNeeded() {
      if (this.Total + this.Size <= MaxFrequency)
        return;

      var total = 0;
      for (var k = 0; k < this.Size; ++k) {
        // Round up so no symbol is ever forgotten; a count of one stays one.
        this._counts[k] = (this._counts[k] + 1) / 2;
        total += this._counts[k];
      }

      this.Total = total;
    }
  }

  /// <summary>The set of contexts of every order the model keeps, keyed by the packed context bytes.</summary>
  private sealed class Model {
    private readonly Dictionary<int, Context>[] _byOrder = CreateTables();

    private static Dictionary<int, Context>[] CreateTables() {
      var tables = new Dictionary<int, Context>[MaxOrder + 1];
      for (var order = 0; order <= MaxOrder; ++order)
        tables[order] = [];
      return tables;
    }

    /// <summary>Packs the <paramref name="order"/> bytes preceding position <paramref name="position"/> into a context key.</summary>
    private static int KeyOf(int order, ReadOnlySpan<byte> history, int position) {
      var key = 0;
      for (var k = order; k >= 1; --k)
        key = (key << 8) | history[position - k];
      return key;
    }

    /// <summary>Returns the context of the given order at the given position, or <see langword="null"/> when it has never been seen.</summary>
    public Context? Find(int order, ReadOnlySpan<byte> history, int position) {
      if (order > MaxOrder || position < order)
        return null;
      return this._byOrder[order].GetValueOrDefault(KeyOf(order, history, position));
    }

    /// <summary>Records <paramref name="symbol"/> in every context of order 0..<see cref="MaxOrder"/> that applies at <paramref name="position"/>.</summary>
    public void Update(ReadOnlySpan<byte> history, int position, int symbol) {
      var highestOrder = Math.Min(MaxOrder, position);
      for (var order = 0; order <= highestOrder; ++order) {
        var table = this._byOrder[order];
        var key = KeyOf(order, history, position);
        if (!table.TryGetValue(key, out var context)) {
          context = new Context();
          table[key] = context;
        }

        context.Increment(symbol);
      }
    }
  }

  /// <summary>
  /// The encoding half of the Witten-Neal-Cleary incremental arithmetic coder:
  /// a 16-bit interval that is renormalised a bit at a time, with straddling
  /// (underflow) intervals counted rather than emitted until their direction is
  /// known.
  /// </summary>
  private sealed class ArithmeticEncoder(byte[] header) {
    private readonly List<byte> _output = [.. header];
    private uint _low;
    private uint _high = TopValue;
    private long _pending;
    private int _bitBuffer;
    private int _bitCount;

    /// <summary>Narrows the interval to the sub-range [<paramref name="cumulativeLow"/>, <paramref name="cumulativeHigh"/>) out of <paramref name="total"/>.</summary>
    public void Encode(int cumulativeLow, int cumulativeHigh, int total) {
      var range = (long)(this._high - this._low) + 1;
      this._high = (uint)(this._low + range * cumulativeHigh / total - 1);
      this._low = (uint)(this._low + range * cumulativeLow / total);

      while (true) {
        if (this._high < Half) {
          this.EmitWithPending(0);
        } else if (this._low >= Half) {
          this.EmitWithPending(1);
          this._low -= Half;
          this._high -= Half;
        } else if (this._low >= FirstQuarter && this._high < ThirdQuarter) {
          ++this._pending;
          this._low -= FirstQuarter;
          this._high -= FirstQuarter;
        } else {
          break;
        }

        this._low <<= 1;
        this._high = (this._high << 1) | 1;
      }
    }

    /// <summary>Disambiguates the final interval, flushes the bit buffer and returns the complete stream.</summary>
    public byte[] Finish() {
      ++this._pending;
      this.EmitWithPending(this._low < FirstQuarter ? 0 : 1);
      while (this._bitCount != 0)
        this.PutBit(0);
      return [.. this._output];
    }

    private void EmitWithPending(int bit) {
      this.PutBit(bit);
      var opposite = 1 - bit;
      while (this._pending > 0) {
        this.PutBit(opposite);
        --this._pending;
      }
    }

    private void PutBit(int bit) {
      this._bitBuffer = (this._bitBuffer << 1) | bit;
      if (++this._bitCount != 8)
        return;
      this._output.Add((byte)this._bitBuffer);
      this._bitBuffer = 0;
      this._bitCount = 0;
    }
  }

  /// <summary>The decoding half of the same coder; bits past the end of the stream read as zero.</summary>
  private sealed class ArithmeticDecoder {
    private readonly byte[] _data;
    private int _position;
    private int _bitBuffer;
    private int _bitCount;
    private uint _low;
    private uint _high = TopValue;
    private uint _value;

    public ArithmeticDecoder(ReadOnlySpan<byte> data, int offset) {
      this._data = data.ToArray();
      this._position = offset;
      for (var i = 0; i < CodeBits; ++i)
        this._value = (this._value << 1) | (uint)this.GetBit();
    }

    /// <summary>Reports which of <paramref name="total"/> equal slices of the current interval the encoded value falls in.</summary>
    public int Target(int total) {
      var range = (long)(this._high - this._low) + 1;
      return (int)((((long)(this._value - this._low) + 1) * total - 1) / range);
    }

    /// <summary>Narrows the interval exactly as the encoder did, consuming the symbol just identified.</summary>
    public void Update(int cumulativeLow, int cumulativeHigh, int total) {
      var range = (long)(this._high - this._low) + 1;
      this._high = (uint)(this._low + range * cumulativeHigh / total - 1);
      this._low = (uint)(this._low + range * cumulativeLow / total);

      while (true) {
        if (this._high < Half) {
          // Nothing to subtract: the interval is already in the lower half.
        } else if (this._low >= Half) {
          this._value -= Half;
          this._low -= Half;
          this._high -= Half;
        } else if (this._low >= FirstQuarter && this._high < ThirdQuarter) {
          this._value -= FirstQuarter;
          this._low -= FirstQuarter;
          this._high -= FirstQuarter;
        } else {
          break;
        }

        this._low <<= 1;
        this._high = (this._high << 1) | 1;
        this._value = (this._value << 1) | (uint)this.GetBit();
      }
    }

    private int GetBit() {
      if (this._bitCount == 0) {
        this._bitBuffer = this._position < this._data.Length ? this._data[this._position++] : 0;
        this._bitCount = 8;
      }

      --this._bitCount;
      return (this._bitBuffer >> this._bitCount) & 1;
    }
  }
}
