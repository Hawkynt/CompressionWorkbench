#pragma warning disable CS1591
using System.Buffers.Binary;

namespace FileFormat.ExePackers;

/// <summary>
/// The compressed-stream format used by WinUpack / Upack (Dwing).
/// </summary>
/// <remarks>
/// <para>
/// Format derived clean-room from the behaviour of the in-file decompressor of
/// the packed samples themselves: Upack embeds its unpacker as plain x86 in the
/// packed image, so the container layout, the coder and the branch filter can be
/// read off the loader stub without consulting any third-party source. Nothing
/// here is ported from Upack, from unpacker plugins or from the LZMA SDK.
/// </para>
/// <para>
/// Coder: a binary range coder in the LZMA idiom — 11-bit probabilities
/// (<c>2048</c> total, initialised to <c>1024</c>), a 5-bit adaptation shift, and
/// a 32-bit range normalised one byte at a time whenever the top byte of the
/// range goes to zero. Upack's variant keeps a running <c>low</c> accumulator and
/// re-derives the code word on every query as
/// <c>bigEndian32(input[cursor]) - low</c>, so unlike stock LZMA the stream has no
/// leading padding byte: the first four payload bytes are all significant.
/// </para>
/// <para>
/// Symbol structure is LZMA's: 12 coder states, literal / match / rep-match /
/// short-rep decisions, four recycled distances, a length coder split
/// 8 / 8 / 256 across two choice bits, six-bit position slots selected by the
/// capped match length, direct bits plus a shared four-bit aligned tail for the
/// large slots. Upack fixes <c>lc = 3</c>, <c>lp = 0</c> and <c>pb = 0</c>, which
/// is why the IsMatch decision is indexed by the coder state alone. A match
/// copies <c>len + 1</c> bytes, so the shortest match is two bytes.
/// </para>
/// <para>
/// Probability slots live in one flat array of 7168 entries, laid out as:
/// 0 align, 16 IsMatch, 28 IsRep, 40 IsRepG0, 52 IsRep0Long, 64 IsRepG1,
/// 76 IsRepG2, 88 SpecPos, 216 PosSlot, 472 rep-match length, 746 match length,
/// 1020 literals (8 coders of 768).
/// </para>
/// </remarks>
internal static class WinUpackStream {
  private const int PROB_COUNT = 7168;
  private const int ALIGN = 0;
  private const int IS_MATCH = 16;
  private const int IS_REP = 28;
  private const int IS_REP_G0 = 40;
  private const int IS_REP0_LONG = 52;
  private const int IS_REP_G1 = 64;
  private const int IS_REP_G2 = 76;
  private const int SPEC_POS = 88;
  private const int POS_SLOT = 216;
  private const int REP_LEN_CODER = 472;
  private const int LEN_CODER = 746;
  private const int LITERAL = 1020;

  private const int PROB_INIT = 1 << 10;
  private const int PROB_TOTAL_BITS = 11;
  private const int PROB_MOVE_BITS = 5;

