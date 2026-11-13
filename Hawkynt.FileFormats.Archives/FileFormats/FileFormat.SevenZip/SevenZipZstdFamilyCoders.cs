using System.Buffers.Binary;
using Compression.Core.Checksums;
using Compression.Core.Dictionary.Brotli;
using Compression.Core.Dictionary.Lz4;
using Compression.Core.Dictionary.Lz5;
using Compression.Core.Streams;
using FileFormat.Brotli;
using FileFormat.Lz4;
using FileFormat.Zstd;

namespace FileFormat.SevenZip;

/// <summary>
/// The coders 7-Zip-zstd registers under 0x04F711xx — Zstandard, Brotli, LZ4 and LZ5 — in both
/// directions, streamed: memory stays bounded by one chunk whatever the size of the block.
/// </summary>
/// <remarks>
/// <para>Framing and properties, as measured on archives 7-Zip-zstd 26.02 and py7zr 1.1.0 wrote
/// (Compression.Tests/SevenZip/ReferenceVectors):</para>
/// <list type="bullet">
///   <item><description>Zstandard: one or more standard zstd frames (RFC 8878), nothing around them.
///   Properties: 5 bytes — 1, 5 (the library version), the level, 0, 0.</description></item>
///   <item><description>Brotli: every independently compressed chunk of at most 3 MiB is prefixed by a
///   16-byte skippable frame — UInt32 0x184D2A50, UInt32 8, UInt32 compressed size, UInt16 "BR"
///   (0x5242), UInt16 size hint (the chunk's size in 64 KiB units plus one, at most 48) — followed by
///   that many bytes of a plain RFC 7932 stream; 7-Zip-zstd writes this framing single- and
///   multi-threaded alike. py7zr writes one plain stream with no prefix. Properties: 3 bytes — 1, 0,
///   the level.</description></item>
///   <item><description>LZ4: 7-Zip-zstd's multi-threaded mode prefixes each chunk of at most 4 MiB
///   with a 12-byte skippable frame — UInt32 0x184D2A50, UInt32 4, UInt32 compressed size — followed by
///   standard LZ4 frames; single-threaded mode writes the standard LZ4 frames bare. Properties: 5
///   bytes — 1, 10 (the library version), the level, 0, 0.</description></item>
///   <item><description>LZ5 (1.5): every chunk of at most 4 MiB behind the same 12-byte skippable
///   frame, single- and multi-threaded alike, holding one LZ5 frame. Properties: 5 bytes — 1, 5 (the
///   library version), the level (1-15), 0, 0.</description></item>
/// </list>
/// <para>The writer produces 7-Zip-zstd's own layouts: Zstandard frames of at most
/// <see cref="ZstdFrameSize"/> bytes, the Brotli chunk framing, and the multi-threaded LZ4 chunk
/// framing (one LZ4 frame per chunk), and the same framing for LZ5.</para>
/// </remarks>
internal static class SevenZipZstdFamilyCoders {
  /// <summary>The level 7-Zip-zstd writes when none is given.</summary>
  public const int DefaultLevel = 3;

  /// <summary>Input per Zstandard frame: bounds the encoder's buffer and match finder.</summary>
  public const int ZstdFrameSize = 8 << 20;

  /// <summary>Input per Brotli chunk, as 7-Zip-zstd cuts it (size hint 48 = 48 × 64 KiB).</summary>
  public const int BrotliChunkSize = 3 << 20;

  /// <summary>Input per LZ4 chunk, as 7-Zip-zstd's multi-threaded mode cuts it.</summary>
  public const int Lz4ChunkSize = 4 << 20;

  private const int BrotliHeaderSize = 16;
  private const int Lz4HeaderSize = 12;

