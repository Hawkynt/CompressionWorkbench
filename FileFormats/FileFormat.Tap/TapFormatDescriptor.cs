#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Tap;

/// <summary>
/// ZX Spectrum TAP tape image — length-prefixed blocks as written by the ROM SAVE routine.
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://sinclair.wiki.zxnet.co.uk/wiki/TAP_format</c> — Sinclair wiki — TAP format description</description></item>
///   <item><description>World of Spectrum "File format reference" — long-standing community documentation</description></item>
/// </list>
/// </summary>
public sealed class TapFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveModifiable, IArchiveDefragmentable, IArchiveLayoutMap, IFilesystemBlockMover {
  /// <summary>
  /// Gets the id.
  /// </summary>
public string Id => "Tap";
  /// <summary>
  /// Gets the display name.
  /// </summary>
public string DisplayName => "TAP";
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
public string DefaultExtension => ".tap";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
public IReadOnlyList<string> Extensions => [".tap"];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
public IReadOnlyList<string> CompoundExtensions => [];
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
public IReadOnlyList<MagicSignature> MagicSignatures => [];
  /// <summary>
  /// Gets the methods.
  /// </summary>
public IReadOnlyList<FormatMethodInfo> Methods => [new("tap", "TAP")];
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
public string Description => "ZX Spectrum tape image";

  /// <summary>
  /// Lists the entries in the supplied container.
  /// </summary>
public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new TapReader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(i, e.Name, e.Size, e.Size,
      "Stored", false, false, null)).ToList();
  }

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new TapReader(stream);
    foreach (var e in r.Entries) {
      if (files != null && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, r.Extract(e));
    }
  }

  /// <summary>
  /// Performs the create operation.
  /// </summary>
public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    using var w = new TapWriter(output, leaveOpen: true);
    foreach (var (name, data) in FlatFiles(inputs))
      w.AddFile(name, data);
  }

  // ── IArchiveModifiable (in-place) ─────────────────────────────────────

  /// <summary>
  /// Adds (or replaces by name) files inside an existing TAP tape image.
  /// Uses <see cref="TapModifier"/> for in-place append at EOF (Add) and
  /// byte-shift removal (Remove) — O(touched bytes) for Add, O(tail size)
  /// for Remove.
  /// </summary>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    foreach (var (name, data) in FilesOnly(inputs)) {
      TapModifier.RemoveFile(archive, name);
      TapModifier.AddFile(archive, name, data);
    }
  }

  /// <summary>
  /// Removes named entries from an existing TAP tape image using
  /// <see cref="TapModifier"/> — walks the block chain, shifts trailing bytes.
  /// </summary>
  public void Remove(Stream archive, string[] entryNames) {
    foreach (var name in entryNames)
      TapModifier.RemoveFile(archive, name);
  }

  // ── IFilesystemBlockMover delegation ───────────────────────────────────

  /// <inheritdoc />
  /// <summary>
  /// Performs the move extent operation.
  /// </summary>
public void MoveExtent(Stream image, long srcOffset, long dstOffset, long length, bool zeroSource = false)
    => new TapBlockMover().MoveExtent(image, srcOffset, dstOffset, length, zeroSource);

  /// <inheritdoc />
  /// <summary>
  /// Performs the update allocation after move operation.
  /// </summary>
public void UpdateAllocationAfterMove(Stream image, string fileName, long oldOffset, long newOffset, long length)
    => new TapBlockMover().UpdateAllocationAfterMove(image, fileName, oldOffset, newOffset, length);

  // ── IArchiveDefragmentable ───────────────────────────────────────────

  /// <summary>
  /// Performs the defragment operation.
  /// </summary>
public void Defragment(Stream archive)
    => Defragment(archive, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });

  /// <summary>
  /// Defragments a TAP image via rebuild (TAP is sequential with no directory).
  /// </summary>
  public void Defragment(Stream archive, DefragOptions options)
    => DefragRebuilder.Rebuild(archive, options, ReadEntries, BuildImage);

  // ── IArchiveLayoutMap ────────────────────────────────────────────────

  /// <summary>
  /// Enumerates the byte layout of a TAP tape image. Each file occupies two
  /// blocks: a 19-byte header block (flag + type + name + params + checksum,
  /// preceded by a 2-byte length word) and a variable-size data block
  /// (flag + payload + checksum, preceded by a 2-byte length word). Header
  /// blocks are reported as MetadataReserved; data blocks as Used.
  /// </summary>
  public IEnumerable<DefragBlockInfo> EnumerateLayout(Stream archive) {
    archive.Position = 0;
    using var ms = new MemoryStream();
    archive.CopyTo(ms);
    var data = ms.ToArray();
    var pos = 0;
    string? pendingName = null;

    while (pos + 2 <= data.Length) {
      var blockLength = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(pos));
      if (blockLength == 0 || pos + 2 + blockLength > data.Length)
        break;

      var flag = data[pos + 2];

      if (flag == 0x00 && blockLength == 19) {
        // Header block: 2 (length word) + 19 (block body) = 21 bytes total
        pendingName = System.Text.Encoding.ASCII.GetString(data, pos + 2 + 2, 10).TrimEnd(' ');
        yield return new DefragBlockInfo(pos, 2 + blockLength, DefragBlockKind.MetadataReserved,
          $"Header: {pendingName}");
      } else if (flag == 0xFF) {
        // Data block
        var name = pendingName ?? $"BLOCK_{pos}";
        pendingName = null;
        yield return new DefragBlockInfo(pos, 2 + blockLength, DefragBlockKind.Used, name);
      } else {
        // Unknown block
        yield return new DefragBlockInfo(pos, 2 + blockLength, DefragBlockKind.MetadataReserved,
          $"Unknown block @{pos}");
        pendingName = null;
      }

      pos += 2 + blockLength;
    }

    if (pos < data.Length)
      yield return new DefragBlockInfo(pos, data.Length - pos, DefragBlockKind.Free);
  }

  // ── Shared delegates ─────────────────────────────────────────────────

  private static IEnumerable<(string Name, byte[] Data)> ReadEntries(Stream stream) {
    var r = new TapReader(stream);
    return r.Entries.Select(e => (e.Name, r.Extract(e)));
  }

  private static byte[] BuildImage(IReadOnlyList<(string Name, byte[] Data)> files) {
    using var ms = new MemoryStream();
    var w = new TapWriter(ms, leaveOpen: true);
    foreach (var (n, d) in files)
      w.AddFile(n, d);
    w.Finish();
    return ms.ToArray();
  }
}