  /// <summary>Decodes one Upack payload into <paramref name="outputSize"/> bytes.</summary>
  public static byte[] Decompress(ReadOnlySpan<byte> input, int outputSize) {
    if (outputSize <= 0)
      throw new InvalidDataException("WinUpack: non-positive output size.");

    var coder = new Coder(input);
    var output = new byte[outputSize];
    var reps = new uint[4] { 1, 1, 1, 1 };
    var state = 0;
    var position = 0;
    var afterMatch = false;

    while (position < outputSize) {
      if (coder.DecodeBit(IS_MATCH + state) == 0) {
        var next = state;
        do
          next = next >= 3 ? next - 3 : 0;
        while (next >= 7);

        var literalBase = LITERAL + ((position > 0 ? output[position - 1] : 0) >> 5) * 768;
        var symbol = 1;
        var complete = false;
        if (afterMatch) {
          var matchByte = output[position - (int)reps[0]];
          for (var mask = 0x80; ;) {
            var matchBit = (matchByte & mask) != 0 ? 1 : 0;
            var bit = coder.DecodeBit(literalBase + ((matchBit + 1) << 8) + symbol);
            symbol = ((symbol << 1) | bit) & 0xFF;
            mask >>= 1;
            if (mask == 0) {
              output[position++] = (byte)symbol;
              complete = true;
              break;
            }
            if (bit != matchBit)
              break;
          }
        }

        if (!complete) {
          while (symbol < 0x100)
            symbol = (symbol << 1) | coder.DecodeBit(literalBase + symbol);
          output[position++] = (byte)symbol;
        }

        state = next;
        afterMatch = false;
        continue;
      }

      var matchState = state >= 7 ? 11 : 8;
      uint length;
      if (coder.DecodeBit(IS_REP + state) == 0) {
        reps[3] = reps[2];
        reps[2] = reps[1];
        reps[1] = reps[0];
        length = coder.DecodeLength(LEN_CODER);
        var slot = coder.DecodeBitTree(POS_SLOT + (int)Math.Min(length - 1, 3) * 64, 64);
        uint distance;
        if (slot < 4)
          distance = slot + 1;
        else {
          var directBits = (int)(slot >> 1) - 1;
          var baseValue = (2u | (slot & 1)) << directBits;
          if (directBits <= 5)
            distance = baseValue + coder.DecodeReverseBitTree(SPEC_POS + (int)baseValue, directBits) + 1;
          else {
            baseValue += coder.DecodeDirectBits(directBits - 4) << 4;
            distance = baseValue + coder.DecodeReverseBitTree(ALIGN, 4) + 1;
          }
        }

        reps[0] = distance;
        state = matchState - 1;
      } else {
        if (coder.DecodeBit(IS_REP_G0 + state) == 0) {
          if (coder.DecodeBit(IS_REP0_LONG + state) == 0) {
            state = matchState | 1;
            if (reps[0] > (uint)position)
              throw new InvalidDataException("WinUpack: short-rep distance points before the output.");
            output[position] = output[position - (int)reps[0]];
            ++position;
            afterMatch = true;
            continue;
          }
        } else {
          // Pick one of the older distances and rotate it back to the front.
          uint distance;
          if (coder.DecodeBit(IS_REP_G1 + state) == 0)
            distance = reps[1];
          else {
            if (coder.DecodeBit(IS_REP_G2 + state) == 0)
              distance = reps[2];
            else {
              distance = reps[3];
              reps[3] = reps[2];
            }

            reps[2] = reps[1];
          }

          reps[1] = reps[0];
          reps[0] = distance;
        }

        state = matchState;
        length = coder.DecodeLength(REP_LEN_CODER);
      }

      var count = length + 1;
      if (reps[0] > (uint)position)
        throw new InvalidDataException("WinUpack: match distance points before the output.");
      var source = position - (int)reps[0];
      for (var i = 0u; i < count && position < outputSize; ++i)
        output[position++] = output[source + (int)i];
      afterMatch = true;
    }

    return output;
  }

