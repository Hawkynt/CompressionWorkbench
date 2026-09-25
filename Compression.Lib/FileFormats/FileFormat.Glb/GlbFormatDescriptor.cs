#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using Compression.Registry;

namespace FileFormat.Glb;

/// <summary>
/// Binary glTF (<c>.glb</c>) container: 12-byte header + JSON chunk + optional
/// BIN chunk. Surfaces the JSON as <c>scene.gltf</c> (so any glTF viewer can
/// open it), the binary buffer as <c>binary.bin</c>, and — best-effort — any
/// embedded images referenced by the JSON's <c>images</c> section as
/// <c>images/&lt;name&gt;.&lt;ext&gt;</c>.
/// </summary>
public sealed class GlbFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveInMemoryExtract {
  /// <summary>
  /// Gets the id.
  /// </summary>
  public string Id => "Glb";
  /// <summary>
  /// Gets the display name.
  /// </summary>
  public string DisplayName => "GLB (binary glTF)";
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
  public string DefaultExtension => ".glb";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
  public IReadOnlyList<string> Extensions => [".glb", ".vrm"];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
  public IReadOnlyList<string> CompoundExtensions => [];
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new("glTF"u8.ToArray(), Confidence: 0.95),
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
  public string Description => "Binary glTF 3D scene; JSON + binary buffer + embedded images.";

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

  private readonly record struct SourceRange(int Offset, int Length);
  private readonly record struct BufferViewLayout(int Offset, int Length);
  private readonly record struct ImageLayout(int? BufferView, string MimeType, string? Name);
  private sealed record JsonLayout(
    IReadOnlyList<BufferViewLayout> BufferViews,
    IReadOnlyList<ImageLayout> Images);
  private sealed record EntryLayout(string Name, string Kind, SourceRange Range);

  private static IReadOnlyList<(string Name, string Kind, byte[] Data)> BuildEntries(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    var blob = ms.ToArray();
    return BuildLayout(blob)
      .Select(entry => (
        entry.Name,
        entry.Kind,
        blob.AsSpan(entry.Range.Offset, entry.Range.Length).ToArray()))
      .ToList();
  }

  List<ArchiveEntryInfo> IArchiveFormatOperations.ListSpan(ReadOnlySpan<byte> archive, string? password) =>
    BuildLayout(archive).Select((entry, index) => new ArchiveEntryInfo(
      Index: index, Name: entry.Name,
      OriginalSize: entry.Range.Length, CompressedSize: entry.Range.Length,
      Method: "stored", IsDirectory: false, IsEncrypted: false, LastModified: null,
      Kind: entry.Kind)).ToList();

  void IArchiveFormatOperations.ExtractSpan(
      ReadOnlySpan<byte> archive, string outputDir, string? password, string[]? files) {
    foreach (var entry in BuildLayout(archive)) {
      if (files is { Length: > 0 } && !FormatHelpers.MatchesFilter(entry.Name, files))
        continue;

      using var output = FormatHelpers.CreateEntryFile(outputDir, entry.Name);
      output.Write(archive.Slice(entry.Range.Offset, entry.Range.Length));
    }
  }

  private static List<EntryLayout> BuildLayout(ReadOnlySpan<byte> blob) {
    var entries = new List<EntryLayout> {
      new("FULL.glb", "Container", new SourceRange(0, blob.Length)),
    };
    if (blob.Length < 12) return entries;
    if (blob[0] != 'g' || blob[1] != 'l' || blob[2] != 'T' || blob[3] != 'F') return entries;

    var totalLength = BinaryPrimitives.ReadUInt32LittleEndian(blob[8..]);
    var end = (int)Math.Min((ulong)totalLength, (ulong)blob.Length);

    SourceRange? json = null;
    SourceRange? bin = null;
    var pos = 12;
    while (pos + 8 <= end) {
      var declaredLength = BinaryPrimitives.ReadUInt32LittleEndian(blob[pos..]);
      var bodyOffset = pos + 8;
      var available = Math.Min((long)declaredLength, end - (long)bodyOffset);
      if (available < 0)
        break;

      var bodyLength = checked((int)available);
      if (blob[pos + 4] == 'J' && blob[pos + 5] == 'S'
          && blob[pos + 6] == 'O' && blob[pos + 7] == 'N')
        json = TrimPadding(blob, bodyOffset, bodyLength, 0x20);
      else if (blob[pos + 4] == 'B' && blob[pos + 5] == 'I' && blob[pos + 6] == 'N')
        bin = TrimPadding(blob, bodyOffset, bodyLength, 0x00);

      var next = (long)bodyOffset + declaredLength;
      if (next > int.MaxValue)
        break;
      pos = (int)next;
    }

    if (json is { } jsonRange)
      entries.Add(new EntryLayout("scene.gltf", "Track", jsonRange));
    if (bin is { } binRange)
      entries.Add(new EntryLayout("binary.bin", "Track", binRange));

    if (json is not { } sourceJson || bin is not { } sourceBin)
      return entries;

    try {
      var layout = ParseJsonLayout(blob.Slice(sourceJson.Offset, sourceJson.Length));
      var imageIndex = 0;
      foreach (var image in layout.Images) {
        if (image.BufferView is not { } bufferViewIndex
            || bufferViewIndex < 0
            || bufferViewIndex >= layout.BufferViews.Count) {
          ++imageIndex;
          continue;
        }

        var bufferView = layout.BufferViews[bufferViewIndex];
        if (bufferView.Offset < 0 || bufferView.Length < 0
            || bufferView.Offset > sourceBin.Length - bufferView.Length) {
          ++imageIndex;
          continue;
        }

        var extension = image.MimeType switch {
          "image/png" => ".png",
          "image/jpeg" => ".jpg",
          "image/webp" => ".webp",
          _ => ".bin",
        };
        var name = image.Name is { Length: > 0 } value
          ? Sanitize(value)
          : $"image_{imageIndex:D3}";

        entries.Add(new EntryLayout(
          $"images/{name}{extension}",
          "Track",
          new SourceRange(sourceBin.Offset + bufferView.Offset, bufferView.Length)));
        ++imageIndex;
      }
    } catch {
      // Best effort: malformed JSON leaves the raw JSON/BIN entries available.
    }

    return entries;
  }

