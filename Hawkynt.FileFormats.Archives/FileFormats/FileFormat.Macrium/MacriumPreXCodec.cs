#pragma warning disable CS1591
using System.Buffers.Binary;

namespace FileFormat.Macrium;

/// <summary>
/// Decoder for the proprietary Lempel-Ziv-derived block payload codec used
/// by Macrium Reflect pre-X (.mrimg / .mrbak / .mrex / .mrsql) containers.
/// <para>
/// This is a clean-room C# re-implementation of the algorithm whose layout
/// is documented (algorithmically — no source copy) in the MIT-licensed
/// community reference project <c>ccooper21/mrimg-tools</c>
/// (<see href="https://github.com/ccooper21/mrimg-tools"/>). That project
/// reverse-engineered the Reflect block codec from observed
/// compressed/uncompressed pairs and described the token layout in a Python
/// proof-of-concept; the algorithm itself is unencumbered.
/// </para>
/// <para>
/// Decoder shape:
/// </para>
/// <list type="bullet">
///   <item>A block opens with a 9-byte preamble
///     <c>[flags:1=0x03][compressed_len:4 LE][uncompressed_len:4 LE]</c>;
///     this preamble is consumed by <see cref="MacriumPreXFormatDescriptor"/>
///     before <see cref="DecodeBlock(ReadOnlySpan{byte}, int)"/> is called.
///     The codec entry point receives the post-preamble compressed body and
///     the declared uncompressed length.</item>
///   <item>The compressed body is a stream of <b>tokens</b> guided by an
///     interleaved 32-bit control word. The low bit of the control word
///     decides the next token: <c>0</c> = literal byte, <c>1</c> = back-
///     reference / RLE operation.</item>
///   <item>Each control word starts as <c>1</c> (the sentinel). When the
///     value reaches <c>1</c> again it is time to read the next 4 bytes
///     from the input as a fresh control word, then OR in
///     <c>0x80000000</c> so the top bit acts as the new "still has bits"
///     sentinel — giving 31 token slots per control word.</item>
///   <item>For an operation token the low nibble of the next 4-byte word
///     dispatches to one of six op variants (see method body for the
///     exact layout). Each variant decodes a <c>(segment_len, rel_offset)</c>
///     pair for an LZ77-style back-reference, or a <c>(run_len, byte)</c>
///     pair for run-length-encoding.</item>
///   <item>Back-references are emitted byte-at-a-time so that overlapping
///     matches (used for short repeating patterns) work correctly.</item>
/// </list>
/// <para>
/// The encoder is intentionally NOT implemented here. Reflect produces
/// these blocks; our role is to decode them so callers can read backup
/// content. Round-trip is exercised in tests via hand-crafted reference
/// vectors covering every dispatch branch.
/// </para>
/// </summary>
public static class MacriumPreXCodec {
  /// <summary>The block preamble flags byte that signals a data block.</summary>
  public const byte DataBlockFlags = 0x03;

  /// <summary>Length of the on-disk preamble in bytes (flags + comp_len + uncomp_len).</summary>
  public const int PreambleLength = 9;

  /// <summary>
  /// Maximum value we tolerate for <c>uncompressed_len</c> when validating
  /// a preamble. Reflect's default block size is 1 MiB; legitimate blocks
  /// almost always sit at or under 4 MiB. Anything beyond this cap is
  /// treated as corrupt input.
  /// </summary>
  public const int MaxUncompressedSize = 64 * 1024 * 1024;

  /// <summary>
  /// Bit pattern that primes <c>control_flags</c> on entry to a new control
  /// word: the loop trips on this sentinel and reloads the next 4 bytes.
  /// </summary>
  private const uint ControlWordReloadSentinel = 0x0000_0001u;

  /// <summary>
  /// Bit set into the high bit of a fresh control word to mark "31 token
  /// slots still available". When this single bit is left we trip the
  /// reload sentinel again.
  /// </summary>
  private const uint ControlWordTopSentinel = 0x8000_0000u;

