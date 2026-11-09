namespace Compression.Core.Dictionary.Sqx;

/// <summary>
/// Decoder of SQX methods 1-4 (LZ77 + Huffman, with range-coded multimedia blocks and the
/// optional delta and x86 call filters), converted from Squeez-c's <c>lz.c</c>, <c>huff.c</c>,
/// <c>delta.c</c> and <c>rc.c</c> (MIT; see <c>THIRD-PARTY-NOTICE.Squeez-c.txt</c>). One instance
/// keeps the window, the repeat distances and the last match between files, which a solid
/// archive continues.
/// </summary>
public sealed class SqxLzDecoder {
  private const ushort Ia32ImageFlag = 0x0002;
  private const ushort DeltaFlag = 0x0004;

  private readonly byte[] _window;
  private readonly int _dictionarySize;
  private readonly uint _mask;
  private uint _currPos, _writePos;
  private uint _lastLen, _lastDist, _prevIndex, _distState;
  private readonly uint[] _prevDists = new uint[4];

  private readonly SqxHuffman _meta = new(SqxTables.MetaCodes, SqxTables.MetaCodeBits);
  private readonly SqxHuffman[] _litLen = [new(SqxTables.LitLenCodes20, SqxTables.CodeBits), new(SqxTables.LitLenCodes20, SqxTables.CodeBits)];
  private readonly SqxHuffman _rep = new(SqxTables.LenCodes, SqxTables.CodeBits);
  private readonly SqxHuffman _dist = new(SqxTables.DistCodes11, SqxTables.CodeBits);
  private readonly SqxHuffman _low16 = new(SqxTables.Low16Codes, SqxTables.CodeBits);
  private bool _have16;

  // Per-file state.
  private bool _format20;
  private long _toFlush;
  private List<byte> _output = [];
  private bool _codeDeltas, _needOffset, _imageFilter;
  private int _deltaType, _deltaChannels, _deltaOffset, _deltaOffsetSoFar, _imageSize, _imageLastCount;
  private SqxDelta.ByteFilter[]? _lzDelta;
  private SqxDelta.WordFilter? _lzDelta16;

  /// <summary>Creates a decoder for a dictionary of <paramref name="dictionarySize"/> bytes (a power of two).</summary>
  public SqxLzDecoder(int dictionarySize) {
    if (dictionarySize <= 0 || (dictionarySize & (dictionarySize - 1)) != 0)
      throw new ArgumentOutOfRangeException(nameof(dictionarySize));
    this._dictionarySize = dictionarySize;
    this._mask = (uint)dictionarySize - 1;
    this._window = new byte[dictionarySize + 0x10000];
  }

  /// <summary>
  /// Decodes one file's stream. <paramref name="solid"/> continues the window and match state of
  /// the previous file; <paramref name="extendedHeader"/> is bit 0 of the file's <c>cCompFlags</c>.
  /// </summary>
  /// <exception cref="InvalidDataException">The stream is malformed or ends early.</exception>
  public byte[] Decode(ReadOnlySpan<byte> stream, long originalSize, bool solid, bool extendedHeader, byte format) {
    var data = stream.ToArray();
    try {
      return this.DecodeCore(data, originalSize, solid, extendedHeader, format);
    } catch (IndexOutOfRangeException) {
      throw new InvalidDataException("SQX stream is malformed.");
    }
  }

