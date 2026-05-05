#pragma warning disable CS1591
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Fits;

/// <summary>
/// Read-only archive-shaped descriptor for the FITS (Flexible Image Transport System) format
/// used for astronomical data.
/// Surfaces each HDU as a <c>.header</c>/<c>.data</c> pair, plus a passthrough <c>FULL.fits</c>
/// and a <c>metadata.ini</c> summary.
/// </summary>
public sealed class FitsFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {
  // FITS magic: first card must begin with "SIMPLE  =                    T"
  // (keyword in columns 1-8, "= " at columns 9-10, value 'T' at column 30).
  private static readonly byte[] SimpleMagic =
    "SIMPLE  =                    T"u8.ToArray();

  private const int CopyBufferSize = 64 * 1024;

  public string Id => "Fits";
  public string DisplayName => "FITS";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".fits";
  public IReadOnlyList<string> Extensions => [".fits", ".fit", ".fts"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures =>
    [new(SimpleMagic, Confidence: 0.95)];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Flexible Image Transport System (astronomy)";

  private sealed record HduInfo(string Prefix, FitsHdu Hdu);

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var entries = new List<ArchiveEntryInfo> {
      new(0, "FULL.fits", stream.Length, stream.Length, "stored", false, false, null, "Source"),
    };

    var status = "ok";
    List<FitsHdu> hdus;
    try {
      hdus = FitsParser.ParseAll(stream);
    } catch {
      entries.Add(new ArchiveEntryInfo(
        entries.Count, "metadata.ini", 0, 0, "stored", false, false, null, "Metadata"));
      return entries;
    }

    if (hdus.Count == 0) {
      var metaBytes = BuildMetadata(hdus, "empty");
      entries.Add(new ArchiveEntryInfo(
        entries.Count, "metadata.ini", metaBytes.LongLength, metaBytes.LongLength,
        "stored", false, false, null, "Metadata"));
      return entries;
    }

    for (var i = 0; i < hdus.Count; i++) {
      var hdu = hdus[i];
      var prefix = BuildPrefix(i, hdu);
      var headerText = string.Join("\r\n", hdu.Cards);
      var headerBytes = Encoding.ASCII.GetBytes(headerText);

      entries.Add(new ArchiveEntryInfo(
        entries.Count, $"{prefix}.header",
        headerBytes.LongLength, headerBytes.LongLength,
        "stored", false, false, null, "Header"));

      var dataLen = hdu.DataLength > 0 && hdu.DataOffset + hdu.DataLength <= stream.Length
        ? hdu.DataLength
        : 0L;
      entries.Add(new ArchiveEntryInfo(
        entries.Count, $"{prefix}.data",
        dataLen, dataLen,
        "stored", false, false, null, "Data"));
    }

    var meta = BuildMetadata(hdus, status);
    entries.Add(new ArchiveEntryInfo(
      entries.Count, "metadata.ini", meta.LongLength, meta.LongLength,
      "stored", false, false, null, "Metadata"));
    return entries;
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    // Stream FULL.fits directly — never buffer the whole file.
    if (files == null || files.Length == 0 || MatchesFilter("FULL.fits", files)) {
      stream.Seek(0, SeekOrigin.Begin);
      var fullPath = Path.Combine(outputDir, "FULL.fits");
      var dir = Path.GetDirectoryName(fullPath);
      if (dir != null) Directory.CreateDirectory(dir);
      using var outStream = File.Create(fullPath);
      stream.CopyTo(outStream);
    }

    List<FitsHdu> hdus;
    try {
      hdus = FitsParser.ParseAll(stream);
    } catch {
      if (files == null || files.Length == 0 || MatchesFilter("metadata.ini", files)) {
        WriteFile(outputDir, "metadata.ini",
          Encoding.ASCII.GetBytes("[fits]\r\nparse_status=partial\r\nhdu_count=0\r\n"));
      }
      return;
    }

    var status = hdus.Count == 0 ? "empty" : "ok";
    for (var i = 0; i < hdus.Count; i++) {
      var hdu = hdus[i];
      var prefix = BuildPrefix(i, hdu);

      if (files == null || files.Length == 0 || MatchesFilter($"{prefix}.header", files)) {
        var headerText = string.Join("\r\n", hdu.Cards);
        WriteFile(outputDir, $"{prefix}.header", Encoding.ASCII.GetBytes(headerText));
      }

      if (files == null || files.Length == 0 || MatchesFilter($"{prefix}.data", files)) {
        var dataLen = hdu.DataLength > 0 && hdu.DataOffset + hdu.DataLength <= stream.Length
          ? hdu.DataLength
          : 0L;
        WriteHduData(stream, outputDir, $"{prefix}.data", hdu.DataOffset, dataLen);
      }
    }

    if (files == null || files.Length == 0 || MatchesFilter("metadata.ini", files))
      WriteFile(outputDir, "metadata.ini", BuildMetadata(hdus, status));
  }

