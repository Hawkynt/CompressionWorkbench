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
  public string Id => "Tap";
  public string DisplayName => "TAP";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanModify | FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".tap";
  public IReadOnlyList<string> Extensions => [".tap"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("tap", "TAP")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "ZX Spectrum tape image";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new TapReader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(i, e.Name, e.Size, e.Size,
      "Stored", false, false, null)).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new TapReader(stream);
    foreach (var e in r.Entries) {
      if (files != null && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, r.Extract(e));
    }
  }

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
  public void MoveExtent(Stream image, long srcOffset, long dstOffset, long length, bool zeroSource = false)
    => new TapBlockMover().MoveExtent(image, srcOffset, dstOffset, length, zeroSource);

  /// <inheritdoc />
  public void UpdateAllocationAfterMove(Stream image, string fileName, long oldOffset, long newOffset, long length)
    => new TapBlockMover().UpdateAllocationAfterMove(image, fileName, oldOffset, newOffset, length);

  // ── IArchiveDefragmentable ───────────────────────────────────────────

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
    var walkAborted = false;

    while (pos + 2 <= data.Length) {
      var blockLength = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(pos));
      if (blockLength == 0 || pos + 2 + blockLength > data.Length) {
        walkAborted = true;
        break;
      }

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

    if (pos >= data.Length)
      yield break;

    // A tail the block walk gave up on is undecoded, not proven empty: a length
    // word this reader rejects can still be followed by blocks a real Spectrum
    // loader reads. The map's contract says an unproven region is reserved, and
    // saying Free here handed the generic wipe live tape blocks to zero. Only a
    // remainder too short to hold a length word is genuinely spare.
    yield return walkAborted
      ? new DefragBlockInfo(pos, data.Length - pos, DefragBlockKind.MetadataReserved,
          $"Undecoded tail @{pos}")
      : new DefragBlockInfo(pos, data.Length - pos, DefragBlockKind.Free);
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
