#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Psb;

/// <summary>
/// Photoshop Large Document (.psb) surfaced as an archive. Identical to PSD in layout except
/// that the layer/mask and image-data section lengths are 64-bit. Emits image resources, any
/// embedded thumbnail JPEG, the raw layer+mask section, the raw image-data section, and
/// summary metadata. Pixel data is intentionally not decoded.
/// </summary>
public sealed class PsbFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {
  public string Id => "Psb";
  public string DisplayName => "Photoshop Large Document";
  public FormatCategory Category => FormatCategory.Image;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".psb";
  public IReadOnlyList<string> Extensions => [".psb"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    // 8BPS + BE uint16 version 2 — distinguishes from regular PSD (version 1).
    new(new byte[] { (byte)'8', (byte)'B', (byte)'P', (byte)'S', 0x00, 0x02 }, Confidence: 0.97),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description =>
    "Photoshop Large Document (PSB) — 64-bit variant of PSD. Surfaces thumbnail + resources + " +
    "metadata + raw layer/image sections. Pixel data not decoded.";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) =>
    BuildEntries(stream).Select((e, i) => new ArchiveEntryInfo(
      Index: i, Name: e.Name,
      OriginalSize: e.Data.Length, CompressedSize: e.Data.Length,
      Method: "stored", IsDirectory: false, IsEncrypted: false, LastModified: null,
      Kind: e.Kind)).ToList();

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    foreach (var e in BuildEntries(stream)) {
      if (files != null && files.Length > 0 && !MatchesFilter(e.Name, files))
        continue;
      WriteFile(outputDir, e.Name, e.Data);
    }
  }

  private static IReadOnlyList<(string Name, string Kind, byte[] Data)> BuildEntries(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    var blob = ms.ToArray();

    var entries = new List<(string Name, string Kind, byte[] Data)> {
      ("FULL.psb", "Track", blob),
    };

    if (blob.Length < 26) return entries;
    if (blob[0] != '8' || blob[1] != 'B' || blob[2] != 'P' || blob[3] != 'S') return entries;

    try {
      var version = BinaryPrimitives.ReadUInt16BigEndian(blob.AsSpan(4));
      // Only PSB (version 2); version 1 is handled by PsdFormatDescriptor.
      if (version != 2) return entries;

      var channels = BinaryPrimitives.ReadUInt16BigEndian(blob.AsSpan(12));
      var height = BinaryPrimitives.ReadUInt32BigEndian(blob.AsSpan(14));
      var width = BinaryPrimitives.ReadUInt32BigEndian(blob.AsSpan(18));
      var depth = BinaryPrimitives.ReadUInt16BigEndian(blob.AsSpan(22));
      var colorMode = BinaryPrimitives.ReadUInt16BigEndian(blob.AsSpan(24));

      var pos = 26;
      if (pos + 4 > blob.Length) { EmitMetadata(entries, width, height, channels, depth, colorMode); return entries; }
      var colorModeLen = (int)BinaryPrimitives.ReadUInt32BigEndian(blob.AsSpan(pos));
      pos += 4 + colorModeLen;

      // Image Resources section — same layout as PSD (uint32 length prefix, 8BIM blocks).
      if (pos + 4 > blob.Length) { EmitMetadata(entries, width, height, channels, depth, colorMode); return entries; }
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
          entries.Add(("thumbnail.jpg", "Tag", data[28..]));
        } else {
          var niceName = SanitizeName(blob, pos + 7, nameLen);
          entries.Add(($"image_resources/{resId:X4}_{niceName}.bin", "Tag", data));
        }

        pos = dataStart + 4 + dataSize + (dataSize % 2);
      }
      pos = resourcesEnd;

      // Layer & mask info: PSB uses a 64-bit length.
      if (pos + 8 <= blob.Length) {
        var layerLen = (long)BinaryPrimitives.ReadUInt64BigEndian(blob.AsSpan(pos));
        pos += 8;
        var layerEnd = Math.Min(pos + layerLen, blob.Length);
        if (layerEnd > pos) {
          var len = (int)(layerEnd - pos);
          entries.Add(("layer_and_mask.bin", "Tag", blob.AsSpan(pos, len).ToArray()));
          pos = (int)layerEnd;
        }
      }

      // Image data section: everything remaining to EOF.
      if (pos < blob.Length) {
        entries.Add(("image_data.bin", "Tag", blob.AsSpan(pos, blob.Length - pos).ToArray()));
      }

      EmitMetadata(entries, width, height, channels, depth, colorMode);
    } catch {
      // Keep FULL.psb only.
    }

    return entries;
  }

  private static void EmitMetadata(
    List<(string Name, string Kind, byte[] Data)> entries,
    uint width, uint height, ushort channels, ushort depth, ushort colorMode) {
    var ini = new StringBuilder();
    ini.AppendLine("; Photoshop Large Document (PSB) metadata");
    ini.AppendLine("version=psb");
    ini.Append("width=").AppendLine(width.ToString(CultureInfo.InvariantCulture));
    ini.Append("height=").AppendLine(height.ToString(CultureInfo.InvariantCulture));
    ini.Append("channels=").AppendLine(channels.ToString(CultureInfo.InvariantCulture));
    ini.Append("depth=").AppendLine(depth.ToString(CultureInfo.InvariantCulture));
    ini.Append("color_mode=").AppendLine(colorMode switch {
      0 => "Bitmap", 1 => "Grayscale", 2 => "Indexed", 3 => "RGB",
      4 => "CMYK", 7 => "Multichannel", 8 => "Duotone", 9 => "Lab",
      _ => colorMode.ToString(CultureInfo.InvariantCulture),
    });
    entries.Insert(1, ("metadata.ini", "Tag", Encoding.UTF8.GetBytes(ini.ToString())));
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
