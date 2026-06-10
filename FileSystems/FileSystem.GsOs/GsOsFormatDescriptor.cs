#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.GsOs;

/// <summary>
/// Descriptor for Apple IIgs GS/OS 2IMG disk images. The 2IMG container
/// wraps a ProDOS / HFS / DOS 3.3 volume with a 64-byte header — this
/// descriptor parses the header, surfaces the inner volume, and (for
/// ProDOS-ordered payloads) lets callers add/replace/remove files inside
/// the embedded volume by delegating to
/// <see cref="FileSystem.ProDos.ProDosModifier"/>, which already shifts
/// every block access past the 2IMG header.
/// <para>
/// Detection is by the .gsdos extension; the "2IMG" magic at offset 0 is
/// owned by <c>FileSystem.ProDos</c> (.2mg routing) to avoid a detector
/// first-match conflict.
/// </para>
/// </summary>
public sealed class GsOsFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveModifiable {
  public string Id => "GsOs";
  public string DisplayName => "Apple IIgs GS/OS (2IMG)";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract |
    FormatCapabilities.CanCreate | FormatCapabilities.CanModify |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
  // .2mg is owned by FileSystem.ProDos; we register the GS/OS-specific
  // .gsdos extension only to avoid extension routing conflicts.
  public string DefaultExtension => ".gsdos";
  public IReadOnlyList<string> Extensions => [".gsdos"];
  public IReadOnlyList<string> CompoundExtensions => [];
  // Magic intentionally omitted: ProDos already advertises "2IMG"@0, and
  // we don't want detector first-match to fight over the same bytes.
  // Routing to GS/OS is by extension; the reader still parses the 2IMG header.
  public IReadOnlyList<MagicSignature> MagicSignatures => [];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description =>
    "Apple IIgs GS/OS 2IMG — 64-byte 2IMG header + ProDOS-ordered payload (HFS/DOS-3.3 payloads listed read-only).";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    using var r = new GsOsReader(stream);
    // Walk the inner ProDOS volume so callers see the real per-file
    // entries instead of one opaque blob.
    var entries = new List<ArchiveEntryInfo>();
    if (TryListInnerProDos(stream, entries))
      return entries;
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(
      i, e.Name, e.Size, e.Size, "Stored", e.IsDirectory, false, null)).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    if (TryExtractInnerProDos(stream, outputDir, files))
      return;
    using var r = new GsOsReader(stream);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (files != null && files.Length > 0 && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, r.Extract(e));
    }
  }

  /// <summary>
  /// Creates a fresh 2IMG-wrapped ProDOS image from the given inputs.
  /// </summary>
  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(inputs);
    var w = new GsOsWriter();
    foreach (var (name, data) in FlatFiles(inputs))
      w.AddFile(name, data);
    var image = w.Build();
    output.Write(image);
  }

  /// <summary>
  /// Adds — or replaces by name — files inside the inner ProDOS volume.
  /// The 2IMG header bytes 0..63 stay byte-identical; only the inner
  /// ProDOS catalog + bitmap + data blocks are touched.
  /// </summary>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(inputs);
    foreach (var (name, data) in FlatFiles(inputs))
      GsOsInPlaceModifier.AddFile(archive, name, data);
  }

  /// <summary>
  /// Removes the named entries from the inner ProDOS volume.
  /// </summary>
  public void Remove(Stream archive, string[] entryNames) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryNames);
    foreach (var name in entryNames)
      GsOsInPlaceModifier.RemoveFile(archive, name);
  }

  // ── Inner-ProDOS walk helpers ─────────────────────────────────────────────
  //
  // The legacy GsOsReader surfaces the inner payload as one opaque blob; for
  // ProDOS-ordered images we can do better by handing the payload to the
  // real ProDOS reader. When the inner format is HFS or DOS-3.3 we fall back
  // to the legacy opaque surface (no inner reader available here).

  private static bool TryListInnerProDos(Stream stream, List<ArchiveEntryInfo> entries) {
    if (!IsProDosOrdered2Img(stream)) return false;
    try {
      stream.Position = 0;
      var pr = new FileSystem.ProDos.ProDosReader(stream);
      var i = 0;
      foreach (var e in pr.Entries) {
        entries.Add(new ArchiveEntryInfo(
          i++, e.FullPath, e.Size, e.Size, "Stored", e.IsDirectory, false, null));
      }
      return true;
    } catch {
      return false;
    }
  }

  private static bool TryExtractInnerProDos(Stream stream, string outputDir, string[]? files) {
    if (!IsProDosOrdered2Img(stream)) return false;
    try {
      stream.Position = 0;
      using var pr = new FileSystem.ProDos.ProDosReader(stream);
      foreach (var e in pr.Entries) {
        if (e.IsDirectory) continue;
        if (files != null && files.Length > 0 && !MatchesFilter(e.FullPath, files)) continue;
        WriteFile(outputDir, e.FullPath, pr.Extract(e));
      }
      return true;
    } catch {
      return false;
    }
  }

  private static bool IsProDosOrdered2Img(Stream stream) {
    if (stream.Length < 64) return false;
    var origPos = stream.Position;
    try {
      stream.Position = 0;
      Span<byte> header = stackalloc byte[16];
      var read = 0;
      while (read < header.Length) {
        var n = stream.Read(header[read..]);
        if (n == 0) return false;
        read += n;
      }
      if (header[0] != (byte)'2' || header[1] != (byte)'I' || header[2] != (byte)'M' || header[3] != (byte)'G')
        return false;
      var imageFormat = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(header[12..16]);
      return imageFormat == 1;
    } finally {
      stream.Position = origPos;
    }
  }
}