  private byte[] DecodeCore(byte[] data, long originalSize, bool solid, bool extendedHeader, byte format) {
    var reader = new SqxBitReader(data, 0, data.Length);
    var left = originalSize;
    var blockLeft = 0;
    var maxTreeCode = 0u;
    var fourTrees = false;
    var lastCode = 0u;

    this._format20 = format >= SqxTables.Format20;
    this._toFlush = originalSize;
    this._output = new List<byte>((int)Math.Min(originalSize, 1 << 26));
    if (!solid) {
      this._lastLen = this._lastDist = this._prevIndex = this._writePos = this._currPos = 0;
      Array.Clear(this._prevDists);
    }

    this._codeDeltas = this._needOffset = this._imageFilter = false;
    this._deltaOffsetSoFar = 0;
    this._lzDelta = null;
    this._lzDelta16 = null;
    this._imageSize = (int)originalSize;
    this._imageLastCount = 0;

    if (extendedHeader) {
      var flags = (ushort)(reader.Read(8) | reader.Read(8) << 8);
      if ((flags & DeltaFlag) != 0) {
        this._deltaType = (int)reader.Read(8);
        this._deltaChannels = (int)reader.Read(8);
        var b0 = reader.Read(8);
        var b1 = reader.Read(8);
        var b2 = reader.Read(8);
        var b3 = reader.Read(8);
        this._deltaOffset = (int)(b0 | b1 << 8 | b2 << 16 | b3 << 24);
        this._needOffset = true;
        this._codeDeltas = true;
        if (this._deltaType != SqxDelta.SixteenBit && this._deltaChannels is < 1 or > 4)
          throw new InvalidDataException($"SQX delta filter with {this._deltaChannels} channels.");
      }

      if ((flags & Ia32ImageFlag) != 0)
        this._imageFilter = true;
    }

    while (left > 0) {
      if (blockLeft == 0) {
        var (count, isMultimedia) = this.ReadBlockHeader(reader, out maxTreeCode, out fourTrees);
        if (isMultimedia) {
          this._currPos += (uint)count;
          left -= count;
          if (this._currPos >= this._dictionarySize)
            this.WriteDictionary();
          this._currPos &= this._mask;
          continue;
        }

        if (count <= 0)
          throw new InvalidDataException("SQX block with no symbols.");
        blockLeft = count;
        lastCode = maxTreeCode;
      }

      if (reader.Overrun)
        throw new InvalidDataException("SQX stream ends early.");

      uint code;
      if (!fourTrees)
        code = (uint)this._litLen[0].Decode(reader);
      else
        code = lastCode = (uint)this._litLen[lastCode < maxTreeCode + 1 ? 0 : 1].Decode(reader);
      --blockLeft;

      var symbol = code;
      if (!this._format20)
        symbol = code + (code > 256 ? 1u : 0u) + (code > 283 ? 1u : 0u);

      if (symbol < 256) {
        this.Put((byte)symbol);
        --left;
      } else if (symbol == 256) {
        var dist = this._lastDist;
        this._distState = dist;
        this._prevDists[this._prevIndex++ & 3] = dist;
        this.Copy(dist, this._lastLen);
        left -= this._lastLen;
      } else if (symbol == SqxTables.SymCopyOne) {
        var dist = this._distState;
        if (dist == 0 || dist > this._output.Count + (uint)(this._currPos - this._writePos))
          throw new InvalidDataException("SQX copy before any output.");
        this.Copy(dist, 1);
        --left;
      } else if (symbol < SqxTables.Len2Start20) {
        var k = symbol - 257;
        this._lastDist = this._prevDists[(this._prevIndex - k) & 3];
        var rep = this._rep.Decode(reader);
        if (rep == SqxTables.LenCodes - 1)
          this._lastLen = 257 + reader.Read(14);
        else {
          this._lastLen = SqxTables.LenOffsets[rep];
          var extra = SqxTables.LenExtraBits[rep];
          if (extra > 0)
            this._lastLen += reader.Read(extra);
          this._lastLen += 2;
          if (this._format20) {
            if (this._lastDist > 0x7FFF)
              ++this._lastLen;
          } else {
            if (this._lastDist > SqxTables.MinLen3)
              ++this._lastLen;
            if (this._lastDist > SqxTables.MinLen4)
              ++this._lastLen;
          }
        }

        this._distState = this._lastDist;
        left -= this._lastLen;
        this._prevDists[this._prevIndex++ & 3] = this._lastDist;
        this.Copy(this._lastDist, this._lastLen);
      } else if (symbol < SqxTables.Len3Start20) {
        var k = (int)(symbol - SqxTables.Len2Start20);
        var dist = SqxTables.Len2Offsets[k];
        var extra = SqxTables.Len2ExtraBits[k];
        if (extra > 0)
          dist += reader.Read(extra);
        ++dist;
        left -= 2;
        this._distState = dist;
        this._prevDists[this._prevIndex++ & 3] = dist;
        this.Copy(dist, 2);
      } else if (symbol < SqxTables.LenStart20) {
        var k = (int)(symbol - SqxTables.Len3Start20);
        var dist = SqxTables.Len3Offsets[k];
        var extra = SqxTables.Len3ExtraBits[k];
        if (extra > 0)
          dist += reader.Read(extra);
        ++dist;
        left -= 3;
        this._distState = dist;
        this._prevDists[this._prevIndex++ & 3] = dist;
        this.Copy(dist, 3);
      } else {
        uint length, dist;
        if (symbol == SqxTables.LitLenCodes20 - 1) {
          length = 257 + reader.Read(14);
          dist = this.ReadDistance(reader);
        } else {
          var k = (int)(symbol - SqxTables.LenStart20);
          length = SqxTables.LenOffsets[k];
          var extra = SqxTables.LenExtraBits[k];
          if (extra > 0)
            length += reader.Read(extra);
          length += 4;
          dist = this.ReadDistance(reader);
          if (!this._format20 && dist > SqxTables.MinLen4)
            ++length;
        }

        left -= length;
        this._distState = dist;
        this._prevDists[this._prevIndex++ & 3] = dist;
        this.Copy(dist, length);
      }
    }

    if (left < 0)
      throw new InvalidDataException("SQX stream produces more than the original size.");
    this.WriteDictionary();
    if (this._output.Count != originalSize)
      throw new InvalidDataException("SQX stream did not produce the original size.");
    return [.. this._output];
  }

