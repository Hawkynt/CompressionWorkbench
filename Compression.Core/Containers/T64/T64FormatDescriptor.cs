#pragma warning disable CS1591
using System.Buffers;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.T64;

/// <summary>
/// Commodore 64 T64 tape container — directory of memory-load records.
///
/// References:
/// <list type="bullet">
///   <item><description>Peter Schepers, "C64 File Formats: T64" — the classic reference document</description></item>
///   <item><description><c>https://vice-emu.sourceforge.io/</c> — VICE emulator and T64 documentation</description></item>
/// </list>
/// </summary>
public sealed class T64FormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable,
  IArchiveModifiable, IArchiveDefragmentable, IArchivePurgeable, IArchiveLayoutMap, IFilesystemBlockMover {

  private const int HeaderSize = 64;
  private const int EntrySize = 32;

  /// <summary>
  /// Gets the id.
  /// </summary>
  public string Id => "T64";
  /// <summary>
  /// Gets the display name.
  /// </summary>
  public string DisplayName => "T64";
  /// <summary>
  /// Gets the category.
  /// </summary>
  public FormatCategory Category => FormatCategory.Archive;
  /// <summary>
  /// Gets the capabilities.
  /// </summary>
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanModify | FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
  /// <summary>
  /// Gets the default extension.
  /// </summary>
  public string DefaultExtension => ".t64";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
  public IReadOnlyList<string> Extensions => [".t64"];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
  public IReadOnlyList<string> CompoundExtensions => [];
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
  public IReadOnlyList<MagicSignature> MagicSignatures =>
    [new("C64"u8.ToArray(), Confidence: 0.70)];
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
  public string Description => "Commodore 64 tape container";

  /// <summary>
  /// Lists the entries in the supplied container.
  /// </summary>
  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    using var reader = new T64Reader(stream);
    return reader.Entries.Select((e, i) => new ArchiveEntryInfo(
      i, e.Name, e.Size, e.Size, "Stored", false, false, null
    )).ToList();
  }

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    using var reader = new T64Reader(stream);
    foreach (var entry in reader.Entries) {
      if (files != null && !MatchesFilter(entry.Name, files)) continue;
      WriteFile(outputDir, entry.Name, reader.Extract(entry));
    }
  }

  /// <summary>
  /// Performs the create operation.
  /// </summary>
  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    var writer = new T64Writer();
    foreach (var (name, data) in FlatFiles(inputs))
      writer.AddFile(name.Length > 16 ? name[..16] : name, data);
    output.Write(writer.Build());
  }

  // ── IArchiveModifiable (in-place) ─────────────────────────────────────

  /// <summary>
  /// Adds or replaces files inside an existing T64 image without rebuilding it.
  /// </summary>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(inputs);
    foreach (var (name, data) in FilesOnly(inputs))
      T64InPlaceModifier.AddFile(archive, name.Length > 16 ? name[..16] : name, data);
  }

  /// <summary>
  /// Removes named entries, wipes their payload bytes and compacts the image.
  /// </summary>
  public void Remove(Stream archive, string[] entryNames) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryNames);
    foreach (var name in entryNames)
      T64InPlaceModifier.RemoveFile(archive, name);
  }

  // ── IFilesystemBlockMover delegation ───────────────────────────────────

  /// <inheritdoc />
  public void MoveExtent(Stream image, long srcOffset, long dstOffset, long length, bool zeroSource = false)
    => new T64BlockMover().MoveExtent(image, srcOffset, dstOffset, length, zeroSource);

  /// <inheritdoc />
  public void UpdateAllocationAfterMove(Stream image, string fileName, long oldOffset, long newOffset, long length)
    => new T64BlockMover().UpdateAllocationAfterMove(image, fileName, oldOffset, newOffset, length);

  // ── IArchiveDefragmentable ───────────────────────────────────────────

  /// <summary>
  /// Packs live payloads immediately after the directory with native byte moves.
  /// </summary>
  public void Defragment(Stream archive)
    => Defragment(archive, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });

  /// <summary>
  /// Defragments a T64 image. The canonical consolidate-at-start path moves
  /// normal payloads in place and patches only their absolute data-offset fields.
  /// Other layout modes retain the established rebuild behaviour, but preserve
  /// T64 version, tape name, load address and Commodore file type.
  /// </summary>
  public void Defragment(Stream archive, DefragOptions options) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(options);
    EnsureMutable(archive);
    options.CancellationToken.ThrowIfCancellationRequested();

    if (options.Mode != DefragMode.ConsolidateAtStart) {
      DefragmentByRebuild(archive, options);
      return;
    }

    archive.Position = 0;
    using var reader = new T64Reader(archive);
    if (reader.Entries.Any(static e => e.EntryType != 1))
      throw new InvalidDataException("T64: moving defrag only supports normal directory records (entry type 1).");

    var entries = reader.Entries.OrderBy(static e => e.DataOffset).ToArray();
    var imageSize = archive.Length;
    var cursor = (long)HeaderSize + reader.DirectoryEntryCount * EntrySize;
    var mover = new T64BlockMover();

    options.OnProgress?.Invoke(new DefragProgressEvent(
      Phase: "scanning", Fraction: 0, CurrentReadOffset: 0, CurrentWriteOffset: -1,
      ImageSize: imageSize, BlockMap: EnumerateLayout(archive).ToList(), Status: "Analysing T64 layout"));

    for (var i = 0; i < entries.Length; i++) {
      options.CancellationToken.ThrowIfCancellationRequested();
      var entry = entries[i];
      if (entry.DataOffset != cursor) {
        if (entry.Size > 0)
          mover.MoveExtent(archive, entry.DataOffset, cursor, entry.Size, zeroSource: true);
        mover.UpdateAllocationAfterMove(archive, entry.Name, entry.DataOffset, cursor, entry.Size);
      }
      cursor += entry.Size;

      options.OnProgress?.Invoke(new DefragProgressEvent(
        Phase: "moving", Fraction: entries.Length == 0 ? 1 : (double)(i + 1) / entries.Length,
        CurrentReadOffset: entry.DataOffset, CurrentWriteOffset: cursor,
        ImageSize: imageSize, BlockMap: null, Status: $"Packed {entry.Name}"));
    }

    archive.SetLength(cursor);
    archive.Position = 0;
    options.OnProgress?.Invoke(new DefragProgressEvent(
      Phase: "complete", Fraction: 1, CurrentReadOffset: -1, CurrentWriteOffset: -1,
      ImageSize: archive.Length, BlockMap: EnumerateLayout(archive).ToList(), Status: "Defragmentation complete"));
  }

  // ── IArchivePurgeable ────────────────────────────────────────────────

  /// <summary>
  /// Wipes all directory/payload bytes and leaves the canonical 64-byte empty T64 header.
  /// </summary>
  public void Purge(Stream archive) {
    ArgumentNullException.ThrowIfNull(archive);
    EnsureMutable(archive);

    archive.Position = 0;
    using (var reader = new T64Reader(archive)) { }

    if (archive.Length > HeaderSize)
      ZeroRange(archive, HeaderSize, archive.Length - HeaderSize);

    Span<byte> counts = stackalloc byte[4];
    archive.Position = 34;
    archive.Write(counts);
    archive.SetLength(HeaderSize);
    archive.Position = 0;
  }

  // ── IArchiveLayoutMap / IWipeEmpty ──────────────────────────────────

  /// <summary>
  /// Enumerates every byte of a normal-record T64 image: the fixed header, live
  /// and free directory slots, live payloads, and proven dead gaps/tail bytes.
  /// Unknown record kinds fail closed by exposing no free-space map at all.
  /// </summary>
  public IEnumerable<DefragBlockInfo> EnumerateLayout(Stream archive) {
    ArgumentNullException.ThrowIfNull(archive);
    archive.Position = 0;
    using var reader = new T64Reader(archive);
    if (reader.Entries.Any(static e => e.EntryType != 1))
      yield break;

    var liveBySlot = reader.Entries.ToDictionary(static e => e.DirectoryIndex);

    yield return new DefragBlockInfo(0, HeaderSize, DefragBlockKind.MetadataReserved, "T64 Header");

    for (var i = 0; i < reader.DirectoryEntryCount; i++) {
      var offset = HeaderSize + (long)i * EntrySize;
      if (liveBySlot.TryGetValue(i, out var entry))
        yield return new DefragBlockInfo(offset, EntrySize, DefragBlockKind.MetadataReserved, $"Directory: {entry.Name}");
      else
        yield return new DefragBlockInfo(offset, EntrySize, DefragBlockKind.Free);
    }

    var cursor = (long)HeaderSize + reader.DirectoryEntryCount * EntrySize;
    foreach (var entry in reader.Entries.OrderBy(static e => e.DataOffset)) {
      if (entry.DataOffset > cursor)
        yield return new DefragBlockInfo(cursor, entry.DataOffset - cursor, DefragBlockKind.Free);

      if (entry.Size > 0)
        yield return new DefragBlockInfo(entry.DataOffset, entry.Size, DefragBlockKind.Used, entry.Name);

      cursor = Math.Max(cursor, entry.DataOffset + entry.Size);
    }

    if (cursor < archive.Length)
      yield return new DefragBlockInfo(cursor, archive.Length - cursor, DefragBlockKind.Free);
  }

  private static void DefragmentByRebuild(Stream archive, DefragOptions options) {
    archive.Position = 0;
    using var reader = new T64Reader(archive);
    if (reader.Entries.Any(static e => e.EntryType != 1))
      throw new InvalidDataException("T64: rebuild defrag only supports normal directory records (entry type 1).");

    var tapeName = reader.TapeName;
    var version = reader.Version;
    var metadata = reader.Entries.Select(e => new SnapshotEntry(
      e.Name, e.StartAddress, e.FileType, reader.Extract(e))).ToList();

    DefragRebuilder.Rebuild(archive, options, ReadEntries, files => {
      var remaining = metadata.ToList();
      var writer = new T64Writer();
      foreach (var (name, data) in files) {
        var index = remaining.FindIndex(e =>
          e.Name.Equals(name, StringComparison.Ordinal) && e.Data.AsSpan().SequenceEqual(data));
        if (index < 0) {
          writer.AddFile(name, data);
          continue;
        }

        var source = remaining[index];
        remaining.RemoveAt(index);
        writer.AddFile(name, source.StartAddress, source.FileType, data);
      }
      return writer.Build(tapeName, version);
    });
  }

  private static IEnumerable<(string Name, byte[] Data)> ReadEntries(Stream stream) {
    using var reader = new T64Reader(stream);
    return reader.Entries.Select(e => (e.Name, reader.Extract(e))).ToArray();
  }

  private static void EnsureMutable(Stream archive) {
    if (!archive.CanRead || !archive.CanWrite || !archive.CanSeek)
      throw new ArgumentException("T64: stream must be readable, writable and seekable.", nameof(archive));
  }

  private static void ZeroRange(Stream stream, long offset, long length) {
    var buffer = ArrayPool<byte>.Shared.Rent((int)Math.Min(length, 64 * 1024));
    try {
      Array.Clear(buffer, 0, buffer.Length);
      stream.Position = offset;
      while (length > 0) {
        var chunk = (int)Math.Min(length, buffer.Length);
        stream.Write(buffer, 0, chunk);
        length -= chunk;
      }
    } finally {
      ArrayPool<byte>.Shared.Return(buffer);
    }
  }

  private sealed record SnapshotEntry(string Name, ushort StartAddress, byte FileType, byte[] Data);
}
