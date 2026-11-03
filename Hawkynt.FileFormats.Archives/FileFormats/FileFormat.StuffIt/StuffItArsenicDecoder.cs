using Compression.Core.Checksums;

namespace FileFormat.StuffIt;

/// <summary>
/// StuffIt method 15 (Arsenic, StuffIt 5's "maximum" method): adaptive arithmetic coding of a
/// BWT + move-to-front + zero-run stream, with optional block randomisation and a final
/// bzip2-style run-length stage. The stream carries a CRC-32 of the output, checked at the end.
/// </summary>
/// <remarks>Converted from XADMaster's XADStuffItArsenicHandle (LGPL-2.1-or-later); the randomisation table is copied verbatim.</remarks>
internal static class StuffItArsenicDecoder {
  private const int NumBits = 26;
  private const int One = 1 << (NumBits - 1);
  private const int Half = 1 << (NumBits - 2);

  private sealed class Model {
    private readonly int _increment, _limit, _first;
    private readonly int[] _freq;
    public int Total;

    public Model(int first, int last, int increment, int limit) {
      this._first = first;
      this._increment = increment;
      this._limit = limit;
      this._freq = new int[last - first + 1];
      this.Reset();
    }

    public void Reset() {
      this.Total = this._increment * this._freq.Length;
      Array.Fill(this._freq, this._increment);
    }

    public int Count => this._freq.Length;
    public int Freq(int index) => this._freq[index];
    public int Symbol(int index) => this._first + index;

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

  private sealed class Decoder {
    private readonly StuffItBitReader _reader;
    private int _range = One;
    private int _code;

    public Decoder(StuffItBitReader reader) {
      this._reader = reader;
      this._code = reader.ReadBits(NumBits);
    }

    public int Next(Model model) {
      var frequency = this._code / (this._range / model.Total);
      int cumulative = 0, n;
      for (n = 0; n < model.Count - 1; ++n) {
        if (cumulative + model.Freq(n) > frequency) break;
        cumulative += model.Freq(n);
      }
      this.Narrow(cumulative, model.Freq(n), model.Total);
      model.Increase(n);
      return model.Symbol(n);
    }

    public int NextBits(Model model, int bits) {
      var result = 0;
      for (var i = 0; i < bits; ++i)
        if (this.Next(model) != 0) result |= 1 << i;
      return result;
    }

    private void Narrow(int low, int size, int total) {
      var factor = this._range / total;
      var lowIncrement = factor * low;
      this._code -= lowIncrement;
      if (low + size == total) this._range -= lowIncrement;
      else this._range = size * factor;
      while (this._range <= Half) {
        this._range <<= 1;
        this._code = (this._code << 1) | this._reader.ReadBit();
      }
    }
  }

  public static byte[] Decode(byte[] input, int outputLength) {
    var output = new byte[outputLength];
    if (outputLength == 0) return output;
    var reader = new StuffItBitReader(input);
    var dec = new Decoder(reader);

    var initial = new Model(0, 1, 1, 256);
    var selector = new Model(0, 10, 8, 1024);
    Model[] mtfModels = [
      new(2, 3, 8, 1024), new(4, 7, 4, 1024), new(8, 15, 4, 1024), new(16, 31, 4, 1024),
      new(32, 63, 2, 1024), new(64, 127, 2, 1024), new(128, 255, 1, 1024),
    ];

    if (dec.NextBits(initial, 8) != 'A' || dec.NextBits(initial, 8) != 's')
      throw new InvalidDataException("StuffIt Arsenic stream has no 'As' signature.");
    var blockBits = dec.NextBits(initial, 4) + 9;
    var blockSize = 1 << blockBits;
    var block = new byte[blockSize];
    var transform = new int[blockSize];
    var endOfBlocks = dec.Next(initial) != 0;
    uint storedCrc = 0;

    var crc = new Crc32();
    Span<int> counts = stackalloc int[256];
    Span<int> cumulative = stackalloc int[256];
    var o = 0;
    while (o < outputLength) {
      if (endOfBlocks) throw new InvalidDataException("StuffIt Arsenic stream ended before the recorded size.");

      // ── read one block ──
      var mtf = new byte[256];
      for (var i = 0; i < 256; ++i) mtf[i] = (byte)i;
      var randomized = dec.Next(initial) != 0;
      var transformIndex = dec.NextBits(initial, blockBits);
      var numBytes = 0;
      while (true) {
        var sel = dec.Next(selector);
        if (sel is 0 or 1) {
          int zeroState = 1, zeroCount = 0;
          while (sel < 2) {
            zeroCount += sel == 0 ? zeroState : 2 * zeroState;
            zeroState <<= 1;
            if (zeroCount > blockSize) throw new InvalidDataException("StuffIt Arsenic zero run overflows the block.");
            sel = dec.Next(selector);
          }
          if (numBytes + zeroCount > blockSize) throw new InvalidDataException("StuffIt Arsenic block overflows.");
          block.AsSpan(numBytes, zeroCount).Fill(mtf[0]);
          numBytes += zeroCount;
        }
        if (sel == 10) break;
        var symbol = sel == 2 ? 1 : dec.Next(mtfModels[sel - 3]);
        if (numBytes >= blockSize) throw new InvalidDataException("StuffIt Arsenic block overflows.");
        block[numBytes++] = MoveToFront(mtf, symbol);
      }
      if (transformIndex >= numBytes) throw new InvalidDataException("StuffIt Arsenic BWT index is out of range.");

      selector.Reset();
      foreach (var m in mtfModels) m.Reset();
      if (dec.Next(initial) != 0) {
        storedCrc = (uint)dec.NextBits(initial, 32);
        endOfBlocks = true;
      }

      // Inverse BWT vector.
      counts.Clear();
      for (var i = 0; i < numBytes; ++i) ++counts[block[i]];
      var total = 0;
      for (var i = 0; i < 256; ++i) { cumulative[i] = total; total += counts[i]; counts[i] = 0; }
      for (var i = 0; i < numBytes; ++i) transform[cumulative[block[i]] + counts[block[i]]++] = i;

      // ── emit the block through de-randomisation and the final RLE ──
      int count = 0, last = 0, randIndex = 0, randCount = RandomizationTable[0];
      for (var byteCount = 0; byteCount < numBytes;) {
        transformIndex = transform[transformIndex];
        int b = block[transformIndex];
        if (randomized && randCount == byteCount) {
          b ^= 1;
          randIndex = (randIndex + 1) & 255;
          randCount += RandomizationTable[randIndex];
        }
        ++byteCount;

        if (count == 4) {
          count = 0;
          for (var k = 0; k < b; ++k) Emit(output, ref o, (byte)last, crc);
          continue;
        }
        if (b == last) ++count;
        else { count = 1; last = b; }
        Emit(output, ref o, (byte)b, crc);
      }
    }

    // The CRC-32 arrives with the last block; a stream that still has blocks after the recorded
    // size cannot be verified and is refused rather than trusted.
    if (!endOfBlocks)
      throw new InvalidDataException("StuffIt Arsenic stream continues past the recorded size.");
    if (crc.Value != storedCrc)
      throw new InvalidDataException("StuffIt Arsenic CRC-32 mismatch.");
    return output;
  }