  /// <summary>
  /// Decodes one Reflect block payload.
  /// </summary>
  /// <param name="compressedBody">
  /// The compressed token stream — i.e. the bytes that follow the 9-byte
  /// preamble for one block, excluding the preamble itself. Length must
  /// equal <c>preamble.compressed_len - 9</c>.
  /// </param>
  /// <param name="uncompressedLength">
  /// The declared uncompressed payload length read from the preamble.
  /// The decoder writes exactly this many bytes into the returned array.
  /// </param>
  /// <returns>The decoded payload as a fresh <see cref="byte"/> array.</returns>
  /// <exception cref="ArgumentOutOfRangeException">
  /// Thrown when <paramref name="uncompressedLength"/> is negative or
  /// exceeds <see cref="MaxUncompressedSize"/>.
  /// </exception>
  /// <exception cref="InvalidDataException">
  /// Thrown when the token stream is malformed — truncated, references
  /// data before the start of the block, or produces more bytes than
  /// declared.
  /// </exception>
  public static byte[] DecodeBlock(ReadOnlySpan<byte> compressedBody, int uncompressedLength) {
    if (uncompressedLength < 0) throw new ArgumentOutOfRangeException(nameof(uncompressedLength));
    if (uncompressedLength > MaxUncompressedSize)
      throw new ArgumentOutOfRangeException(nameof(uncompressedLength), "exceeds MaxUncompressedSize");

    var output = new byte[uncompressedLength];
    var produced = DecodeBlockInto(compressedBody, output);
    if (produced != uncompressedLength)
      throw new InvalidDataException(
        $"Macrium pre-X block produced {produced} bytes; expected {uncompressedLength}.");
    return output;
  }

  /// <summary>
  /// Decodes one Reflect block payload into a caller-supplied buffer.
  /// <para>
  /// Bookkeeping note: each back-reference / RLE token writes
  /// <c>encoded_count + 1</c> bytes but only <b>advances</b> the output
  /// cursor by <c>encoded_count</c>. The trailing byte serves as a scratch
  /// slot that the next token's first byte overwrites — unless this is the
  /// final token of the block, in which case the trailing byte is the
  /// final byte of the decoded payload. Literals always advance by 1
  /// (writing one byte). The method therefore tracks
  /// <c>bytesProduced = max(bytesProduced, outputOffset + writeCount)</c>
  /// to report how many bytes are valid in the destination buffer.
  /// </para>
  /// </summary>
  /// <param name="compressedBody">The post-preamble compressed body.</param>
  /// <param name="output">Destination buffer; must be at least <c>uncompressed_len</c> bytes.</param>
  /// <returns>The number of bytes written into <paramref name="output"/>.</returns>
  public static int DecodeBlockInto(ReadOnlySpan<byte> compressedBody, Span<byte> output) {
    var inputOffset = 0;
    var outputOffset = 0;
    var bytesProduced = 0;
    var controlFlags = ControlWordReloadSentinel;

    while (inputOffset < compressedBody.Length && outputOffset < output.Length) {
      if (controlFlags == ControlWordReloadSentinel) {
        if (inputOffset + 4 > compressedBody.Length) break;
        controlFlags = ReadUInt32LittleEndian(compressedBody, inputOffset) | ControlWordTopSentinel;
        inputOffset += 4;
        continue;
      }

      var isLiteral = (controlFlags & 1u) == 0;
      if (isLiteral) {
        if (inputOffset >= compressedBody.Length)
          throw new InvalidDataException("Macrium pre-X: truncated literal byte.");
        if (outputOffset >= output.Length)
          throw new InvalidDataException("Macrium pre-X: output overflow on literal.");
        output[outputOffset++] = compressedBody[inputOffset++];
        if (outputOffset > bytesProduced) bytesProduced = outputOffset;
      } else {
        var (consumedIn, emittedOut, writtenOut) = DecodeOperation(compressedBody, inputOffset, output, outputOffset);
        inputOffset += consumedIn;
        outputOffset += emittedOut;
        var producedAfter = outputOffset - emittedOut + writtenOut;
        if (producedAfter > bytesProduced) bytesProduced = producedAfter;
      }

      controlFlags >>= 1;
    }

    return bytesProduced;
  }