  private static SourceRange TrimPadding(
      ReadOnlySpan<byte> source, int offset, int length, byte padding) {
    var end = offset + length;
    while (end > offset && source[end - 1] == padding)
      --end;
    return new SourceRange(offset, end - offset);
  }

  private static JsonLayout ParseJsonLayout(ReadOnlySpan<byte> json) {
    var bufferViews = new List<BufferViewLayout>();
    var images = new List<ImageLayout>();
    var reader = new Utf8JsonReader(json);

    if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
      throw new JsonException("glTF JSON root must be an object.");

    while (reader.Read()) {
      if (reader.TokenType == JsonTokenType.EndObject)
        break;
      if (reader.TokenType != JsonTokenType.PropertyName)
        throw new JsonException("Expected a top-level glTF property.");

      var isBufferViews = reader.ValueTextEquals("bufferViews"u8);
      var isImages = reader.ValueTextEquals("images"u8);
      if (!reader.Read())
        throw new JsonException("Missing glTF property value.");

      if (isBufferViews)
        ReadBufferViews(ref reader, bufferViews);
      else if (isImages)
        ReadImages(ref reader, images);
      else
        SkipValue(ref reader);
    }

    return new JsonLayout(bufferViews, images);
  }

  private static void ReadBufferViews(
      ref Utf8JsonReader reader, List<BufferViewLayout> bufferViews) {
    if (reader.TokenType != JsonTokenType.StartArray) {
      SkipValue(ref reader);
      return;
    }

    while (reader.Read() && reader.TokenType != JsonTokenType.EndArray) {
      if (reader.TokenType != JsonTokenType.StartObject) {
        SkipValue(ref reader);
        continue;
      }

      var offset = 0;
      int? length = null;
      while (reader.Read() && reader.TokenType != JsonTokenType.EndObject) {
        if (reader.TokenType != JsonTokenType.PropertyName)
          throw new JsonException("Expected bufferView property.");

        var isOffset = reader.ValueTextEquals("byteOffset"u8);
        var isLength = reader.ValueTextEquals("byteLength"u8);
        if (!reader.Read())
          throw new JsonException("Missing bufferView property value.");

        if (isOffset && reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var parsedOffset))
          offset = parsedOffset;
        else if (isLength && reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var parsedLength))
          length = parsedLength;
        else
          SkipValue(ref reader);
      }

      bufferViews.Add(new BufferViewLayout(offset, length ?? -1));
    }
  }

  private static void ReadImages(ref Utf8JsonReader reader, List<ImageLayout> images) {
    if (reader.TokenType != JsonTokenType.StartArray) {
      SkipValue(ref reader);
      return;
    }

    while (reader.Read() && reader.TokenType != JsonTokenType.EndArray) {
      if (reader.TokenType != JsonTokenType.StartObject) {
        SkipValue(ref reader);
        continue;
      }

      int? bufferView = null;
      var mimeType = "";
      string? name = null;
      while (reader.Read() && reader.TokenType != JsonTokenType.EndObject) {
        if (reader.TokenType != JsonTokenType.PropertyName)
          throw new JsonException("Expected image property.");

        var isBufferView = reader.ValueTextEquals("bufferView"u8);
        var isMimeType = reader.ValueTextEquals("mimeType"u8);
        var isName = reader.ValueTextEquals("name"u8);
        if (!reader.Read())
          throw new JsonException("Missing image property value.");

        if (isBufferView && reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var parsedBufferView))
          bufferView = parsedBufferView;
        else if (isMimeType && reader.TokenType == JsonTokenType.String)
          mimeType = reader.GetString() ?? "";
        else if (isName && reader.TokenType == JsonTokenType.String)
          name = reader.GetString();
        else
          SkipValue(ref reader);
      }

      images.Add(new ImageLayout(bufferView, mimeType, name));
    }
  }

  private static void SkipValue(ref Utf8JsonReader reader) {
    if (reader.TokenType is not JsonTokenType.StartObject and not JsonTokenType.StartArray)
      return;

    var depth = 1;
    while (depth > 0 && reader.Read()) {
      if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
        ++depth;
      else if (reader.TokenType is JsonTokenType.EndObject or JsonTokenType.EndArray)
        --depth;
    }

    if (depth != 0)
      throw new JsonException("Incomplete JSON value.");
  }

  private static string Sanitize(string s) {
    var sb = new StringBuilder(Math.Min(s.Length, 40));
    foreach (var c in s) {
      if (sb.Length >= 40) break;
      if (char.IsLetterOrDigit(c) || c is '-' or '_') sb.Append(c);
      else if (sb.Length > 0 && sb[^1] != '_') sb.Append('_');
    }
    return sb.Length > 0 ? sb.ToString().Trim('_') : "image";
  }
}
