#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace CompressionWorkbench.FileFormat.Ico;

/// <summary>
/// Pseudo-archive descriptor for Windows ICO/CUR icon bundles. Each embedded image
/// (PNG or DIB) is exposed as its own archive entry; creating a bundle from PNG/BMP
/// inputs is supported.
/// </summary>
public sealed class IcoFormatDescriptor :
  IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveWriteConstraints {

  public string Id => "Ico";
  public string DisplayName => "Windows ICO/CUR";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract |
    FormatCapabilities.CanCreate | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".ico";
  public IReadOnlyList<string> Extensions => [".ico"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new([0x00, 0x00, 0x01, 0x00], Confidence: 0.85),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description =>
    "Windows ICO/CUR icon bundle — pseudo-archive of one or more PNG/DIB images " +
    "(reader reconstructs BITMAPFILEHEADER for DIB entries; writer accepts PNG and BMP inputs).";

  public long? MaxTotalArchiveSize => null;
  public long? MinTotalArchiveSize => null;
  public string AcceptedInputsDescription => "Accepts PNG and BMP image files; max 65535 images.";

  public bool CanAccept(ArchiveInputInfo input, out string? reason) {
    if (input.IsDirectory) { reason = "Directories not supported in ICO bundles."; return false; }
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
    if (images.Count == 0) throw new InvalidOperationException("ICO: no images to write.");
    var bytes = IcoWriter.BuildIco(images);
    output.Write(bytes);
  }

  private static IcoReader.Bundle ReadBundle(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    return IcoReader.Read(ms.GetBuffer().AsSpan(0, (int)ms.Length));
  }
}
