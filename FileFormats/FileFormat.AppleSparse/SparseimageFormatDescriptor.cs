#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.AppleSparse;

/// <summary>
/// Apple <c>sparseimage</c> — a single-file expanding disk image produced by
/// <c>hdiutil create -type SPARSE</c> and used by Time Machine, FileVault and
/// HDIUTIL workflows. The on-disk format is a 4096-byte <c>sprs</c> header
/// plus a Band Allocation Table (BAT) mapping virtual bands (typically 1 MB
/// each) to physical bands stored sequentially in the file; unallocated
/// virtual bands read as zeros.
/// </summary>
/// <remarks>
/// <para>R/W descriptor: <c>Create</c> emits a fresh sparseimage, and
/// <c>Add</c>/<c>Remove</c> mutate an existing image in place at the band
/// surface — see <see cref="SparseimageInPlaceModifier"/> for the byte-level
/// semantic. Inner-FS delegation (HFS+, APFS, FAT, etc.) is performed via
/// <see cref="InnerFsDetector"/> so listing/extracting a sparseimage that
/// wraps a known volume returns the inner file tree rather than the raw
/// <c>disk.img</c> blob.</para>
/// <para><b>R/W honest scope.</b> The descriptor exposes synthetic
/// <c>band-NNNN.bin</c> entries for <c>Add</c>/<c>Remove</c>. Writing to an
/// existing band rewrites its physical payload in place at the fixed offset
/// derived from the BAT; writing to an unallocated band appends a fresh
/// physical slot at end-of-stream. The 4 096-byte header preamble, every
/// other BAT entry, and every other allocated band's payload stay
/// byte-identical at their original byte offsets. Inner HFS+/APFS/FAT
/// directory mutation is delegated to the matching filesystem descriptors
/// and is out of scope for the band-rewrite surface. Sparsebundle (the
/// directory-layout sibling produced by <c>hdiutil create -type
/// SPARSEBUNDLE</c>) is handled by
/// <see cref="SparsebundleFormatDescriptor"/> and is a separate descriptor
/// — only the single-file <c>.sparseimage</c> form is mutated here.</para>
/// </remarks>
public sealed class SparseimageFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveModifiable {

  public string Id => "Sparseimage";
  public string DisplayName => "Apple Sparseimage";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract |
    FormatCapabilities.CanTest | FormatCapabilities.CanCreate |
    FormatCapabilities.CanModify |
    FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".sparseimage";
  public IReadOnlyList<string> Extensions => [".sparseimage"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures =>
    [new("sprs"u8.ToArray(), Offset: 0, Confidence: 0.95)];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Apple sparseimage (hdiutil expanding disk image)";

  // ── IArchiveFormatOperations ──────────────────────────────────────

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    ArgumentNullException.ThrowIfNull(stream);
    if (SparseimageStream.TryOpen(stream) is { } vStream) {
      using (vStream) {
        var inner = InnerFsDetector.Detect(vStream);
        if (inner is IArchiveFormatOperations ops) {
          try {
            vStream.Position = 0;
            return ops.List(vStream, password);
          } catch {
            // fall through to raw listing
          }
        }
      }
    }

    // Fallback: surface the raw disk as a single entry
    if (stream.CanSeek) stream.Position = 0;
    try {
      using var r = new SparseimageReader(stream, leaveOpen: true);
      return [new ArchiveEntryInfo(0, "disk.img", r.VirtualSize, stream.Length, "Stored", false, false, null)];
    } catch {
      return [];
    }
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    ArgumentNullException.ThrowIfNull(stream);
    if (SparseimageStream.TryOpen(stream) is { } vStream) {
      using (vStream) {
        var inner = InnerFsDetector.Detect(vStream);
        if (inner is IArchiveFormatOperations ops) {
          try {
            vStream.Position = 0;
            ops.Extract(vStream, outputDir, password, files);
            return;
          } catch {
            // fall through to raw extraction
          }
        }
      }
    }

    if (stream.CanSeek) stream.Position = 0;
    using var r = new SparseimageReader(stream, leaveOpen: true);
    if (files != null && !MatchesFilter("disk.img", files))
      return;
    WriteFile(outputDir, "disk.img", r.ExtractDisk());
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(inputs);
    // WORM: the first file becomes the virtual disk image. With zero inputs an
    // empty sparseimage with a 0-band BAT is produced.
    byte[] diskData = [];
    foreach (var i in inputs) {
      if (i.IsDirectory) continue;
      diskData = i.ReadContent();
      break;
    }
    var w = new SparseimageWriter();
    w.SetDiskData(diskData);
    output.Write(w.Build());
  }

  // ── IArchiveModifiable ──────────────────────────────────────────────

  /// <summary>
  /// Rewrites bands in place. Inputs whose <c>ArchiveName</c> matches
  /// <c>band-NNNN.bin</c> and carry exactly <c>band_size</c> bytes are
  /// written at the fixed physical offset derived from the BAT (in-place
  /// rewrite when the band is already allocated, fresh EOF slot otherwise);
  /// everything outside the touched band's payload window and its 4-byte
  /// BAT entry stays byte-identical.
  ///
  /// <para>Inputs not matching the synthetic band schema are silently
  /// skipped — inner HFS+/APFS/FAT directory mutation is delegated to the
  /// matching filesystem descriptors and is out of scope for the
  /// band-rewrite modifier.</para>
  /// </summary>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(inputs);
    SparseimageInPlaceModifier.AddOrReplaceBands(archive,
      inputs.Where(i => !i.IsDirectory).Select(i => (i.ArchiveName, i.ReadContent())));
  }

  /// <summary>
  /// Zeros the physical payload of each named band and clears its BAT
  /// entry. The physical slot stays in place as a zero-filled hole so
  /// other bands' offsets don't shift; that matches the reader's existing
  /// semantic of returning zero bytes for BAT entries that are 0.
  /// </summary>
  public void Remove(Stream archive, string[] entryNames) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryNames);
    SparseimageInPlaceModifier.RemoveBands(archive, entryNames);
  }
}
