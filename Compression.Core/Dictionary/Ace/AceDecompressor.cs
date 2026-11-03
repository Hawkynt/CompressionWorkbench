// ACE decompression engine, converted from acefile (https://github.com/droe/acefile, BSD-2-Clause,
// Copyright (c) 2017-2026 Daniel Roethlisberger and contributors). See THIRD-PARTY-NOTICE.acefile.txt.

namespace Compression.Core.Dictionary.Ace;

/// <summary>
/// Decompresses ACE members. One instance serves a whole archive: the LZ77 dictionary carries over from
/// member to member, which is what solid archives rely on (members must then be decoded in order).
/// </summary>
public sealed class AceDecompressor {
  private readonly AceLz77 _lz77 = new();
  private readonly AceSound _sound = new();
  private readonly AcePic _pic = new();

  /// <summary>
  /// Decodes an archive or file comment: a 15-bit length, one LZ77-sized Huffman tree, then literals and
  /// copies whose source is found through a hash of the two preceding bytes.
  /// </summary>
  public static byte[] DecompressComment(ReadOnlySpan<byte> packed) {
    var padded = new byte[(packed.Length + 3) & ~3];
    packed.CopyTo(padded);
    var bs = new AceBitStream(padded);
    try {
      var wantSize = (int)bs.ReadBits(15);
      var tree = AceHuffman.ReadTree(bs, AceConstants.Lz77MaxCodeWidth, AceConstants.Lz77NumMainCodes);
      var comment = new List<byte>(wantSize);
      var htab = new int[511];
      while (comment.Count < wantSize) {
        int sourcePos;
        if (comment.Count > 1) {
          var hval = comment[^1] + comment[^2];
          sourcePos = htab[hval];
          htab[hval] = comment.Count;
        } else
          sourcePos = 0;
        var code = tree.ReadSymbol(bs);
        if (code < 256)
          comment.Add((byte)code);
        else
          for (var i = 0; i < code - 256 + 2; ++i)
            comment.Add(comment[sourcePos + i]);
      }
      return [.. comment];
    } catch (Exception e) when (e is EndOfStreamException or ArgumentOutOfRangeException) {
      throw new InvalidDataException("Corrupt ACE comment.", e);
    }
  }

  /// <summary>Decodes one member of <paramref name="size"/> bytes compressed with <paramref name="compressionType"/>.</summary>
  public byte[] Decompress(ReadOnlyMemory<byte> packed, long size, int compressionType, int dictionaryBits) {
    if (size > int.MaxValue)
      throw new NotSupportedException("ACE members larger than 2 GiB are not supported.");
    var dicSize = 1 << Math.Clamp(dictionaryBits, AceConstants.MinDictBits, AceConstants.MaxDictBits);
    try {
      return compressionType switch {
        AceConstants.CompStore => this.Stored(packed, (int)size, dicSize),
        AceConstants.CompAce10 => this.Lz77(packed, (int)size, dicSize),
        AceConstants.CompAce20 => this.Blocked(packed, (int)size, dicSize),
        _ => throw new NotSupportedException($"ACE compression type {compressionType} is unknown."),
      };
    } catch (Exception e) when (e is EndOfStreamException or IndexOutOfRangeException or ArgumentException) {
      throw new InvalidDataException("Corrupt ACE member data.", e);
    }
  }

  private byte[] Stored(ReadOnlyMemory<byte> packed, int size, int dicSize) {
    this._lz77.SetDictionarySize(dicSize);
    if (packed.Length < size)
      throw new InvalidDataException("Truncated stored ACE member.");
    var data = packed[..size].ToArray();
    this._lz77.Register(data);
    return data;
  }

  private static AceBitStream Bits(ReadOnlyMemory<byte> packed) {
    // The reference decoders read whole 32-bit words; a ragged tail (e.g. encryption padding cut off)
    // is padded with zeros, which only ever reaches the never-consumed slack.
    if (packed.Length % 4 == 0)
      return new AceBitStream(packed);
    var padded = new byte[(packed.Length + 3) & ~3];
    packed.Span.CopyTo(padded);
    return new AceBitStream(padded);
  }

  private byte[] Lz77(ReadOnlyMemory<byte> packed, int size, int dicSize) {
    this._lz77.SetDictionarySize(dicSize);
    this._lz77.Reinit();
    var bs = Bits(packed);
    var output = new byte[size];
    var produced = 0;
    try {
      while (produced < size) {
        var (chunk, nextMode) = this._lz77.Read(bs, size - produced);
        if (nextMode != null)
          throw new InvalidDataException("ACE 1.0 LZ77 stream contains a mode instruction.");
        chunk.CopyTo(output, produced);
        produced += chunk.Length;
      }
    } catch (EndOfStreamException e) {
      throw new InvalidDataException("Truncated ACE LZ77 stream.", e);
    }
    return output;
  }

