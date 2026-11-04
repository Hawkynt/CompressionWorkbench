// HA's HSC method (order-4 finite-context model with exclusion over HA's arithmetic coder),
// implemented from docs/HA-ON-DISK.md §4.

namespace Compression.Core.Dictionary.Ha;

/// <summary>
/// HA's HSC method: an order-4 context model with exclusion, a fixed pool of 10 000 contexts and
/// 32 760 frequency nodes recycled by least-recent use, and an order −1 fallback.
/// </summary>
/// <remarks>
/// Specification: docs/HA-ON-DISK.md §4. The encoder and decoder share one model; the encoder's
/// output is byte-identical to HA 0.999's for the same input.
/// </remarks>
public static class HaHsc {
  /// <summary>Encodes <paramref name="data"/> as one HSC stream.</summary>
  /// <param name="data">The bytes to encode.</param>
  /// <returns>The HSC stream, terminated as HA terminates it.</returns>
  public static byte[] Encode(ReadOnlySpan<byte> data) {
    var encoder = new HaArithmeticEncoder();
    var model = new HscModel(encoder, null);
    foreach (var b in data)
      model.Encode(b);
    model.EncodeEnd();
    return encoder.Finish();
  }

  /// <summary>Decodes one HSC stream.</summary>
  /// <param name="source">The member's data (the stream terminates itself).</param>
  /// <param name="expectedLength">The member's original length; producing more is refused.</param>
  /// <returns>The decoded bytes.</returns>
  /// <exception cref="InvalidDataException">The stream is corrupt or not exactly <paramref name="expectedLength"/> long.</exception>
  public static byte[] Decode(ReadOnlyMemory<byte> source, int expectedLength) {
    ArgumentOutOfRangeException.ThrowIfNegative(expectedLength);

    var model = new HscModel(null, new HaArithmeticDecoder(source));
    var output = new byte[expectedLength];
    var produced = 0;
    while (true) {
      var symbol = model.Decode();
      if (symbol == HscModel.EndOfStream)
        break;
      if (produced == expectedLength)
        throw new InvalidDataException("HSC stream is longer than the member's original length.");
      output[produced++] = (byte)symbol;
    }

    if (produced != expectedLength)
      throw new InvalidDataException($"HSC stream ended after {produced} of {expectedLength} bytes.");
    return output;
  }
}

/// <summary>The HSC model state and its per-symbol coding steps (docs/HA-ON-DISK.md §4.2–§4.10).</summary>
internal sealed class HscModel {
  public const int EndOfStream = 256;

  private const int ContextCount = 10000;
  private const int NodeCount = 32760;
  private const int BucketCount = 16384;
  private const int MaxLength = 4;
  private const int LowThreshold = 3;
  private const int TotalLimit = 8000;
  private const int RefreshStart = 4;
  private const int InitialEscapeLimit = 32;
  private const int NoEscapeLimit = 10;
  private const int LengthFourRecyclingsBeforeDrop = 2500;
  private const int None = -1;

  private static readonly int[] _random = BuildRandomTable();

  private readonly HaArithmeticEncoder? _encoder;
  private readonly HaArithmeticDecoder? _decoder;

  // Contexts.
  private readonly int[] _length = new int[ContextCount];
  private readonly uint[] _bytes = new uint[ContextCount];
  private readonly int[] _bucketNext = new int[ContextCount];
  private readonly int[] _newer = new int[ContextCount];
  private readonly int[] _older = new int[ContextCount];
  private readonly int[] _total = new int[ContextCount];
  private readonly int[] _low = new int[ContextCount];
  private readonly int[] _extra = new int[ContextCount];
  private readonly int[] _refresh = new int[ContextCount];
  private readonly int[] _bucketHead = new int[BucketCount];
  private int _newest;
  private int _oldest;

  // Frequency nodes; node x (x < ContextCount) is context x's first node.
  private readonly int[] _frequency = new int[NodeCount];
  private readonly byte[] _symbol = new byte[NodeCount];
  private readonly int[] _next = new int[NodeCount];
  private int _free;

