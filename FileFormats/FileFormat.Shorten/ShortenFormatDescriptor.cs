#pragma warning disable CS1591
using System.Globalization;
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Shorten;

/// <summary>
/// Exposes Tony Robinson's Shorten (.shn) lossless audio file as a read-only
/// archive of <c>FULL.shn</c>, a <c>metadata.ini</c> describing the header
/// (version, internal sample type, channels, block size, max LPC order), and the
/// raw Rice-coded payload as a structural entry (<c>payload.bin</c>).
/// </summary>
/// <remarks>
/// <para>Shorten files start with the magic "ajkg" followed by a single version
/// byte. The body is a Rice-coded bitstream: a sequence of variable-length
/// unsigned integers (<c>uvar</c>) and Rice-coded values. The opening header
/// carries the internal data type, channel count, block size and maximum
/// predictor (LPC) order, which we decode here via a minimal bit reader.</para>
/// <para><b>Deferred:</b> full audio decode (Rice-coded residuals of polynomial /
/// LPC predictors, channel de-correlation, and the byte-stream uLaw/PCM
/// reconstruction) is not implemented — this descriptor surfaces the container
/// structure and header metadata only, so the compressed payload round-trips
/// verbatim but is not turned back into PCM.</para>
/// </remarks>
public sealed class ShortenFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations,
  IArchiveInMemoryExtract {

  public string Id => "Shorten";
  public string DisplayName => "Shorten lossless audio (.shn)";
  public FormatCategory Category => FormatCategory.Audio;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".shn";
  public IReadOnlyList<string> Extensions => [".shn"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new("ajkg"u8.ToArray(), Confidence: 0.95),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored"), new("shorten", "Shorten")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Classic;
  public string Description =>
    "Shorten lossless audio; full file + header metadata + raw payload. Audio decode (Rice/LPC) deferred — structural only.";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) =>
    BuildEntries(stream).Select((e, i) => new ArchiveEntryInfo(
      Index: i, Name: e.Name,
      OriginalSize: e.Data.Length, CompressedSize: e.Data.Length,
      Method: e.Kind == "Payload" ? "shorten" : "stored",
      IsDirectory: false, IsEncrypted: false, LastModified: null,
      Kind: e.Kind)).ToList();

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    foreach (var e in BuildEntries(stream)) {
      if (files != null && files.Length > 0 && !MatchesFilter(e.Name, files))
        continue;
      WriteFile(outputDir, e.Name, e.Data);
    }
  }

  public void ExtractEntry(Stream input, string entryName, Stream output, string? password) {
    foreach (var e in BuildEntries(input)) {
      if (e.Name.Equals(entryName, StringComparison.OrdinalIgnoreCase)) {
        output.Write(e.Data);
        return;
      }
    }
    throw new FileNotFoundException($"Entry not found: {entryName}");
  }

  // Shorten internal data-type codes (TYPE_*) as documented by the reference
  // implementation; only the type number matters here for reporting.
  private static string TypeName(int t) => t switch {
    0 => "u8 (uchar)",
    1 => "s8 (char)",
    2 => "u16 high-low",
    3 => "s16 high-low",
    4 => "u16 low-high",
    5 => "s16 low-high",
    6 => "ulaw",
    7 => "alaw",
    9 => "ulaw (variant)",
    _ => $"unknown ({t})",
  };

  private static List<(string Name, string Kind, byte[] Data)> BuildEntries(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    var file = ms.ToArray();

    var entries = new List<(string, string, byte[])> {
      ("FULL.shn", "Container", file),
    };

    var meta = new StringBuilder();
    meta.AppendLine("[shorten]");

    if (file.Length < 5 || file[0] != 'a' || file[1] != 'j' || file[2] != 'k' || file[3] != 'g') {
      meta.AppendLine("parse_status=partial");
      entries.Add(("metadata.ini", "Tag", Encoding.UTF8.GetBytes(meta.ToString())));
      return entries;
    }

    var version = file[4];
    meta.Append("version=").AppendLine(version.ToString(CultureInfo.InvariantCulture));

    // The Rice-coded bitstream starts right after the version byte.
    var payloadStart = 5;
    if (payloadStart < file.Length)
      entries.Add(("payload.bin", "Payload", file.AsSpan(payloadStart).ToArray()));

    try {
      var reader = new ShortenBitReader(file, payloadStart);
      // Header layout (post-magic): ulong fileType, ulong nchan, then (v>=2)
      //   ulong blocksize, ulong maxnlpc, ulong nmean, ulong nskip + nskip bytes.
      var fileType = reader.ReadUlong();
      var nchan = reader.ReadUlong();
      meta.Append("internal_type=").AppendLine(TypeName((int)fileType));
      meta.Append("channels=").AppendLine(nchan.ToString(CultureInfo.InvariantCulture));

      if (version >= 2) {
        var blocksize = reader.ReadUlong();
        var maxnlpc = reader.ReadUlong();
        var nmean = reader.ReadUlong();
        meta.Append("block_size=").AppendLine(blocksize.ToString(CultureInfo.InvariantCulture));
        meta.Append("max_lpc_order=").AppendLine(maxnlpc.ToString(CultureInfo.InvariantCulture));
        meta.Append("predictor_type=")
          .AppendLine(maxnlpc == 0 ? "polynomial" : "lpc");
        meta.Append("mean_count=").AppendLine(nmean.ToString(CultureInfo.InvariantCulture));
      } else {
        meta.AppendLine("predictor_type=polynomial");
      }
    } catch {
      // Header decode is best-effort; the version + payload are still surfaced.
      meta.AppendLine("header_status=partial");
    }

    entries.Add(("metadata.ini", "Tag", Encoding.UTF8.GetBytes(meta.ToString())));
    return entries;
  }
}
