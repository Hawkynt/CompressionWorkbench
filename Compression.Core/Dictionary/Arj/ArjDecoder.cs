namespace Compression.Core.Dictionary.Arj;

/// <summary>
/// Decodes ARJ-compressed data (methods 1-3).
/// Uses LZSS with Huffman-coded literals/lengths and positions.
/// Matches the real ARJ bitstream format (MSB-first bit packing,
/// three-level tree encoding compatible with 7-Zip and original ARJ).
/// </summary>
public sealed class ArjDecoder {
  private const int NC = 256 + MaxMatch - Threshold + 1; // 510
  private const int NP = 17;
  private const int NT = 19;
  private const int Threshold = 3;
  private const int MaxMatch = 256;
  private const int MaxCodeBits = 16;
  private const int CTableBits = 12;
  private const int PTableBits = 8;
  private const int CTableSize = 1 << CTableBits; // 4096
  private const int PTableSize = 1 << PTableBits; // 256

  private readonly Stream _stream;
  private readonly int _windowSize;

  // MSB-first bit buffer (matches original ARJ fillbuf/getbits)
  private ushort _bitBuf;
  private int _bitCount;
  private int _byteBuf;
  private long _compSize;

  // Current block state
  private int _blockSize;
  private readonly byte[] _cLen = new byte[NC];
  private readonly byte[] _ptLen = new byte[NT > NP ? NT : NP];
  private readonly ushort[] _cTable = new ushort[CTableSize];
  private readonly ushort[] _ptTable = new ushort[PTableSize];
  // Overflow tree for codes longer than table bits
  private readonly ushort[] _left = new ushort[2 * NC];
  private readonly ushort[] _right = new ushort[2 * NC];

  /// <summary>
  /// Initializes a new <see cref="ArjDecoder"/>.
  /// </summary>
  /// <param name="input">The stream containing compressed data.</param>
  /// <param name="method">The ARJ compression method (1, 2, or 3).</param>
  public ArjDecoder(Stream input, int method = 1) {
    this._stream = input;
    this._windowSize = method == 1 ? 26624 : 2048;
    this._compSize = input.Length - input.Position;
    // Initialize bit buffer by priming with 2 bytes
    this._bitBuf = 0;
    this._bitCount = 0;
    this._byteBuf = 0;
    FillBuf(16);
  }

  /// <summary>
  /// Decodes the compressed data.
  /// </summary>
  /// <param name="originalSize">The expected uncompressed size.</param>
  /// <returns>The decompressed data.</returns>
  public byte[] Decode(int originalSize) {
    if (originalSize == 0)
      return [];

    var output = new byte[originalSize];
    var window = new byte[this._windowSize];
    Array.Fill(window, (byte)0x20);
    var windowPos = 0;
    var outPos = 0;

    while (outPos < originalSize) {
      var c = DecodeC();
      if (c < 256) {
        var b = (byte)c;
        output[outPos++] = b;
        window[windowPos] = b;
        windowPos = (windowPos + 1) % this._windowSize;
      } else {
        var length = c - 256 + Threshold;
        var position = DecodeP();

        var srcPos = ((windowPos - position - 1) % this._windowSize + this._windowSize) % this._windowSize;
        for (var j = 0; j < length && outPos < originalSize; ++j) {
          var b = window[srcPos];
          output[outPos++] = b;
          window[windowPos] = b;
          windowPos = (windowPos + 1) % this._windowSize;
          srcPos = (srcPos + 1) % this._windowSize;
        }
      }
    }

    return output;
  }

  // -----------------------------------------------------------------------
  // MSB-first bit input (matches original ARJ fillbuf/getbits)
  // -----------------------------------------------------------------------

  private void FillBuf(int n) {
    while (this._bitCount < n) {
      this._bitBuf = (ushort)((this._bitBuf << this._bitCount) |
        ((this._byteBuf >> (8 - this._bitCount)) & 0xFF));
      n -= this._bitCount;

      if (this._compSize > 0) {
        this._compSize--;
        var b = this._stream.ReadByte();
        this._byteBuf = b >= 0 ? b : 0;
      } else {
        this._byteBuf = 0;
      }
      this._bitCount = 8;
    }
    this._bitCount -= n;
    this._bitBuf = (ushort)((this._bitBuf << n) | (this._byteBuf >> (8 - n)));
    this._byteBuf = (this._byteBuf << n) & 0xFF;
  }

  private int GetBits(int n) {
    var rc = this._bitBuf >> (16 - n);
    FillBuf(n);
    return rc;
  }

  // -----------------------------------------------------------------------
  // decode_c: decode one character/length code
  // -----------------------------------------------------------------------

  private int DecodeC() {
    if (this._blockSize == 0) {
      this._blockSize = GetBits(16);
      ReadPtLen(NT, 5, 3);  // code-length tree
      ReadCLen();            // char/length tree
      ReadPtLen(NP, 5, -1); // position tree
    }
    --this._blockSize;

    // Lookup in c_table (12-bit index)
    int j = this._cTable[this._bitBuf >> (16 - CTableBits)];
    if (j >= NC) {
      var mask = 1 << (16 - CTableBits - 1); // 1 << 3
      do {
        j = (this._bitBuf & mask) != 0 ? this._right[j] : this._left[j];
        mask >>= 1;
      } while (j >= NC);
    }
    FillBuf(this._cLen[j]);
    return j;
  }

  // -----------------------------------------------------------------------
  // decode_p: decode one position value
  // -----------------------------------------------------------------------

