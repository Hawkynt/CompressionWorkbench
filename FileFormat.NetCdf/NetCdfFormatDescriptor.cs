#pragma warning disable CS1591
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.NetCdf;

/// <summary>
/// Read-only descriptor for NetCDF Classic (CDF-1, CDF-2, CDF-5). Fires on the
/// <c>CDF\x01 / CDF\x02 / CDF\x05</c> magic. NetCDF-4 files (HDF5-based) are
/// handled by the HDF5 descriptor.
/// </summary>
public sealed class NetCdfFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {
  private static readonly byte[] Magic1 = [(byte)'C', (byte)'D', (byte)'F', 0x01];
  private static readonly byte[] Magic2 = [(byte)'C', (byte)'D', (byte)'F', 0x02];
  private static readonly byte[] Magic5 = [(byte)'C', (byte)'D', (byte)'F', 0x05];

  public string Id => "NetCdf";
  public string DisplayName => "NetCDF (Classic)";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".nc";
  public IReadOnlyList<string> Extensions => [".nc", ".cdf"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new(Magic1, Confidence: 0.95),
    new(Magic2, Confidence: 0.95),
    new(Magic5, Confidence: 0.95),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Network Common Data Form (classic CDF-1/2/5)";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var entries = BuildEntries(stream);
    var result = new List<ArchiveEntryInfo>(entries.Count);
    for (var i = 0; i < entries.Count; i++) {
      var e = entries[i];
      result.Add(new ArchiveEntryInfo(
        Index: i, Name: e.Name,
        OriginalSize: e.Data.LongLength, CompressedSize: e.Data.LongLength,
        Method: "stored", IsDirectory: false, IsEncrypted: false, LastModified: null,
        Kind: e.Kind));
    }
    return result;
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var entries = BuildEntries(stream);
    foreach (var e in entries) {
      if (files != null && files.Length > 0 && !MatchesFilter(e.Name, files))
        continue;
      WriteFile(outputDir, e.Name, e.Data);
    }
  }

  private static List<(string Name, string Kind, byte[] Data)> BuildEntries(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    var blob = ms.ToArray();

    var entries = new List<(string, string, byte[])> {
      ("FULL.nc", "Source", blob),
    };

    var status = "partial";
    NetCdfHeader? header = null;
    try {
      header = NetCdfParser.Parse(blob);
      status = "ok";
    } catch {
      // fall through — partial
    }

    if (header != null) {
      foreach (var v in header.Variables) {
        var name = $"vars/{SanitizeName(v.Name)}.bin";
        var data = SliceSafe(blob, v.BeginOffset, v.VsizeBytes);
        entries.Add((name, "Variable", data));
      }
    }

    entries.Add(("metadata.ini", "Metadata",
      BuildMetadata(header, status, blob)));
    return entries;
  }

  private static byte[] SliceSafe(byte[] blob, long offset, long length) {
    if (offset < 0 || length <= 0 || offset >= blob.LongLength)
      return [];
    var len = (int)Math.Min(length, blob.LongLength - offset);
    if (len <= 0) return [];
    var result = new byte[len];
    Array.Copy(blob, offset, result, 0, len);
    return result;
  }

  private static byte[] BuildMetadata(NetCdfHeader? header, string status, byte[] blob) {
    var sb = new StringBuilder();
    sb.Append("[netcdf]\r\n");
    sb.Append("parse_status=").Append(status).Append("\r\n");
    sb.Append("version=")
      .Append(blob.Length >= 4 ? blob[3].ToString() : "?")
      .Append("\r\n");
    if (header != null) {
      sb.Append("numrecs=").Append(header.NumRecs).Append("\r\n");
      sb.Append("dim_count=").Append(header.Dimensions.Count).Append("\r\n");
      sb.Append("var_count=").Append(header.Variables.Count).Append("\r\n");
      sb.Append("gatt_count=").Append(header.GlobalAttributes.Count).Append("\r\n");
      for (var i = 0; i < header.Dimensions.Count; i++) {
        var d = header.Dimensions[i];
        sb.Append($"[dim_{i:D2}]\r\n");
        sb.Append("name=").Append(d.Name).Append("\r\n");
        sb.Append("length=").Append(d.Length).Append("\r\n");
        sb.Append("is_unlimited=").Append(d.IsUnlimited ? "true" : "false").Append("\r\n");
      }
      for (var i = 0; i < header.Variables.Count; i++) {
        var v = header.Variables[i];
        sb.Append($"[var_{i:D2}]\r\n");
        sb.Append("name=").Append(v.Name).Append("\r\n");
        sb.Append("type=").Append(v.NcType).Append("\r\n");
        sb.Append("vsize=").Append(v.VsizeBytes).Append("\r\n");
        sb.Append("begin=").Append(v.BeginOffset).Append("\r\n");
        sb.Append("dim_ids=").Append(string.Join(",", v.DimIds)).Append("\r\n");
      }
    }
    return Encoding.ASCII.GetBytes(sb.ToString());
  }

  private static string SanitizeName(string name) {
    var sb = new StringBuilder(name.Length);
    foreach (var c in name)
      sb.Append((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') ||
                (c >= '0' && c <= '9') || c == '_' || c == '-' || c == '.'
        ? c
        : '_');
    var s = sb.ToString();
    return string.IsNullOrEmpty(s) ? "unnamed" : s;
  }
}
