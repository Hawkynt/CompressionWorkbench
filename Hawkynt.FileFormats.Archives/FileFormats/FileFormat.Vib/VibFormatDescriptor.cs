#pragma warning disable CS1591
using System.Globalization;
using Compression.Core.Deflate;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Vib;

/// <summary>
/// VMware vSphere Installation Bundle (<c>.vib</c>) — a Unix <c>ar</c> archive of
/// <c>descriptor.xml</c>, <c>sig.pkcs7</c> and a compressed TGZ payload.
/// Listing/extraction surface the descriptor + raw signature and unpack the payload
/// tree under <c>payload/</c>. Creation emits an unsigned, standards-shaped
/// <c>CommunitySupported</c> VIB with an empty signature member; higher acceptance
/// levels require a VMware-trusted signing identity and are therefore not forged here.
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://blogs.vmware.com/cloud-foundation/2011/09/13/whats-in-a-vib/</c> — VMware: CommunitySupported VIBs may be unsigned but still require an empty signature file</description></item>
///   <item><description><c>https://knowledge.broadcom.com/external/article/318056</c> — ESXi 8 requires SHA-256 + gunzip payload verification metadata</description></item>
///   <item><description>VMware VIB Author / community VIB 5.0 descriptors — TGZ payload, file-list, SHA-256 compressed digest and SHA-256/SHA-1 gunzip digests</description></item>
/// </list>
/// </summary>
public sealed class VibFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IFormatOptionsSchema {
  /// <summary>
  /// Gets the id.
  /// </summary>
  public string Id => "Vib";
  /// <summary>
  /// Gets the display name.
  /// </summary>
  public string DisplayName => "vSphere Installation Bundle";
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
    FormatCapabilities.SupportsDirectories;
  /// <summary>
  /// Gets the default extension.
  /// </summary>
  public string DefaultExtension => ".vib";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
  public IReadOnlyList<string> Extensions => [".vib"];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
  public IReadOnlyList<string> CompoundExtensions => [];
  // VIB shares the ar global magic "!<arch>\n"; extension + descriptor.xml member
  // distinguish it, so no generic ar magic is claimed here.
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
  public IReadOnlyList<MagicSignature> MagicSignatures => [];
  /// <summary>
  /// Gets the methods.
  /// </summary>
  public IReadOnlyList<FormatMethodInfo> Methods => [new("tgz", "TGZ payload")];
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
  public string Description => "VMware vSphere Installation Bundle (CommunitySupported creation, AR + descriptor + empty signature + TGZ payload)";

  /// <summary>
  /// Gets the options schema.
  /// </summary>
  public IReadOnlyList<FormatOptionDescriptor> OptionsSchema => [
    new("Name", "Package name", FormatOptionKind.String, VibWriterOptions.DefaultName,
      Description: "VIB package identifier; ASCII letters, digits, dot, underscore and hyphen."),
    new("Version", "Version", FormatOptionKind.String, VibWriterOptions.DefaultVersion,
      Description: "VIB package version, for example 1.0.0-0.0.0."),
    new("Vendor", "Vendor", FormatOptionKind.String, VibWriterOptions.DefaultVendor,
      Description: "Vendor text written to descriptor.xml."),
    new("Summary", "Summary", FormatOptionKind.String, VibWriterOptions.DefaultSummary,
      Description: "Short descriptor summary."),
    new("Description", "Description", FormatOptionKind.String, VibWriterOptions.DefaultDescription,
      Description: "Long descriptor description."),
    new("ReleaseDate", "Release date", FormatOptionKind.String, "1970-01-01T00:00:00Z",
      Description: "ISO-8601 release timestamp. Epoch is the deterministic default."),
    new("LiveInstallAllowed", "Live install", FormatOptionKind.Boolean, "true",
      Description: "Allow installation without a reboot/maintenance cycle where ESXi permits it."),
    new("LiveRemoveAllowed", "Live remove", FormatOptionKind.Boolean, "true",
      Description: "Allow removal without a reboot/maintenance cycle where ESXi permits it."),
    new("StatelessReady", "Stateless ready", FormatOptionKind.Boolean, "true",
      Description: "Mark the package suitable for stateless ESXi images."),
    new("MaintenanceMode", "Requires maintenance mode", FormatOptionKind.Boolean, "false",
      Description: "Require ESXi maintenance mode before installation."),
    new("FileMode", "File mode", FormatOptionKind.String, "0644",
      Description: "Unix mode for payload files, written as an octal value."),
    new("DirectoryMode", "Directory mode", FormatOptionKind.String, "0755",
      Description: "Unix mode for payload directories, written as an octal value."),
  ];

  /// <summary>
  /// Lists the entries in the supplied container.
  /// </summary>
  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    using var r = new VibReader(stream);
    var entries = new List<ArchiveEntryInfo>();
    var idx = 0;

    if (r.DescriptorXml is { } xml)
      entries.Add(new ArchiveEntryInfo(idx++, VibConstants.DescriptorName, xml.Length, xml.Length,
        "stored", false, false, null, Kind: "Tag"));

    if (r.Signature is { } sig)
      entries.Add(new ArchiveEntryInfo(idx++, VibConstants.SignatureName, sig.Length, sig.Length,
        "stored", false, false, null, Kind: "Tag"));

    foreach (var e in r.ReadPayloadEntries())
      entries.Add(new ArchiveEntryInfo(idx++, PayloadPath(e.Path), e.Data.Length, e.Data.Length,
        "tgz", e.IsDirectory, false, null));

