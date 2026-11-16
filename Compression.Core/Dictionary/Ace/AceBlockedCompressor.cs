using System.Buffers.Binary;

namespace Compression.Core.Dictionary.Ace;

/// <summary>
/// Writes ACE 2.0 blocked streams (compression type 2): LZ77 with the four repeat-distance codes,
/// optionally behind the EXE filter (x86 CALL targets made absolute) or the DELTA filter (bytes
/// split into planes and differenced), or in the SOUND (audio predictor) or PIC (pixel predictor)
/// mode, chosen per member by trial. One instance per archive; with
/// <c>solid</c> the dictionary carries over between members as the decoder's does.
/// </summary>
/// <remarks>
/// The stream layout mirrors <see cref="AceDecompressor"/>: a mode instruction is the main-tree
/// symbol after the distance codes followed by 8 bits of mode and its parameters (DELTA: 8-bit
/// distance, 17-bit length; EXE: 8-bit variant), and the LZ77 dictionary holds the filtered bytes.
/// WAVE files are also tried in the four SOUND modes and BMP pixel rows in PIC mode; those blocks
/// enter the dictionary without LZ77 symbols, as the decoder registers them. The match search is
/// our own hash-chain parser.
/// </remarks>
public sealed class AceBlockedCompressor {
  private const int HashBits = 16;
  private const int MaxChain = 96;
  private const int MaxBlockSymbols = 32767;
  private const int MaxDeltaLength = 1 << 16; // WinAce's block size; the field holds up to 2^17 - 1
  private const int ExeVariant32 = 1;

  private readonly int _dictBits;
  private readonly bool _solid;
  private byte[] _history = [];

  /// <summary>Creates a compressor with a dictionary of 2^<paramref name="dictionaryBits"/> bytes.</summary>
  public AceBlockedCompressor(int dictionaryBits = AceConstants.DefaultDictBits, bool solid = false) {
    this._dictBits = Math.Clamp(dictionaryBits, AceConstants.MinDictBits, AceConstants.MaxDictBits);
    this._solid = solid;
  }

  /// <summary>Dictionary size in bits (what the file header's parameter field records).</summary>
  public int DictionaryBits => this._dictBits;

  /// <summary>The filter the last <see cref="Compress"/> call chose: "lz77", "exe", "delta:N", "sound:N" (N the mode number) or "pic".</summary>
  public string LastMode { get; private set; } = "lz77";

  /// <summary>Tells a solid compressor about a member that was stored: the decoder adds stored bytes to its dictionary too.</summary>
  public void RegisterStored(ReadOnlySpan<byte> data) {
    if (this._solid)
      this.Remember(data);
  }

  private void Remember(ReadOnlySpan<byte> data) {
    var keep = 1 << this._dictBits;
    var combined = new byte[Math.Min(this._history.Length + data.Length, keep)];
    var fromData = Math.Min(data.Length, combined.Length);
    var fromHistory = combined.Length - fromData;
    this._history.AsSpan(this._history.Length - fromHistory).CopyTo(combined);
    data[(data.Length - fromData)..].CopyTo(combined.AsSpan(fromHistory));
    this._history = combined;
  }

  /// <summary>One segment of the LZ77 input: the bytes the dictionary will hold, and the mode instruction before them (null: none).</summary>
  /// <remarks>A SOUND or PIC segment carries its coded <paramref name="Media"/> bits, which follow the mode instruction; its bytes enter the dictionary without LZ77 symbols.</remarks>
  private sealed record Segment(byte[] Bytes, AceMode? Mode, AceBitWriter? Media = null);

  /// <summary>Compresses one member, trying the plain, EXE, DELTA, SOUND (WAVE files) and PIC (BMP files) layouts and keeping the smallest.</summary>
  public byte[] Compress(ReadOnlySpan<byte> data) {
    if (data.IsEmpty)
      return [];
    List<(string Name, Segment[] Plan)> candidates = this.ForcedMode is { } forced ? [(forced, Forced(data, forced))] : Candidates(data);

    byte[]? best = null;
    Segment[]? bestPlan = null;
    foreach (var (name, plan) in candidates) {
      var packed = this.Encode(plan);
      if (best == null || packed.Length < best.Length) {
        best = packed;
        bestPlan = plan;
        this.LastMode = name;
      }
    }
    if (this._solid)
      foreach (var segment in bestPlan!)
        this.Remember(segment.Bytes);
    return best!;
  }

