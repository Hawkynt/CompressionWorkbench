#pragma warning disable CS1591
using Compression.Core.Streams;
using FileFormat.Zlib;

namespace FileFormat.EaseUs;

/// <summary>
/// Linear scanner that locates zlib substreams inside an EaseUS Todo
/// Backup <c>.pbd</c> container and runs a trial inflate against each
/// candidate to confirm the substream is well-formed.
///
/// <para>
/// <b>Why scan instead of follow a chunk table?</b> EaseUS has never
/// published the on-disk chunk-table framing that wraps each zlib stream
/// inside a .pbd file, and the parent-chain index that maps logical
/// sectors back to compressed chunks lives behind the AES-256 key
/// envelope when encryption is enabled. The only universally observable
/// landmark inside the body is the zlib header itself (0x78 followed by
/// 0x01 / 0x9C / 0xDA — the three FCHECK-valid combinations RFC 1950
/// permits with the EaseUS-writer's CINFO=7 / FLEVEL = {fastest, default,
/// max}). Binwalk has confirmed this layout across every public sample.
/// </para>
///
/// <para>
/// <b>Trial-inflate guard.</b> A bare 0x78-byte produces ~0.4% false
/// positives even in random data, and the FCHECK constraint only narrows
/// that down to a few-per-MiB pattern hit rate. The only reliable way to
/// distinguish a real zlib substream from a coincidental byte sequence is
/// to actually inflate it — the Adler-32 trailer plus the DEFLATE
/// terminal-block bit make this a strong test. We use
/// <see cref="ZlibStream"/> for the inflation; failures (header invalid,
/// truncated, corrupt past header) are captured as
/// <see cref="EaseUsChunkInflateStatus"/> values rather than thrown so
/// forensic users see the full candidate inventory.
/// </para>
/// </summary>
public static class EaseUsZlibScanner {

  /// <summary>Maximum decompressed bytes retained per chunk before <see cref="EaseUsChunkInflateStatus.InflatedOverCap"/> kicks in.</summary>
  public const int DefaultMaxRetainedPayloadBytes = 64 * 1024;

  /// <summary>Maximum total candidate substream count to evaluate (cheap guard against pathological inputs).</summary>
  public const int DefaultMaxCandidates = 4096;

  /// <summary>
  /// Walks <paramref name="data"/> from offset <paramref name="startOffset"/>
  /// onward, locating each <c>0x78 {0x01|0x9C|0xDA}</c> candidate header and
  /// running a trial inflate. Returns the full chunk inventory in scan order.
  /// </summary>
  /// <param name="data">.pbd file bytes loaded into memory.</param>
  /// <param name="startOffset">Where to start scanning from (typically the 12-byte IMGF header end).</param>
  /// <param name="maxRetainedPayloadBytes">Per-chunk decompressed-byte retention cap; <c>0</c> means never retain payloads, only count.</param>
  /// <param name="maxCandidates">Hard guard on the number of candidates evaluated.</param>
  /// <param name="onlyOverlapping">When false (default) the scanner moves past every confirmed inflate; when true it still steps byte-by-byte (used by tests).</param>
  public static List<EaseUsZlibChunk> Scan(
    byte[] data,
    int startOffset = EaseUsReader.HeaderSize,
    int maxRetainedPayloadBytes = DefaultMaxRetainedPayloadBytes,
    int maxCandidates = DefaultMaxCandidates,
    bool onlyOverlapping = false
  ) {
    ArgumentNullException.ThrowIfNull(data);
    if (startOffset < 0) throw new ArgumentOutOfRangeException(nameof(startOffset));
    if (maxRetainedPayloadBytes < 0) throw new ArgumentOutOfRangeException(nameof(maxRetainedPayloadBytes));
    if (maxCandidates < 0) throw new ArgumentOutOfRangeException(nameof(maxCandidates));

    var chunks = new List<EaseUsZlibChunk>();
    if (data.Length <= startOffset + 1) return chunks;

    var i = startOffset;
    while (i + 1 < data.Length && chunks.Count < maxCandidates) {
      if (data[i] != 0x78) { i++; continue; }
      var fch = data[i + 1];
      if (fch is not (0x01 or 0x9C or 0xDA)) { i++; continue; }

      var chunk = TryInflate(data, i, maxRetainedPayloadBytes);
      chunks.Add(chunk);

      if (!onlyOverlapping && chunk.InflateStatus is EaseUsChunkInflateStatus.Inflated or EaseUsChunkInflateStatus.InflatedOverCap && chunk.CompressedLength > 0) {
        i += (int)chunk.CompressedLength;
      } else {
        i++;
      }
    }

    return chunks;
  }