  // Exclusion set.
  private readonly bool[] _excluded = new bool[256];
  private readonly int[] _excludedList = new int[256];
  private int _excludedCount;

  // Update stack.
  private readonly int[] _stackContext = new int[MaxLength + 1];
  private readonly int[] _stackNode = new int[MaxLength + 1];
  private readonly bool[] _stackEscaped = new bool[MaxLength + 1];
  private int _stackSize;

  private readonly int[] _initialEscape = [16, 15, 15, 15, 15];
  private readonly int[] _hash = new int[MaxLength + 1];
  private uint _history;
  private int _noEscape;
  private int _maxLength = MaxLength;
  private int _lengthFourRecyclings;
  private int _releaseCursor;
  private int _searchBelow;

  public HscModel(HaArithmeticEncoder? encoder, HaArithmeticDecoder? decoder) {
    this._encoder = encoder;
    this._decoder = decoder;

    Array.Fill(this._length, None);
    Array.Fill(this._bucketHead, None);
    for (var x = 0; x < ContextCount; ++x) {
      this._older[x] = x + 1 < ContextCount ? x + 1 : None;
      this._newer[x] = x - 1;
      this._next[x] = None;
    }

    this._newest = 0;
    this._oldest = ContextCount - 1;
    for (var n = ContextCount; n < NodeCount; ++n)
      this._next[n] = n + 1 < NodeCount ? n + 1 : None;
    this._free = ContextCount;
  }

  // Park–Miller minimal standard generator, seed 10; low 14 bits of each output (§4.2).
  private static int[] BuildRandomTable() {
    var table = new int[BucketCount];
    long z = 10;
    for (var i = 0; i < table.Length; ++i) {
      z = z * 16807 % 2147483647;
      table[i] = (int)(z & (BucketCount - 1));
    }

    return table;
  }

  private static int HashOf(uint bytes, int length) {
    var h = 0;
    for (var k = 0; k < length; ++k) {
      var b = (int)((bytes >> (8 * k)) & 0xFF);
      h = k == 0 ? _random[b] : _random[(b + h) & (BucketCount - 1)];
    }

    return h;
  }

  // ── Per-symbol driver (§4.4) ─────────────────────────────────────────────

  public void Encode(int c) {
    var x = this._FindLongest();
    var lo = x == None ? 0 : this._length[x] + 1;
    var hi = this._maxLength + 1;
    while (true) {
      if (x == None) {
        this._EncodeOrderMinusOne(c);
        break;
      }

      if (this._excludedCount == 0 ? this._EncodeFirst(x, c) : this._EncodeFurther(x, c)) {
        this._MakeNewest(x);
        break;
      }

      x = this._FindNext();
    }

    this._Learn(c, lo, hi);
  }

  public void EncodeEnd() {
    for (var x = this._FindLongest(); x != None; x = this._FindNext())
      _ = this._excludedCount == 0 ? this._EncodeFirst(x, EndOfStream) : this._EncodeFurther(x, EndOfStream);
    this._EncodeOrderMinusOne(EndOfStream);
  }

  public int Decode() {
    var x = this._FindLongest();
    var lo = x == None ? 0 : this._length[x] + 1;
    var hi = this._maxLength + 1;
    int c;
    while (true) {
      if (x == None) {
        c = this._DecodeOrderMinusOne();
        break;
      }

      c = this._excludedCount == 0 ? this._DecodeFirst(x) : this._DecodeFurther(x);
      if (c != EndOfStream) {
        this._MakeNewest(x);
        break;
      }

      x = this._FindNext();
    }

    if (c != EndOfStream)
      this._Learn(c, lo, hi);
    return c;
  }

  private void _Learn(int c, int lo, int hi) {
    this._Update(c);
    while (hi > lo)
      this._CreateContext(--hi, c);
    this._history = (this._history << 8) | (uint)c;
  }

  // ── Context search ───────────────────────────────────────────────────────

