namespace Compression.Core.Dictionary.Lzh;

/// <summary>
/// Decodes the LZHUF stage of the Amiga Zoom disk archiver: Yoshizaki/Okumura LZHUF with Zoom's own
/// parameters.
/// </summary>
/// <remarks>
/// <para>Adaptive Huffman over 317 symbols (256 literals, 60 match lengths, and 0x13C as the end
/// symbol); the tree stops re-weighting once the root count reaches 0x8000 instead of rebuilding. A
/// match symbol <c>s</c> copies <c>s − 255</c> bytes from <c>distance</c> bytes back in a 4 KiB ring that
/// starts zero-filled, with no −1 adjustment. Distances use LZHUF's fixed position code: the top six bits
/// through a 3–8-bit prefix table, then six raw bits.</para>
/// <para>Sourcing: converted from horsicq/XArchive <c>Algos/xlzhufdecoder.cpp</c> (MIT, Zoom options);
/// the position tables are the LZHUF ones, copied exactly. See THIRD-PARTY-NOTICE.XArchive.txt beside
/// this file.</para>
/// </remarks>
public static class ZoomLzhufDecoder {
  private const int NChar = 0x13D;
  private const int TreeSize = NChar * 2 - 1;
  private const int Root = TreeSize - 1;
  private const int MaxFreq = 0x8000;
  private const int EndCode = 0x13C;
  private const int RingSize = 0x1000;

  private static readonly byte[] DLen = [3, 3, 4, 4, 4, 5, 5, 5, 5, 6, 6, 6, 7, 7, 7, 8];
  private static readonly byte[] DCode = BuildDCode();

  private static byte[] BuildDCode() {
    // {0}×32, {1..3}×16, {4..11}×8, {12..23}×4, {24..47}×2, {48..63}×1 — LZHUF's d_code table.
    var code = new byte[256];
    var p = 0;
    void Run(int from, int to, int repeat) {
      for (var v = from; v <= to; ++v)
        for (var i = 0; i < repeat; ++i)
          code[p++] = (byte)v;
    }
    Run(0, 0, 32);
    Run(1, 3, 16);
    Run(4, 11, 8);
    Run(12, 23, 4);
    Run(24, 47, 2);
    Run(48, 63, 1);
    return code;
  }

  /// <summary>Decodes <paramref name="packed"/> to exactly <paramref name="size"/> bytes.</summary>
  /// <exception cref="InvalidDataException">The stream ends early, overruns the size, or lacks its end symbol.</exception>
  public static byte[] Decode(ReadOnlySpan<byte> packed, int size) {
    ArgumentOutOfRangeException.ThrowIfNegative(size);
    var state = new Tree(packed.ToArray());
    var ring = new byte[RingSize];
    var output = new byte[size];
    var outPos = 0;
    var r = 0;
    while (true) {
      var code = state.DecodeChar();
      if (code < 0x100) {
        if (outPos >= size)
          throw new InvalidDataException("Zoom LZHUF stream produces more than its stated size.");
        output[outPos++] = ring[r] = (byte)code;
        r = (r + 1) & (RingSize - 1);
        continue;
      }
      if (code == EndCode)
        break;
      var source = (r - state.DecodePosition()) & (RingSize - 1);
      var length = code - 0xFF;
      if (length > size - outPos)
        throw new InvalidDataException("Zoom LZHUF match runs past the stated size.");
      for (var i = 0; i < length; ++i) {
        var b = ring[source];
        output[outPos++] = ring[r] = b;
        r = (r + 1) & (RingSize - 1);
        source = (source + 1) & (RingSize - 1);
      }
    }
    if (outPos != size)
      throw new InvalidDataException($"Zoom LZHUF stream ended after {outPos} of {size} bytes.");
    return output;
  }

  private sealed class Tree {
    private readonly byte[] _data;
    private int _pos;
    private uint _buffer;
    private int _count;
    private readonly ushort[] _freq = new ushort[TreeSize + 1];
    private readonly int[] _son = new int[TreeSize];
    private readonly int[] _prnt = new int[TreeSize + NChar];

    public Tree(byte[] data) {
      this._data = data;
      for (var i = 0; i < NChar; ++i) {
        this._freq[i] = 1;
        this._son[i] = i + TreeSize;
        this._prnt[i + TreeSize] = i;
      }
      for (int i = 0, j = NChar; j <= Root; i += 2, ++j) {
        this._freq[j] = (ushort)(this._freq[i] + this._freq[i + 1]);
        this._son[j] = i;
        this._prnt[i] = this._prnt[i + 1] = j;
      }
      this._freq[TreeSize] = 0xFFFF;
      this._prnt[Root] = 0;
    }

    private void Fill() {
      if (this._pos >= this._data.Length)
        throw new InvalidDataException("Zoom LZHUF stream ends before its end symbol.");
      this._buffer |= (uint)this._data[this._pos++] << ((8 - this._count) & 0x1F);
      this._count += 8;
    }

    private int GetBit() {
      if (this._count == 0)
        this.Fill();
      var value = this._buffer;
      this._buffer <<= 1;
      --this._count;
      return (value & 0x8000) != 0 ? 1 : 0;
    }

    private int GetByte() {
      while (this._count < 8)
        this.Fill();
      var value = this._buffer;
      this._buffer <<= 8;
      this._count -= 8;
      return (int)((value >> 8) & 0xFF);
    }

    public int DecodeChar() {
      var c = this._son[Root];
      while (c < TreeSize)
        c = this._son[c + this.GetBit()];
      c -= TreeSize;
      this.Update(c);
      return c;
    }

    public int DecodePosition() {
      var b = this.GetByte();
      var extra = DLen[b >> 4] - 2;
      var high = DCode[b] << 6;
      while (extra-- > 0)
        b = ((b << 1) + this.GetBit()) & 0xFFFF;
      return high | (b & 0x3F);
    }

    private void Update(int symbol) {
      if (this._freq[Root] == MaxFreq)
        return;  // Zoom stops re-weighting here rather than rebuilding the tree
      var c = this._prnt[symbol + TreeSize];
      do {
        var k = ++this._freq[c];
        var l = c + 1;
        if (this._freq[l] < k) {
          l = c + 2;
          while (l <= TreeSize && this._freq[l] < k)
            ++l;
          --l;
          this._freq[c] = this._freq[l];
          this._freq[l] = k;
          var i = this._son[c];
          this._prnt[i] = l;
          if (i < TreeSize)
            this._prnt[i + 1] = l;
          var j = this._son[l];
          this._son[l] = i;
          this._prnt[j] = c;
          if (j < TreeSize)
            this._prnt[j + 1] = c;
          this._son[c] = j;
          c = l;
        }
        c = this._prnt[c];
      } while (c != 0);
    }
  }
}
