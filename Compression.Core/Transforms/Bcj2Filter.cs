using Compression.Core.Entropy.RangeCoding;

namespace Compression.Core.Transforms;

/// <summary>
/// The 7z BCJ2 coder (method <c>0303011B</c>): x86 code split into four streams — the main stream with
/// every byte that is not a converted branch operand, the big-endian absolute targets of the
/// converted CALLs (E8) and of the converted JMPs and conditional jumps (E9, 0F 8x), and a range
/// coded bit per branch candidate saying whether it was converted.
/// </summary>
/// <remarks>
/// Converted from the LZMA SDK by Igor Pavlov (public domain): the decoder from C/Bcj2.c
/// (<c>Bcj2_Decode</c>, SDK 9.20), the encoder from CPP/7zip/Compress/Bcj2Coder.cpp
/// (<c>CEncoder::CodeReal</c>, 9.20). The probability contexts are the SDK's: one per preceding
/// byte for E8, one for E9, one for the conditional jumps; the range coder is the LZMA one
/// (11-bit probabilities, 5 move bits). 7-Zip's later BCJ2 coder (15.00 onwards) reads and writes
/// the same streams. See THIRD-PARTY-NOTICE.lzma-sdk.txt.
/// </remarks>
public static class Bcj2Filter {
  /// <summary>The probability contexts: 256 for E8 by preceding byte, then E9, then Jcc.</summary>
  private const int NumContexts = 256 + 2;

  private static bool IsJcc(byte previous, byte current) => previous == 0x0F && (current & 0xF0) == 0x80;

  private static bool IsJ(byte previous, byte current) => (current & 0xFE) == 0xE8 || IsJcc(previous, current);

  private static int ContextOf(byte previous, byte current) => current == 0xE8 ? previous : current == 0xE9 ? 256 : 257;

  /// <summary>Decodes BCJ2 by pulling from its four input streams; exactly the declared number of bytes come out.</summary>
  public sealed class DecodeStream : Stream {
    private readonly Stream _main;
    private readonly Stream _call;
    private readonly Stream _jump;
    private readonly RangeDecoder _rc;
    private readonly long _length;
    private readonly int[] _probs = new int[NumContexts];
    private readonly byte[] _mainBuffer = new byte[1 << 16];
    private int _mainPos;
    private int _mainEnd;
    private long _outPos;
    private byte _prevByte;
    private uint _pending;
    private int _pendingCount;

    /// <summary>Opens the decoder over its four inputs.</summary>
    public DecodeStream(Stream main, Stream call, Stream jump, Stream rangeCoded, long length) {
      this._main = main;
      this._call = new BufferedStream(call, 1 << 12);
      this._jump = new BufferedStream(jump, 1 << 12);
      this._rc = new RangeDecoder(new BufferedStream(rangeCoded, 1 << 12));
      this._length = length;
      this._probs.AsSpan().Fill(RangeEncoder.ProbInitValue);
    }

    /// <inheritdoc />
    public override bool CanRead => true;
    /// <inheritdoc />
    public override bool CanSeek => false;
    /// <inheritdoc />
    public override bool CanWrite => false;
    /// <inheritdoc />
    public override long Length => this._length;
    /// <inheritdoc />
    public override long Position { get => this._outPos; set => throw new NotSupportedException(); }

    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count) => this.Read(buffer.AsSpan(offset, count));

    /// <inheritdoc />
    public override int Read(Span<byte> destination) {
      var written = 0;
      while (written < destination.Length && this._outPos < this._length) {
        if (this._pendingCount > 0) {
          destination[written++] = (byte)this._pending;
          this._pending >>= 8;
          --this._pendingCount;
          ++this._outPos;
          continue;
        }

        var b = this.NextMainByte();
        destination[written++] = b;
        ++this._outPos;
        if (!IsJ(this._prevByte, b)) {
          this._prevByte = b;
          continue;
        }
        // A candidate as the very last byte is never followed by its bit.
        if (this._outPos == this._length)
          break;

        if (this._rc.DecodeBit(ref this._probs[ContextOf(this._prevByte, b)]) == 0) {
          this._prevByte = b;
          continue;
        }

        var target = ReadBigEndian(b == 0xE8 ? this._call : this._jump);
        var relative = target - (uint)(this._outPos + 4);
        this._pending = relative;
        this._pendingCount = 4;
        this._prevByte = (byte)(relative >> 24);
      }
      return written;
    }

