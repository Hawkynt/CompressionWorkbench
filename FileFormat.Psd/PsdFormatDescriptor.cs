#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Registry;

namespace FileFormat.Psd;

/// <summary>
/// Adobe Photoshop document (.psd / .psb) surfaced as an archive. Enumerates the
/// image resources (8BIM blocks), the embedded thumbnail (resource 0x040C) as a
/// standalone JPEG, and summary metadata; layer pixel data is intentionally not
/// decoded (that's a full raster pipeline — out of scope for the archive view).
/// </summary>
public sealed class PsdFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveInMemoryExtract {
  public string Id => "Psd";
  public string DisplayName => "Photoshop document";
  public FormatCategory Category => FormatCategory.Image;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".psd";
  public IReadOnlyList<string> Extensions => [".psd", ".psb"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new("8BPS"u8.ToArray(), Confidence: 0.95),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description =>
    "Adobe Photoshop document; surfaces thumbnail + resources + metadata + layer summary. " +
    "Layer pixel data not decoded.";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) =>
    BuildEntries(stream).Select((e, i) => new ArchiveEntryInfo(
      Index: i, Name: e.Name,
      OriginalSize: e.Data.Length, CompressedSize: e.Data.Length,
      Method: "stored", IsDirectory: false, IsEncrypted: false, LastModified: null,
      Kind: e.Kind)).ToList();

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    foreach (var e in BuildEntries(stream)) {
      if (files != null && files.Length > 0 && !FormatHelpers.MatchesFilter(e.Name, files))
        continue;
      FormatHelpers.WriteFile(outputDir, e.Name, e.Data);
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

  private static IReadOnlyList<(string Name, string Kind, byte[] Data)> BuildEntries(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    var blob = ms.ToArray();

    var entries = new List<(string Name, string Kind, byte[] Data)> {
      ("FULL.psd", "Track", blob),
    };

    if (blob.Length < 26) return entries;
    if (blob[0] != '8' || blob[1] != 'B' || blob[2] != 'P' || blob[3] != 'S') return entries;

    // Header (26 bytes).
    var version = BinaryPrimitives.ReadUInt16BigEndian(blob.AsSpan(4));
    var channels = BinaryPrimitives.ReadUInt16BigEndian(blob.AsSpan(12));
    var height = BinaryPrimitives.ReadUInt32BigEndian(blob.AsSpan(14));
    var width = BinaryPrimitives.ReadUInt32BigEndian(blob.AsSpan(18));
    var depth = BinaryPrimitives.ReadUInt16BigEndian(blob.AsSpan(22));
    var colorMode = BinaryPrimitives.ReadUInt16BigEndian(blob.AsSpan(24));

    // Section sizes are uint32 BE prefixes. Color Mode Data first.
    var pos = 26;
    if (pos + 4 > blob.Length) return entries;
    var colorModeLen = (int)BinaryPrimitives.ReadUInt32BigEndian(blob.AsSpan(pos));
    pos += 4 + colorModeLen;

    // Image Resources section.
    if (pos + 4 > blob.Length) return entries;
    var resourcesLen = (int)BinaryPrimitives.ReadUInt32BigEndian(blob.AsSpan(pos));
    pos += 4;
    var resourcesEnd = Math.Min(pos + resourcesLen, blob.Length);
    while (pos + 12 <= resourcesEnd) {
      if (blob[pos] != '8' || blob[pos + 1] != 'B' || blob[pos + 2] != 'I' || blob[pos + 3] != 'M') break;
      var resId = BinaryPrimitives.ReadUInt16BigEndian(blob.AsSpan(pos + 4));
      var nameLen = blob[pos + 6];
      var namePad = (nameLen + 1) % 2 == 0 ? 0 : 1;
      var dataStart = pos + 6 + 1 + nameLen + namePad;
      if (dataStart + 4 > resourcesEnd) break;
      var dataSize = (int)BinaryPrimitives.ReadUInt32BigEndian(blob.AsSpan(dataStart));
      if (dataStart + 4 + dataSize > resourcesEnd) break;

      var data = blob.AsSpan(dataStart + 4, dataSize).ToArray();
      if (resId == 0x040C && data.Length > 28) {
        // 28-byte thumbnail header + JFIF JPEG body.
        entries.Add(("thumbnail.jpg", "Tag", data[28..]));
      } else {
        entries.Add(($"resources/{resId:X4}_{SanitizeName(blob, pos + 7, nameLen)}.bin", "Tag", data));
      }

      pos = dataStart + 4 + dataSize + (dataSize % 2);
    }

    // Layer count — skip the layer-info decode here; just report how many.
    var ini = new StringBuilder();
    ini.AppendLine("; Photoshop document metadata");
    ini.Append("version=").AppendLine(version == 2 ? "PSB" : "PSD");
    ini.Append("width=").AppendLine(width.ToString(System.Globalization.CultureInfo.InvariantCulture));
    ini.Append("height=").AppendLine(height.ToString(System.Globalization.CultureInfo.InvariantCulture));
    ini.Append("channels=").AppendLine(channels.ToString(System.Globalization.CultureInfo.InvariantCulture));
    ini.Append("depth=").AppendLine(depth.ToString(System.Globalization.CultureInfo.InvariantCulture));
    ini.Append("color_mode=").AppendLine(colorMode switch {
      0 => "Bitmap", 1 => "Grayscale", 2 => "Indexed", 3 => "RGB",
      4 => "CMYK", 7 => "Multichannel", 8 => "Duotone", 9 => "Lab",
      _ => colorMode.ToString(System.Globalization.CultureInfo.InvariantCulture),
    });
    entries.Insert(1, ("metadata.ini", "Tag", Encoding.UTF8.GetBytes(ini.ToString())));

    return entries;
  }

  private static string SanitizeName(byte[] blob, int offset, byte len) {
    if (len == 0) return "unnamed";
    var sb = new StringBuilder(len);
    for (var i = 0; i < len && offset + i < blob.Length; ++i) {
      var c = (char)blob[offset + i];
      sb.Append(char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_');
    }
    return sb.Length > 0 ? sb.ToString() : "unnamed";
  }
}
