#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace CompressionWorkbench.FileFormat.Ico;

/// <summary>
/// Pseudo-archive descriptor for Windows CUR cursor bundles. Same on-disk layout as
/// ICO with the type field set to 2 — directory-entry planes/bitcount fields encode
/// hotspot X/Y instead of plane count and bit depth.
/// </summary>
public sealed class CurFormatDescriptor :
  IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveWriteConstraints {

  public string Id => "Cur";
  public string DisplayName => "Windows CUR cursor";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract |
    FormatCapabilities.CanCreate | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".cur";
  public IReadOnlyList<string> Extensions => [".cur"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new([0x00, 0x00, 0x02, 0x00], Confidence: 0.85),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description =>
    "Windows CUR cursor bundle — pseudo-archive of one or more PNG/DIB images " +
    "with hotspot fields. Hotspots default to (0,0) when creating from raw images.";

  public long? MaxTotalArchiveSize => null;
  public long? MinTotalArchiveSize => null;
  public string AcceptedInputsDescription => "Accepts PNG and BMP image files; max 65535 cursors.";

  public bool CanAccept(ArchiveInputInfo input, out string? reason) {
    if (input.IsDirectory) { reason = "Directories not supported in CUR bundles."; return false; }
    var ext = Path.GetExtension(input.ArchiveName).ToLowerInvariant();
    if (ext is not (".png" or ".bmp" or ".dib")) {
      reason = $"Unsupported input extension '{ext}' (need .png/.bmp/.dib).";
      return false;
    }
    reason = null;
    return true;
  }

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var bundle = ReadBundle(stream);
    return bundle.Entries.Select(e => new ArchiveEntryInfo(
      Index: e.Index, Name: e.Name,
      OriginalSize: e.Data.LongLength, CompressedSize: e.Data.LongLength,
      Method: e.IsPng ? "png" : "dib",
      IsDirectory: false, IsEncrypted: false, LastModified: null)).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var bundle = ReadBundle(stream);
    foreach (var e in bundle.Entries) {
      if (files != null && files.Length > 0 && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, e.Data);
    }
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    var images = inputs
      .Where(i => !i.IsDirectory)
      .Select(i => new IcoWriter.Image(File.ReadAllBytes(i.FullPath)))
      .ToList();
    if (images.Count == 0) throw new InvalidOperationException("CUR: no images to write.");
    var bytes = IcoWriter.BuildCur(images);
    output.Write(bytes);
  }

  private static IcoReader.Bundle ReadBundle(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    return IcoReader.Read(ms.GetBuffer().AsSpan(0, (int)ms.Length));
  }
}
