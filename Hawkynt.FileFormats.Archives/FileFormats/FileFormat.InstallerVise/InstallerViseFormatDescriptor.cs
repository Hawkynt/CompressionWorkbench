using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.InstallerVise;

/// <summary>
/// Installer VISE (MindVision / Digital Wisdom), the classic Mac OS installer builder, VISE 3.0
/// to 8.5. Data forks are listed under their catalog paths, resource forks as <c>name.rsrc</c>.
/// </summary>
/// <remarks>
/// Read-only. The installer comes as MacBinary or BinHex (both forks inside), or as a data fork
/// with its resource fork beside it (<c>._name</c> AppleDouble, or <c>name.rsrc</c> next to
/// <c>name.data</c>); a payload companion <c>name.data</c> beside the application is used when the
/// members live there. Converted from unvise (MIT).
/// </remarks>
public sealed class InstallerViseFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {
  /// <inheritdoc />
  public string Id => "InstallerVise";
  /// <inheritdoc />
  public string DisplayName => "Installer VISE";
  /// <inheritdoc />
  public FormatCategory Category => FormatCategory.Archive;
  /// <inheritdoc />
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract |
    FormatCapabilities.SupportsMultipleEntries | FormatCapabilities.SupportsDirectories;
  /// <inheritdoc />
  public string DefaultExtension => "";
  /// <inheritdoc />
  public IReadOnlyList<string> Extensions => [];
  /// <inheritdoc />
  public IReadOnlyList<string> CompoundExtensions => [];
  /// <inheritdoc />
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new("SVCT"u8.ToArray(), Confidence: 0.85),
    new("SVCT"u8.ToArray(), Offset: 128, Confidence: 0.85),
  ];
  /// <inheritdoc />
  public IReadOnlyList<FormatMethodInfo> Methods => [new("vise-deflate", "VISE DEFLATE (permuted, 16-bit words)")];
  /// <inheritdoc />
  public string? TarCompressionFormatId => null;
  /// <inheritdoc />
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  /// <inheritdoc />
  public string Description =>
    "Classic Mac OS Installer VISE 3.0-8.5 application: catalog files with data and resource forks, " +
    "each checked against its CRC-32. Actions, base-dependent updates and members on other media are not files.";

  /// <summary>Opens the installer in <paramref name="stream"/>, with the sidecars beside it when it is a file.</summary>
  internal static ViseInstaller Open(Stream stream) {
    stream.Position = 0;
    using var copy = new MemoryStream();
    stream.CopyTo(copy);
    byte[]? resource = null, companion = null;
    if (stream is FileStream file) {
      resource = ViseTransport.SidecarResource(file.Name);
      var payload = file.Name + ".data";
      if (File.Exists(payload))
        companion = File.ReadAllBytes(payload);
    }
    return ViseInstaller.Open(copy.ToArray(), resource, companion);
  }

  internal static string EntryName(ViseFork f) => f.Fork == 0 ? f.Path : f.Path + ".rsrc";

  /// <summary>The installer and its forks, or why there are none.</summary>
  private static (ViseInstaller? Installer, IReadOnlyList<ViseFork> Forks, string? Problem) Read(Stream stream) {
    try {
      var installer = Open(stream);
      return (installer, installer.Extract(), null);
    } catch (Exception e) when (e is InvalidDataException or NotSupportedException or EndOfStreamException or ArgumentException) {
      return (null, [], e.Message);
    }
  }

  private static byte[] Problem(string problem) => System.Text.Encoding.UTF8.GetBytes(
    "[InstallerVise]\nproblem=" + problem.Replace('\n', ' ') + "\nmember_enumeration=unavailable\nparse_status=partial\n");

  /// <inheritdoc />
  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var (installer, forks, problem) = Read(stream);
    if (installer == null) {
      var length = stream.Length;
      return [
        new(0, "FULL.bin", length, length, "Stored", false, false, null, Kind: "Track"),
        new(1, "metadata.ini", Problem(problem!).Length, 0, "Stored", false, false, null, Kind: "Tag"),
      ];
    }
    var result = new List<ArchiveEntryInfo>();
    foreach (var dir in installer.Directories)
      result.Add(new ArchiveEntryInfo(result.Count, dir, 0, 0, "", true, false, null));
    foreach (var f in forks)
      result.Add(new ArchiveEntryInfo(result.Count, EntryName(f), f.Data.Length, -1, f.Fork == 0 ? "data fork" : "resource fork", false, false, f.Modified));
    return result;
  }

  /// <inheritdoc />
  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var (installer, forks, problem) = Read(stream);
    if (installer == null) {
      if (files is not { Length: > 0 } || MatchesFilter("FULL.bin", files)) {
        stream.Position = 0;
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        WriteFile(outputDir, "FULL.bin", ms.ToArray());
      }
      if (files is not { Length: > 0 } || MatchesFilter("metadata.ini", files))
        WriteFile(outputDir, "metadata.ini", Problem(problem!));
      return;
    }
    foreach (var f in forks) {
      var name = EntryName(f);
      if (files is { Length: > 0 } && !MatchesFilter(name, files))
        continue;
      WriteFile(outputDir, name, f.Data);
    }
  }
}
