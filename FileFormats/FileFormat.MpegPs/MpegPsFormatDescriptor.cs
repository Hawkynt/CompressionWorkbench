#pragma warning disable CS1591
using System.Globalization;
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.MpegPs;

/// <summary>
/// Pseudo-archive descriptor for MPEG program streams — <c>.mpg</c>/<c>.mpeg</c>
/// (MPEG-1 system and MPEG-2 program streams), DVD-Video <c>.vob</c> and
/// <c>.m2p</c>. Every elementary stream is exposed as one entry holding the raw
/// stream with PES framing removed; the DVD private-stream-1 substreams (AC-3,
/// DTS, LPCM, sub-pictures) become entries of their own. A <c>metadata.ini</c>
/// summarises packs, packets and per-stream timestamps.
///
/// <para>
/// Creation rebuilds the systems layer as an MPEG-2 Program Stream and keeps the
/// elementary-stream bytes opaque. The writer accepts MPEG-1/2 video, MPEG-4 Part 2,
/// AVC/H.264, HEVC/H.265, MPEG audio and AAC/ADTS elementary streams. DVD private
/// streams remain read-only because demuxing intentionally strips authoring metadata
/// that is required to reconstruct their private-stream headers faithfully.
/// </para>
///
/// References:
/// <list type="bullet">
///   <item><description>ISO/IEC 13818-1 §2.5 — program stream, pack header, system header, PES packet and program stream map syntax</description></item>
///   <item><description>ISO/IEC 11172-1 §2.4 — MPEG-1 system stream pack and packet layout</description></item>
///   <item><description><c>https://dvd.sourceforge.net/dvdinfo/mpeghdrs.html</c> — DVD private stream 1 substream ids and headers</description></item>
/// </list>
/// </summary>
public sealed class MpegPsFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveInMemoryExtract,
  IArchiveWriteConstraints, IArchiveCreatable, IAudioContainerFormat, IAudioMuxTarget {

  private static readonly string[] AudioMuxCodecs = ["aac", "aac-lc", "mp2", "mp3"];

  public string Id => "MpegPs";
  public string DisplayName => "MPEG Program Stream";
  public FormatCategory Category => FormatCategory.Video;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".mpg";
  public IReadOnlyList<string> Extensions => [".mpg", ".mpeg", ".vob", ".m2p", ".ps"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new([0x00, 0x00, 0x01, 0xBA], Confidence: 0.90), // pack_start_code
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Elementary-stream remux")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description =>
    "MPEG-1/MPEG-2 program stream demux; MPEG-2 PS mux from common video/audio elementary streams and packet-preserving AAC/MPEG-audio remux.";

  public long? MaxTotalArchiveSize => null;
  public string AcceptedInputsDescription =>
    "MPEG-PS mux accepts metadata.ini plus .m1v/.m2v/.m4v/.h264/.264/.h265/.265/.hevc video, .mp2/.mp3 MPEG audio, and .aac ADTS streams.";

  public bool CanAccept(ArchiveInputInfo input, out string? reason) {
    ArgumentNullException.ThrowIfNull(input);
    if (input.IsDirectory) {
      reason = "MPEG-PS is a stream container and does not accept directories.";
      return false;
    }

    var leaf = Path.GetFileName(input.ArchiveName);
    if (leaf.Equals("metadata.ini", StringComparison.OrdinalIgnoreCase) || IsSupportedElementaryExtension(leaf)) {
      reason = null;
      return true;
    }

    reason = $"unsupported MPEG-PS mux input '{input.ArchiveName}'; {this.AcceptedInputsDescription}";
    return false;
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(inputs);
    ArgumentNullException.ThrowIfNull(options);

    var streams = new List<MpegPsWriter.ElementaryStream>();
    var usedIds = new HashSet<byte>();
    foreach (var input in inputs) {
      if (!this.CanAccept(input, out var reason))
        throw new InvalidOperationException(reason);

      var leaf = Path.GetFileName(input.ArchiveName);
      if (leaf.Equals("metadata.ini", StringComparison.OrdinalIgnoreCase))
        continue;

      var data = input.ReadContent();
      if (!TryClassifyElementary(leaf, data, out var kind, out var streamType))
        throw new InvalidOperationException($"Cannot classify MPEG-PS elementary stream '{input.ArchiveName}'.");

      byte streamId;
      if (TryParseExplicitStreamId(leaf, out var explicitId)) {
        var valid = kind == MpegPsWriter.StreamKind.Audio
          ? explicitId is >= 0xC0 and <= 0xDF
          : explicitId is >= 0xE0 and <= 0xEF;
        if (!valid)
          throw new InvalidOperationException(
            $"Entry '{input.ArchiveName}' encodes stream id 0x{explicitId:X2}, which does not match its elementary-stream kind.");
        if (!usedIds.Add(explicitId))
          throw new InvalidOperationException($"Duplicate MPEG-PS stream id 0x{explicitId:X2}.");
        streamId = explicitId;
      } else {
        streamId = AllocateStreamId(kind, usedIds);
      }

      streams.Add(new MpegPsWriter.ElementaryStream(streamId, streamType, kind, data));
    }

    if (streams.Count == 0)
      throw new InvalidOperationException("MPEG-PS creation requires at least one elementary stream input.");

    MpegPsWriter.WriteProgram(output, streams);
  }

  public IReadOnlyList<string> SupportedMuxCodecs => AudioMuxCodecs;

  public bool CanMux(AudioStreamFormat stream, FormatCreateOptions options, out string? reason) {
    ArgumentNullException.ThrowIfNull(stream);
    ArgumentNullException.ThrowIfNull(options);

    if (!AudioMuxCodecs.Contains(stream.CodecId, StringComparer.OrdinalIgnoreCase)) {
      reason = $"MPEG-PS audio remux supports AAC, MP2 and MP3 packets, not codec '{stream.CodecId}'.";
      return false;
    }
    if (stream.SampleRate <= 0) {
      reason = "MPEG-PS audio remux requires a positive sample rate for 90 kHz PTS generation.";
      return false;
    }

    if (stream.CodecId.Equals("aac", StringComparison.OrdinalIgnoreCase)
        || stream.CodecId.Equals("aac-lc", StringComparison.OrdinalIgnoreCase)) {
      if (!MpegPsWriter.IsAacSampleRate(stream.SampleRate)) {
        reason = $"AAC/ADTS has no standard sample-rate index for {stream.SampleRate} Hz.";
        return false;
      }
      if (stream.Channels is < 1 or > 7) {
        reason = "AAC/ADTS remux requires a channel configuration between 1 and 7.";
        return false;
      }
    } else if (stream.Channels is < 1 or > 2) {
      reason = "MPEG Layer II/III remux requires mono or stereo stream geometry.";
      return false;
    }

    reason = null;
    return true;
  }

  public void Mux(Stream output, AudioEncodedStream stream, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(stream);
    ArgumentNullException.ThrowIfNull(options);
    if (!this.CanMux(stream.Format, options, out var reason))
      throw new NotSupportedException(reason);
    MpegPsWriter.WriteAudio(output, stream);
  }

  public List<ArchiveEntryInfo> List(Stream stream, string? password) =>
    BuildEntries(stream).Select((e, i) => new ArchiveEntryInfo(
      Index: i, Name: e.Name,
      OriginalSize: e.Data.Length, CompressedSize: e.Data.Length,
      Method: "stored", IsDirectory: false, IsEncrypted: false,
      LastModified: null, Kind: e.Kind)).ToList();

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    foreach (var e in BuildEntries(stream)) {
      if (files != null && files.Length > 0 && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, e.Data);
    }
  }

  public void ExtractEntry(Stream input, string entryName, Stream output, string? password) {
    foreach (var e in BuildEntries(input))
      if (e.Name.Equals(entryName, StringComparison.OrdinalIgnoreCase)) {
        output.Write(e.Data);
        return;
      }
    throw new FileNotFoundException($"Entry not found: {entryName}");
  }

  private static byte AllocateStreamId(MpegPsWriter.StreamKind kind, HashSet<byte> usedIds) {
    var first = kind == MpegPsWriter.StreamKind.Audio ? 0xC0 : 0xE0;
    var last = kind == MpegPsWriter.StreamKind.Audio ? 0xDF : 0xEF;
    for (var id = first; id <= last; ++id) {
      var candidate = (byte)id;
      if (usedIds.Add(candidate))
        return candidate;
    }
    throw new InvalidOperationException($"No free MPEG-PS {kind.ToString().ToLowerInvariant()} stream id remains.");
  }

  private static bool TryParseExplicitStreamId(string leaf, out byte streamId) {
    streamId = 0;
    return leaf.Length > 9
           && leaf.StartsWith("stream_", StringComparison.OrdinalIgnoreCase)
           && leaf[9] == '_'
           && byte.TryParse(leaf.AsSpan(7, 2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out streamId);
  }

  private static bool IsSupportedElementaryExtension(string name) =>
    Path.GetExtension(name).ToLowerInvariant() is
      ".m1v" or ".m2v" or ".m4v" or ".h264" or ".264" or ".h265" or ".265" or ".hevc"
      or ".mp2" or ".mp3" or ".aac";

  private static bool TryClassifyElementary(
      string name,
      ReadOnlySpan<byte> data,
      out MpegPsWriter.StreamKind kind,
      out byte streamType) {
    var extension = Path.GetExtension(name).ToLowerInvariant();
    switch (extension) {
      case ".m1v":
        kind = MpegPsWriter.StreamKind.Video;
        streamType = 0x01;
        return true;
      case ".m2v":
        kind = MpegPsWriter.StreamKind.Video;
        streamType = 0x02;
        return true;
      case ".mp2":
      case ".mp3":
        kind = MpegPsWriter.StreamKind.Audio;
        streamType = MpegPsWriter.DetectMpegAudioStreamType(data);
        return true;
      case ".aac":
        kind = MpegPsWriter.StreamKind.Audio;
        streamType = 0x0F;
        return true;
      case ".m4v":
        kind = MpegPsWriter.StreamKind.Video;
        streamType = 0x10;
        return true;
      case ".h264":
      case ".264":
        kind = MpegPsWriter.StreamKind.Video;
        streamType = 0x1B;
        return true;
      case ".h265":
      case ".265":
      case ".hevc":
        kind = MpegPsWriter.StreamKind.Video;
        streamType = 0x24;
        return true;
      default:
        kind = default;
        streamType = 0;
        return false;
    }
  }

  private static List<(string Name, string Kind, byte[] Data)> BuildEntries(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    var ps = MpegPsReader.Read(ms.GetBuffer().AsSpan(0, (int)ms.Length));

    var result = new List<(string, string, byte[])> {
      ("metadata.ini", "Tag", BuildMetadata(ps)),
    };
    foreach (var es in ps.Streams)
      result.Add((es.EntryName, "Payload", es.Payload));
    return result;
  }

  private static byte[] BuildMetadata(MpegPsReader.ProgramStream ps) {
    var sb = new StringBuilder();
    sb.Append("[mpegps]\n");
    sb.Append(CultureInfo.InvariantCulture, $"mpeg_version = {ps.MpegVersion}\n");
    sb.Append(CultureInfo.InvariantCulture, $"pack_count = {ps.PackCount}\n");
    sb.Append(CultureInfo.InvariantCulture, $"pes_packet_count = {ps.PesPacketCount}\n");
    sb.Append(CultureInfo.InvariantCulture, $"stream_count = {ps.Streams.Count}\n");
    sb.Append(CultureInfo.InvariantCulture, $"program_end = {(ps.HasProgramEnd ? "yes" : "no")}\n");
    foreach (var s in ps.Streams) {
      sb.Append('\n');
      sb.Append(CultureInfo.InvariantCulture, $"[{s.EntryName}]\n");
      sb.Append(CultureInfo.InvariantCulture, $"stream_id = 0x{s.StreamId:X2}\n");
      if (s.SubstreamId >= 0)
        sb.Append(CultureInfo.InvariantCulture, $"substream_id = 0x{s.SubstreamId:X2}\n");
      sb.Append(CultureInfo.InvariantCulture, $"kind = {s.Kind}\n");
      sb.Append(CultureInfo.InvariantCulture, $"pes_packets = {s.PacketCount}\n");
      sb.Append(CultureInfo.InvariantCulture, $"bytes = {s.Payload.Length}\n");
      if (s.FirstPts is { } first)
        sb.Append(CultureInfo.InvariantCulture, $"first_pts_ms = {first / 90.0:F3}\n");
      if (s.LastPts is { } last)
        sb.Append(CultureInfo.InvariantCulture, $"last_pts_ms = {last / 90.0:F3}\n");
    }
    return Encoding.UTF8.GetBytes(sb.ToString());
  }
}
