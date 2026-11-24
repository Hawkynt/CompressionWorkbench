#pragma warning disable CS1591
using System.Text;
using Compression.Registry;
using Compression.Registry.Streaming;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Uif;

/// <summary>
/// MagicISO UIF (Universal Image Format) disc image. The <c>bbis</c> signature sits in the trailer, not at
/// the start, so the image is found by its extension. An ISO image lists <c>metadata.ini</c> and
/// <c>image.iso</c>; a mixed-mode image lists its raw image (.bin/.mdf/.img/.nrg) and the original
/// descriptor (.cue/.mds/.ccd+.sub) under the names MagicISO stored. Password-protected images need the
/// password. Creation writes a version-1 ISO image, optionally password-protected (WORM: no in-place edit).
/// </summary>
public sealed class UifFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable {
  private const string ImageInput = "image.iso";

  public string Id => "Uif";
  public string DisplayName => "MagicISO UIF";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities => FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.CanCreate | FormatCapabilities.SupportsMultipleEntries | FormatCapabilities.SupportsPassword;
  public string DefaultExtension => ".uif";
  public IReadOnlyList<string> Extensions => [".uif"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("zlib", "zlib-compressed sector runs")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "MagicISO UIF disc image (bbis trailer, blhr block table, zlib sector runs, optional DES password)";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    try {
      var image = UifImage.Read(Seekable(stream), password);
      var ini = Encoding.UTF8.GetBytes(image.RenderMetadata());
      List<ArchiveEntryInfo> entries = [new(0, "metadata.ini", ini.Length, ini.Length, "stored", false, false, null)];
      foreach (var part in image.Parts)
        entries.Add(new(entries.Count, part.Name, part.Size, part.Kind == UifImage.PartKind.Image ? stream.Length : part.Size,
          part.Kind == UifImage.PartKind.Image ? "zlib" : "stored", false, image.IsEncrypted, null));
      return entries;
    } catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or ArchivePasswordException) {
      var length = stream.CanSeek ? stream.Length : 0;
      return [new(0, "FULL.uif", length, length, "stored", false, ex is ArchivePasswordException, null)];
    }
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var source = Seekable(stream);
    var image = UifImage.Read(source, password);
    if (image.NeedsPassword) throw new ArchivePasswordException("UIF: this image is protected by a password.", passwordWasSupplied: false);
    if (files is not { Length: > 0 } || MatchesFilter("metadata.ini", files))
      WriteFile(outputDir, "metadata.ini", Encoding.UTF8.GetBytes(image.RenderMetadata()));
    foreach (var part in image.Parts) {
      if (files is { Length: > 0 } && !MatchesFilter(part.Name, files)) continue;
      if (part.Kind != UifImage.PartKind.Image) {
        WriteFile(outputDir, part.Name, image.PartBytes(part));
        continue;
      }
      Directory.CreateDirectory(outputDir);
      var target = Path.Combine(outputDir, part.Name);
      try {
        using var output = new FileStream(target, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1 << 16);
        image.CopyTo(source, output);
      } catch {
        File.Delete(target);
        throw;
      }
    }
  }

  /// <summary>Writes the one image file among the inputs (metadata.ini is skipped) as a version-1 ISO UIF.</summary>
  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(inputs);
    ArgumentNullException.ThrowIfNull(options);
    var input = PickImage(inputs.Where(i => !i.IsDirectory).Select(i => (i.ArchiveName, i)).ToList());
    using var image = input.InMemoryContent is { } bytes ? new MemoryStream(bytes, writable: false) : (Stream)File.OpenRead(input.FullPath);
    UifWriter.Write(output, image, image.Length, NonEmpty(options.Password));
  }

  public void CreateFromStreams(Stream target, IEnumerable<StreamingArchiveInput> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(target);
    ArgumentNullException.ThrowIfNull(inputs);
    ArgumentNullException.ThrowIfNull(options);
    var input = PickImage(inputs.Where(i => !i.IsDirectory).Select(i => (i.Name, i)).ToList());
    using var image = input.OpenStream();
    UifWriter.Write(target, image, input.Size, NonEmpty(options.Password));
  }

  private static string? NonEmpty(string? password) => string.IsNullOrEmpty(password) ? null : password;

  /// <summary>One image file: <c>image.iso</c> when present, else the single non-metadata input.</summary>
  private static T PickImage<T>(List<(string Name, T Input)> files) {
    var named = files.Where(f => Path.GetFileName(f.Name.Replace('\\', '/')).Equals(ImageInput, StringComparison.OrdinalIgnoreCase)).ToList();
    if (named.Count == 1) return named[0].Input;
    var images = files.Where(f => !Path.GetFileName(f.Name.Replace('\\', '/')).Equals("metadata.ini", StringComparison.OrdinalIgnoreCase)).ToList();
    return images.Count == 1
      ? images[0].Input
      : throw new ArgumentException($"UIF: give exactly one ISO image to compress ({images.Count} files given).", nameof(files));
  }

  private static Stream Seekable(Stream stream) {
    if (stream.CanSeek) return stream;
    var ms = new MemoryStream();
    stream.CopyTo(ms);
    return ms;
  }
}