  private int _FindLongest() {
    for (var k = 1; k <= MaxLength; ++k) {
      var b = (int)((this._history >> (8 * (k - 1))) & 0xFF);
      this._hash[k] = k == 1 ? _random[b] : _random[(b + this._hash[k - 1]) & (BucketCount - 1)];
    }

    for (; this._excludedCount > 0; --this._excludedCount)
      this._excluded[this._excludedList[this._excludedCount - 1]] = false;
    this._stackSize = 0;
    this._searchBelow = MaxLength + 1;
    return this._FindNext();
  }

  private int _FindNext() {
    for (var length = this._searchBelow - 1; length >= 0; --length) {
      var mask = length == 4 ? uint.MaxValue : (1u << (8 * length)) - 1;
      for (var x = this._bucketHead[this._hash[length]]; x != None; x = this._bucketNext[x]) {
        if (this._length[x] != length || ((this._bytes[x] ^ this._history) & mask) != 0)
          continue;
        this._searchBelow = length;
        return x;
      }
    }

    return None;
  }

  // ── Coding in one context (§4.5, §4.6, §4.7) ─────────────────────────────

  private int _EscapeWeight(int raw, int x) {
    var total = this._total[x];
    if (total == 1)
      return this._initialEscape[this._length[x]] >= InitialEscapeLimit / 2 ? 2 : 1;

    var extra = this._extra[x];
    if (extra == 255)
      return 1;

    if (extra > 0 && 2 * (extra + 1) >= total) {
      raw = raw * 2 * (extra + 1) / total;
      if (extra + 1 == total)
        raw += (extra + 1) >> 1;
    }

    return raw > 0 ? raw : 1;
  }

  private int _FirstScale(int x) {
    if (this._noEscape < 5)
      return 0;
    return this._total[x] <= 4 && this._noEscape == NoEscapeLimit ? 2 : 1;
  }

  private void _Found(int x, int node, bool first) {
    if (first) {
      if (this._noEscape < NoEscapeLimit)
        ++this._noEscape;
    } else
      ++this._noEscape;

    if (this._total[x] == 1 && this._initialEscape[this._length[x]] > 0)
      --this._initialEscape[this._length[x]];
    this._Push(x, node, escaped: false);
  }

  private void _Escaped(int x, bool first) {
    if (first)
      this._noEscape = 0;
    if (this._total[x] == 1 && this._initialEscape[this._length[x]] < InitialEscapeLimit)
      ++this._initialEscape[this._length[x]];

    var last = x;
    for (var n = x; n != None; n = this._next[n]) {
      if (!this._excluded[this._symbol[n]]) {
        this._excluded[this._symbol[n]] = true;
        this._excludedList[this._excludedCount++] = this._symbol[n];
      }

      last = n;
    }

    this._Push(x, last, escaped: true);
  }

  private void _Push(int x, int node, bool escaped) {
    this._stackContext[this._stackSize] = x;
    this._stackNode[this._stackSize] = node;
    this._stackEscaped[this._stackSize] = escaped;
    ++this._stackSize;
  }

  private bool _EncodeFirst(int x, int c) {
    var escape = this._EscapeWeight(this._low[x], x);
    var shift = this._FirstScale(x);
    var total = this._total[x] << shift;
    var before = 0;
    for (var n = x; n != None; n = this._next[n]) {
      if (this._symbol[n] == c) {
        this._encoder!.Code(before << shift, (before + this._frequency[n]) << shift, total + escape);
        this._Found(x, n, first: true);
        return true;
      }

      before += this._frequency[n];
    }

    this._encoder!.Code(total, total + escape, total + escape);
    this._Escaped(x, first: true);
    return false;
  }