  private void Put(byte value) {
    this._window[this._currPos] = value;
    if (++this._currPos >= this._dictionarySize)
      this.WriteDictionary();
    this._currPos &= this._mask;
  }

  private void Copy(uint dist, uint length) {
    if (length > 0 && (dist == 0 || dist > this._dictionarySize))
      throw new InvalidDataException($"SQX match distance {dist} is outside the dictionary.");
    while (length-- > 0) {
      this._window[this._currPos] = this._window[(this._currPos - dist) & this._mask];
      if (++this._currPos >= this._dictionarySize)
        this.WriteDictionary();
      this._currPos &= this._mask;
    }
  }

  private uint ReadDistance(SqxBitReader reader) {
    var code = this._dist.Decode(reader);
    if (code >= SqxTables.DistBase.Length)
      throw new InvalidDataException($"SQX distance code {code} is not supported.");
    var dist = SqxTables.DistBase[code];
    var extra = SqxTables.DistExtraBits[code];
    if (extra > 0) {
      if (this._format20 && this._have16 && extra >= 4) {
        var high = extra > 4 ? reader.Read(extra - 4) : 0;
        var low = (uint)this._low16.Decode(reader);
        dist += (high << 4) + low;
      } else
        dist += reader.Read(extra);
    }

    return dist + 1;
  }

  /// <summary>Reads a block header; returns the block's symbol count and whether it was a multimedia block (already decoded into the window).</summary>
  private (int Count, bool Multimedia) ReadBlockHeader(SqxBitReader reader, out uint maxTreeCode, out bool fourTrees) {
    maxTreeCode = 0;
    fourTrees = false;
    if (reader.Read(1) == 0) {
      var count = (int)reader.Read(15);
      this._have16 = false;
      if (this._format20) {
        this._have16 = reader.Read(1) == 1;
        if (this._have16) {
          var width = (int)reader.Read(4);
          for (var i = 0; i < SqxTables.Low16Codes; ++i)
            this._low16.Lengths[i] = (byte)reader.Read(width);
          Require(this._low16.Build());
        }
      }

      fourTrees = reader.Read(1) == 1;
      var litLenCodes = this._format20 ? SqxTables.LitLenCodes20 : SqxTables.LitLenCodes11;
      this._litLen[0].Symbols = this._litLen[1].Symbols = litLenCodes;
      this._dist.Symbols = this._format20 ? SqxTables.DistCodes20 : SqxTables.DistCodes11;

      if (!fourTrees) {
        if (reader.Read(1) == 0) {
          this.ReadMeta(reader);
          Require(this._litLen[0].ReadTree(reader, this._meta));
          Require(this._rep.ReadTree(reader, this._meta));
          Require(this._dist.ReadTree(reader, this._meta));
        } else {
          Require(this._litLen[0].ReadFlat(reader));
          Require(this._rep.ReadFlat(reader));
          Require(this._dist.ReadFlat(reader));
        }
      } else {
        maxTreeCode = reader.Read(9);
        this.ReadMeta(reader);
        Require(this._litLen[0].ReadTree(reader, this._meta));
        Require(this._litLen[1].ReadTree(reader, this._meta));
        Require(this._rep.ReadFlat(reader));
        Require(this._dist.ReadFlat(reader));
      }

      return (count, false);
    }

    var deltaBase = reader.Read(1);
    var deltaStart = reader.Read(3);
    var deltaChannels = reader.Read(2) + 1;
    var entropyChannels = (int)reader.Read(2) + 1;
    var size = (int)reader.Read(15);
    var target = this._window.AsSpan((int)this._currPos, size);
    this.DecodeMultimedia(reader, entropyChannels, target);
    SqxDelta.DecodeBlock(deltaStart, deltaBase, deltaChannels, target);
    return (size, true);
  }

