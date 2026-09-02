#pragma warning disable CS1591
using System.Globalization;
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.MpegTs;

/// <summary>
/// Pseudo-archive descriptor for MPEG-2 Transport Streams. Each detected elementary
/// stream is exposed as <c>stream_&lt;PID&gt;_&lt;type&gt;.bin</c> containing the
/// concatenated PES payload bytes for that PID.
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://www.itu.int/rec/T-REC-H.222.0</c> — ITU-T H.222.0 / ISO/IEC 13818-1 — MPEG-2 Systems (transport stream) standard</description></item>
///   <item><description><c>https://en.wikipedia.org/wiki/MPEG_transport_stream</c> — Wikipedia</description></item>
/// </list>
/// </summary>
public sealed class MpegTsFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveInMemoryExtract {

  /// <summary>
  /// Gets the id.
  /// </summary>
public string Id => "MpegTs";
  /// <summary>
  /// Gets the display name.
  /// </summary>
public string DisplayName => "MPEG-2 Transport Stream";
  /// <summary>
  /// Gets the category.
  /// </summary>
public FormatCategory Category => FormatCategory.Archive;
  /// <summary>
  /// Gets the capabilities.
  /// </summary>
public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
  /// <summary>
  /// Gets the default extension.
  /// </summary>
public string DefaultExtension => ".ts";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
public IReadOnlyList<string> Extensions => [".ts", ".m2ts", ".mts"];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
public IReadOnlyList<string> CompoundExtensions => [];
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
public IReadOnlyList<MagicSignature> MagicSignatures => [
    // Sync byte 0x47 at offsets 0, 188, and 376 — three consecutive 188-byte packets.
    // Encoded as a single signature here at offset 0; FormatDetector matches at offset
    // 0 only, so the descriptor's own List() does the multi-offset confirmation.
    new([0x47], Confidence: 0.30),
  ];
  /// <summary>
  /// Gets the methods.
  /// </summary>
public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  /// <summary>
  /// Gets the tar compression format id.
  /// </summary>
public string? TarCompressionFormatId => null;
  /// <summary>
  /// Gets the family.
  /// </summary>
public AlgorithmFamily Family => AlgorithmFamily.Archive;
  /// <summary>
  /// Gets the description.
  /// </summary>
public string Description => "MPEG-2 Transport Stream container demuxed into per-PID elementary streams.";

  /// <summary>
  /// Lists the entries in the supplied container.
  /// </summary>
public List<ArchiveEntryInfo> List(Stream stream, string? password) =>
    BuildEntries(stream).Select((e, i) => new ArchiveEntryInfo(
      Index: i, Name: e.Name,
      OriginalSize: e.Data.Length, CompressedSize: e.Data.Length,
      Method: "stored", IsDirectory: false, IsEncrypted: false,
      LastModified: null, Kind: e.Kind)).ToList();

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    foreach (var e in BuildEntries(stream)) {
      if (files != null && files.Length > 0 && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, e.Data);
    }
  }

  /// <summary>
  /// Performs the extract entry operation.
  /// </summary>
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
    var data = ms.GetBuffer().AsSpan(0, (int)ms.Length);

    // Strict three-sync-byte detection per spec brief — even though MagicSignatures only
    // checks offset 0, we re-verify here so List/Extract reject false positives.
    if (data.Length < MpegTsReader.PacketSize * 3 + 1) {
      // Tiny files can still be valid TS — fall through to the reader to decide.
    } else if (data[0] != MpegTsReader.SyncByte
               || data[MpegTsReader.PacketSize] != MpegTsReader.SyncByte
               || data[MpegTsReader.PacketSize * 2] != MpegTsReader.SyncByte) {
      // Try .m2ts (192-byte stride) as a second guess before failing.
      if (data.Length < MpegTsReader.M2tsPacketSize * 2 + 5
          || data[4] != MpegTsReader.SyncByte
          || data[MpegTsReader.M2tsPacketSize + 4] != MpegTsReader.SyncByte) {
        throw new InvalidDataException("MPEG-TS: sync bytes not at offsets 0/188/376 (or 4/196 for m2ts).");
      }
    }

    var ts = MpegTsReader.Read(data);

    var result = new List<(string, string, byte[])> {
      ("metadata.ini", "Tag", BuildMetadata(ts)),
    };
    foreach (var es in ts.Streams) {
      var name = $"stream_{es.Pid:X4}_{MpegTsReader.StreamTypeName(es.StreamType)}.bin";
      result.Add((name, "Payload", es.Payload));
    }
    return result;
  }

  private static byte[] BuildMetadata(MpegTsReader.TransportStream ts) {
    var sb = new StringBuilder();
    sb.AppendLine("[mpegts]");
    sb.Append("packet_count = ").Append(ts.PacketCount).Append('\n');
    sb.Append("packet_size = ").Append(ts.PacketSizeUsed).Append(ts.PacketSizeUsed == 192 ? " (m2ts)\n" : "\n");
    sb.Append("program_count = ").Append(ts.Programs.Count).Append('\n');
    sb.Append("stream_count = ").Append(ts.Streams.Count).Append('\n');
    foreach (var p in ts.Programs)
      sb.Append(CultureInfo.InvariantCulture, $"program {p.ProgramNumber} -> PMT PID 0x{p.PmtPid:X4}\n");
    foreach (var s in ts.Streams)
      sb.Append(CultureInfo.InvariantCulture,
        $"stream PID 0x{s.Pid:X4} type 0x{s.StreamType:X2} ({MpegTsReader.StreamTypeName(s.StreamType)}) program {s.ProgramNumber} bytes {s.Payload.Length}\n");
    return Encoding.UTF8.GetBytes(sb.ToString());
  }
}