  private int _DecodeFirst(int x) {
    var escape = this._EscapeWeight(this._low[x], x);
    var shift = this._FirstScale(x);
    var total = this._total[x] << shift;
    var target = this._decoder!.Threshold(total + escape) >> shift;
    var before = 0;
    for (var n = x; n != None; n = this._next[n]) {
      if (before + this._frequency[n] <= target) {
        before += this._frequency[n];
        continue;
      }

      this._decoder.Consume(before << shift, (before + this._frequency[n]) << shift, total + escape);
      this._Found(x, n, first: true);
      return this._symbol[n];
    }

    this._decoder.Consume(total, total + escape, total + escape);
    this._Escaped(x, first: true);
    return EndOfStream;
  }

  private (int Total, int Escape) _Unexcluded(int x) {
    var total = 0;
    var low = 0;
    for (var n = x; n != None; n = this._next[n]) {
      if (this._excluded[this._symbol[n]])
        continue;
      total += this._frequency[n];
      if (this._frequency[n] < LowThreshold)
        ++low;
    }

    return (total, this._EscapeWeight(low, x));
  }

  private bool _EncodeFurther(int x, int c) {
    var (total, escape) = this._Unexcluded(x);
    var before = 0;
    for (var n = x; n != None; n = this._next[n]) {
      if (this._excluded[this._symbol[n]])
        continue;
      if (this._symbol[n] == c) {
        this._encoder!.Code(before, before + this._frequency[n], total + escape);
        this._Found(x, n, first: false);
        return true;
      }

      before += this._frequency[n];
    }

    this._encoder!.Code(total, total + escape, total + escape);
    this._Escaped(x, first: false);
    return false;
  }

  private int _DecodeFurther(int x) {
    var (total, escape) = this._Unexcluded(x);
    var target = this._decoder!.Threshold(total + escape);
    var before = 0;
    for (var n = x; n != None; n = this._next[n]) {
      if (this._excluded[this._symbol[n]])
        continue;
      if (before + this._frequency[n] <= target) {
        before += this._frequency[n];
        continue;
      }

      this._decoder.Consume(before, before + this._frequency[n], total + escape);
      this._Found(x, n, first: false);
      return this._symbol[n];
    }

    this._decoder.Consume(total, total + escape, total + escape);
    this._Escaped(x, first: false);
    return EndOfStream;
  }

  private void _EncodeOrderMinusOne(int c) {
    var below = 0;
    for (var s = 0; s < c; ++s)
      if (!this._excluded[s])
        ++below;
    this._encoder!.Code(below, below + 1, 257 - this._excludedCount);
  }

  private int _DecodeOrderMinusOne() {
    var total = 257 - this._excludedCount;
    var target = this._decoder!.Threshold(total);
    var below = 0;
    var s = 0;
    for (; s < 256; ++s) {
      if (this._excluded[s])
        continue;
      if (below + 1 > target)
        break;
      ++below;
    }

    this._decoder.Consume(below, below + 1, total);
    return s;
  }

  // ── Update (§4.8) ────────────────────────────────────────────────────────

  private void _Update(int c) {
    while (this._stackSize > 0) {
      --this._stackSize;
      var x = this._stackContext[this._stackSize];
      var node = this._stackNode[this._stackSize];

      if (this._stackEscaped[this._stackSize]) {
        if (this._free == None)
          this._Release();
        var fresh = this._free;
        this._free = this._next[fresh];
        this._next[node] = fresh;
        this._next[fresh] = None;
        this._frequency[fresh] = 1;
        this._symbol[fresh] = (byte)c;
        ++this._extra[x];
        ++this._low[x];
        node = fresh;
      } else if (++this._frequency[node] == LowThreshold)
        --this._low[x];

      ++this._total[x];
      if (2 * this._frequency[node] < this._total[x] / (this._extra[x] + 1))
        --this._refresh[x];
      else if (this._refresh[x] < RefreshStart)
        ++this._refresh[x];

      if (this._refresh[x] != 0 && this._total[x] < TotalLimit)
        continue;

      ++this._refresh[x];
      this._total[x] = 0;
      this._low[x] = 0;
      for (var n = x; n != None; n = this._next[n]) {
        if (this._frequency[n] > 1)
          this._frequency[n] >>= 1;
        this._total[x] += this._frequency[n];
        if (this._frequency[n] < LowThreshold)
          ++this._low[x];
      }
    }
  }