  private static void Emit(byte[] output, ref int o, byte b, Crc32 crc) {
    if (o >= output.Length) throw new InvalidDataException("StuffIt Arsenic stream is longer than the recorded size.");
    output[o++] = b;
    crc.Update(b);
  }

  private static byte MoveToFront(byte[] table, int symbol) {
    var value = table[symbol];
    Array.Copy(table, 0, table, 1, symbol);
    table[0] = value;
    return value;
  }

  private static readonly ushort[] RandomizationTable = [
    0xee, 0x56, 0xf8, 0xc3, 0x9d, 0x9f, 0xae, 0x2c, 0xad, 0xcd, 0x24, 0x9d, 0xa6, 0x101, 0x18, 0xb9,
    0xa1, 0x82, 0x75, 0xe9, 0x9f, 0x55, 0x66, 0x6a, 0x86, 0x71, 0xdc, 0x84, 0x56, 0x96, 0x56, 0xa1,
    0x84, 0x78, 0xb7, 0x32, 0x6a, 0x3, 0xe3, 0x2, 0x11, 0x101, 0x8, 0x44, 0x83, 0x100, 0x43, 0xe3,
    0x1c, 0xf0, 0x86, 0x6a, 0x6b, 0xf, 0x3, 0x2d, 0x86, 0x17, 0x7b, 0x10, 0xf6, 0x80, 0x78, 0x7a,
    0xa1, 0xe1, 0xef, 0x8c, 0xf6, 0x87, 0x4b, 0xa7, 0xe2, 0x77, 0xfa, 0xb8, 0x81, 0xee, 0x77, 0xc0,
    0x9d, 0x29, 0x20, 0x27, 0x71, 0x12, 0xe0, 0x6b, 0xd1, 0x7c, 0xa, 0x89, 0x7d, 0x87, 0xc4, 0x101,
    0xc1, 0x31, 0xaf, 0x38, 0x3, 0x68, 0x1b, 0x76, 0x79, 0x3f, 0xdb, 0xc7, 0x1b, 0x36, 0x7b, 0xe2,
    0x63, 0x81, 0xee, 0xc, 0x63, 0x8b, 0x78, 0x38, 0x97, 0x9b, 0xd7, 0x8f, 0xdd, 0xf2, 0xa3, 0x77,
    0x8c, 0xc3, 0x39, 0x20, 0xb3, 0x12, 0x11, 0xe, 0x17, 0x42, 0x80, 0x2c, 0xc4, 0x92, 0x59, 0xc8,
    0xdb, 0x40, 0x76, 0x64, 0xb4, 0x55, 0x1a, 0x9e, 0xfe, 0x5f, 0x6, 0x3c, 0x41, 0xef, 0xd4, 0xaa,
    0x98, 0x29, 0xcd, 0x1f, 0x2, 0xa8, 0x87, 0xd2, 0xa0, 0x93, 0x98, 0xef, 0xc, 0x43, 0xed, 0x9d,
    0xc2, 0xeb, 0x81, 0xe9, 0x64, 0x23, 0x68, 0x1e, 0x25, 0x57, 0xde, 0x9a, 0xcf, 0x7f, 0xe5, 0xba,
    0x41, 0xea, 0xea, 0x36, 0x1a, 0x28, 0x79, 0x20, 0x5e, 0x18, 0x4e, 0x7c, 0x8e, 0x58, 0x7a, 0xef,
    0x91, 0x2, 0x93, 0xbb, 0x56, 0xa1, 0x49, 0x1b, 0x79, 0x92, 0xf3, 0x58, 0x4f, 0x52, 0x9c, 0x2,
    0x77, 0xaf, 0x2a, 0x8f, 0x49, 0xd0, 0x99, 0x4d, 0x98, 0x101, 0x60, 0x93, 0x100, 0x75, 0x31, 0xce,
    0x49, 0x20, 0x56, 0x57, 0xe2, 0xf5, 0x26, 0x2b, 0x8a, 0xbf, 0xde, 0xd0, 0x83, 0x34, 0xf4, 0x17,
  ];
}