  /// <summary>The coder properties 7-Zip-zstd writes for <paramref name="codec"/> at <paramref name="level"/>.</summary>
  public static byte[] Properties(SevenZipCodec codec, int level) => codec switch {
    SevenZipCodec.Zstd => [1, 5, (byte)ClampLevel(codec, level), 0, 0],
    SevenZipCodec.Brotli => [1, 0, (byte)ClampLevel(codec, level)],
    SevenZipCodec.Lz4 => [1, 10, (byte)ClampLevel(codec, level), 0, 0],
    SevenZipCodec.Lz5 => [1, 5, (byte)ClampLevel(codec, level), 0, 0],
    _ => throw new ArgumentOutOfRangeException(nameof(codec)),
  };

  /// <summary>The coder id of <paramref name="codec"/>.</summary>
  public static byte[] CodecId(SevenZipCodec codec) => codec switch {
    SevenZipCodec.Zstd => SevenZipConstants.CodecZstd.ToArray(),
    SevenZipCodec.Brotli => SevenZipConstants.CodecBrotli.ToArray(),
    SevenZipCodec.Lz4 => SevenZipConstants.CodecLz4.ToArray(),
    SevenZipCodec.Lz5 => SevenZipConstants.CodecLz5.ToArray(),
    _ => throw new ArgumentOutOfRangeException(nameof(codec)),
  };

  /// <summary>7-Zip-zstd's level range per method: Zstandard 1-22, Brotli 0-11, LZ4 1-12, LZ5 1-15.</summary>
  public static int ClampLevel(SevenZipCodec codec, int level) => codec switch {
    SevenZipCodec.Brotli => Math.Clamp(level, 0, 11),
    SevenZipCodec.Lz4 => Math.Clamp(level, 1, 12),
    SevenZipCodec.Lz5 => Math.Clamp(level, 1, 15),
    _ => Math.Clamp(level, 1, 22),
  };

