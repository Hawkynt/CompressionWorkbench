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
/// Read-only descriptor with WORM <c>Create</c> support for our synthetic
/// single-header form. Inner-FS delegation (HFS+, APFS, FAT, etc.) is
/// performed via <see cref="InnerFsDetector"/> so listing/extracting a
/// sparseimage that wraps a known volume returns the inner file tree rather
/// than the raw <c>disk.img</c> blob.
/// </remarks>
public sealed class SparseimageFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable {

  public string Id => "Sparseimage";
  public string DisplayName => "Apple Sparseimage";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract |
    FormatCapabilities.CanTest | FormatCapabilities.CanCreate |
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
}