    return entries;
  }

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    using var r = new VibReader(stream);

    if (r.DescriptorXml is { } xml && Wants(files, VibConstants.DescriptorName))
      WriteFile(outputDir, VibConstants.DescriptorName, xml);

    if (r.Signature is { } sig && Wants(files, VibConstants.SignatureName))
      WriteFile(outputDir, VibConstants.SignatureName, sig);

    foreach (var e in r.ReadPayloadEntries()) {
      if (e.IsDirectory)
        continue;
      var path = PayloadPath(e.Path);
      if (Wants(files, path))
        WriteFile(outputDir, path, e.Data);
    }
  }

  /// <summary>
  /// Performs the create operation.
  /// </summary>
  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(inputs);
    ArgumentNullException.ThrowIfNull(options);

    if (!string.IsNullOrEmpty(options.Password) || options.EncryptFilenames || !string.IsNullOrEmpty(options.EncryptionMethod))
      throw new NotSupportedException("CommunitySupported VIB creation does not support encryption or signed acceptance levels.");

    if (!string.IsNullOrWhiteSpace(options.MethodName) &&
        !options.MethodName.Equals("tgz", StringComparison.OrdinalIgnoreCase) &&
        !options.MethodName.Equals("gzip", StringComparison.OrdinalIgnoreCase))
      throw new NotSupportedException($"VIB creation supports only the TGZ payload method, not '{options.MethodName}'.");

    var releaseDateText = options.GetOption("ReleaseDate", "1970-01-01T00:00:00Z");
    if (!DateTimeOffset.TryParse(releaseDateText, CultureInfo.InvariantCulture,
          DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeUniversal, out var releaseDate))
      throw new ArgumentException($"Invalid VIB ReleaseDate '{releaseDateText}'.", nameof(options));

    var writerOptions = new VibWriterOptions {
      Name = options.GetOption("Name", VibWriterOptions.DefaultName),
      Version = options.GetOption("Version", VibWriterOptions.DefaultVersion),
      Vendor = options.GetOption("Vendor", VibWriterOptions.DefaultVendor),
      Summary = options.GetOption("Summary", VibWriterOptions.DefaultSummary),
      Description = options.GetOption("Description", VibWriterOptions.DefaultDescription),
      ReleaseDate = releaseDate,
      LiveInstallAllowed = options.GetOptionBool("LiveInstallAllowed", true),
      LiveRemoveAllowed = options.GetOptionBool("LiveRemoveAllowed", true),
      StatelessReady = options.GetOptionBool("StatelessReady", true),
      MaintenanceMode = options.GetOptionBool("MaintenanceMode", false),
      FileMode = ParseOctalMode(options.GetOption("FileMode", "0644"), "FileMode"),
      DirectoryMode = ParseOctalMode(options.GetOption("DirectoryMode", "0755"), "DirectoryMode"),
      CompressionLevel = MapCompressionLevel(options),
    };

    using var writer = new VibWriter(output, writerOptions, leaveOpen: true);
    var extractedShape = LooksLikeExtractedVib(inputs);
    foreach (var input in inputs) {
      var name = NormalizeInputName(input.ArchiveName);
      if (extractedShape && IsSyntheticMetadata(name))
        continue;
      if (extractedShape && name.StartsWith("payload/", StringComparison.Ordinal))
        name = name["payload/".Length..];
      if (extractedShape && name == "payload" && input.IsDirectory)
        continue;

      writer.AddEntry(name, input.IsDirectory ? [] : input.ReadContent(), input.IsDirectory);
    }
    writer.Finish();
  }

  private static DeflateCompressionLevel MapCompressionLevel(FormatCreateOptions options) {
    if (options.Optimize)
      return DeflateCompressionLevel.Maximum;
    if (!options.Level.HasValue)
      return DeflateCompressionLevel.Default;
    return options.Level.Value switch {
      <= 0 => DeflateCompressionLevel.None,
      <= 3 => DeflateCompressionLevel.Fast,
      <= 6 => DeflateCompressionLevel.Default,
      _ => DeflateCompressionLevel.Best,
    };
  }

  private static int ParseOctalMode(string value, string key) {
    try {
      var mode = Convert.ToInt32(value, 8);
      if (mode is < 0 or > 0xFFF)
        throw new FormatException();
      return mode;
    } catch (Exception e) when (e is FormatException or OverflowException or ArgumentException) {
      throw new ArgumentException($"VIB {key} must be an octal Unix mode between 0000 and 7777; got '{value}'.", key);
    }
  }

  private static bool LooksLikeExtractedVib(IReadOnlyList<ArchiveInputInfo> inputs) {
    var names = inputs.Select(i => NormalizeInputName(i.ArchiveName)).ToArray();
    if (!names.Contains(VibConstants.DescriptorName, StringComparer.OrdinalIgnoreCase) ||
        !names.Contains(VibConstants.SignatureName, StringComparer.OrdinalIgnoreCase))
      return false;
    return names.All(n => IsSyntheticMetadata(n) || n == "payload" || n.StartsWith("payload/", StringComparison.Ordinal));
  }

  private static bool IsSyntheticMetadata(string name)
    => name.Equals(VibConstants.DescriptorName, StringComparison.OrdinalIgnoreCase) ||
       name.Equals(VibConstants.SignatureName, StringComparison.OrdinalIgnoreCase);

  private static string NormalizeInputName(string name)
    => name.Replace('\\', '/').TrimEnd('/');

  private static string PayloadPath(string tarPath) {
    var normalized = tarPath.Replace('\\', '/').TrimStart('.', '/');
    return "payload/" + normalized;
  }

  private static bool Wants(string[]? files, string name)
    => files == null || files.Length == 0 || MatchesFilter(name, files);
}
