#pragma warning disable CS1591
using System.Globalization;
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Numpy;

/// <summary>
/// Pseudo-archive descriptor for NumPy's NPY array serialization format.
/// Splits an <c>.npy</c> file into <c>metadata.ini</c> (dtype, shape, header-length,
/// version, fortran order) and <c>array.bin</c> (the raw payload bytes after the
/// header).
/// </summary>
/// <remarks>
/// NPY magic is 6 bytes (<c>\x93NUMPY</c>) + 2-byte version + header length,
/// followed by an ASCII Python-dict header and raw array bytes. Supports v1
/// (u16 header length), v2 (u32), and v3 (UTF-8 dict, otherwise identical to v2).
/// </remarks>
public sealed class NpyFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {

  public string Id => "Npy";
  public string DisplayName => "NumPy NPY";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".npy";
  public IReadOnlyList<string> Extensions => [".npy"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new([0x93, (byte)'N', (byte)'U', (byte)'M', (byte)'P', (byte)'Y'], Confidence: 0.95),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description =>
    "NumPy NPY array serialization (v1/v2/v3); surfaces dtype + shape + raw array bytes.";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) =>
    BuildEntries(stream)
      .Select((e, i) => new ArchiveEntryInfo(i, e.Name, e.Data.LongLength, e.Data.LongLength,
        "stored", false, false, null, e.Kind))
      .ToList();

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    foreach (var e in this.BuildEntries(stream)) {
      if (files != null && files.Length > 0 && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, e.Data);
    }
  }

  private List<(string Name, byte[] Data, string Kind)> BuildEntries(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    var span = ms.GetBuffer().AsSpan(0, (int)ms.Length);

    var result = new List<(string, byte[], string)>();
    try {
      var arr = NpyReader.Read(span);
      result.Add(("metadata.ini", BuildMetadata(arr, ms.Length), "Metadata"));
      result.Add(("header.bin", arr.HeaderBytes, "Header"));
      result.Add(("array.bin", arr.ArrayBytes, "Payload"));
    } catch (Exception ex) {
      var sb = new StringBuilder();
      sb.Append("[npy]\r\n");
      sb.Append("parse_status=error\r\n");
      sb.Append("file_size=").Append(ms.Length).Append("\r\n");
      sb.Append("error=").Append(ex.Message).Append("\r\n");
      result.Add(("metadata.ini", Encoding.UTF8.GetBytes(sb.ToString()), "Metadata"));
    }
    return result;
  }

  private static byte[] BuildMetadata(NpyReader.NpyArray a, long fileSize) {
    var sb = new StringBuilder();
    sb.Append("[npy]\r\n");
    sb.Append("parse_status=ok\r\n");
    sb.Append(CultureInfo.InvariantCulture, $"file_size={fileSize}\r\n");
    sb.Append(CultureInfo.InvariantCulture, $"version={a.MajorVersion}.{a.MinorVersion}\r\n");
    sb.Append(CultureInfo.InvariantCulture, $"header_len={a.HeaderLength}\r\n");
    sb.Append(CultureInfo.InvariantCulture, $"header_bytes={a.HeaderBytes.Length}\r\n");
    sb.Append(CultureInfo.InvariantCulture, $"array_bytes={a.ArrayBytes.Length}\r\n");
    sb.Append("dtype=").Append(a.Dtype ?? "(unknown)").Append("\r\n");
    sb.Append("shape=").Append(a.Shape ?? "(unknown)").Append("\r\n");
    sb.Append("fortran_order=").Append(a.FortranOrder ? "true" : "false").Append("\r\n");
    return Encoding.UTF8.GetBytes(sb.ToString());
  }
}
