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
  /// <summary>
  /// Gets the id.
  /// </summary>
  public string Id => "Psd";
  /// <summary>
  /// Gets the display name.
  /// </summary>
  public string DisplayName => "Photoshop document";
  /// <summary>
  /// Gets the category.
  /// </summary>
  public FormatCategory Category => FormatCategory.Image;
  /// <summary>
  /// Gets the capabilities.
  /// </summary>
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries;
  /// <summary>
  /// Gets the default extension.
  /// </summary>
  public string DefaultExtension => ".psd";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
  public IReadOnlyList<string> Extensions => [".psd", ".psb"];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
  public IReadOnlyList<string> CompoundExtensions => [];
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new("8BPS"u8.ToArray(), Confidence: 0.95),
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
  public string Description =>
    "Adobe Photoshop document; surfaces thumbnail + resources + metadata + layer summary. " +
    "Layer pixel data not decoded.";

  /// <summary>
  /// Lists the entries in the supplied container.
  /// </summary>
  public List<ArchiveEntryInfo> List(Stream stream, string? password) =>
    BuildEntries(stream).Select((e, i) => new ArchiveEntryInfo(
      Index: i, Name: e.Name,
      OriginalSize: e.Data.Length, CompressedSize: e.Data.Length,
      Method: "stored", IsDirectory: false, IsEncrypted: false, LastModified: null,
      Kind: e.Kind)).ToList();

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    foreach (var e in BuildEntries(stream)) {
      if (files != null && files.Length > 0 && !FormatHelpers.MatchesFilter(e.Name, files))
        continue;
      FormatHelpers.WriteFile(outputDir, e.Name, e.Data);
    }
  }

  /// <summary>
  /// Performs the extract entry operation.
  /// </summary>
  public void ExtractEntry(Stream input, string entryName, Stream output, string? password) {
    foreach (var e in BuildEntries(input)) {
      if (e.Name.Equals(entryName, StringComparison.OrdinalIgnoreCase)) {
        output.Write(e.Data);
        return;
      }
    }
    throw new FileNotFoundException($"Entry not found: {entryName}");
  }

  private sealed record EntryLayout(string Name, string Kind, int Offset, int Length, byte[]? Generated = null) {
    public int Size => this.Generated?.Length ?? this.Length;
  }

  private static IReadOnlyList<(string Name, string Kind, byte[] Data)> BuildEntries(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    var blob = ms.ToArray();
    return BuildLayout(blob)
      .Select(e => (e.Name, e.Kind, e.Generated ?? blob.AsSpan(e.Offset, e.Length).ToArray()))
      .ToList();
  }

  List<ArchiveEntryInfo> IArchiveFormatOperations.ListSpan(ReadOnlySpan<byte> archive, string? password) =>
    BuildLayout(archive).Select((e, i) => new ArchiveEntryInfo(
      Index: i, Name: e.Name,
      OriginalSize: e.Size, CompressedSize: e.Size,
      Method: "stored", IsDirectory: false, IsEncrypted: false, LastModified: null,
      Kind: e.Kind)).ToList();

  void IArchiveFormatOperations.ExtractSpan(
      ReadOnlySpan<byte> archive, string outputDir, string? password, string[]? files) {
    foreach (var entry in BuildLayout(archive)) {
      if (files is { Length: > 0 } && !FormatHelpers.MatchesFilter(entry.Name, files))
        continue;
      if (entry.Generated is { } generated)
        FormatHelpers.WriteFile(outputDir, entry.Name, generated);
      else {
        using var output = FormatHelpers.CreateEntryFile(outputDir, entry.Name);
        output.Write(archive.Slice(entry.Offset, entry.Length));
      }
    }
  }

  private static List<EntryLayout> BuildLayout(ReadOnlySpan<byte> blob) {
    var entries = new List<EntryLayout> {
      new("FULL.psd", "Container", 0, blob.Length),
    };

    if (blob.Length < 26) return entries;
    if (blob[0] != '8' || blob[1] != 'B' || blob[2] != 'P' || blob[3] != 'S') return entries;

    var version = BinaryPrimitives.ReadUInt16BigEndian(blob[4..]);
    var channels = BinaryPrimitives.ReadUInt16BigEndian(blob[12..]);
    var height = BinaryPrimitives.ReadUInt32BigEndian(blob[14..]);
    var width = BinaryPrimitives.ReadUInt32BigEndian(blob[18..]);
    var depth = BinaryPrimitives.ReadUInt16BigEndian(blob[22..]);
    var colorMode = BinaryPrimitives.ReadUInt16BigEndian(blob[24..]);

    var pos = 26;
    if (pos + 4 > blob.Length) return entries;
    var colorModeLen = (int)BinaryPrimitives.ReadUInt32BigEndian(blob[pos..]);
    if (colorModeLen < 0 || colorModeLen > blob.Length - pos - 4) return entries;
    pos += 4 + colorModeLen;

    if (pos + 4 > blob.Length) return entries;
    var resourcesLen = (int)BinaryPrimitives.ReadUInt32BigEndian(blob[pos..]);
    pos += 4;
    if (resourcesLen < 0) return entries;
    var resourcesEnd = Math.Min((long)pos + resourcesLen, blob.Length);

    while ((long)pos + 12 <= resourcesEnd) {
      if (blob[pos] != '8' || blob[pos + 1] != 'B' || blob[pos + 2] != 'I' || blob[pos + 3] != 'M') break;
      var resId = BinaryPrimitives.ReadUInt16BigEndian(blob[(pos + 4)..]);
      var nameLen = blob[pos + 6];
      var namePad = (nameLen + 1) % 2 == 0 ? 0 : 1;
      var dataStart = pos + 6 + 1 + nameLen + namePad;
      if ((long)dataStart + 4 > resourcesEnd) break;
      var dataSize = (int)BinaryPrimitives.ReadUInt32BigEndian(blob[dataStart..]);
      if (dataSize < 0 || (long)dataStart + 4 + dataSize > resourcesEnd) break;

      var dataOffset = dataStart + 4;
      if (resId == 0x040C && dataSize > 28)
        entries.Add(new EntryLayout("thumbnail.jpg", "Tag", dataOffset + 28, dataSize - 28));
      else
        entries.Add(new EntryLayout(
          $"resources/{resId:X4}_{SanitizeName(blob, pos + 7, nameLen)}.bin",
          "Tag", dataOffset, dataSize));

      pos = dataStart + 4 + dataSize + (dataSize % 2);
    }

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
    entries.Insert(1, new EntryLayout("metadata.ini", "Tag", 0, 0, Encoding.UTF8.GetBytes(ini.ToString())));

    return entries;
  }

  private static string SanitizeName(ReadOnlySpan<byte> blob, int offset, byte len) {
    if (len == 0) return "unnamed";
    var sb = new StringBuilder(len);
    for (var i = 0; i < len && offset + i < blob.Length; ++i) {
      var c = (char)blob[offset + i];
      sb.Append(char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_');
    }
    return sb.Length > 0 ? sb.ToString() : "unnamed";
  }
}