  // Stream-copies the HDU data region (bounded by long offsets) to a file without ever
  // materializing a byte[] of the full payload.
  private static void WriteHduData(Stream stream, string outputDir, string entryName, long offset, long length) {
    var safeName = entryName.Replace('\\', '/').TrimStart('/');
    if (safeName.Contains("..")) safeName = Path.GetFileName(safeName);
    var fullPath = Path.Combine(outputDir, safeName);
    var dir = Path.GetDirectoryName(fullPath);
    if (dir != null) Directory.CreateDirectory(dir);

    using var outStream = File.Create(fullPath);
    if (length <= 0) return;

    stream.Seek(offset, SeekOrigin.Begin);
    var buf = new byte[Math.Min(CopyBufferSize, length)];
    var remaining = length;
    while (remaining > 0) {
      var toRead = (int)Math.Min(buf.Length, remaining);
      var n = stream.Read(buf, 0, toRead);
      if (n <= 0) break;
      outStream.Write(buf, 0, n);
      remaining -= n;
    }
  }

  private static string BuildPrefix(int i, FitsHdu hdu)
    => i == 0 ? "hdu_00_primary" : $"hdu_{i:D2}_{SanitizeXtension(hdu.Xtension ?? "ext")}";

  private static byte[] BuildMetadata(IReadOnlyList<FitsHdu> hdus, string status) {
    var sb = new StringBuilder();
    sb.Append("[fits]\r\n");
    sb.Append("parse_status=").Append(status).Append("\r\n");
    sb.Append("hdu_count=").Append(hdus.Count).Append("\r\n");
    for (var i = 0; i < hdus.Count; i++) {
      var hdu = hdus[i];
      sb.Append($"[hdu_{i:D2}]\r\n");
      sb.Append("xtension=").Append(hdu.Xtension ?? (i == 0 ? "PRIMARY" : "UNKNOWN"))
        .Append("\r\n");
      sb.Append("bitpix=").Append(hdu.Bitpix).Append("\r\n");
      sb.Append("naxis=").Append(hdu.Naxis).Append("\r\n");
      for (var d = 0; d < hdu.AxisSizes.Count; d++)
        sb.Append($"naxis{d + 1}=").Append(hdu.AxisSizes[d]).Append("\r\n");
      sb.Append("data_bytes=").Append(hdu.DataLength).Append("\r\n");
      if (hdu.Object != null)
        sb.Append("object=").Append(hdu.Object).Append("\r\n");
      if (hdu.Telescope != null)
        sb.Append("telescope=").Append(hdu.Telescope).Append("\r\n");
    }
    return Encoding.ASCII.GetBytes(sb.ToString());
  }

  private static string SanitizeXtension(string xt) {
    var sb = new StringBuilder(xt.Length);
    foreach (var c in xt.Trim()) {
      if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') ||
          (c >= '0' && c <= '9') || c == '-' || c == '_')
        sb.Append(c);
      else
        sb.Append('_');
    }
    var s = sb.ToString().ToLowerInvariant();
    return string.IsNullOrEmpty(s) ? "ext" : s;
  }
}
