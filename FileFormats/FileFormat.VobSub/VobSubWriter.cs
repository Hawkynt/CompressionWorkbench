#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace FileFormat.VobSub;

/// <summary>
/// Builds VobSub <c>.idx</c>/<c>.sub</c> pairs from either already-framed VobSub
/// program-stream chunks or raw DVD sub-picture (SPU) packets.
/// </summary>
/// <remarks>
/// The VobSub <c>.sub</c> side is an MPEG-2 program stream. Raw SPU packets are
/// packetized as DVD Private Stream 1 (<c>0xBD</c>) PES packets in 2,048-byte
/// sectors; already-framed chunks are copied byte-for-byte for lossless remuxing.
/// The textual index is retained as a template and only its timestamp/file-position
/// directives are regenerated.
/// </remarks>
public static class VobSubWriter {
  private const int SectorSize = 2048;
  private const int PackHeaderSize = 14;
  private const int PesPrefixSize = 6;
  private const int PesHeaderSize = 3;
  private const int PtsSize = 5;
  private const int SubstreamIdSize = 1;
  private const int ProgramMuxRate = 25200;
  private const long PtsMask = (1L << 33) - 1;
  private const long ScrLeadTicks = 9000;
  private const long ScrTicksPerSector = 147;

  /// <summary>Describes how one subtitle frame should be written.</summary>
  public enum FrameKind {
    /// <summary>The bytes already contain the MPEG program-stream framing and are copied exactly.</summary>
    ProgramStream,
    /// <summary>The bytes are one raw DVD SPU packet and are wrapped in MPEG-2 PS/PES framing.</summary>
    RawSpu,
  }

  /// <summary>One subtitle frame supplied to the writer.</summary>
  public sealed record Frame(byte[] Data, FrameKind Kind = FrameKind.ProgramStream);

  /// <summary>The two files that make up a VobSub subtitle stream.</summary>
  public sealed record Pair(byte[] IndexBytes, byte[] SubBytes);

  /// <summary>
  /// Builds a VobSub pair. When <paramref name="timestamps"/> is omitted, timestamps
  /// are read from <paramref name="indexTemplate"/>. Supplying them is used by
  /// pair-aware editing when frames are removed and the surviving timestamp set changes.
  /// </summary>
  public static Pair Build(
      string indexTemplate,
      IReadOnlyList<Frame> frames,
      IReadOnlyList<TimeSpan>? timestamps = null) {
    ArgumentNullException.ThrowIfNull(indexTemplate);
    ArgumentNullException.ThrowIfNull(frames);

    var parsed = VobSubReader.ReadIndex(indexTemplate);
    timestamps ??= parsed.Entries.Select(static entry => entry.Timestamp).ToArray();
    if (timestamps.Count != frames.Count)
      throw new ArgumentException(
        $"VobSub requires one timestamp per subtitle frame ({timestamps.Count} timestamps, {frames.Count} frames).",
        nameof(frames));

    var streamIndex = ParseSingleStreamIndex(indexTemplate);
    using var sub = new MemoryStream();
    var offsets = new long[frames.Count];

    for (var i = 0; i < frames.Count; ++i) {
      var frame = frames[i] ?? throw new ArgumentException($"VobSub frame {i} is null.", nameof(frames));
      ArgumentNullException.ThrowIfNull(frame.Data);
      if (frame.Data.Length == 0)
        throw new InvalidDataException($"VobSub frame {i} is empty.");
      if (timestamps[i] < TimeSpan.Zero)
        throw new InvalidDataException($"VobSub frame {i} has a negative timestamp.");

      offsets[i] = sub.Position;
      if (frame.Kind == FrameKind.RawSpu)
        WriteSpuProgramStream(sub, frame.Data, timestamps[i], streamIndex);
      else
        sub.Write(frame.Data);
    }

    var rewritten = RewriteIndex(indexTemplate, timestamps, offsets);
    return new Pair(Encoding.UTF8.GetBytes(rewritten), sub.ToArray());
  }

