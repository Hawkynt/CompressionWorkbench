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
/// References:
/// <list type="bullet">
///   <item><description>ISO/IEC 13818-1 §2.5 — program stream, pack header, system header, PES packet and program stream map syntax</description></item>
///   <item><description>ISO/IEC 11172-1 §2.4 — MPEG-1 system stream pack and packet layout</description></item>
///   <item><description><c>https://dvd.sourceforge.net/dvdinfo/mpeghdrs.html</c> — DVD private stream 1 substream ids and headers</description></item>
/// </list>
/// </summary>
public sealed class MpegPsFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveInMemoryExtract {

  public string Id => "MpegPs";
  public string DisplayName => "MPEG Program Stream";
  public FormatCategory Category => FormatCategory.Video;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".mpg";
  public IReadOnlyList<string> Extensions => [".mpg", ".mpeg", ".vob", ".m2p", ".ps"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new([0x00, 0x00, 0x01, 0xBA], Confidence: 0.90), // pack_start_code
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "MPEG-1/MPEG-2 program stream (MPG, VOB) demuxed into per-stream elementary streams.";

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