  // ── Releasing nodes (§4.10) ──────────────────────────────────────────────

  private bool _OnStack(int x) {
    for (var k = 0; k <= this._stackSize; ++k)
      if (this._stackContext[k] == x)
        return true;
    return false;
  }

  private void _Release() {
    int x;
    do {
      do
        this._releaseCursor = this._releaseCursor + 1 == ContextCount ? 0 : this._releaseCursor + 1;
      while (this._next[this._releaseCursor] == None);
      x = this._releaseCursor;
    } while (this._OnStack(x));

    var divisor = this._frequency[x];
    for (var n = this._next[x]; n != None; n = this._next[n])
      divisor = Math.Min(divisor, this._frequency[n]);
    ++divisor;

    if (this._frequency[x] < divisor) {
      var keep = this._next[x];
      while (this._frequency[keep] < divisor && this._next[keep] != None)
        keep = this._next[keep];

      this._frequency[x] = this._frequency[keep];
      this._symbol[x] = this._symbol[keep];
      var rest = this._next[keep];
      this._next[keep] = this._free;
      this._free = this._next[x];
      this._next[x] = rest;
      if (rest == None) {
        this._extra[x] = 0;
        this._total[x] = this._frequency[x];
        this._low[x] = this._total[x] < LowThreshold ? 1 : 0;
        return;
      }
    }

    this._frequency[x] /= divisor;
    this._total[x] = this._frequency[x];
    this._low[x] = this._total[x] < LowThreshold ? 1 : 0;
    this._extra[x] = 0;
    for (int previous = x, n = this._next[x]; n != None;) {
      if (this._frequency[n] < divisor) {
        this._next[previous] = this._next[n];
        this._next[n] = this._free;
        this._free = n;
        n = this._next[previous];
        continue;
      }

      ++this._extra[x];
      this._frequency[n] /= divisor;
      this._total[x] += this._frequency[n];
      if (this._frequency[n] < LowThreshold)
        ++this._low[x];
      previous = n;
      n = this._next[n];
    }
  }

  // ── Context pool (§4.9) ──────────────────────────────────────────────────

  private void _MakeNewest(int x) {
    if (x == this._newest)
      return;

    var newer = this._newer[x];
    var older = this._older[x];
    this._older[newer] = older;
    if (older == None)
      this._oldest = newer;
    else
      this._newer[older] = newer;

    this._newer[x] = None;
    this._older[x] = this._newest;
    this._newer[this._newest] = x;
    this._newest = x;
  }

  private void _CreateContext(int length, int c) {
    var x = this._oldest;
    this._MakeNewest(x);

    if (this._length[x] != None) {
      if (this._length[x] == 4 && ++this._lengthFourRecyclings == LengthFourRecyclingsBeforeDrop)
        this._maxLength = MaxLength - 1;

      var bucket = HashOf(this._bytes[x], this._length[x]);
      if (this._bucketHead[bucket] == x)
        this._bucketHead[bucket] = this._bucketNext[x];
      else {
        var before = this._bucketHead[bucket];
        while (this._bucketNext[before] != x)
          before = this._bucketNext[before];
        this._bucketNext[before] = this._bucketNext[x];
      }

      if (this._next[x] != None) {
        var last = this._next[x];
        while (this._next[last] != None)
          last = this._next[last];
        this._next[last] = this._free;
        this._free = this._next[x];
      }
    }

    this._next[x] = None;
    this._frequency[x] = 1;
    this._symbol[x] = (byte)c;
    this._total[x] = 1;
    this._low[x] = 1;
    this._extra[x] = 0;
    this._refresh[x] = RefreshStart;
    this._length[x] = length;
    this._bytes[x] = this._history;

    var head = HashOf(this._history, length);
    this._bucketNext[x] = this._bucketHead[head];
    this._bucketHead[head] = x;
  }
}