  /// <summary>
  /// Rewrites only the timestamp/file-position directives of an index template.
  /// All non-timestamp directives and comments are retained verbatim.
  /// </summary>
  public static string RewriteIndex(
      string indexTemplate,
      IReadOnlyList<TimeSpan> timestamps,
      IReadOnlyList<long> offsets) {
    ArgumentNullException.ThrowIfNull(indexTemplate);
    ArgumentNullException.ThrowIfNull(timestamps);
    ArgumentNullException.ThrowIfNull(offsets);
    if (timestamps.Count != offsets.Count)
      throw new ArgumentException("VobSub timestamp and file-position counts must match.", nameof(offsets));

    var newline = indexTemplate.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
    var hadTrailingNewline = indexTemplate.EndsWith("\n", StringComparison.Ordinal);
    var normalized = indexTemplate.Replace("\r\n", "\n", StringComparison.Ordinal);
    var lines = normalized.Split('\n');
    if (hadTrailingNewline && lines.Length > 0 && lines[^1].Length == 0)
      lines = lines[..^1];

    var rewritten = new List<string>(lines.Length + Math.Max(0, timestamps.Count - 1));
    var inserted = false;
    foreach (var line in lines) {
      if (!line.TrimStart().StartsWith("timestamp:", StringComparison.OrdinalIgnoreCase)) {
        rewritten.Add(line);
        continue;
      }

      if (inserted) continue;
      AppendTimestampLines(rewritten, timestamps, offsets);
      inserted = true;
    }

    if (!inserted && timestamps.Count > 0) {
      if (rewritten.Count > 0 && rewritten[^1].Length != 0)
        rewritten.Add(string.Empty);
      AppendTimestampLines(rewritten, timestamps, offsets);
    }

    var result = string.Join(newline, rewritten);
    return hadTrailingNewline ? result + newline : result;
  }

  private static void AppendTimestampLines(
      List<string> destination,
      IReadOnlyList<TimeSpan> timestamps,
      IReadOnlyList<long> offsets) {
    for (var i = 0; i < timestamps.Count; ++i) {
      if (timestamps[i] < TimeSpan.Zero)
        throw new InvalidDataException($"VobSub timestamp {i} is negative.");
      if (offsets[i] < 0)
        throw new InvalidDataException($"VobSub file position {i} is negative.");
      destination.Add(FormattableString.Invariant(
        $"timestamp: {FormatTimestamp(timestamps[i])}, filepos: {offsets[i]:X10}"));
    }
  }

  private static string FormatTimestamp(TimeSpan timestamp) {
    var hours = checked((long)Math.Floor(timestamp.TotalHours));
    return string.Create(CultureInfo.InvariantCulture,
      $"{hours:D2}:{timestamp.Minutes:D2}:{timestamp.Seconds:D2}:{timestamp.Milliseconds:D3}");
  }

  private static int ParseSingleStreamIndex(string indexTemplate) {
    var result = 0;
    var foundStream = false;
    foreach (var raw in indexTemplate.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n')) {
      var line = raw.Trim();
      if (!line.StartsWith("id:", StringComparison.OrdinalIgnoreCase)) continue;
      if (foundStream)
        throw new NotSupportedException("Writing multi-language/multi-substream VobSub indexes is not supported yet.");
      foundStream = true;

      foreach (var field in line[3..].Split(',')) {
        var part = field.Trim();
        if (!part.StartsWith("index:", StringComparison.OrdinalIgnoreCase)) continue;
        if (!int.TryParse(part[6..].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out result))
          throw new InvalidDataException("VobSub id directive contains an invalid stream index.");
      }
    }

    if (result is < 0 or > 31)
      throw new InvalidDataException("VobSub subtitle stream index must be in the DVD private-stream range 0..31.");
    return result;
  }

  private static void WriteSpuProgramStream(Stream output, ReadOnlySpan<byte> spu, TimeSpan timestamp, int streamIndex) {
    ValidateSpu(spu);
    var ptsTicks = checked((long)Math.Round(timestamp.TotalSeconds * 90000d, MidpointRounding.AwayFromZero));
    var pts = ptsTicks & PtsMask;
    var remaining = spu;
    var sectorOrdinal = 0L;
    var first = true;

    while (!remaining.IsEmpty) {
      var includePts = first;
      var maximumPayload = SectorSize - PackHeaderSize - PesPrefixSize - PesHeaderSize
                           - (includePts ? PtsSize : 0) - SubstreamIdSize;
      var payloadLength = Math.Min(maximumPayload, remaining.Length);
      var baseScrTicks = Math.Max(0, ptsTicks - ScrLeadTicks);
      var scr = Math.Min(ptsTicks, baseScrTicks + sectorOrdinal * ScrTicksPerSector) & PtsMask;
      WriteSector(output, remaining[..payloadLength], pts, scr, (byte)(0x20 | streamIndex), includePts);
      remaining = remaining[payloadLength..];
      ++sectorOrdinal;
      first = false;
    }
  }

