#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.BinaryII;

/// <summary>
/// Apple II Binary II / BLU archive (.bny/.bqy).
/// </summary>
/// <remarks>
/// Binary II is a deliberately simple record stream: every member is a 128-byte
/// metadata header followed by its payload rounded to a 128-byte boundary.
/// Version-1 headers preserve ProDOS/GS/OS metadata and mark Squeeze-compressed
/// payloads with data flag 0x80. Historical BLU extractors also infer Squeeze
/// from a .QQ member suffix, which this reader accepts for compatibility.
/// </remarks>
public sealed class BinaryIIFormatDescriptor :
  IFormatDescriptor,
  IArchiveFormatOperations,
  IArchiveCreatable,
  IArchiveModifiable,
  IArchiveDefragmentable,
  IArchiveLayoutMap {

  /// <summary>
  /// Gets the id.
  /// </summary>
public string Id => "BinaryII";
  /// <summary>
  /// Gets the display name.
  /// </summary>
public string DisplayName => "Apple II Binary II";
  /// <summary>
  /// Gets the category.
  /// </summary>
public FormatCategory Category => FormatCategory.Archive;
  /// <summary>
  /// Gets the capabilities.
  /// </summary>
public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList |
    FormatCapabilities.CanExtract |
    FormatCapabilities.CanCreate |
    FormatCapabilities.CanModify |
    FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries |
    FormatCapabilities.SupportsDirectories;
  /// <summary>
  /// Gets the default extension.
  /// </summary>
public string DefaultExtension => ".bny";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
public IReadOnlyList<string> Extensions => [".bny", ".bqy"];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
public IReadOnlyList<string> CompoundExtensions => [];
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
public IReadOnlyList<MagicSignature> MagicSignatures =>
    [new([0x0A, 0x47, 0x4C], Confidence: 0.99)];
  /// <summary>
  /// Gets the methods.
  /// </summary>
public IReadOnlyList<FormatMethodInfo> Methods => [
    new("stored", "Stored"),
    new("squeeze", "Squeeze"),
    new("auto", "Auto", true),
  ];
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
    "Apple II Binary II / BLU record archive with stored or Squeeze-compressed members and direct 128-byte-record mutation";

  /// <summary>
  /// Lists the entries in the supplied container.
  /// </summary>
public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    ArgumentNullException.ThrowIfNull(stream);
    var reader = new BinaryIIReader(stream);
    return reader.Entries.Select((entry, index) => new ArchiveEntryInfo(
      Index: index,
      Name: entry.Name,
      OriginalSize: entry.IsDirectory ? 0 : entry.IsCompressed ? -1 : entry.StoredLength,
      CompressedSize: entry.StoredLength,
      Method: entry.IsCompressed ? "Squeeze" : "Stored",
      IsDirectory: entry.IsDirectory,
      IsEncrypted: entry.IsEncrypted,
      LastModified: null,
      Kind: entry.IsPhantom ? "phantom" : null
    )).ToList();
  }

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    ArgumentNullException.ThrowIfNull(stream);
    ArgumentNullException.ThrowIfNull(outputDir);
    var reader = new BinaryIIReader(stream);
    foreach (var entry in reader.Entries) {
      if (files is not null && !MatchesFilter(entry.Name, files))
        continue;
      if (entry.IsDirectory) {
        CreateSafeDirectory(outputDir, entry.Name);
        continue;
      }
      WriteFile(outputDir, entry.Name, reader.Extract(entry));
    }
  }

  /// <summary>
  /// Performs the extract entry to memory operation.
  /// </summary>
public byte[] ExtractEntryToMemory(Stream archive, string entryName, string? password) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryName);
    var reader = new BinaryIIReader(archive);
    var entry = reader.Entries.FirstOrDefault(e =>
      string.Equals(e.Name, entryName, StringComparison.OrdinalIgnoreCase)
      || string.Equals(Path.GetFileName(e.Name), entryName, StringComparison.OrdinalIgnoreCase));
    if (entry is null)
      return [];
    return reader.Extract(entry);
  }

  /// <summary>
  /// Performs the create operation.
  /// </summary>
public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(inputs);
    ArgumentNullException.ThrowIfNull(options);
    if (!output.CanWrite)
      throw new ArgumentException("Output stream is not writable.", nameof(output));
    if (!string.IsNullOrEmpty(options.Password) || options.EncryptFilenames || !string.IsNullOrEmpty(options.EncryptionMethod))
      throw new NotSupportedException("Binary II does not define an interoperable encryption method.");

    var method = (options.MethodName ?? "stored").Trim().ToLowerInvariant();
    var mode = method switch {
      "" or "stored" or "store" => BinaryIICompressionMode.Stored,
      "squeeze" or "sq" => BinaryIICompressionMode.Squeeze,
      "auto" => BinaryIICompressionMode.Auto,
      _ => throw new NotSupportedException($"Binary II compression method '{options.MethodName}' is not supported."),
    };

    var bytes = BinaryIIWriter.Build(inputs, mode);
    if (output.CanSeek) {
      output.Position = 0;
      output.SetLength(0);
    }
    output.Write(bytes);
  }

  /// <summary>
  /// Adds new entries or replaces same-name entries by editing the existing
  /// 128-byte record stream in place. Existing payloads are not decoded/re-encoded.
  /// </summary>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs)
    => BinaryIIInPlaceModifier.Add(archive, inputs);

  /// <summary>
  /// Removes named entries (and descendants of named directory entries) by
  /// shifting the following record tail left and truncating the stream.
  /// </summary>
  public void Remove(Stream archive, string[] entryNames)
    => BinaryIIInPlaceModifier.Remove(archive, entryNames);

  /// <summary>
  /// Performs the defragment operation.
  /// </summary>
public void Defragment(Stream archive)
    => BinaryIIInPlaceModifier.Defragment(archive);

  /// <summary>
  /// Performs the defragment operation.
  /// </summary>
public void Defragment(Stream archive, DefragOptions options)
    => BinaryIIInPlaceModifier.Defragment(archive);

  /// <summary>
  /// Enumerates the layout.
  /// </summary>
public IEnumerable<DefragBlockInfo> EnumerateLayout(Stream archive) {
    ArgumentNullException.ThrowIfNull(archive);
    var reader = new BinaryIIReader(archive);
    foreach (var entry in reader.PhysicalRecords) {
      yield return new DefragBlockInfo(entry.HeaderOffset, BinaryIIConstants.HeaderSize, DefragBlockKind.MetadataReserved, entry.Name + " header");
      if (entry.StoredLength > 0)
        yield return new DefragBlockInfo(entry.DataOffset, entry.StoredLength, DefragBlockKind.Used, entry.Name);
      var padding = entry.PhysicalLength - BinaryIIConstants.HeaderSize - entry.StoredLength;
      if (padding > 0)
        yield return new DefragBlockInfo(entry.DataOffset + entry.StoredLength, padding, DefragBlockKind.Free, entry.Name + " padding");
    }
  }

  private static void CreateSafeDirectory(string baseDir, string entryName) {
    var parts = entryName.Replace('\\', '/')
      .Split('/', StringSplitOptions.RemoveEmptyEntries)
      .Where(p => p is not "." and not "..")
      .ToArray();
    if (parts.Length == 0)
      return;
    var all = new string[parts.Length + 1];
    all[0] = baseDir;
    Array.Copy(parts, 0, all, 1, parts.Length);
    Directory.CreateDirectory(Path.Combine(all));
  }
}