  private int DecodeP() {
    int j = this._ptTable[this._bitBuf >> (16 - PTableBits)];
    if (j >= NP) {
      var mask = 1 << (16 - PTableBits - 1); // 1 << 7
      do {
        j = (this._bitBuf & mask) != 0 ? this._right[j] : this._left[j];
        mask >>= 1;
      } while (j >= NP);
    }
    FillBuf(this._ptLen[j]);
    if (j != 0) {
      var bits = j - 1;
      j = (1 << bits) + GetBits(bits);
    }
    return j;
  }

  // -----------------------------------------------------------------------
  // read_pt_len: read preliminary tree lengths
  // -----------------------------------------------------------------------

  private void ReadPtLen(int nn, int nbit, int iSpecial) {
    var n = GetBits(nbit);
    if (n == 0) {
      var c = GetBits(nbit);
      for (var i = 0; i < nn; ++i)
        this._ptLen[i] = 0;
      for (var i = 0; i < PTableSize; ++i)
        this._ptTable[i] = (ushort)c;
      return;
    }

    var idx = 0;
    if (n > nn) n = nn;
    while (idx < n) {
      // Decode code length using unary for values > 6
      int c = this._bitBuf >> 13; // top 3 bits
      if (c == 7) {
        var mask = 1 << 12;
        while ((this._bitBuf & mask) != 0) {
          mask >>= 1;
          c++;
        }
      }
      FillBuf(c < 7 ? 3 : c - 3);
      this._ptLen[idx++] = (byte)c;

      if (idx == iSpecial) {
        var skip = GetBits(2);
        while (--skip >= 0 && idx < nn)
          this._ptLen[idx++] = 0;
      }
    }
    while (idx < nn)
      this._ptLen[idx++] = 0;

    MakeTable(nn, this._ptLen, PTableBits, this._ptTable, PTableSize);
  }

  // -----------------------------------------------------------------------
  // read_c_len: read char/length code lengths
  // -----------------------------------------------------------------------

  private void ReadCLen() {
    var n = GetBits(9); // CBIT = 9
    if (n == 0) {
      var c = GetBits(9);
      for (var i = 0; i < NC; ++i)
        this._cLen[i] = 0;
      for (var i = 0; i < CTableSize; ++i)
        this._cTable[i] = (ushort)c;
      return;
    }

    var idx = 0;
    while (idx < n) {
      // Decode using pt_table
      int c = this._ptTable[this._bitBuf >> (16 - PTableBits)];
      if (c >= NT) {
        var mask = 1 << (16 - PTableBits - 1);
        do {
          c = (this._bitBuf & mask) != 0 ? this._right[c] : this._left[c];
          mask >>= 1;
        } while (c >= NT);
      }
      FillBuf(this._ptLen[c]);

      if (c <= 2) {
        int runLen;
        if (c == 0)
          runLen = 1;
        else if (c == 1)
          runLen = GetBits(4) + 3;
        else
          runLen = GetBits(9) + 20;
        while (--runLen >= 0 && idx < NC)
          this._cLen[idx++] = 0;
      } else
        this._cLen[idx++] = (byte)(c - 2);
    }
    while (idx < NC)
      this._cLen[idx++] = 0;

    MakeTable(NC, this._cLen, CTableBits, this._cTable, CTableSize);
  }

  // -----------------------------------------------------------------------
  // make_table: build decode lookup table from code lengths
  // Codes that fit in tableBits go directly into the table.
  // Longer codes use an overflow binary tree (left/right arrays).
  // -----------------------------------------------------------------------

  private void MakeTable(int nchar, byte[] bitLen, int tableBits, ushort[] table, int tableSize) {
    // Count code lengths
    var count = new int[MaxCodeBits + 1];
    for (var i = 0; i < nchar; ++i)
      if (bitLen[i] > 0 && bitLen[i] <= MaxCodeBits)
        ++count[bitLen[i]];

    // Generate start codes (canonical Huffman)
    var start = new int[MaxCodeBits + 2];
    for (var i = 1; i <= MaxCodeBits; ++i)
      start[i + 1] = (start[i] + count[i]) << 1;

    // Assign codes
    var code = new int[nchar];
    for (var i = 0; i < nchar; ++i)
      if (bitLen[i] > 0)
        code[i] = start[bitLen[i]]++;

    // Clear table
    Array.Clear(table, 0, tableSize);

    var avail = nchar;

    for (var sym = 0; sym < nchar; ++sym) {
      var len = (int)bitLen[sym];
      if (len == 0) continue;

      if (len <= tableBits) {
        // Code fits in table: fill all entries that share this prefix
        var prefix = code[sym] << (tableBits - len);
        var fillCount = 1 << (tableBits - len);
        for (var j = 0; j < fillCount; ++j)
          table[prefix + j] = (ushort)sym;
      } else {
        // Code is longer than tableBits: build overflow tree
        // Navigate using the first tableBits bits to find the table entry,
        // then build a binary tree for the remaining bits
        var prefix = code[sym] >> (len - tableBits);

        // Ensure there's a tree node at this table position
        if (table[prefix] == 0) {
          this._left[avail] = 0;
          this._right[avail] = 0;
          table[prefix] = (ushort)avail++;
        }

        var node = (int)table[prefix];
        // Walk bits from position (len - tableBits - 1) down to 0
        for (var bit = len - tableBits - 1; bit > 0; --bit) {
          if ((code[sym] & (1 << bit)) != 0) {
            if (this._right[node] == 0) {
              this._left[avail] = 0;
              this._right[avail] = 0;
              this._right[node] = (ushort)avail++;
            }
            node = this._right[node];
          } else {
            if (this._left[node] == 0) {
              this._left[avail] = 0;
              this._right[avail] = 0;
              this._left[node] = (ushort)avail++;
            }
            node = this._left[node];
          }
        }
        // Last bit determines final placement
        if ((code[sym] & 1) != 0)
          this._right[node] = (ushort)sym;
        else
          this._left[node] = (ushort)sym;
      }
    }
  }
}
