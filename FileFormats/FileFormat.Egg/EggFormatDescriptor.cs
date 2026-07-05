#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Egg;

/// <summary>
/// EGG (ALZip) archive — ESTsoft's native container for newer ALZip versions,
/// with Unicode filenames, per-file algorithm selection (Store/Deflate/Bzip2/AZO/LZMA),
/// solid and split (multi-volume) compression, and optional AES encryption.
///
/// Read-only: this descriptor lists entries and extracts Store and Deflate blocks.
/// Entries compressed with AZO/LZMA (and, currently, Bzip2), and encrypted or
/// split-volume archives, are listed honestly but raise <see cref="NotSupportedException"/>
/// on extraction rather than returning wrong bytes.
///
/// References:
/// <list type="bullet">
///   <item><description><c>http://justsolve.archiveteam.org/wiki/EGG_(ALZip)</c> — format-wiki entry (points to the official spec)</description></item>
///   <item><description><c>EGG Format Specification, Version 1.0</c> (ESTsoft Corp., 2009–2011) — the published byte-layout document this reader is built from</description></item>
///   <item><description><c>https://en.wikipedia.org/wiki/ALZip</c> — application background</description></item>
/// </list>
/// No official public spec is served today; the layout was taken from ESTsoft's
/// published specification document, and the reader is verified by struct-parity
/// unit tests against hand-crafted spec-conformant buffers only (no local EGG oracle).
/// </summary>
public sealed class EggFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {

  public string Id => "Egg";
  public string DisplayName => "EGG";
  public FormatCategory Category => FormatCategory.Archive;
  // Read-only: no CanCreate / CanModify. Extraction covers Store + Deflate;
  // other methods and encrypted/split archives are listed but not extractable.
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries | FormatCapabilities.SupportsDirectories;
  public string DefaultExtension => ".egg";
  public IReadOnlyList<string> Extensions => [".egg"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [new([(byte)'E', (byte)'G', (byte)'G', (byte)'A'], Confidence: 0.95)];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("store", "Store"), new("deflate", "Deflate")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "ESTsoft ALZip EGG archive (read-only)";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    using var r = new EggReader(stream, leaveOpen: true);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(i, e.Name, e.UncompressedSize, e.CompressedSize,
      e.MethodName, e.IsDirectory, e.IsEncrypted, e.LastModified)).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    using var r = new EggReader(stream, leaveOpen: true);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) { Directory.CreateDirectory(Path.Combine(outputDir, e.Name)); continue; }
      if (files != null && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, r.Extract(e));
    }
  }

  /// <summary>
  /// Opens a single entry as a bounded read-only stream. The reader materialises the
  /// entry's decoded bytes; the result is wrapped in a
  /// <see cref="Compression.Registry.Streaming.BoundedEntryStream"/> sized to its logical length.
  /// </summary>
  public Stream OpenEntry(Stream archive, string entryName, string? password) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryName);
    if (archive.CanSeek) archive.Position = 0;
    using var r = new EggReader(archive, leaveOpen: true);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (!string.Equals(e.Name, entryName, StringComparison.OrdinalIgnoreCase)) continue;
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
}
