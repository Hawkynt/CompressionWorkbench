#pragma warning disable CS1591
using Compression.Registry;
using Compression.Registry.Streaming;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Cab;

/// <summary>
/// Microsoft Cabinet (CAB) archive — CFHEADER/CFFOLDER/CFFILE structures with MSZIP/Quantum/LZX-compressed folders.
///
/// References:
/// <list type="bullet">
///   <item><description>[MS-CAB]: Cabinet File Format — Microsoft Open Specifications</description></item>
///   <item><description><c>https://www.cabextract.org.uk/libmspack/</c> — libmspack — maintained open-source CAB implementation</description></item>
///   <item><description><c>https://en.wikipedia.org/wiki/Cabinet_(file_format)</c> — format overview</description></item>
/// </list>
/// </summary>
public sealed class CabFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveModifiable, IArchiveDefragmentable, IArchiveLayoutMap {
  /// <inheritdoc />
  /// <summary>
  /// Enumerates the layout.
  /// </summary>
public IEnumerable<DefragBlockInfo> EnumerateLayout(Stream archive) => CabLayoutMap.Enumerate(archive);

  /// <summary>Rebuild-based defrag: extracts then re-creates the CAB in listing order.</summary>
  public void Defragment(Stream archive)
    => this.Defragment(archive, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });

  /// <summary>Rebuild-based defrag: extracts then re-creates the CAB per the requested mode.</summary>
  public void Defragment(Stream archive, DefragOptions options) {
    DefragRebuilder.Rebuild(archive, options,
      readEntries: stream => {
        var r = new CabReader(stream);
        return r.Entries.Select(e => (e.FileName, r.ExtractEntry(e)));
      },
      buildImage: files => {
        using var ms = new MemoryStream();
        var w = new CabWriter(CabCompressionType.MsZip);
        foreach (var (n, d) in files) w.AddFile(n, d);
        w.WriteTo(ms);
        return ms.ToArray();
      });
  }

  /// <summary>
  /// Gets the id.
  /// </summary>
public string Id => "Cab";
  /// <summary>
  /// Gets the display name.
  /// </summary>
public string DisplayName => "CAB";
  /// <summary>
  /// Gets the category.
  /// </summary>
public FormatCategory Category => FormatCategory.Archive;
  // R/W: a mutable archive. Add/Replace/Remove go through the verified extract -> edit ->
  // re-create rebuild (default IArchiveModifiable); CAB folders are solid-compressed, so
  // the data blocks are rewritten — moving data is acceptable for a read-write archive.
  // See FormatCapabilities.cs (WORM vs R/W).
  /// <summary>
  /// Gets the capabilities.
  /// </summary>
public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanModify | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries;
  /// <summary>
  /// Gets the default extension.
  /// </summary>
public string DefaultExtension => ".cab";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
public IReadOnlyList<string> Extensions => [".cab"];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
public IReadOnlyList<string> CompoundExtensions => [];
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
public IReadOnlyList<MagicSignature> MagicSignatures => [new([(byte)'M', (byte)'S', (byte)'C', (byte)'F'], Confidence: 0.95)];
  /// <summary>
  /// Gets the methods.
  /// </summary>
public IReadOnlyList<FormatMethodInfo> Methods => [
    new("mszip", "MS-ZIP"), new("lzx", "LZX"), new("quantum", "Quantum"), new("none", "Store")
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
public string Description => "Microsoft Cabinet archive, used in Windows installers";

  /// <summary>
  /// Lists the entries in the supplied container.
  /// </summary>
public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new CabReader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(i, e.FileName, e.UncompressedSize, -1,
      "CAB", false, false, e.LastModified)).ToList();
  }

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new CabReader(stream);
    foreach (var e in r.Entries) {
      if (files != null && !MatchesFilter(e.FileName, files)) continue;
      WriteFile(outputDir, e.FileName, r.ExtractEntry(e));
    }
  }

  /// <summary>
  /// Opens a single CAB entry as a bounded read-only <see cref="Stream"/>.
  /// The reader's per-entry extractor returns the fully-decompressed bytes;
  /// they are wrapped in a <see cref="BoundedEntryStream"/> sized to the
  /// entry's uncompressed size.
  /// </summary>
  public Stream OpenEntry(Stream archive, string entryName, string? password) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryName);
    if (archive.CanSeek) archive.Position = 0;
    var r = new CabReader(archive, leaveOpen: true);
    foreach (var e in r.Entries) {
      if (!string.Equals(e.FileName, entryName, StringComparison.OrdinalIgnoreCase)) continue;
      var bytes = r.ExtractEntry(e);
      return new BoundedEntryStream(new MemoryStream(bytes, writable: false),
        bytes.Length, leaveOpen: false);
    }
    return new BoundedEntryStream(new MemoryStream(System.Array.Empty<byte>(), writable: false),
      0, leaveOpen: false);
  }

  /// <summary>Native in-memory single-entry extraction.</summary>
  public byte[] ExtractEntryToMemory(Stream archive, string entryName, string? password) {
    using var s = this.OpenEntry(archive, entryName, password);
    using var ms = new MemoryStream();
    s.CopyTo(ms);
    return ms.ToArray();
  }

  /// <summary>
  /// Performs the create operation.
  /// </summary>
public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    var compType = options.MethodName switch {
      "lzx" => CabCompressionType.Lzx,
      "quantum" => CabCompressionType.Quantum,
      "none" or "store" => CabCompressionType.None,
      _ => CabCompressionType.MsZip,
    };
    var lzxWindow = options.Level.HasValue ? Math.Clamp(options.Level.Value, 15, 21) : 15;
    var quantumWindow = options.Level.HasValue ? Math.Clamp(options.Level.Value, 10, 21) : 15;
    var w = new CabWriter(compType, lzxWindowBits: lzxWindow, quantumWindowBits: quantumWindow);
    foreach (var (name, data) in FormatHelpers.FilesOnly(inputs))
      w.AddFile(name, data);
    w.WriteTo(output);
  }
}