  /// <summary>Encodes all of <paramref name="input"/> with <paramref name="codec"/> into <paramref name="output"/>.</summary>
  public static void Encode(SevenZipCodec codec, Stream input, Stream output, int level) {
    level = ClampLevel(codec, level);
    switch (codec) {
      case SevenZipCodec.Zstd:
        EncodeChunks(input, ZstdFrameSize, chunk => {
          using var zstd = new ZstdStream(output, CompressionStreamMode.Compress, Math.Min(level, 9), leaveOpen: true);
          zstd.Write(chunk);
        });
        break;
      case SevenZipCodec.Brotli: {
        var tier = level switch { <= 2 => BrotliCompressionLevel.Fast, <= 9 => BrotliCompressionLevel.Default, _ => BrotliCompressionLevel.Best };
        var header = new byte[BrotliHeaderSize];
        EncodeChunks(input, BrotliChunkSize, chunk => {
          var packed = BrotliStream.Compress(chunk, tier);
          BinaryPrimitives.WriteUInt32LittleEndian(header, SevenZipConstants.MtSkippableFrameMagic);
          BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), 8);
          BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8), (uint)packed.Length);
          BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(12), SevenZipConstants.BrotliMtMarker);
          BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(14), (ushort)Math.Min((chunk.Length >> 16) + 1, BrotliChunkSize >> 16));
          output.Write(header);
          output.Write(packed);
        });
        break;
      }
      case SevenZipCodec.Lz4: {
        var tier = level switch { < 3 => Lz4CompressionLevel.Fast, < 9 => Lz4CompressionLevel.Hc, _ => Lz4CompressionLevel.Max };
        EncodeFramedChunks(input, output, (chunk, frame) => new Lz4FrameWriter(frame, Lz4Constants.MaxBlockSize, level: tier).Write(chunk));
        break;
      }
      case SevenZipCodec.Lz5:
        EncodeFramedChunks(input, output, Lz5Frame.Write);
        break;
      default:
        throw new ArgumentOutOfRangeException(nameof(codec));
    }
  }

  private delegate void ChunkAction(ReadOnlySpan<byte> chunk);

  private delegate void FrameWriter(ReadOnlySpan<byte> chunk, Stream frame);

  /// <summary>
  /// 7-Zip-zstd's 12-byte chunk framing: every chunk of at most <see cref="Lz4ChunkSize"/> bytes
  /// encoded by <paramref name="write"/> behind a skippable frame (magic, 4, packed size).
  /// </summary>
  private static void EncodeFramedChunks(Stream input, Stream output, FrameWriter write) {
    using var frame = new MemoryStream();
    var header = new byte[Lz4HeaderSize];
    EncodeChunks(input, Lz4ChunkSize, chunk => {
      frame.SetLength(0);
      write(chunk, frame);
      BinaryPrimitives.WriteUInt32LittleEndian(header, SevenZipConstants.MtSkippableFrameMagic);
      BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), 4);
      BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8), (uint)frame.Length);
      output.Write(header);
      output.Write(frame.GetBuffer(), 0, (int)frame.Length);
    });
  }

  /// <summary>Hands <paramref name="input"/> to <paramref name="action"/> in full chunks of <paramref name="size"/> bytes and one final shorter one.</summary>
  private static void EncodeChunks(Stream input, int size, ChunkAction action) {
    var buffer = new byte[size];
    while (true) {
      var n = input.ReadAtLeast(buffer, size, throwOnEndOfStream: false);
      if (n > 0)
        action(buffer.AsSpan(0, n));
      if (n < size)
        return;
    }
  }

  /// <summary>A stream of the Zstandard coder's output over its packed stream.</summary>
  public static Stream OpenZstd(Stream packed) {
    // An empty packed stream holds no frame (nothing to decode); the frame decoder wants one.
    var first = packed.ReadByte();
    return first < 0
      ? Stream.Null
      : new ZstdStream(new PrefixedStream((byte)first, packed), CompressionStreamMode.Decompress, leaveOpen: true);
  }

  /// <summary>One already-read byte in front of the rest of a stream.</summary>
  private sealed class PrefixedStream(byte first, Stream rest) : Stream {
    private bool _served;

    public override int Read(byte[] buffer, int offset, int count) => this.Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer) {
      if (buffer.IsEmpty)
        return 0;
      if (this._served)
        return rest.Read(buffer);
      this._served = true;
      buffer[0] = first;
      return 1;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
  }

  /// <summary>A stream of the Brotli coder's output over its packed stream, one chunk in memory at a time.</summary>
  public static Stream OpenBrotli(Stream packed) => new BrotliChunkReadStream(packed);

  /// <summary>A stream of the LZ4 coder's output over its packed stream, one block in memory at a time.</summary>
  public static Stream OpenLz4(Stream packed) => new Lz4FramesReadStream(packed);

  /// <summary>A stream of the LZ5 coder's output over its packed stream, one chunk in memory at a time.</summary>
  public static Stream OpenLz5(Stream packed) => new FramedChunkReadStream(packed, "LZ5", Lz5Frame.Decode);

  public static byte[] DecodeZstd(byte[] data) => ReadAll(OpenZstd(new MemoryStream(data, writable: false)));

  public static byte[] DecodeBrotli(byte[] data) => ReadAll(OpenBrotli(new MemoryStream(data, writable: false)));

  public static byte[] DecodeLz4(byte[] data) => ReadAll(OpenLz4(new MemoryStream(data, writable: false)));

  public static byte[] DecodeLz5(byte[] data) => ReadAll(OpenLz5(new MemoryStream(data, writable: false)));

  private static byte[] ReadAll(Stream stream) {
    using (stream) {
      using var output = new MemoryStream();
      stream.CopyTo(output);
      return output.ToArray();
    }
  }

  /// <summary>Reads exactly <paramref name="buffer"/>.Length bytes, or none at the end of the stream.</summary>
  private static bool TryReadExactly(Stream input, Span<byte> buffer, string what) {
    var n = input.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
    if (n == 0)
      return false;
    if (n < buffer.Length)
      throw new InvalidDataException($"7z {what}: the packed stream is truncated.");
    return true;
  }

  /// <summary>Common shape of the two chunk readers: refill a buffer, serve it, repeat.</summary>
  private abstract class ChunkReadStream : Stream {
    private byte[] _chunk = [];
    private int _position;
    private int _length;
    private bool _done;

    /// <summary>Produces the next run of output, or <see langword="null"/> at the end.</summary>
    protected abstract ArraySegment<byte>? NextChunk();

    public override int Read(byte[] buffer, int offset, int count) => this.Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer) {
      while (this._position == this._length) {
        if (this._done || buffer.IsEmpty)
          return 0;
        if (this.NextChunk() is not { } next) {
          this._done = true;
          return 0;
        }
        this._chunk = next.Array!;
        this._position = next.Offset;
        this._length = next.Offset + next.Count;
      }
      var n = Math.Min(buffer.Length, this._length - this._position);
      this._chunk.AsSpan(this._position, n).CopyTo(buffer);
      this._position += n;
      return n;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
  }

  private delegate void FrameDecoder(ReadOnlySpan<byte> frames, Stream output);

  /// <summary>
  /// Chunks behind 7-Zip-zstd's 12-byte skippable-frame headers, each decoded whole by
  /// <paramref name="decode"/>; frames without such headers are decoded together.
  /// </summary>
  private sealed class FramedChunkReadStream(Stream input, string name, FrameDecoder decode) : ChunkReadStream {
    private readonly byte[] _header = new byte[Lz4HeaderSize];
    private readonly MemoryStream _output = new();

    protected override ArraySegment<byte>? NextChunk() {
      while (true) {
        var n = input.ReadAtLeast(this._header, Lz4HeaderSize, throwOnEndOfStream: false);
        if (n == 0)
          return null;
        byte[] packed;
        if (n == Lz4HeaderSize && IsSkippable(this._header, 4)) {
          var size = BinaryPrimitives.ReadUInt32LittleEndian(this._header.AsSpan(8));
          if (size > Array.MaxLength)
            throw new InvalidDataException($"7z {name}: chunk overruns the packed stream.");
          packed = new byte[size];
          if (input.ReadAtLeast(packed, packed.Length, throwOnEndOfStream: false) < packed.Length)
            throw new InvalidDataException($"7z {name}: chunk overruns the packed stream.");
        } else {
          using var all = new MemoryStream();
          all.Write(this._header, 0, n);
          input.CopyTo(all);
          packed = all.ToArray();
        }
        this._output.SetLength(0);
        decode(packed, this._output);
        if (this._output.Length > 0)
          return new ArraySegment<byte>(this._output.GetBuffer(), 0, (int)this._output.Length);
      }
    }
  }

  /// <summary>
  /// Brotli: 7-Zip-zstd's skippable-framed chunks, decoded one at a time; a stream that does not
  /// open with such a header is one plain Brotli stream (py7zr) and is decoded whole.
  /// </summary>
  private sealed class BrotliChunkReadStream(Stream input) : ChunkReadStream {
    private bool _started;
    private bool _plain;
    private readonly byte[] _header = new byte[BrotliHeaderSize];
    private long _offset;

    protected override ArraySegment<byte>? NextChunk() {
      if (this._plain)
        return null;
      while (true) {
        var n = input.ReadAtLeast(this._header, BrotliHeaderSize, throwOnEndOfStream: false);
        if (n == 0)
          return null;
        var framed = n >= 8 && IsSkippable(this._header, 8);
        if (!this._started && !framed) {
          // One plain stream: what py7zr writes. It has no chunk boundaries to stream along.
          this._plain = true;
          using var all = new MemoryStream();
          all.Write(this._header, 0, n);
          input.CopyTo(all);
          return BrotliStream.Decompress(all.GetBuffer().AsSpan(0, (int)all.Length));
        }
        this._started = true;
        if (!framed || n < BrotliHeaderSize)
          throw new InvalidDataException($"7z Brotli: expected a skippable-frame chunk header at offset {this._offset}.");
        if (BinaryPrimitives.ReadUInt16LittleEndian(this._header.AsSpan(12)) != SevenZipConstants.BrotliMtMarker)
          throw new InvalidDataException($"7z Brotli: chunk header at offset {this._offset} lacks the BR marker.");
        var size = BinaryPrimitives.ReadUInt32LittleEndian(this._header.AsSpan(8));
        if (size > Array.MaxLength)
          throw new InvalidDataException("7z Brotli: chunk overruns the packed stream.");
        var packed = new byte[size];
        if (input.ReadAtLeast(packed, packed.Length, throwOnEndOfStream: false) < packed.Length)
          throw new InvalidDataException("7z Brotli: chunk overruns the packed stream.");
        this._offset += BrotliHeaderSize + size;
        var chunk = BrotliStream.Decompress(packed);
        if (chunk.Length > 0)
          return chunk;
      }
    }

  }

  /// <summary>
  /// LZ4: a sequence of standard LZ4 frames, any of them preceded by skippable frames (7-Zip-zstd's
  /// chunk headers are skippable frames whose payload is the chunk's size), decoded one block at a
  /// time with the 64 KiB of history linked blocks may reach into.
  /// </summary>
  private sealed class Lz4FramesReadStream(Stream input) : ChunkReadStream {
    private const int History = 64 * 1024;
    private byte[] _window = [];
    private byte[] _block = [];
    private int _historyLength;
    private int _lastEnd;
    private bool _inFrame;
    private bool _independent;
    private bool _blockChecksum;
    private bool _contentChecksum;
    private int _blockMaxSize;
    private XxHash32? _hash;

    protected override ArraySegment<byte>? NextChunk() {
      Span<byte> word = stackalloc byte[4];
      while (true) {
        if (!this._inFrame) {
          if (!TryReadExactly(input, word, "LZ4"))
            return null;
          var magic = BinaryPrimitives.ReadUInt32LittleEndian(word);
          if ((magic & 0xFFFFFFF0u) == (SevenZipConstants.MtSkippableFrameMagic & 0xFFFFFFF0u)) {
            if (!TryReadExactly(input, word, "LZ4"))
              throw new InvalidDataException("7z LZ4: truncated chunk header.");
            var skip = BinaryPrimitives.ReadUInt32LittleEndian(word);
            var skipped = input.CopyToLimit(Stream.Null, skip);
            if (skipped < skip)
              throw new InvalidDataException("7z LZ4: truncated chunk header.");
            continue;
          }
          if (magic != Lz4Constants.FrameMagic)
            throw new InvalidDataException("7z LZ4: packed data is not a sequence of LZ4 frames.");
          this.BeginFrame();
          continue;
        }

        if (!TryReadExactly(input, word, "LZ4"))
          throw new InvalidDataException("7z LZ4: the frame ends before its end mark.");
        var blockHeader = BinaryPrimitives.ReadUInt32LittleEndian(word);
        if (blockHeader == 0) {
          if (this._contentChecksum) {
            if (!TryReadExactly(input, word, "LZ4"))
              throw new InvalidDataException("7z LZ4: the frame lacks its content checksum.");
            if (BinaryPrimitives.ReadUInt32LittleEndian(word) != this._hash!.Value)
              throw new InvalidDataException("7z LZ4: frame content checksum mismatch.");
          }
          this._inFrame = false;
          continue;
        }

        var stored = (blockHeader & 0x80000000u) != 0;
        var size = (int)(blockHeader & 0x7FFFFFFFu);
        if (size > this._blockMaxSize)
          throw new InvalidDataException("7z LZ4: a block exceeds the frame's maximum block size.");
        if (this._block.Length < size)
          this._block = new byte[this._blockMaxSize];
        if (input.ReadAtLeast(this._block.AsSpan(0, size), size, throwOnEndOfStream: false) < size)
          throw new InvalidDataException("7z LZ4: a block is truncated.");
        if (this._blockChecksum) {
          if (!TryReadExactly(input, word, "LZ4"))
            throw new InvalidDataException("7z LZ4: a block lacks its checksum.");
          if (BinaryPrimitives.ReadUInt32LittleEndian(word) != XxHash32.Compute(this._block.AsSpan(0, size)))
            throw new InvalidDataException("7z LZ4: block checksum mismatch.");
        }

        // Keep the last 64 KiB of the frame's output in front of the block for linked blocks.
        var keep = this._independent ? 0 : Math.Min(this._historyLength, History);
        if (keep > 0 && this._lastEnd - keep != 0)
          this._window.AsSpan(this._lastEnd - keep, keep).CopyTo(this._window);
        int written;
        if (stored) {
          this._block.AsSpan(0, size).CopyTo(this._window.AsSpan(keep));
          written = size;
        } else
          written = Lz4BlockDecompressor.DecompressAfterPrefix(this._block.AsSpan(0, size), this._window, keep);
        this._lastEnd = keep + written;
        this._historyLength = keep + written;
        this._hash?.Update(this._window.AsSpan(keep, written));
        if (written > 0)
          return new ArraySegment<byte>(this._window, keep, written);
      }
    }

    private void BeginFrame() {
      var flg = input.ReadByte();
      var bd = input.ReadByte();
      if (flg < 0 || bd < 0)
        throw new InvalidDataException("7z LZ4: truncated frame header.");
      if ((flg >> 6) != 1)
        throw new InvalidDataException("7z LZ4: unsupported frame version.");
      Span<byte> descriptor = stackalloc byte[(flg & 0x08) != 0 ? 10 : 2];
      descriptor[0] = (byte)flg;
      descriptor[1] = (byte)bd;
      if (descriptor.Length > 2 && input.ReadAtLeast(descriptor[2..], 8, throwOnEndOfStream: false) < 8)
        throw new InvalidDataException("7z LZ4: truncated frame header.");
      if ((flg & 0x01) != 0) {
        // A dictionary id: the 7z coder has no dictionary to offer.
        throw new InvalidDataException("7z LZ4: a frame needs an external dictionary.");
      }
      var hc = input.ReadByte();
      if (hc < 0 || (byte)hc != (byte)(XxHash32.Compute(descriptor) >> 8))
        throw new InvalidDataException("7z LZ4: frame header checksum mismatch.");

      this._independent = (flg & 0x20) != 0;
      this._blockChecksum = (flg & 0x10) != 0;
      this._contentChecksum = (flg & 0x04) != 0;
      this._blockMaxSize = ((bd >> 4) & 7) switch {
        4 => 64 * 1024,
        5 => 256 * 1024,
        6 => 1024 * 1024,
        7 => 4 * 1024 * 1024,
        _ => throw new InvalidDataException("7z LZ4: invalid maximum block size."),
      };
      if (this._window.Length < History + this._blockMaxSize)
        this._window = new byte[History + this._blockMaxSize];
      this._historyLength = 0;
      this._lastEnd = 0;
      this._hash = this._contentChecksum ? new XxHash32() : null;
      this._inFrame = true;
    }

  }

  private static bool IsSkippable(byte[] data, uint headerPayload)
    => BinaryPrimitives.ReadUInt32LittleEndian(data) == SevenZipConstants.MtSkippableFrameMagic
       && BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4)) == headerPayload;
}

/// <summary>Stream helpers for the chunk readers.</summary>
internal static class SevenZipStreamExtensions {
  /// <summary>Copies at most <paramref name="limit"/> bytes to <paramref name="destination"/>, returning how many were copied.</summary>
  public static long CopyToLimit(this Stream source, Stream destination, long limit) {
    var buffer = new byte[Math.Clamp(limit, 1, 81920)];
    var total = 0L;
    while (total < limit) {
      var n = source.Read(buffer, 0, (int)Math.Min(buffer.Length, limit - total));
      if (n <= 0)
        break;
      destination.Write(buffer, 0, n);
      total += n;
    }
    return total;
  }
}