    private byte NextMainByte() {
      if (this._mainPos == this._mainEnd) {
        this._mainEnd = this._main.Read(this._mainBuffer, 0, this._mainBuffer.Length);
        this._mainPos = 0;
        if (this._mainEnd <= 0)
          throw new InvalidDataException("BCJ2: the main stream ended before the declared size.");
      }
      return this._mainBuffer[this._mainPos++];
    }

    private static uint ReadBigEndian(Stream stream) {
      var value = 0u;
      for (var i = 0; i < 4; ++i) {
        var b = stream.ReadByte();
        if (b < 0)
          throw new InvalidDataException("BCJ2: a call/jump stream ended early.");
        value = (value << 8) | (uint)b;
      }
      return value;
    }

    /// <inheritdoc />
    public override void Flush() { }
    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    /// <inheritdoc />
    public override void SetLength(long value) => throw new NotSupportedException();
    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
  }

  /// <summary>Decodes BCJ2 from its four streams held in memory.</summary>
  public static byte[] Decode(ReadOnlySpan<byte> mainStream, ReadOnlySpan<byte> callStream,
      ReadOnlySpan<byte> jumpStream, ReadOnlySpan<byte> rangeStream, int outputSize) {
    using var decoder = new DecodeStream(new MemoryStream(mainStream.ToArray()), new MemoryStream(callStream.ToArray()),
      new MemoryStream(jumpStream.ToArray()), new MemoryStream(rangeStream.ToArray()), outputSize);
    var output = new byte[outputSize];
    var done = 0;
    while (done < outputSize) {
      var n = decoder.Read(output, done, outputSize - done);
      if (n <= 0)
        throw new InvalidDataException("BCJ2: the streams end before the declared size.");
      done += n;
    }
    return output;
  }

  /// <summary>
  /// Splits <paramref name="data"/> into the four BCJ2 streams. A branch is converted when its
  /// operand's top byte is 0x00 or 0xFF, the SDK encoder's rule without sub-stream bounds; any
  /// choice decodes, this one keeps the target streams to genuine near branches.
  /// </summary>
  public static (byte[] Main, byte[] Call, byte[] Jump, byte[] RangeCoded) Encode(ReadOnlySpan<byte> data) {
    using var main = new MemoryStream(data.Length);
    using var call = new MemoryStream();
    using var jump = new MemoryStream();
    using var rangeCoded = new MemoryStream();
    var rc = new RangeEncoder(rangeCoded);
    var probs = new int[NumContexts];
    probs.AsSpan().Fill(RangeEncoder.ProbInitValue);

    byte prevByte = 0;
    var pos = 0;
    var limit = data.Length - 5;
    while (pos <= limit) {
      var b = data[pos];
      main.WriteByte(b);
      if (!IsJ(prevByte, b)) {
        prevByte = b;
        ++pos;
        continue;
      }

      var context = ContextOf(prevByte, b);
      var next = data[pos + 4];
      if (next is 0x00 or 0xFF) {
        rc.EncodeBit(ref probs[context], 1);
        var src = (uint)data[pos + 1] | (uint)data[pos + 2] << 8 | (uint)data[pos + 3] << 16 | (uint)next << 24;
        var dest = (uint)(pos + 5) + src;
        var target = b == 0xE8 ? call : jump;
        target.WriteByte((byte)(dest >> 24));
        target.WriteByte((byte)(dest >> 16));
        target.WriteByte((byte)(dest >> 8));
        target.WriteByte((byte)dest);
        prevByte = next;
        pos += 5;
      } else {
        rc.EncodeBit(ref probs[context], 0);
        prevByte = b;
        ++pos;
      }
    }

    // The last four bytes cannot hold a whole operand: candidates there are written unconverted.
    for (; pos < data.Length; ++pos) {
      var b = data[pos];
      main.WriteByte(b);
      if (IsJ(prevByte, b))
        rc.EncodeBit(ref probs[ContextOf(prevByte, b)], 0);
      prevByte = b;
    }

    rc.Finish();
    return (main.ToArray(), call.ToArray(), jump.ToArray(), rangeCoded.ToArray());
  }

  /// <summary>
  /// The BCJ2 encoder as a pull stream: reading it yields the main stream while the call, jump and
  /// range-coded streams are written to the streams given, so input of any length is split with
  /// bounded memory. For the same input the four streams equal <see cref="Encode"/>'s.
  /// </summary>
  public sealed class EncodeStream : Stream {
    private const int ChunkSize = 1 << 16;
    private readonly Stream _input;
    private readonly Stream _call;
    private readonly Stream _jump;
    private readonly RangeEncoder _rc;
    private readonly int[] _probs = new int[NumContexts];
    private readonly byte[] _buffer = new byte[ChunkSize + 8];
    private readonly MemoryStream _main = new();
    private int _filled;
    private long _bufferStart;
    private bool _inputEnded;
    private bool _finished;
    private byte _prevByte;
    private long _mainRead;

    /// <summary>Starts splitting <paramref name="input"/>; the side streams are written as the main stream is read.</summary>
    public EncodeStream(Stream input, Stream call, Stream jump, Stream rangeCoded) {
      this._input = input ?? throw new ArgumentNullException(nameof(input));
      this._call = call ?? throw new ArgumentNullException(nameof(call));
      this._jump = jump ?? throw new ArgumentNullException(nameof(jump));
      this._rc = new RangeEncoder(rangeCoded ?? throw new ArgumentNullException(nameof(rangeCoded)));
      this._probs.AsSpan().Fill(RangeEncoder.ProbInitValue);
    }

    /// <inheritdoc />
    public override bool CanRead => true;
    /// <inheritdoc />
    public override bool CanSeek => false;
    /// <inheritdoc />
    public override bool CanWrite => false;
    /// <inheritdoc />
    public override long Length => throw new NotSupportedException();
    /// <inheritdoc />
    public override long Position { get => this._mainRead; set => throw new NotSupportedException(); }

    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count) => this.Read(buffer.AsSpan(offset, count));

    /// <inheritdoc />
    public override int Read(Span<byte> destination) {
      while (this._main.Length - this._main.Position == 0 && !this._finished)
        this.Pump();
      var n = this._main.Read(destination);
      this._mainRead += n;
      return n;
    }

    /// <summary>Reads the next chunk of input and splits every position that can be decided.</summary>
    private void Pump() {
      this._main.SetLength(0);
      if (!this._inputEnded) {
        var read = this._input.Read(this._buffer, this._filled, ChunkSize + 8 - this._filled);
        if (read <= 0)
          this._inputEnded = true;
        else
          this._filled += read;
      }

      var data = this._buffer;
      var i = 0;
      while (i < this._filled) {
        var b = data[i];
        if (i + 5 > this._filled && !this._inputEnded)
          break; // its operand may still arrive

        this._main.WriteByte(b);
        if (!IsJ(this._prevByte, b)) {
          this._prevByte = b;
          ++i;
          continue;
        }

        var context = ContextOf(this._prevByte, b);
        if (i + 5 > this._filled) {
          // The last four bytes of the input cannot hold a whole operand.
          this._rc.EncodeBit(ref this._probs[context], 0);
          this._prevByte = b;
          ++i;
          continue;
        }

        var next = data[i + 4];
        if (next is 0x00 or 0xFF) {
          this._rc.EncodeBit(ref this._probs[context], 1);
          var src = (uint)data[i + 1] | (uint)data[i + 2] << 8 | (uint)data[i + 3] << 16 | (uint)next << 24;
          var dest = (uint)(this._bufferStart + i + 5) + src;
          var target = b == 0xE8 ? this._call : this._jump;
          target.WriteByte((byte)(dest >> 24));
          target.WriteByte((byte)(dest >> 16));
          target.WriteByte((byte)(dest >> 8));
          target.WriteByte((byte)dest);
          this._prevByte = next;
          i += 5;
        } else {
          this._rc.EncodeBit(ref this._probs[context], 0);
          this._prevByte = b;
          ++i;
        }
      }

      Buffer.BlockCopy(data, i, data, 0, this._filled - i);
      this._filled -= i;
      this._bufferStart += i;
      if (this._inputEnded && this._filled == 0 && !this._finished) {
        this._rc.Finish();
        this._finished = true;
      }
      this._main.Position = 0;
    }

    /// <inheritdoc />
    public override void Flush() { }
    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    /// <inheritdoc />
    public override void SetLength(long value) => throw new NotSupportedException();
    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
  }
}