  private static void ValidateSpu(ReadOnlySpan<byte> spu) {
    if (spu.Length < 5)
      throw new InvalidDataException("VobSub raw SPU packet is too short.");
    if (spu.Length > ushort.MaxValue)
      throw new InvalidDataException("VobSub raw SPU packet exceeds the 16-bit DVD packet length field.");

    var declaredLength = BinaryPrimitives.ReadUInt16BigEndian(spu);
    if (declaredLength != spu.Length)
      throw new InvalidDataException(
        $"VobSub raw SPU packet declares {declaredLength} bytes but contains {spu.Length}.");

    var controlOffset = BinaryPrimitives.ReadUInt16BigEndian(spu[2..]);
    if (controlOffset is < 4 || controlOffset >= declaredLength)
      throw new InvalidDataException("VobSub raw SPU packet has an invalid control-sequence offset.");
  }

  private static void WriteSector(
      Stream output,
      ReadOnlySpan<byte> payload,
      long pts,
      long scr,
      byte substreamId,
      bool includePts) {
    Span<byte> sector = stackalloc byte[SectorSize];
    var position = 0;

    WritePackHeader(sector, ref position, scr);

    var ptsLength = includePts ? PtsSize : 0;
    var bytesWithoutPadding = PackHeaderSize + PesPrefixSize + PesHeaderSize + ptsLength + SubstreamIdSize + payload.Length;
    var tail = SectorSize - bytesWithoutPadding;
    var pesStuffing = tail is > 0 and < 6 ? tail : 0;
    var headerDataLength = ptsLength + pesStuffing;
    var pesPacketLength = PesHeaderSize + headerDataLength + SubstreamIdSize + payload.Length;

    sector[position++] = 0x00;
    sector[position++] = 0x00;
    sector[position++] = 0x01;
    sector[position++] = 0xBD;
    BinaryPrimitives.WriteUInt16BigEndian(sector[position..], checked((ushort)pesPacketLength));
    position += 2;
    sector[position++] = 0x80;
    sector[position++] = includePts ? (byte)0x80 : (byte)0x00;
    sector[position++] = checked((byte)headerDataLength);
    if (includePts)
      WriteTimestamp(sector, ref position, pts);
    sector.Slice(position, pesStuffing).Fill(0xFF);
    position += pesStuffing;
    sector[position++] = substreamId;
    payload.CopyTo(sector[position..]);
    position += payload.Length;

    var padding = SectorSize - position;
    if (padding > 0) {
      if (padding < 6)
        throw new InvalidOperationException("VobSub sector padding invariant was violated.");
      sector[position++] = 0x00;
      sector[position++] = 0x00;
      sector[position++] = 0x01;
      sector[position++] = 0xBE;
      BinaryPrimitives.WriteUInt16BigEndian(sector[position..], checked((ushort)(padding - 6)));
      position += 2;
      sector.Slice(position, padding - 6).Fill(0xFF);
      position += padding - 6;
    }

    if (position != SectorSize)
      throw new InvalidOperationException("VobSub writer produced a non-sector-sized MPEG-PS block.");
    output.Write(sector);
  }

  private static void WritePackHeader(Span<byte> destination, ref int position, long scr) {
    destination[position++] = 0x00;
    destination[position++] = 0x00;
    destination[position++] = 0x01;
    destination[position++] = 0xBA;
    destination[position++] = (byte)(0x40 | ((scr >> 27) & 0x38) | 0x04 | ((scr >> 28) & 0x03));
    destination[position++] = (byte)(scr >> 20);
    destination[position++] = (byte)(((scr >> 12) & 0xF8) | 0x04 | ((scr >> 13) & 0x03));
    destination[position++] = (byte)(scr >> 5);
    destination[position++] = (byte)(((scr & 0x1F) << 3) | 0x04);
    destination[position++] = 0x01;
    destination[position++] = (byte)(ProgramMuxRate >> 14);
    destination[position++] = (byte)(ProgramMuxRate >> 6);
    destination[position++] = (byte)(((ProgramMuxRate & 0x3F) << 2) | 0x03);
    destination[position++] = 0xF8;
  }

  private static void WriteTimestamp(Span<byte> destination, ref int position, long pts) {
    destination[position++] = (byte)(0x20 | (((pts >> 30) & 0x07) << 1) | 0x01);
    destination[position++] = (byte)(pts >> 22);
    destination[position++] = (byte)((((pts >> 15) & 0x7F) << 1) | 0x01);
    destination[position++] = (byte)(pts >> 7);
    destination[position++] = (byte)(((pts & 0x7F) << 1) | 0x01);
  }
}
