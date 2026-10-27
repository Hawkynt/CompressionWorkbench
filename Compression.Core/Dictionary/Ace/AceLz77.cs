// ACE LZ77 engine, converted from acefile (https://github.com/droe/acefile, BSD-2-Clause,
// Copyright (c) 2017-2026 Daniel Roethlisberger and contributors). See THIRD-PARTY-NOTICE.acefile.txt.

namespace Compression.Core.Dictionary.Ace;

/// <summary>A mode instruction of the ACE 2.0 blocked format.</summary>
internal sealed record AceMode(int Mode, int DeltaDist = 0, int DeltaLen = 0, int ExeMode = 0) {
  public static AceMode ReadFrom(AceBitStream bs) {
    var mode = (int)bs.ReadBits(8);
    return mode switch {
      AceConstants.ModeLz77Delta => new(mode, DeltaDist: (int)bs.ReadBits(8), DeltaLen: (int)bs.ReadBits(17)),
      AceConstants.ModeLz77Exe => new(mode, ExeMode: (int)bs.ReadBits(8)),
      _ => new(mode),
    };
  }
}

/// <summary>
/// The LZ77 dictionary: every byte produced (by any mode) is appended, and matches copy from it. It
/// doubles as the output buffer and is trimmed to the dictionary size only occasionally.
/// </summary>
internal sealed class AceDictionary {
  private byte[] _data = new byte[1 << 16];
  private int _count;
  private int _dicSize = AceConstants.MinDictSize;

  public int Count => this._count;

  public void SetSize(int dicSize) => this._dicSize = Math.Min(Math.Max(dicSize, this._dicSize), AceConstants.MaxDictSize);

  private void Ensure(int extra) {
    if (this._count + extra <= this._data.Length)
      return;
    var size = this._data.Length;
    while (size < this._count + extra)
      size *= 2;
    Array.Resize(ref this._data, size);
  }

  public void Append(byte b) {
    this.Ensure(1);
    this._data[this._count++] = b;
  }

  public void Extend(ReadOnlySpan<byte> buf) {
    this.Ensure(buf.Length);
    buf.CopyTo(this._data.AsSpan(this._count));
    this._count += buf.Length;
  }

  public void Copy(int dist, int n) {
    var source = this._count - dist;
    if (source < 0)
      throw new InvalidDataException("ACE LZ77 match reaches before the start of the data.");
    this.Ensure(n);
    for (var i = 0; i < n; ++i)
      this._data[this._count++] = this._data[source + i];
  }

  /// <summary>Registers bytes another mode produced, then trims.</summary>
  public void CopyIn(ReadOnlySpan<byte> buf) {
    this.Extend(buf);
    this.Truncate();
  }

  /// <summary>Returns the last <paramref name="n"/> bytes appended, then trims.</summary>
  public byte[] CopyOut(int n) {
    var chunk = n > 0 ? this._data.AsSpan(this._count - n, n).ToArray() : [];
    this.Truncate();
    return chunk;
  }

  private void Truncate() {
    if (this._count <= 8 * this._dicSize)
      return;
    var keep = this._dicSize;
    Array.Copy(this._data, this._count - keep, this._data, 0, keep);
    this._count = keep;
  }
}

/// <summary>ACE 1.0 / 2.0 LZ77 decoding over two Huffman trees (main symbols and lengths).</summary>
internal sealed class AceLz77 {
  private readonly AceDictionary _dictionary = new();
  private AceHuffmanTree? _mainTree;
  private AceHuffmanTree? _lenTree;
  private int _symsToRead;
  private readonly int[] _distHist = new int[AceConstants.DistHistSize];

  public void Reinit() {
    this._symsToRead = 0;
    this._mainTree = this._lenTree = null;
    Array.Clear(this._distHist);
  }

  public void SetDictionarySize(int dicSize) => this._dictionary.SetSize(dicSize);

  public void Register(ReadOnlySpan<byte> buf) => this._dictionary.CopyIn(buf);

  private int ReadMainSymbol(AceBitStream bs) {
    if (this._symsToRead == 0) {
      this._mainTree = AceHuffman.ReadTree(bs, AceConstants.Lz77MaxCodeWidth, AceConstants.Lz77NumMainCodes);
      this._lenTree = AceHuffman.ReadTree(bs, AceConstants.Lz77MaxCodeWidth, AceConstants.Lz77NumLenCodes);
      this._symsToRead = (int)bs.ReadBits(15);
    }
    --this._symsToRead;
    return this._mainTree!.ReadSymbol(bs);
  }

  private int Retrieve(int offset) {
    // pop(SIZE - offset - 1), append: the chosen distance moves to the most recent slot
    var index = AceConstants.DistHistSize - offset - 1;
    var dist = this._distHist[index];
    for (var i = index; i < AceConstants.DistHistSize - 1; ++i)
      this._distHist[i] = this._distHist[i + 1];
    this._distHist[^1] = dist;
    return dist;
  }

  private void Append(int dist) {
    for (var i = 0; i < AceConstants.DistHistSize - 1; ++i)
      this._distHist[i] = this._distHist[i + 1];
    this._distHist[^1] = dist;
  }

  /// <summary>
  /// Decodes until <paramref name="wantSize"/> bytes are available or a mode instruction ends the block.
  /// </summary>
  public (byte[] Chunk, AceMode? NextMode) Read(AceBitStream bs, int wantSize) {
    var have = 0;
    AceMode? nextMode = null;
    while (have < wantSize) {
      var symbol = this.ReadMainSymbol(bs);
      if (symbol <= 255) {
        this._dictionary.Append((byte)symbol);
        ++have;
      } else if (symbol < AceConstants.Lz77TypeCode) {
        int copyLen, copyDist;
        if (symbol <= 259) {
          copyLen = this._lenTree!.ReadSymbol(bs);
          var offset = symbol & 3;
          copyDist = this.Retrieve(offset);
          copyLen += offset > 1 ? 3 : 2;
        } else {
          copyDist = (int)bs.ReadKnownWidthUInt(symbol - 260);
          copyLen = this._lenTree!.ReadSymbol(bs);
          this.Append(copyDist);
          copyLen += copyDist <= AceConstants.MaxDistAtLen2 ? 2 : copyDist <= AceConstants.MaxDistAtLen3 ? 3 : 4;
        }
        ++copyDist;
        if (have + copyLen > wantSize)
          throw new InvalidDataException("ACE LZ77 match runs past the end of the data.");
        this._dictionary.Copy(copyDist, copyLen);
        have += copyLen;
      } else if (symbol == AceConstants.Lz77TypeCode) {
        nextMode = AceMode.ReadFrom(bs);
        break;
      } else
        throw new InvalidDataException("ACE LZ77 symbol beyond the type code.");
    }
    return (this._dictionary.CopyOut(have), nextMode);
  }
}