  private byte[] Blocked(ReadOnlyMemory<byte> packed, int size, int dicSize) {
    var bs = Bits(packed);
    this._lz77.SetDictionarySize(dicSize);
    this._lz77.Reinit();
    var output = new byte[size];
    var produced = 0;
    var exeLeftover = new List<byte>();
    var lastDelta = 0;
    AceMode? nextMode = null;
    var mode = new AceMode(AceConstants.ModeLz77);

    try {
      while (produced < size) {
        if (nextMode != null) {
          if (mode.Mode != nextMode.Mode) {
            if (nextMode.Mode is >= AceConstants.ModeSound8 and <= AceConstants.ModeSound32B)
              this._sound.Reinit(nextMode.Mode);
            else if (nextMode.Mode == AceConstants.ModePic)
              this._pic.Reinit(bs);
          }
          mode = nextMode;
          nextMode = null;
        }

        var outChunk = new List<byte>();
        switch (mode.Mode) {
          case AceConstants.ModeLz77Delta: {
            var delta = new List<byte>();
            while (delta.Count < mode.DeltaLen) {
              var (chunk, nm) = this._lz77.Read(bs, mode.DeltaLen - delta.Count);
              delta.AddRange(chunk);
              if (nm != null) {
                if (nextMode != null)
                  throw new InvalidDataException("ACE DELTA block clobbers a pending mode.");
                nextMode = nm;
                if (delta.Count == 0)
                  break;
              }
            }
            if (delta.Count == 0 && nextMode != null)
              continue;
            for (var i = 0; i < delta.Count; ++i) {
              delta[i] = (byte)(delta[i] + lastDelta);
              lastDelta = delta[i];
            }
            if (mode.DeltaDist == 0)
              throw new InvalidDataException("ACE DELTA block with zero distance.");
            var planeSize = mode.DeltaLen / mode.DeltaDist;
            for (var planePos = 0; planePos < planeSize; ++planePos)
              for (var plane = 0; plane < mode.DeltaLen; plane += planeSize)
                outChunk.Add(delta[plane + planePos]);
            break;
          }
          case AceConstants.ModeLz77:
          case AceConstants.ModeLz77Exe: {
            outChunk.AddRange(exeLeftover);
            exeLeftover.Clear();
            var (chunk, nm) = this._lz77.Read(bs, size - produced - outChunk.Count);
            nextMode = nm;
            outChunk.AddRange(chunk);
            if (mode.Mode == AceConstants.ModeLz77Exe)
              exeLeftover = UndoExeFilter(outChunk, produced, mode.ExeMode);
            break;
          }
          case >= AceConstants.ModeSound8 and <= AceConstants.ModeSound32B: {
            var (chunk, nm) = this._sound.Read(bs, size - produced);
            nextMode = nm;
            outChunk.AddRange(chunk);
            this._lz77.Register(chunk);
            break;
          }
          case AceConstants.ModePic: {
            var (chunk, nm) = this._pic.Read(bs, size - produced);
            nextMode = nm;
            outChunk.AddRange(chunk);
            this._lz77.Register(chunk);
            break;
          }
          default:
            throw new InvalidDataException($"ACE: unknown mode {mode.Mode}.");
        }

        if (produced + outChunk.Count > size)
          throw new InvalidDataException("ACE block produces more data than the member holds.");
        outChunk.CopyTo(output, produced);
        produced += outChunk.Count;
      }
    } catch (EndOfStreamException e) {
      throw new InvalidDataException("Truncated ACE stream.", e);
    } catch (ArgumentOutOfRangeException e) {
      throw new InvalidDataException("Corrupt ACE stream.", e);
    }
    return output;
  }

  /// <summary>
  /// Reverts the LZ77_EXE preprocessor (absolute CALL/JMP targets back to relative) in place and returns
  /// the bytes of an instruction cut off at the end of the chunk, which belong to the next block.
  /// </summary>
  private static List<byte> UndoExeFilter(List<byte> chunk, int produced, int exeMode) {
    var i = 0;
    for (; i < chunk.Count; ++i) {
      if (i + 4 >= chunk.Count)
        break;
      if (chunk[i] == 0xE8) {
        var pos = produced + i;
        if (exeMode == 0) {
          var rel16 = (chunk[i + 1] | (chunk[i + 2] << 8)) - pos;
          chunk[i + 1] = (byte)rel16;
          chunk[i + 2] = (byte)(rel16 >> 8);
          i += 2;
        } else {
          var rel32 = (uint)(chunk[i + 1] | (chunk[i + 2] << 8) | (chunk[i + 3] << 16) | (chunk[i + 4] << 24)) - (uint)pos;
          chunk[i + 1] = (byte)rel32;
          chunk[i + 2] = (byte)(rel32 >> 8);
          chunk[i + 3] = (byte)(rel32 >> 16);
          chunk[i + 4] = (byte)(rel32 >> 24);
          i += 4;
        }
      } else if (chunk[i] == 0xE9) {
        var pos = produced + i;
        var rel16 = (chunk[i + 1] | (chunk[i + 2] << 8)) - pos;
        chunk[i + 1] = (byte)rel16;
        chunk[i + 2] = (byte)(rel16 >> 8);
        i += 2;
      }
    }
    // The reference keeps scanning the remaining tail (the iterator resumes after the break element)
    // and holds back from the LAST opcode byte it sees.
    var leftover = new List<byte>();
    for (var j = i + 1; j < chunk.Count; ++j)
      if (chunk[j] == 0xE8 || chunk[j] == 0xE9) {
        leftover = chunk.GetRange(j, chunk.Count - j);
        chunk.RemoveRange(j, chunk.Count - j);
        break;
      }
    return leftover;
  }
}