  /// <summary>
  /// Reverses Upack's call/jump filter over an already decompressed image.
  /// </summary>
  /// <remarks>
  /// The packer rewrites the four operand bytes of up to <paramref name="count"/>
  /// <c>E8</c>/<c>E9</c> instructions as <c>tag, b1, b2, b3</c>, where
  /// <c>b1..b3</c> is a big-endian 24-bit target and <c>tag</c> is a per-file
  /// marker byte that keeps the scan from mangling data that merely looks like a
  /// call. Restoring an entry means <c>rel32 = (b1&lt;&lt;16 | b2&lt;&lt;8 | b3) +
  /// filterBase - va</c>, with <c>va</c> the address of the operand itself; the
  /// packer folds the usual "relative to the end of the instruction" bias into
  /// <c>filterBase</c>, which is always the image start minus four.
  /// </remarks>
  public static void UndoBranchFilter(byte[] image, uint imageVirtualAddress, uint filterBase, uint count, byte tag) {
    if (count == 0 || image.Length < 8)
      return;

    var cursor = 0;
    var current = image[cursor];
    var limit = image.Length - 4;
    while (count > 0 && cursor < limit) {
      ++cursor;
      if (((current + 0x18) & 0xFF) >= 2) {
        current = image[cursor];
        continue;
      }

      var stored = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(cursor));
      current = (byte)stored;
      if (current != tag)
        continue;

      var target = ((stored >> 8) & 0xFF) << 16 | ((stored >> 16) & 0xFF) << 8 | (stored >> 24) & 0xFF;
      var relative = target + filterBase - (imageVirtualAddress + (uint)cursor);
      BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(cursor), relative);
      cursor += 4;
      --count;
      if (cursor >= image.Length)
        return;
      current = image[cursor];
    }
  }

  private sealed class Coder {
    private readonly byte[] _input;
    private readonly ushort[] _probabilities = new ushort[PROB_COUNT];
    private int _cursor;
    private uint _range = uint.MaxValue;
    private uint _low;

    public Coder(ReadOnlySpan<byte> input) {
      // The coder always reads a full big-endian word, so the tail needs padding.
      this._input = new byte[input.Length + 8];
      input.CopyTo(this._input);
      this._probabilities.AsSpan().Fill(PROB_INIT);
    }

    private uint Code => BinaryPrimitives.ReadUInt32BigEndian(this._input.AsSpan(this._cursor)) - this._low;

    private void Normalize() {
      if (this._range >> 24 != 0)
        return;
      if (this._cursor + 4 >= this._input.Length)
        throw new InvalidDataException("WinUpack: compressed stream ended early.");
      ++this._cursor;
      this._low <<= 8;
      this._range <<= 8;
    }

    public int DecodeBit(int index) {
      var probability = this._probabilities[index];
      var bound = (this._range >> PROB_TOTAL_BITS) * probability;
      int bit;
      if (bound > this.Code) {
        this._range = bound;
        this._probabilities[index] = (ushort)(probability + (((1 << PROB_TOTAL_BITS) - probability) >> PROB_MOVE_BITS));
        bit = 0;
      } else {
        this._low += bound;
        this._range -= bound;
        this._probabilities[index] = (ushort)(probability - (probability >> PROB_MOVE_BITS));
        bit = 1;
      }

      this.Normalize();
      return bit;
    }

    public uint DecodeDirectBits(int count) {
      var value = 0u;
      for (var i = 0; i < count; ++i) {
        this._range >>= 1;
        value <<= 1;
        if (this.Code >= this._range) {
          value |= 1;
          this._low += this._range;
        }

        this.Normalize();
      }

      return value;
    }

    public uint DecodeBitTree(int probabilityBase, uint limit) {
      var node = 1u;
      while (node < limit)
        node = (node << 1) | (uint)this.DecodeBit(probabilityBase + (int)node);
      return node - limit;
    }

    public uint DecodeReverseBitTree(int probabilityBase, int bits) {
      var symbol = this.DecodeBitTree(probabilityBase, 1u << bits);
      var reversed = 0u;
      for (var i = 0; i < bits; ++i) {
        reversed = (reversed << 1) | (symbol & 1);
        symbol >>= 1;
      }

      return reversed;
    }

    public uint DecodeLength(int probabilityBase) {
      if (this.DecodeBit(probabilityBase) == 0)
        return this.DecodeBitTree(probabilityBase + 2, 8) + 1;
      if (this.DecodeBit(probabilityBase + 1) == 0)
        return this.DecodeBitTree(probabilityBase + 10, 8) + 9;
      return this.DecodeBitTree(probabilityBase + 18, 256) + 17;
    }
  }
}
