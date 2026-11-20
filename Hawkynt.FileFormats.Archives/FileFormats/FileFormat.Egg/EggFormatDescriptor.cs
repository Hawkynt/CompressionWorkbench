#pragma warning disable CS1591
using Compression.Core.Deflate;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Egg;

/// <summary>
/// EGG (ALZip) archive — ESTsoft's native container for newer ALZip versions,
/// with Unicode filenames, per-file algorithm selection, solid/split modes, and
/// optional encryption. The native implementation reads every method, cipher, solid and
/// split archive, and creates non-solid store/deflate/bzip2/LZMA archives, optionally
/// encrypted and split, which ALZip 12.38 extracts; AZO and solid are read only.
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://github.com/alkegi/docs/blob/master/egg.md</c> — CC0 EGG Archive Format Specification</description></item>
///   <item><description><c>http://justsolve.archiveteam.org/wiki/EGG_(ALZip)</c> — ArchiveTeam format summary and historical references</description></item>
///   <item><description><c>EGG Format Specification, Version 1.0</c> (ESTsoft Corp.) — original published layout</description></item>
/// </list>
/// </summary>
public sealed class EggFormatDescriptor :
  IFormatDescriptor,
  IArchiveFormatOperations,
  IArchiveCreatable,
  IArchiveModifiable,
  IArchiveDefragmentable,
  IFormatOptionsSchema {

  /// <inheritdoc />
  public IReadOnlyList<FormatOptionDescriptor> OptionsSchema { get; } = [
    new FormatOptionDescriptor(
      Key: FormatOptionKeys.VolumeSize,
      DisplayName: "Volume size",
      Kind: FormatOptionKind.String,
      Default: "",
      Description: "Split into volumes of at most this size (bytes, or with k/m/g). The target must be named name.vol1.egg; name.vol2.egg, ... follow beside it. Empty writes one file.",
      IsOptimizationAxis: false),
  ];

  /// <summary>
  /// Gets the id.
  /// </summary>
  public string Id => "Egg";
  /// <summary>
  /// Gets the display name.
  /// </summary>
  public string DisplayName => "EGG";
  /// <summary>
  /// Gets the category.
  /// </summary>
  public FormatCategory Category => FormatCategory.Archive;
  /// <summary>
  /// Gets the capabilities.
  /// </summary>
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries |
    FormatCapabilities.SupportsDirectories | FormatCapabilities.SupportsPassword;
  /// <summary>
  /// Gets the default extension.
  /// </summary>
  public string DefaultExtension => ".egg";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
  public IReadOnlyList<string> Extensions => [".egg"];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
  public IReadOnlyList<string> CompoundExtensions => [];
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
  public IReadOnlyList<MagicSignature> MagicSignatures => [new([(byte)'E', (byte)'G', (byte)'G', (byte)'A'], Confidence: 0.95)];
  /// <summary>
  /// Gets the methods.
  /// </summary>
  public IReadOnlyList<FormatMethodInfo> Methods => [
    new("store", "Store"),
    new("deflate", "Deflate", SupportsOptimize: true),
    new("bzip2", "Bzip2"),
    new("lzma", "LZMA"),
  ];
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
  public string Description => "ESTsoft ALZip EGG archive (reads stored/deflate/bzip2/AZO/LZMA, ZIP 2.0/AES/LEA encryption, solid, split sets; writes stored/deflate/bzip2/LZMA, encrypted and split)";

  /// <summary>
  /// Lists the entries in the supplied container.
  /// </summary>
  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    using var r = EggReader.Open(stream, leaveOpen: true);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(i, e.Name, e.UncompressedSize, e.CompressedSize,
      e.MethodName, e.IsDirectory, e.IsEncrypted, e.LastModified)).ToList();
  }

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    using var r = EggReader.Open(stream, leaveOpen: true);
    foreach (var e in r.Entries) {
      if (files != null && !MatchesFilter(e.Name, files))
        continue;
      if (e.IsDirectory) {
        CreateSafeDirectory(outputDir, e.Name);
        continue;
      }
      WriteFile(outputDir, e.Name, r.Extract(e, password));
    }
  }

  /// <summary>
  /// Opens a single entry as a bounded read-only stream. The reader materialises the
  /// entry's decoded bytes; the result is wrapped in a bounded stream sized to its logical length.
  /// </summary>
  public Stream OpenEntry(Stream archive, string entryName, string? password) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryName);
    if (archive.CanSeek) archive.Position = 0;
    using var r = EggReader.Open(archive, leaveOpen: true);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (!string.Equals(e.Name, entryName, StringComparison.OrdinalIgnoreCase)) continue;
      var bytes = r.Extract(e, password);
      return new Compression.Registry.Streaming.BoundedEntryStream(
        new MemoryStream(bytes, writable: false), bytes.Length, leaveOpen: false);
    }
    return new Compression.Registry.Streaming.BoundedEntryStream(
      new MemoryStream(Array.Empty<byte>(), writable: false), 0, leaveOpen: false);
  }

  /// <summary>Native in-memory single-entry extraction routed through the bounded <see cref="OpenEntry"/>.</summary>
  public byte[] ExtractEntryToMemory(Stream archive, string entryName, string? password) {
    using var s = this.OpenEntry(archive, entryName, password);
    using var memoryStream = new MemoryStream();
    s.CopyTo(memoryStream);
    return memoryStream.ToArray();
  }

  /// <summary>Creates a non-solid EGG archive (store, deflate, bzip2 or LZMA; optionally encrypted; split with VolumeSize).</summary>
  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(inputs);
    ArgumentNullException.ThrowIfNull(options);
    if (string.IsNullOrEmpty(options.Password) && !string.IsNullOrEmpty(options.EncryptionMethod))
      throw new ArgumentException("An EGG encryption method needs a password.");

    var volumeSize = ParseVolumeSize(options.GetString(FormatOptionKeys.VolumeSize));
    if (volumeSize > 0) {
      var path = OutputFilePath(output) ?? throw new ArgumentException("An EGG split set must be written to a file, so its other volumes can go beside it.");
      var setBase = EggVolumes.SetBase(path);
      if (setBase is null || !Path.GetFileName(path).Equals(Path.GetFileName(EggVolumes.VolumePath(setBase, 1)), StringComparison.OrdinalIgnoreCase))
        throw new ArgumentException($"The first volume of an EGG split set is named name.vol1.egg; '{Path.GetFileName(path)}' is not.");
      using var whole = new MemoryStream();
      this.WriteArchive(whole, inputs, options);
      var volumes = EggVolumes.Split(whole.ToArray(), volumeSize);
      output.Write(volumes[0]);
      for (var i = 1; i < volumes.Count; ++i) File.WriteAllBytes(EggVolumes.VolumePath(setBase, i + 1), volumes[i]);
      return;
    }
    this.WriteArchive(output, inputs, options);
  }

  private void WriteArchive(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    var requestedMethod = ResolveMethod(options.MethodName);
    var level = ResolveLevel(options);
    using var writer = new EggWriter(output, leaveOpen: true, password: options.Password, encryption: ResolveEncryption(options.EncryptionMethod));
    foreach (var input in inputs) {
      if (input.IsDirectory) {
        writer.AddDirectory(input.ArchiveName);
        continue;
      }

      var method = requestedMethod;
      if (method == EggCompressionMethod.Auto) {
        if (options.ForceCompress)
          method = EggCompressionMethod.Deflate;
        else if (options.IncompressiblePaths?.Contains(input.ArchiveName) == true)
          method = EggCompressionMethod.Store;
      }
      writer.AddEntry(input.ArchiveName, input.ReadContent(), method, level);
    }
  }

  private static EggCompressionMethod ResolveMethod(string? methodName) {
    if (string.IsNullOrWhiteSpace(methodName) || methodName.Equals("auto", StringComparison.OrdinalIgnoreCase))
      return EggCompressionMethod.Auto;
    if (methodName.Equals("store", StringComparison.OrdinalIgnoreCase)
        || methodName.Equals("stored", StringComparison.OrdinalIgnoreCase))
      return EggCompressionMethod.Store;
    if (methodName.Equals("deflate", StringComparison.OrdinalIgnoreCase))
      return EggCompressionMethod.Deflate;
    if (methodName.Equals("bzip2", StringComparison.OrdinalIgnoreCase))
      return EggCompressionMethod.Bzip2;
    if (methodName.Equals("lzma", StringComparison.OrdinalIgnoreCase))
      return EggCompressionMethod.Lzma;
    throw new NotSupportedException($"Native EGG creation supports Store, Deflate, Bzip2 and LZMA, not '{methodName}'.");
  }

  private static EggEncryptionMethod ResolveEncryption(string? name) => (name ?? "").Trim().ToLowerInvariant().Replace("-", "") switch {
    "" or "aes256" => EggEncryptionMethod.Aes256,
    "aes128" or "aes" => EggEncryptionMethod.Aes128,
    "zip20" or "zip2.0" or "zipcrypto" or "standard" => EggEncryptionMethod.Zip20,
    "lea128" or "lea" => EggEncryptionMethod.Lea128,
    "lea256" => EggEncryptionMethod.Lea256,
    var m => throw new NotSupportedException($"EGG encryption '{m}' is not supported (zip20, aes128, aes256, lea128, lea256)."),
  };

  private static DeflateCompressionLevel ResolveLevel(FormatCreateOptions options) {
    if (options.Optimize)
      return DeflateCompressionLevel.Maximum;
    return options.Level switch {
      null => DeflateCompressionLevel.Default,
      <= 0 => DeflateCompressionLevel.None,
      <= 3 => DeflateCompressionLevel.Fast,
      <= 8 => DeflateCompressionLevel.Default,
      _ => DeflateCompressionLevel.Best,
    };
  }

  private static void CreateSafeDirectory(string baseDir, string entryName) {
    var safeName = entryName.Replace('\\', '/').TrimStart('/');
    if (safeName.Contains("..", StringComparison.Ordinal))
      safeName = Path.GetFileName(safeName);
    if (safeName.Length == 0)
      return;
    Directory.CreateDirectory(Path.Combine(baseDir, safeName));
  }
}
