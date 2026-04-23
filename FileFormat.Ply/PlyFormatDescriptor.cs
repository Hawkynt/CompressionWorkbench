#pragma warning disable CS1591
using System.Globalization;
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Ply;

/// <summary>
/// Stanford PLY polygon file. Starts with <c>ply\n</c> (or <c>ply\r\n</c>), declares
/// <c>format ascii 1.0</c> / <c>format binary_little_endian 1.0</c> / <c>format
/// binary_big_endian 1.0</c>, then a series of <c>element &lt;name&gt; &lt;count&gt;</c>
/// blocks each with one or more <c>property &lt;type&gt; &lt;name&gt;</c> lines.
/// Header terminates with <c>end_header\n</c>; the body that follows is either ASCII
/// records or the declared binary layout.
/// </summary>
/// <remarks>
/// Reference: http://paulbourke.net/dataformats/ply/ — Paul Bourke's classic description.
/// </remarks>
public sealed class PlyFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveInMemoryExtract {
  /// <summary>Format identifier.</summary>
  public string Id => "Ply";
  /// <summary>Display name.</summary>
  public string DisplayName => "PLY (Stanford polygon)";
  /// <summary>Archive category.</summary>
  public FormatCategory Category => FormatCategory.Archive;
  /// <summary>Read-only archive capabilities.</summary>
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries;
  /// <summary>Default extension.</summary>
  public string DefaultExtension => ".ply";
  /// <summary>Known extensions.</summary>
  public IReadOnlyList<string> Extensions => [".ply"];
  /// <summary>No compound extensions.</summary>
  public IReadOnlyList<string> CompoundExtensions => [];
  /// <summary>Magic: <c>ply\n</c> at offset 0 (high confidence, unique).</summary>
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new([(byte)'p', (byte)'l', (byte)'y', (byte)'\n'], Offset: 0, Confidence: 0.97),
    new([(byte)'p', (byte)'l', (byte)'y', (byte)'\r'], Offset: 0, Confidence: 0.95),
  ];
  /// <summary>Stored only.</summary>
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  /// <summary>Not a tar compound format.</summary>
  public string? TarCompressionFormatId => null;
  /// <summary>Archive family.</summary>
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  /// <summary>Short description.</summary>
  public string Description => "Stanford PLY polygon file; surfaces header + body.";

  /// <inheritdoc />
  public List<ArchiveEntryInfo> List(Stream stream, string? password) =>
    BuildEntries(stream).Select((e, i) => new ArchiveEntryInfo(
      Index: i, Name: e.Name,
      OriginalSize: e.Data.Length, CompressedSize: e.Data.Length,
      Method: "stored", IsDirectory: false, IsEncrypted: false, LastModified: null,
      Kind: e.Kind)).ToList();

  /// <inheritdoc />
  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    foreach (var e in BuildEntries(stream)) {
      if (files != null && files.Length > 0 && !MatchesFilter(e.Name, files))
        continue;
      WriteFile(outputDir, e.Name, e.Data);
    }
  }

  /// <inheritdoc />
  public void ExtractEntry(Stream input, string entryName, Stream output, string? password) {
    foreach (var e in BuildEntries(input)) {
      if (e.Name.Equals(entryName, StringComparison.OrdinalIgnoreCase)) {
        output.Write(e.Data);
        return;
      }
    }
    throw new FileNotFoundException($"Entry not found: {entryName}");
  }

  private static List<(string Name, string Kind, byte[] Data)> BuildEntries(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    var blob = ms.ToArray();

    var entries = new List<(string Name, string Kind, byte[] Data)> {
      ("FULL.ply", "Track", blob),
    };

    // Find "end_header\n" — header is ASCII regardless of body format.
    var endHeaderBytes = Encoding.ASCII.GetBytes("end_header");
    var endHeaderPos = IndexOf(blob, endHeaderBytes);
    var bodyStart = -1;
    var headerBytes = Array.Empty<byte>();
    if (endHeaderPos >= 0) {
      // Advance past "end_header" and following newline (supporting \n or \r\n).
      var afterKeyword = endHeaderPos + endHeaderBytes.Length;
      while (afterKeyword < blob.Length && (blob[afterKeyword] == '\r' || blob[afterKeyword] == '\n')) afterKeyword++;
      bodyStart = afterKeyword;
      headerBytes = new byte[bodyStart];
      Array.Copy(blob, headerBytes, bodyStart);
    }

    var (format, elements) = ParseHeader(headerBytes);

    entries.Add(("header.txt", "Track", headerBytes));
    if (bodyStart >= 0 && bodyStart < blob.Length) {
      var body = new byte[blob.Length - bodyStart];
      Array.Copy(blob, bodyStart, body, 0, body.Length);
      entries.Add(("body.bin", "Track", body));
    }

    var meta = new StringBuilder();
    meta.AppendLine("; PLY metadata");
    meta.Append("format=").AppendLine(format ?? "");
    meta.Append("element_count=").AppendLine(elements.Count.ToString(CultureInfo.InvariantCulture));
    meta.Append("header_length=").AppendLine(headerBytes.Length.ToString(CultureInfo.InvariantCulture));
    meta.Append("body_length=").AppendLine((bodyStart < 0 ? 0 : blob.Length - bodyStart).ToString(CultureInfo.InvariantCulture));
    foreach (var el in elements) {
      meta.Append("element.").Append(el.Name).Append(".count=").AppendLine(el.Count.ToString(CultureInfo.InvariantCulture));
      meta.Append("element.").Append(el.Name).Append(".properties=").AppendLine(string.Join(',', el.Properties));
    }
    entries.Insert(1, ("metadata.ini", "Tag", Encoding.UTF8.GetBytes(meta.ToString())));

    return entries;
  }

  internal sealed record Element(string Name, int Count, List<string> Properties);

  private static (string? Format, List<Element> Elements) ParseHeader(byte[] headerBytes) {
    var text = Encoding.ASCII.GetString(headerBytes);
    var lines = text.Replace("\r\n", "\n").Split('\n');
    string? format = null;
    var elements = new List<Element>();
    Element? current = null;

    foreach (var line in lines) {
      var trimmed = line.Trim();
      if (trimmed.StartsWith("format ", StringComparison.Ordinal)) {
        format = trimmed[7..].Trim();
      } else if (trimmed.StartsWith("element ", StringComparison.Ordinal)) {
        if (current != null) elements.Add(current);
        var parts = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var count = 0;
        if (parts.Length >= 3) _ = int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out count);
        current = new Element(parts.Length >= 2 ? parts[1] : "", count, []);
      } else if (trimmed.StartsWith("property ", StringComparison.Ordinal) && current != null) {
        var parts = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2) current.Properties.Add(parts[^1]);
      }
    }
    if (current != null) elements.Add(current);
    return (format, elements);
  }

  private static int IndexOf(byte[] haystack, byte[] needle) {
    for (var i = 0; i <= haystack.Length - needle.Length; i++) {
      var match = true;
      for (var j = 0; j < needle.Length; j++) {
        if (haystack[i + j] != needle[j]) { match = false; break; }
      }
      if (match) return i;
    }
    return -1;
  }
}