  /// <summary>
  /// Decodes one operation token (back-reference or RLE run) starting at
  /// <paramref name="inputOffset"/> in the compressed body, writing into
  /// <paramref name="output"/> at <paramref name="outputOffset"/>.
  /// Returns the number of input bytes consumed, the number of output
  /// positions <b>advanced</b> (encoded count), and the number of bytes
  /// actually <b>written</b> to the buffer (encoded count + 1). See the
  /// algorithm note in <see cref="DecodeBlockInto"/> for why these differ.
  /// </summary>
  private static (int ConsumedIn, int EmittedOut, int WrittenOut) DecodeOperation(
    ReadOnlySpan<byte> compressedBody, int inputOffset,
    Span<byte> output, int outputOffset) {
    // Peek the next 4 bytes as an LE uint32 (zero-padding if the block tail
    // doesn't contain a full DWORD — the operation may only use the low
    // 1..3 bytes).
    var word = PeekUInt32LittleEndian(compressedBody, inputOffset);
    var op = (int)(word & 0x0F);

    int consumedIn;
    int emittedOut;
    int writtenOut;

    if (op == 0x0F) {
      // RLE: bits[4..16) = 12-bit run_len-1 hint, bits[16..24) = byte to emit.
      // If run_len bits are zero, the actual 32-bit run_len follows after the
      // first 3 bytes. The on-disk run-length is biased by +1 (Reflect
      // encodes a 1-byte run as "run_len=0").
      var runLenField = (int)ExtractBits(word, 4, 12);
      var fillByte = (byte)ExtractBits(word, 16, 8);
      consumedIn = 3;
      var runLen = runLenField;
      if (runLen == 0) {
        runLen = (int)ReadUInt32LittleEndian(compressedBody, inputOffset + 3);
        consumedIn += 4;
      }
      EmitRun(output, outputOffset, fillByte, runLen + 1);
      emittedOut = runLen;
      writtenOut = runLen + 1;
    } else if (op == 0x07) {
      // Long back-reference. The fixed offset (3) is added to the encoded
      // segment_len so we can express the minimum 3-byte copy as 0.
      // bits[4..15) = 11-bit segment_len delta; bits[15..32) = 17-bit
      // rel_offset. If both are zero, two extended DWORDs follow that carry
      // the full 32-bit values.
      var segmentLen = 3 + (int)ExtractBits(word, 4, 11);
      var relOffset = (int)ExtractBits(word, 15, 17);
      consumedIn = 4;
      if (segmentLen - 3 == 0 && relOffset == 0) {
        segmentLen = (int)ReadUInt32LittleEndian(compressedBody, inputOffset + 4);
        relOffset = (int)ReadUInt32LittleEndian(compressedBody, inputOffset + 8);
        consumedIn += 8;
      }
      CopyBackReference(output, outputOffset, relOffset, segmentLen + 1);
      emittedOut = segmentLen;
      writtenOut = segmentLen + 1;
    } else if ((op & 0x07) == 0x03) {
      // Medium back-reference. bits[3..8) = 5-bit segment_len delta;
      // bits[8..24) = 16-bit rel_offset.
      var segmentLen = 3 + (int)ExtractBits(word, 3, 5);
      var relOffset = (int)ExtractBits(word, 8, 16);
      consumedIn = 3;
      CopyBackReference(output, outputOffset, relOffset, segmentLen + 1);
      emittedOut = segmentLen;
      writtenOut = segmentLen + 1;
    } else if ((op & 0x03) == 0x02) {
      // Short back-reference. bits[2..6) = 4-bit segment_len delta;
      // bits[6..16) = 10-bit rel_offset.
      var segmentLen = 3 + (int)ExtractBits(word, 2, 4);
      var relOffset = (int)ExtractBits(word, 6, 10);
      consumedIn = 2;
      CopyBackReference(output, outputOffset, relOffset, segmentLen + 1);
      emittedOut = segmentLen;
      writtenOut = segmentLen + 1;
    } else if ((op & 0x03) == 0x01) {
      // Fixed-length short back-reference (always 3 bytes).
      // bits[2..16) = 14-bit rel_offset.
      const int segmentLen = 3;
      var relOffset = (int)ExtractBits(word, 2, 14);
      consumedIn = 2;
      CopyBackReference(output, outputOffset, relOffset, segmentLen + 1);
      emittedOut = segmentLen;
      writtenOut = segmentLen + 1;
    } else { // (op & 0x03) == 0x00
      // Fixed-length tiny back-reference (always 3 bytes).
      // bits[2..8) = 6-bit rel_offset.
      const int segmentLen = 3;
      var relOffset = (int)ExtractBits(word, 2, 6);
      consumedIn = 1;
      CopyBackReference(output, outputOffset, relOffset, segmentLen + 1);
      emittedOut = segmentLen;
      writtenOut = segmentLen + 1;
    }

    return (consumedIn, emittedOut, writtenOut);
  }