  /// <summary>
  /// Attempts to inflate the candidate zlib substream starting at
  /// <paramref name="offset"/> in <paramref name="data"/>. The decoder
  /// reads ahead, but on a seekable stream <see cref="ZlibStream"/> hands
  /// the read-ahead back once the Adler-32 trailer is checked, so the
  /// stream position afterwards is the exact compressed length that
  /// multi-stream scanning needs.
  /// </summary>
  public static EaseUsZlibChunk TryInflate(byte[] data, int offset, int maxRetainedPayloadBytes = DefaultMaxRetainedPayloadBytes) {
    ArgumentNullException.ThrowIfNull(data);
    if (offset < 0 || offset + 2 > data.Length)
      return new EaseUsZlibChunk { Offset = offset, InflateStatus = EaseUsChunkInflateStatus.FailedHeaderInvalid };

    var fch = data[offset + 1];

    using var src = new MemoryStream(data, offset, data.Length - offset, writable: false);
    using var z = new ZlibStream(src, CompressionStreamMode.Decompress, leaveOpen: true);

    long produced = 0;
    var keep = maxRetainedPayloadBytes > 0;
    var sink = keep ? new MemoryStream(capacity: 4096) : null;
    var buffer = new byte[16 * 1024];

    while (true) {
      int read;
      try {
        read = z.Read(buffer, 0, buffer.Length);
      } catch (InvalidDataException) {
        return new EaseUsZlibChunk {
          Offset = offset,
          FchByte = fch,
          InflateStatus = produced == 0
            ? EaseUsChunkInflateStatus.FailedHeaderInvalid
            : EaseUsChunkInflateStatus.FailedCorrupt,
        };
      } catch (EndOfStreamException) {
        return new EaseUsZlibChunk {
          Offset = offset,
          FchByte = fch,
          InflateStatus = EaseUsChunkInflateStatus.FailedTruncated,
        };
      } catch (NotSupportedException) {
        return new EaseUsZlibChunk {
          Offset = offset,
          FchByte = fch,
          InflateStatus = EaseUsChunkInflateStatus.FailedHeaderInvalid,
        };
      }

      if (read == 0) break;
      produced += read;

      if (keep && sink!.Length + read <= maxRetainedPayloadBytes)
        sink.Write(buffer, 0, read);
      else
        keep = false;
    }

    // A successfully-opened ZlibStream that produces zero bytes still
    // means the header was valid AND a terminal DEFLATE block was found.
    // The Adler-32 trailer was checked when the stream reported its end —
    // we count "produced == 0" as a documented edge case (empty payload)
    // and treat it as Inflated only if the stream actually consumed bytes
    // past the header + trailer.
    var compressedLength = src.Position;
    if (produced == 0 && compressedLength <= 2) {
      return new EaseUsZlibChunk {
        Offset = offset,
        FchByte = fch,
        InflateStatus = EaseUsChunkInflateStatus.FailedHeaderInvalid,
      };
    }

    var underCap = produced <= maxRetainedPayloadBytes && maxRetainedPayloadBytes > 0;
    return new EaseUsZlibChunk {
      Offset = offset,
      FchByte = fch,
      CompressedLength = compressedLength,
      DecompressedLength = produced,
      InflateStatus = underCap ? EaseUsChunkInflateStatus.Inflated : EaseUsChunkInflateStatus.InflatedOverCap,
      PayloadRetained = underCap,
      Payload = underCap ? sink!.ToArray() : [],
    };
  }
}
