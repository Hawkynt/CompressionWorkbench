#pragma warning disable CS1591
using System.IO.Compression;

namespace FileFormat.EaseUs;

/// <summary>
/// Locates zlib streams inside an EaseUS Todo Backup <c>.pbd</c> file. Each
/// chunk is reported as a <see cref="ZlibChunk"/> with its byte offset, the
/// observed CMF/FLG header pair, the compressed byte count consumed, and the
/// inflated payload length. The walker is read-only and does NOT know how
/// chunks map back to logical disk sectors — that mapping lives in the
/// proprietary block-allocation table which is not surfaced by this scanner.
/// </summary>
internal static class PbdChunkScanner {

  /// <summary>One zlib stream found inside a PBD container.</summary>
  /// <param name="Offset">Absolute byte offset of the zlib CMF byte inside the .pbd.</param>
  /// <param name="Cmf">Compression method/flags byte (typically 0x78).</param>
  /// <param name="Flg">Flags/check byte (e.g. 0x01, 0x9C, 0xDA).</param>
  /// <param name="CompressedLength">Number of compressed bytes consumed (excluding any trailing Adler-32 already accounted for by the inflater).</param>
  /// <param name="InflatedLength">Number of bytes the chunk decompressed to.</param>
  /// <param name="Payload">The inflated payload.</param>
  internal sealed record ZlibChunk(
    long Offset,
    byte Cmf,
    byte Flg,
    long CompressedLength,
    long InflatedLength,
    byte[] Payload
  );

  /// <summary>
  /// Scans <paramref name="stream"/> for zlib headers. <paramref name="maxChunks"/>
  /// bounds the returned list so a huge backup file does not balloon the synthetic
  /// archive view. Stream position is restored before returning.
  /// </summary>
  internal static List<ZlibChunk> Scan(Stream stream, int maxChunks = 16, int maxPayloadBytes = 8 * 1024 * 1024) {
    ArgumentNullException.ThrowIfNull(stream);
    if (!stream.CanSeek) throw new ArgumentException("PBD chunk scanning requires a seekable stream.", nameof(stream));

    var saved = stream.Position;
    try {
      var result = new List<ZlibChunk>();
      var len = stream.Length;
      if (len < 4) return result;

      // Walk byte-by-byte looking for 0x78 followed by a plausible FLG.
      // The CMF byte for raw DEFLATE-inside-zlib at 32 KiB window is 0x78;
      // FLG completes the FCHECK invariant (CMF*256+FLG) % 31 == 0. The most
      // common FLG values are 0x01 (no preset, level 0/fastest), 0x9C
      // (default), and 0xDA (best). We accept any FLG that satisfies the
      // FCHECK constraint, then verify by attempting a real DEFLATE inflate.
      stream.Position = 0;
      var buffer = new byte[Math.Min(len, 64 * 1024)];
      var bufferStart = 0L;
      var bufferLen = stream.Read(buffer, 0, buffer.Length);

      for (long pos = 0; pos < len - 1 && result.Count < maxChunks; pos++) {
        var (cmf, flg) = PeekTwoBytes(stream, buffer, ref bufferStart, ref bufferLen, pos);
        if (cmf != 0x78) continue;
        if (((cmf << 8) | flg) % 31 != 0) continue;

        if (TryInflate(stream, pos, maxPayloadBytes, out var consumed, out var payload)) {
          result.Add(new ZlibChunk(pos, cmf, flg, consumed, payload.LongLength, payload));
          // We can NOT reliably advance by `consumed` because DeflateStream
          // buffer-reads ahead by up to 4 KiB, so `counter.BytesRead` is an
          // upper bound rather than an exact count. Advancing by `consumed`
          // would skip past adjacent chunks. The for-loop's `pos++` is enough
          // for the next iteration to resume scanning safely, because every
          // valid zlib header has to match FCHECK + survive a real inflate
          // attempt — so accidental overlap with another chunk's interior is
          // both rare and self-rejecting.
        }
      }

      return result;
    } finally {
      stream.Position = saved;
    }
  }

  private static (byte cmf, byte flg) PeekTwoBytes(
      Stream stream, byte[] buffer, ref long bufferStart, ref int bufferLen, long pos) {
    // Local read-through buffer to avoid one stream read per byte. Falls back
    // to direct stream reads for positions past the current window.
    if (pos < bufferStart || pos + 1 >= bufferStart + bufferLen) {
      stream.Position = pos;
      bufferStart = pos;
      bufferLen = stream.Read(buffer, 0, buffer.Length);
      if (bufferLen < 2) return (0, 0);
    }
    var off = (int)(pos - bufferStart);
    return (buffer[off], buffer[off + 1]);
  }

  private static bool TryInflate(Stream stream, long pos, int maxPayloadBytes, out long compressedConsumed, out byte[] payload) {
    compressedConsumed = 0;
    payload = [];
    try {
      stream.Position = pos + 2; // skip CMF + FLG to get raw DEFLATE stream
      // CountingStream so we know how many compressed bytes the DEFLATE
      // decoder actually consumed — we don't need exact Adler-32 accounting
      // because PBD does not always store one consistently, but we DO need
      // to refuse chunks that fail to inflate after the first ~16 bytes.
      using var counter = new CountingStream(stream);
      using var inflater = new DeflateStream(counter, CompressionMode.Decompress, leaveOpen: true);
      using var output = new MemoryStream();
      var buf = new byte[8192];
      var total = 0;
      while (true) {
        int read;
        try {
          read = inflater.Read(buf, 0, buf.Length);
        } catch (InvalidDataException) {
          // Not a real zlib chunk after all (false-positive header match).
          return false;
        }
        if (read <= 0) break;
        total += read;
        if (total > maxPayloadBytes) {
          // Treat oversized chunks as opaque; cap to avoid pathological memory blow-up.
          output.Write(buf, 0, Math.Max(0, maxPayloadBytes - (total - read)));
          payload = output.ToArray();
          // Add 2 for CMF+FLG.
          compressedConsumed = counter.BytesRead + 2;
          return true;
        }
        output.Write(buf, 0, read);
      }

      payload = output.ToArray();
      // Add 2 for CMF+FLG. Adler-32 (4 bytes) is consumed by the wrapping
      // zlib but we used raw DEFLATE — so DON'T over-count it. The next
      // scanner position picks up after compressedConsumed.
      compressedConsumed = counter.BytesRead + 2;
      // Reject zero-length matches; they indicate a degenerate header guess.
      return payload.Length > 0;
    } catch {
      return false;
    }
  }

  private sealed class CountingStream(Stream inner) : Stream {
    private readonly Stream _inner = inner;
    public long BytesRead { get; private set; }
    public override bool CanRead => this._inner.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => this._inner.Length;
    public override long Position {
      get => this._inner.Position;
      set => throw new NotSupportedException();
    }
    public override void Flush() => this._inner.Flush();
    public override int Read(byte[] buffer, int offset, int count) {
      var n = this._inner.Read(buffer, offset, count);
      this.BytesRead += n;
      return n;
    }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
  }
}