  /// <summary>
  /// Emits a run of <paramref name="byteToEmit"/> for <paramref name="count"/>
  /// bytes into the output buffer starting at <paramref name="outputOffset"/>.
  /// </summary>
  private static void EmitRun(Span<byte> output, int outputOffset, byte byteToEmit, int count) {
    if (count <= 0) return;
    if (outputOffset + count > output.Length)
      throw new InvalidDataException(
        $"Macrium pre-X: RLE run of {count} bytes overflows output buffer at offset {outputOffset}.");
    output.Slice(outputOffset, count).Fill(byteToEmit);
  }

  /// <summary>
  /// Copies <paramref name="count"/> bytes from the back-reference window
  /// (output position - rel_offset) to the current output position. Done
  /// byte-at-a-time so that overlapping LZ77 matches (rel_offset &lt; count)
  /// behave like a forward run.
  /// </summary>
  private static void CopyBackReference(Span<byte> output, int outputOffset, int relOffset, int count) {
    if (relOffset <= 0)
      throw new InvalidDataException(
        $"Macrium pre-X: back-reference with rel_offset {relOffset} at output offset {outputOffset}.");
    if (relOffset > outputOffset)
      throw new InvalidDataException(
        $"Macrium pre-X: back-reference points before block start (rel_offset {relOffset} > output offset {outputOffset}).");
    if (outputOffset + count > output.Length)
      throw new InvalidDataException(
        $"Macrium pre-X: back-reference copy of {count} bytes overflows output buffer at offset {outputOffset}.");
    var sourceStart = outputOffset - relOffset;
    for (var i = 0; i < count; i++)
      output[outputOffset + i] = output[sourceStart + i];
  }

  /// <summary>
  /// Reads a 32-bit little-endian value from <paramref name="span"/> at
  /// <paramref name="offset"/>. Throws when fewer than 4 bytes remain.
  /// </summary>
  private static uint ReadUInt32LittleEndian(ReadOnlySpan<byte> span, int offset) {
    if (offset + 4 > span.Length)
      throw new InvalidDataException(
        $"Macrium pre-X: truncated DWORD at input offset {offset} (need 4 bytes, have {span.Length - offset}).");
    return BinaryPrimitives.ReadUInt32LittleEndian(span[offset..]);
  }

  /// <summary>
  /// Reads up to 4 bytes from <paramref name="span"/> at <paramref name="offset"/>
  /// as a little-endian uint32, zero-padding missing high bytes. Used when
  /// the operation token consumes only 1..3 bytes but is dispatched off the
  /// low nibble of a peek into a (possibly truncated) DWORD.
  /// </summary>
  private static uint PeekUInt32LittleEndian(ReadOnlySpan<byte> span, int offset) {
    if (offset >= span.Length)
      throw new InvalidDataException(
        $"Macrium pre-X: operation peek past end of input at offset {offset}.");
    uint result = 0;
    var available = Math.Min(4, span.Length - offset);
    for (var i = 0; i < available; i++)
      result |= (uint)span[offset + i] << (i * 8);
    return result;
  }

  /// <summary>
  /// Extracts <paramref name="count"/> bits from <paramref name="value"/>
  /// starting at bit <paramref name="firstBit"/>.
  /// </summary>
  private static uint ExtractBits(uint value, int firstBit, int count) {
    if (count <= 0 || count > 32) return 0;
    if (firstBit < 0 || firstBit >= 32) return 0;
    var maskWidth = firstBit + count;
    var mask = maskWidth >= 32 ? 0xFFFF_FFFFu : ((1u << maskWidth) - 1u);
    return (value & mask) >> firstBit;
  }
}
