#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Mhk;

/// <summary>
/// Cyan / Broderbund Mohawk (MHWK) resource archive used by Myst, Riven and Living Books titles.
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://github.com/scummvm/scummvm</c> — ScummVM — the Mohawk engine is the de-facto reference implementation</description></item>
///   <item><description><c>https://wiki.scummvm.org</c> — ScummVM wiki — Mohawk engine and archive documentation</description></item>
/// </list>
/// </summary>
public sealed class MhkFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveDefragmentable, IArchiveLayoutMap {

  /// <inheritdoc />
  /// <summary>
  /// Enumerates the layout.
  /// </summary>
  public IEnumerable<DefragBlockInfo> EnumerateLayout(Stream archive) {
    archive.Position = 0;
    var r = new MhkReader(archive);
    foreach (var e in r.Entries) {
      if (e.Size > 0)
        yield return new DefragBlockInfo(e.Offset, e.Size, DefragBlockKind.Used, FileName: e.DisplayName);
    }
  }

  /// <summary>
  /// Gets the id.
  /// </summary>
  public string Id => "Mhk";
  /// <summary>
  /// Gets the display name.
  /// </summary>
  public string DisplayName => "Cyan Mohawk";
  /// <summary>
  /// Gets the category.
  /// </summary>
  public FormatCategory Category => FormatCategory.Archive;
  /// <summary>
  /// Gets the capabilities.
  /// </summary>
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
  /// <summary>
  /// Gets the default extension.
  /// </summary>
  public string DefaultExtension => ".mhk";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
  public IReadOnlyList<string> Extensions => [".mhk"];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
  public IReadOnlyList<string> CompoundExtensions => [];
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new("MHWK"u8.ToArray(), Confidence: 0.95)
  ];
  /// <summary>
  /// Gets the methods.
  /// </summary>
  public IReadOnlyList<FormatMethodInfo> Methods => [new("mhk", "Mohawk")];
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
  public string Description => "Cyan Mohawk archive (Myst / Riven / Cosmic Osmo / Living Books)";

  /// <summary>
  /// Lists the entries in the supplied container.
  /// </summary>
  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new MhkReader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(
      i, e.DisplayName, e.Size, e.Size, "Stored", false, false, null)).ToList();
  }

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new MhkReader(stream);
    foreach (var e in r.Entries) {
      // Surface entries as flat files named by display key + .bin so multi-tag archives don't collide.
      var fileName = e.DisplayName + ".bin";
      if (files != null && !MatchesFilter(fileName, files)) continue;
      WriteFile(outputDir, fileName, r.Extract(e));
    }
  }

  /// <summary>
  /// Opens a single Mohawk entry as a bounded read-only stream. Entry names
  /// follow the flat <c>DisplayName + ".bin"</c> convention used by Extract.
  /// The decoded bytes are wrapped in a
  /// <see cref="Compression.Registry.Streaming.BoundedEntryStream"/> sized to
  /// the entry's logical length.
  /// </summary>
  public Stream OpenEntry(Stream archive, string entryName, string? password) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryName);
    if (archive.CanSeek) archive.Position = 0;
    var r = new MhkReader(archive);
    foreach (var e in r.Entries) {
      var fileName = e.DisplayName + ".bin";
      if (!string.Equals(fileName, entryName, StringComparison.OrdinalIgnoreCase)) continue;
      var bytes = r.Extract(e);
      return new Compression.Registry.Streaming.BoundedEntryStream(
        new MemoryStream(bytes, writable: false), bytes.Length, leaveOpen: false);
    }
    return new Compression.Registry.Streaming.BoundedEntryStream(
      new MemoryStream(System.Array.Empty<byte>(), writable: false), 0, leaveOpen: false);
  }

  /// <summary>Native in-memory single-entry extraction routed through the bounded <see cref="OpenEntry"/>.</summary>
  public byte[] ExtractEntryToMemory(Stream archive, string entryName, string? password) {
    using var s = this.OpenEntry(archive, entryName, password);
    using var memoryStream = new MemoryStream();
    s.CopyTo(memoryStream);
    return memoryStream.ToArray();
  }

  /// <summary>
  /// Performs the create operation.
  /// </summary>
  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    using var w = new MhkWriter(output, leaveOpen: true);
    var autoId = (ushort)1000;
    foreach (var (name, data) in FormatHelpers.FlatFiles(inputs)) {
      var (type, id, resName) = SplitInputName(name, ref autoId);
      w.AddEntry(type, id, resName, data);
    }
  }

  /// <summary>
  /// Performs the defragment operation.
  /// </summary>
  public void Defragment(Stream archive)
    => this.Defragment(archive, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });

  /// <summary>
  /// Performs the defragment operation.
  /// </summary>
  public void Defragment(Stream archive, DefragOptions options) {
    // Capture (type, id, name) tuples up-front so the rebuild preserves the full
    // resource identity. Pass through DefragRebuilder using DisplayName as the
    // sortable key and stash the side-channel info in a parallel dictionary.
    var meta = new Dictionary<string, (string Type, ushort Id, string? Name)>(StringComparer.Ordinal);

    DefragRebuilder.Rebuild(archive, options,
      readEntries: stream => {
        var r = new MhkReader(stream);
        var list = new List<(string, byte[])>(r.Entries.Count);
        foreach (var e in r.Entries) {
          meta[e.DisplayName] = (e.Type, e.Id, e.Name);
          list.Add((e.DisplayName, r.Extract(e)));
        }
        return list;
      },
      buildImage: files => {
        using var ms = new MemoryStream();
        using (var w = new MhkWriter(ms, leaveOpen: true)) {
          foreach (var (display, data) in files) {
            if (meta.TryGetValue(display, out var info))
              w.AddEntry(info.Type, info.Id, info.Name, data);
          }
        }
        return ms.ToArray();
      });
  }

  // Convention for round-tripping flat input files: map filenames of the form
  // "TYPE_id_name.ext" or "TYPE_id.ext" back to (type, id, name). When a file's
  // stem doesn't fit the convention we fall back to a "tDAT" type with an auto-incrementing id
  // so callers can still create archives from arbitrary inputs.
  private static (string Type, ushort Id, string? Name) SplitInputName(string filename, ref ushort autoId) {
    var stem = Path.GetFileNameWithoutExtension(filename);
    var firstUnderscore = stem.IndexOf('_');
    if (firstUnderscore == MhkConstants.TypeTagSize) {
      var type = stem[..MhkConstants.TypeTagSize];
      if (IsAscii(type)) {
        var rest = stem[(firstUnderscore + 1)..];
        var secondUnderscore = rest.IndexOf('_');
        var idText = secondUnderscore < 0 ? rest : rest[..secondUnderscore];
        if (ushort.TryParse(idText, out var id)) {
          var resName = secondUnderscore < 0 ? null : rest[(secondUnderscore + 1)..];
          return (type, id, string.IsNullOrEmpty(resName) ? null : resName);
        }
      }
    }
    return ("tDAT", autoId++, null);
  }

  private static bool IsAscii(string s) {
    foreach (var ch in s) {
      if (ch > 0x7F)
        return false;
    }
    return true;
  }
}
