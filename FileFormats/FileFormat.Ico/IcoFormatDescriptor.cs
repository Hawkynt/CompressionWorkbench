#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace CompressionWorkbench.FileFormat.Ico;

/// <summary>
/// Pseudo-archive descriptor for Windows ICO/CUR icon bundles. Each embedded image
/// (PNG or DIB) is exposed as its own archive entry; creating a bundle from PNG/BMP
/// inputs is supported.
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://en.wikipedia.org/wiki/ICO_(file_format)</c> — Wikipedia — documents the ICONDIR / ICONDIRENTRY directory and PNG/DIB payloads</description></item>
///   <item><description>"The evolution of the ICO file format" — Raymond Chen, The Old New Thing (Microsoft DevBlogs) series</description></item>
/// </list>
/// </summary>
public sealed class IcoFormatDescriptor :
  IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveModifiable, IArchiveDefragmentable, IArchiveWriteConstraints {

  /// <summary>
  /// Performs the defragment operation.
  /// </summary>
public void Defragment(Stream archive)
    => throw new NotSupportedException(
      "ICO is a Windows icon bundle (image bitmaps + DIB/PNG payloads) — defragmentation isn't meaningful.");
  /// <summary>
  /// Performs the defragment operation.
  /// </summary>
public void Defragment(Stream archive, DefragOptions options) => this.Defragment(archive);


  /// <summary>
  /// Gets the id.
  /// </summary>
public string Id => "Ico";
  /// <summary>
  /// Gets the display name.
  /// </summary>
public string DisplayName => "Windows ICO/CUR";
  /// <summary>
  /// Gets the category.
  /// </summary>
public FormatCategory Category => FormatCategory.Archive;
  /// <summary>
  /// Gets the capabilities.
  /// </summary>
public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract |
    FormatCapabilities.CanCreate | FormatCapabilities.CanModify | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries;
  /// <summary>
  /// Gets the default extension.
  /// </summary>
public string DefaultExtension => ".ico";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
public IReadOnlyList<string> Extensions => [".ico"];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
public IReadOnlyList<string> CompoundExtensions => [];
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
public IReadOnlyList<MagicSignature> MagicSignatures => [
    new([0x00, 0x00, 0x01, 0x00], Confidence: 0.85),
  ];
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
public string Description =>
    "Windows ICO/CUR icon bundle — pseudo-archive of one or more PNG/DIB images. " +
    "Reader reconstructs BITMAPFILEHEADER for DIB entries; writer accepts PNG and BMP inputs; " +
    "in-place R/W mutates the ICONDIR + ICONDIRENTRY table directly (existing payload bytes " +
    "are preserved, only their offsets shift).";

  /// <summary>
  /// Gets the max total archive size.
  /// </summary>
public long? MaxTotalArchiveSize => null;
  /// <summary>
  /// Gets the min total archive size.
  /// </summary>
public long? MinTotalArchiveSize => null;
  /// <summary>
  /// Gets the accepted inputs description.
  /// </summary>
public string AcceptedInputsDescription => "Accepts PNG and BMP image files; max 65535 images.";

  /// <summary>
  /// Performs the can accept operation.
  /// </summary>
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

  /// <summary>
  /// Lists the entries in the supplied container.
  /// </summary>
public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var bundle = ReadBundle(stream);
    return bundle.Entries.Select(e => new ArchiveEntryInfo(
      Index: e.Index, Name: e.Name,
      OriginalSize: e.Data.LongLength, CompressedSize: e.Data.LongLength,
      Method: e.IsPng ? "png" : "dib",
      IsDirectory: false, IsEncrypted: false, LastModified: null)).ToList();
  }

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var bundle = ReadBundle(stream);
    foreach (var e in bundle.Entries) {
      if (files != null && files.Length > 0 && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, e.Data);
    }
  }

  /// <summary>
  /// Performs the create operation.
  /// </summary>
public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    var images = inputs
      .Where(i => !i.IsDirectory)
      .Select(i => new IcoWriter.Image(i.ReadContent()))
      .ToList();
    if (images.Count == 0) throw new InvalidOperationException("ICO: no images to write.");
    var bytes = IcoWriter.BuildIco(images);
    output.Write(bytes);
  }

  // ── IArchiveModifiable ───────────────────────────────────────────────────

  /// <summary>
  /// Appends one or more new images (PNG or BMP file paths) into the existing
  /// ICO bundle in place. Routed through <see cref="IcoInPlaceModifier.AddImage"/>.
  /// </summary>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(inputs);
    foreach (var input in inputs) {
      if (input.IsDirectory) continue;
      var image = input.ReadContent();
      IcoInPlaceModifier.AddImage(archive, image);
    }
  }

  /// <summary>
  /// Removes the named entries (matching the reader's computed display name,
  /// e.g. <c>icon_00_32x32x32.png</c>) from the bundle in place. Routed through
  /// <see cref="IcoInPlaceModifier.RemoveImage"/>.
  /// </summary>
  public void Remove(Stream archive, string[] entryNames) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryNames);
    foreach (var name in entryNames)
      IcoInPlaceModifier.RemoveImage(archive, name);
  }

  private static IcoReader.Bundle ReadBundle(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    return IcoReader.Read(ms.GetBuffer().AsSpan(0, (int)ms.Length));
  }
}