  /// <summary>
  /// Encodes every member with one layout instead of the smallest (null, the default: choose by size):
  /// "sound:N" puts the whole member in SOUND mode N (3 to 6) whatever it holds, "pic:W:P" codes rows
  /// of W bytes with P interleaved planes from the start and the remainder as LZ77. A layout that does
  /// not fit the data throws <see cref="ArgumentException"/>.
  /// </summary>
  public string? ForcedMode { get; set; }

  private static Segment[] Forced(ReadOnlySpan<byte> data, string mode) {
    var parts = mode.Split(':');
    int Arg(int i) => i < parts.Length && int.TryParse(parts[i], System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : -1;
    var plan = parts[0] switch {
      "sound" when parts.Length == 2 && Arg(1) is >= AceConstants.ModeSound8 and <= AceConstants.ModeSound32B => SoundPlan(data, Arg(1)),
      "pic" when parts.Length == 3 && Arg(1) > 0 && Arg(2) is > 0 and <= 255 => PicSegments(data, 0, data.Length / Arg(1) * Arg(1), Arg(1), Arg(2)),
      _ => null,
    };
    return plan ?? throw new ArgumentException($"ACE layout '{mode}' does not fit this data.", nameof(mode));
  }

  private static List<(string Name, Segment[] Plan)> Candidates(ReadOnlySpan<byte> data) {
    var candidates = new List<(string Name, Segment[] Plan)> { ("lz77", [new(data.ToArray(), null)]) };
    if (LooksLikeX86(data))
      candidates.Add(("exe", [new(ExeForward(data), new AceMode(AceConstants.ModeLz77Exe, ExeMode: ExeVariant32))]));
    if (data.Length >= 64)
      foreach (var distance in new[] { 1, 2, 3, 4 })
        candidates.Add(($"delta:{distance}", DeltaPlan(data, distance)));
    if (IsWave(data))
      for (var mode = AceConstants.ModeSound8; mode <= AceConstants.ModeSound32B; ++mode)
        if (SoundPlan(data, mode) is { } sound)
          candidates.Add(($"sound:{mode}", sound));
    if (PicPlan(data) is { } pic)
      candidates.Add(("pic", pic));
    return candidates;
  }

  /// <summary>A rough x86 test: CALL opcodes are frequent enough to make the EXE filter worth a trial.</summary>
  private static bool LooksLikeX86(ReadOnlySpan<byte> data) {
    var calls = 0;
    foreach (var b in data)
      if (b == 0xE8)
        ++calls;
    return data.Length >= 64 && calls * 128 >= data.Length;
  }

  /// <summary>
  /// The inverse of the decoder's EXE step over a whole member: each CALL (E8) target becomes
  /// absolute in 32 bits, each JMP (E9) target in 16 bits, scanning as the decoder does, with the
  /// last four bytes left as they are.
  /// </summary>
  internal static byte[] ExeForward(ReadOnlySpan<byte> data) {
    var output = data.ToArray();
    for (var i = 0; i < output.Length; ++i) {
      if (i + 4 >= output.Length)
        break;
      if (output[i] == 0xE8) {
        var rel = (uint)(output[i + 1] | (output[i + 2] << 8) | (output[i + 3] << 16) | (output[i + 4] << 24));
        var abs = rel + (uint)i;
        output[i + 1] = (byte)abs;
        output[i + 2] = (byte)(abs >> 8);
        output[i + 3] = (byte)(abs >> 16);
        output[i + 4] = (byte)(abs >> 24);
        i += 4;
      } else if (output[i] == 0xE9) {
        var abs = (output[i + 1] | (output[i + 2] << 8)) + i;
        output[i + 1] = (byte)abs;
        output[i + 2] = (byte)(abs >> 8);
        i += 2;
      }
    }
    return output;
  }

  /// <summary>
  /// DELTA blocks of up to 64 KiB, a multiple of <paramref name="distance"/> bytes (a final block
  /// that is not uses distance 1), each behind its own mode instruction: each block is split into <paramref name="distance"/> planes, laid out plane after
  /// plane, then differenced with a running previous byte that carries from block to block.
  /// </summary>
  private static Segment[] DeltaPlan(ReadOnlySpan<byte> data, int distance) {
    var segments = new List<Segment>();
    var full = MaxDeltaLength / distance * distance;
    byte previous = 0;
    for (var at = 0; at < data.Length;) {
      var length = Math.Min(full, data.Length - at);
      var dist = length % distance == 0 ? distance : 1;
      var block = data.Slice(at, length);
      var planeSize = length / dist;
      var coded = new byte[length];
      for (var i = 0; i < length; ++i) {
        var value = block[(i % planeSize) * dist + i / planeSize];
        coded[i] = (byte)(value - previous);
        previous = value;
      }
      var mode = new AceMode(AceConstants.ModeLz77Delta, DeltaDist: dist, DeltaLen: length);
      // Every block gets its own mode instruction, as WinAce writes it; unace expects one per block.
      segments.Add(new(coded, mode));
      at += length;
    }
    return [.. segments];
  }

  /// <summary>A RIFF WAVE file: the SOUND modes are worth a trial.</summary>
  private static bool IsWave(ReadOnlySpan<byte> data)
    => data.Length >= 64 && data[..4].SequenceEqual("RIFF"u8) && data.Slice(8, 4).SequenceEqual("WAVE"u8);

  /// <summary>
  /// The whole member in SOUND <paramref name="mode"/>, header included, as WinAce 2.6 lays a WAVE file
  /// out. The SOUND reader decodes the member's remaining size rounded down to four bytes, so a member
  /// that is not a multiple of four ends with the sound block at least four bytes short of that, a
  /// switch back to LZ77, and the tail as LZ77.
  /// </summary>
  private static Segment[]? SoundPlan(ReadOnlySpan<byte> data, int mode) {
    var switchTo = new AceMode(mode);
    if (data.Length % 4 == 0)
      return [new(data.ToArray(), switchTo, AceSound.Encode(data, mode, switchBack: false))];
    var length = (data.Length & ~3) - 4;
    if (length <= 0)
      return null;
    var sound = data[..length];
    return [new(sound.ToArray(), switchTo, AceSound.Encode(sound, mode, switchBack: true)), new(data[length..].ToArray(), null)];
  }

  /// <summary>
  /// An uncompressed BMP (8, 16, 24 or 32 bits per pixel): the headers and palette as LZ77, the pixel
  /// rows in PIC mode with the row stride as width and one plane per byte of a pixel, and whatever
  /// follows the rows as LZ77 again.
  /// </summary>
  private static Segment[]? PicPlan(ReadOnlySpan<byte> data) {
    if (data.Length < 54 || data[0] != (byte)'B' || data[1] != (byte)'M')
      return null;
    var offset = BinaryPrimitives.ReadInt32LittleEndian(data[10..]);
    var headerSize = BinaryPrimitives.ReadInt32LittleEndian(data[14..]);
    if (headerSize < 40 || offset < 14 + headerSize || offset >= data.Length)
      return null;
    var width = BinaryPrimitives.ReadInt32LittleEndian(data[18..]);
    var height = Math.Abs(BinaryPrimitives.ReadInt32LittleEndian(data[22..]));
    var bitsPerPixel = BinaryPrimitives.ReadUInt16LittleEndian(data[28..]);
    var compression = BinaryPrimitives.ReadInt32LittleEndian(data[30..]);
    if (width <= 0 || height <= 0 || compression != 0 || bitsPerPixel is not (8 or 16 or 24 or 32))
      return null;
    var stride = (int)(((long)width * bitsPerPixel + 31) / 32 * 4);
    var size = (long)stride * height;
    return size > data.Length - offset ? null : PicSegments(data, offset, (int)size, stride, bitsPerPixel / 8);
  }

  /// <summary>LZ77 up to <paramref name="offset"/>, <paramref name="length"/> bytes of PIC rows, LZ77 for the rest.</summary>
  private static Segment[]? PicSegments(ReadOnlySpan<byte> data, int offset, int length, int width, int planes) {
    if (length <= 0)
      return null;
    var rows = data.Slice(offset, length);
    var end = offset + length;
    var segments = new List<Segment>();
    if (offset > 0)
      segments.Add(new(data[..offset].ToArray(), null));
    segments.Add(new(rows.ToArray(), new AceMode(AceConstants.ModePic), AcePic.Encode(rows, width, planes, switchBack: end < data.Length)));
    if (end < data.Length)
      segments.Add(new(data[end..].ToArray(), null));
    return [.. segments];
  }

  private static int BitLength(uint v) => v == 0 ? 0 : 32 - System.Numerics.BitOperations.LeadingZeroCount(v);

  private static int MinLengthFor(int dist) => dist - 1 <= AceConstants.MaxDistAtLen2 ? 2 : dist - 1 <= AceConstants.MaxDistAtLen3 ? 3 : 4;

  /// <summary>A token: literal, explicit match, repeat match (slot 0 = most recent) or mode instruction.</summary>
  private readonly record struct Token(int Kind, int Value, int Length, AceMode? Mode, AceBitWriter? Media = null) {
    public const int Literal = 0, Match = 1, Repeat = 2, ModeSwitch = 3;
  }

  private byte[] Encode(Segment[] plan) {
    var history = this._solid ? this._history : [];
    var total = history.Length + plan.Sum(s => s.Bytes.Length);
    var buffer = new byte[total];
    history.CopyTo(buffer, 0);
    var fill = history.Length;
    foreach (var s in plan) {
      s.Bytes.CopyTo(buffer, fill);
      fill += s.Bytes.Length;
    }

    var maxDist = (1 << this._dictBits) - 1;
    var head = new int[1 << HashBits];
    Array.Fill(head, -1);
    var prev = new int[buffer.Length];
    static int Hash(byte[] b, int i) => ((b[i] << 10) ^ (b[i + 1] << 5) ^ b[i + 2]) & ((1 << HashBits) - 1);
    void Insert(int i) {
      if (i + 2 >= buffer.Length)
        return;
      var h = Hash(buffer, i);
      prev[i] = head[h];
      head[h] = i;
    }
    for (var i = 0; i < history.Length; ++i)
      Insert(i);

    // The decoder's distance history (values are distance - 1), most recent last; it starts zeroed.
    var hist = new int[AceConstants.DistHistSize];
    var tokens = new List<Token>();
    var pos = history.Length;
    foreach (var segment in plan) {
      if (segment.Mode != null)
        tokens.Add(new(Token.ModeSwitch, 0, 0, segment.Mode, segment.Media));
      var end = pos + segment.Bytes.Length;
      if (segment.Media != null) {
        // The decoder registers SOUND and PIC output in the dictionary, so later matches may reach into it.
        for (; pos < end; ++pos)
          Insert(pos);
        continue;
      }
      while (pos < end) {
        var remaining = end - pos;
        int MatchLength(int dist, int limit) {
          var len = 0;
          while (len < limit && buffer[pos - dist + len] == buffer[pos + len])
            ++len;
          return len;
        }

        // Repeat distances first: slot k is hist[3 - k], with a length base of 2 (slots 0, 1) or 3.
        int repSlot = -1, repLen = 0;
        for (var slot = 0; slot < AceConstants.DistHistSize; ++slot) {
          var dist = hist[AceConstants.DistHistSize - 1 - slot] + 1;
          if (dist > pos)
            continue;
          var minLen = slot > 1 ? 3 : 2;
          var len = MatchLength(dist, Math.Min(remaining, 254 + minLen));
          if (len >= minLen && len > repLen) {
            repLen = len;
            repSlot = slot;
          }
        }

        int bestLen = 0, bestDist = 0;
        if (remaining >= 3) {
          var candidate = head[Hash(buffer, pos)];
          var chain = 0;
          while (candidate >= 0 && chain++ < MaxChain) {
            var dist = pos - candidate;
            if (dist > maxDist)
              break;
            var minLen = MinLengthFor(dist);
            var len = MatchLength(dist, Math.Min(remaining, 254 + minLen));
            if (len >= minLen && len > bestLen) {
              bestLen = len;
              bestDist = dist;
              if (len == Math.Min(remaining, 254 + minLen))
                break;
            }
            candidate = prev[candidate];
          }
        }

        int taken;
        if (repSlot >= 0 && repLen + 1 >= bestLen) {
          tokens.Add(new(Token.Repeat, repSlot, repLen, null));
          var index = AceConstants.DistHistSize - repSlot - 1;
          var value = hist[index];
          for (var i = index; i < AceConstants.DistHistSize - 1; ++i)
            hist[i] = hist[i + 1];
          hist[^1] = value;
          taken = repLen;
        } else if (bestLen > 0) {
          tokens.Add(new(Token.Match, bestDist - 1, bestLen, null));
          for (var i = 0; i < AceConstants.DistHistSize - 1; ++i)
            hist[i] = hist[i + 1];
          hist[^1] = bestDist - 1;
          taken = bestLen;
        } else {
          tokens.Add(new(Token.Literal, buffer[pos], 1, null));
          taken = 1;
        }
        for (var k = 0; k < taken; ++k)
          Insert(pos + k);
        pos += taken;
      }
    }

    var writer = new AceBitWriter();
    for (int blockStart = 0, count; blockStart < tokens.Count; blockStart += count) {
      // A block also ends with a switch into SOUND or PIC: the LZ77 symbols after that excursion
      // start a block of their own, as WinAce writes them and unace expects.
      count = 0;
      while (count < MaxBlockSymbols && blockStart + count < tokens.Count)
        if (tokens[blockStart + count++].Media != null)
          break;
      var mainFreq = new long[AceConstants.Lz77NumMainCodes];
      var lenFreq = new long[AceConstants.Lz77NumLenCodes];
      for (var t = blockStart; t < blockStart + count; ++t) {
        var token = tokens[t];
        switch (token.Kind) {
          case Token.Literal: ++mainFreq[token.Value]; break;
          case Token.ModeSwitch: ++mainFreq[AceConstants.Lz77TypeCode]; break;
          case Token.Repeat:
            ++mainFreq[256 + token.Value];
            ++lenFreq[token.Length - (token.Value > 1 ? 3 : 2)];
            break;
          default:
            ++mainFreq[260 + BitLength((uint)token.Value)];
            ++lenFreq[token.Length - MinLengthFor(token.Value + 1)];
            break;
        }
      }
      var mainWidths = AceHuffman.BuildWidths(mainFreq, AceConstants.Lz77MaxCodeWidth);
      var lenWidths = AceHuffman.BuildWidths(lenFreq, AceConstants.Lz77MaxCodeWidth);
      AceHuffman.WriteTree(writer, mainWidths);
      AceHuffman.WriteTree(writer, lenWidths);
      writer.WriteBits((uint)count, 15);
      var mainCodes = AceHuffman.Codes(mainWidths, AceConstants.Lz77MaxCodeWidth);
      var lenCodes = AceHuffman.Codes(lenWidths, AceConstants.Lz77MaxCodeWidth);
      void Put((uint Code, int Width) code) => writer.WriteBits(code.Code, code.Width);
      for (var t = blockStart; t < blockStart + count; ++t) {
        var token = tokens[t];
        switch (token.Kind) {
          case Token.Literal:
            Put(mainCodes[token.Value]);
            break;
          case Token.ModeSwitch: {
            Put(mainCodes[AceConstants.Lz77TypeCode]);
            var mode = token.Mode!;
            writer.WriteBits((uint)mode.Mode, 8);
            if (mode.Mode == AceConstants.ModeLz77Delta) {
              writer.WriteBits((uint)mode.DeltaDist, 8);
              writer.WriteBits((uint)mode.DeltaLen, 17);
            } else if (mode.Mode == AceConstants.ModeLz77Exe)
              writer.WriteBits((uint)mode.ExeMode, 8);
            if (token.Media != null)
              writer.Append(token.Media);
            break;
          }
          case Token.Repeat:
            Put(mainCodes[256 + token.Value]);
            Put(lenCodes[token.Length - (token.Value > 1 ? 3 : 2)]);
            break;
          default: {
            var width = BitLength((uint)token.Value);
            Put(mainCodes[260 + width]);
            writer.WriteKnownWidthUInt((uint)token.Value, width);
            Put(lenCodes[token.Length - MinLengthFor(token.Value + 1)]);
            break;
          }
        }
      }
    }
    return writer.ToArray();
  }
}