  private void ReadMeta(SqxBitReader reader) {
    for (var i = 0; i < SqxTables.MetaCodes; ++i)
      this._meta.Lengths[i] = (byte)reader.Read(4);
    Require(this._meta.Build());
  }

  private void DecodeMultimedia(SqxBitReader reader, int channels, Span<byte> target) {
    var max = target.Length / channels * channels;
    var initial = this._format20 ? 8u : 1u;
    var models = new SqxRangeModel[channels];
    for (var i = 0; i < channels; ++i)
      models[i] = new SqxRangeModel(256, 8, 1u << 12, true, initial);
    var decoder = new SqxRangeDecoder();
    decoder.Start(reader);
    var index = 0;
    for (var count = 0; count < max; ++count) {
      target[count] = (byte)decoder.Decode(models[index], reader);
      if (++index == channels)
        index = 0;
    }

    for (var i = max; i < target.Length; ++i)
      target[i] = (byte)reader.Read(8);
  }

  private static void Require(bool ok) {
    if (!ok)
      throw new InvalidDataException("SQX Huffman table is not a complete code.");
  }

  /// <summary>Hands buffered window bytes to the output, applying the file's filters (Squeez-c's <c>write_dictionary</c>).</summary>
  private void WriteDictionary() {
    if (this._toFlush < 1)
      return;
    if (this._currPos == this._dictionarySize && this._writePos == this._dictionarySize) {
      this.WriteOut(0, (int)this._currPos);
      this._writePos = 0;
      this._toFlush -= this._dictionarySize;
    } else if (this._currPos < this._writePos) {
      var n = (uint)(-(int)this._writePos) & this._mask;
      this.WriteOut((int)this._writePos, (int)n);
      this._toFlush -= n;
      this.WriteOut(0, (int)this._currPos);
      this._toFlush -= this._currPos;
      this._writePos = this._currPos;
    } else {
      this.WriteOut((int)this._writePos, (int)(this._currPos - this._writePos));
      this._toFlush -= this._currPos - this._writePos;
      this._writePos = this._currPos;
    }
  }

  private void WriteOut(int start, int count) {
    var buffer = this._window.AsSpan(start, count).ToArray();
    if (this._codeDeltas && count > 0) {
      if (this._needOffset) {
        if (this._deltaOffsetSoFar + count > this._deltaOffset) {
          this._needOffset = false;
          this.InitLzDelta();
          var index = count - (this._deltaOffsetSoFar + count - this._deltaOffset);
          this.ApplyLzDelta(buffer.AsSpan(index));
        } else
          this._deltaOffsetSoFar += count;
      } else
        this.ApplyLzDelta(buffer);
    }

    if (this._imageFilter)
      this.ReImage(buffer);
    this._output.AddRange(buffer);
  }

  private void InitLzDelta() {
    if (this._deltaType == SqxDelta.EightBit) {
      this._lzDelta = new SqxDelta.ByteFilter[4];
      for (var i = 0; i < 4; ++i)
        this._lzDelta[i] = new SqxDelta.ByteFilter(0, SqxDelta.StandardFrequency);
    } else
      this._lzDelta16 = new SqxDelta.WordFilter(0, SqxDelta.StandardFrequency);
  }

  private void ApplyLzDelta(Span<byte> buffer) {
    if (this._deltaType == SqxDelta.SixteenBit)
      SqxDelta.Decode(this._lzDelta16!, buffer);
    else
      SqxDelta.Decode(this._lzDelta!, this._deltaChannels, buffer);
  }

  /// <summary>Undoes the x86 relative-call transform on one output chunk.</summary>
  private void ReImage(byte[] buffer) {
    var length = buffer.Length;
    if (length < 0x100) {
      this._imageLastCount += length;
      return;
    }

    var lastCount = this._imageLastCount;
    var imageSize = this._imageSize;
    for (var i = 0; i < length - 7; ++i) {
      if (buffer[i] != 0xE8)
        continue;
      var a = BitConverter.ToInt32(buffer, i + 1);
      if (a is int.MinValue or int.MaxValue) {
        i += 4;
        continue;
      }

      if (a > 0 && a < imageSize)
        a -= i + lastCount;
      else if (-a < i + lastCount && -a > 0)
        a = imageSize + a;
      else {
        i += 4;
        continue;
      }

      BitConverter.TryWriteBytes(buffer.AsSpan(i + 1, 4), a);
      i += 4;
    }

    this._imageLastCount += length;
  }
}
