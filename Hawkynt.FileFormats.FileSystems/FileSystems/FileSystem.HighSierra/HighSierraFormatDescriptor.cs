#pragma warning disable CS1591
using Compression.Registry;
using Compression.Registry.Streaming;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.HighSierra;

/// <summary>
/// High Sierra Group CD-ROM format — the 1986 predecessor of ISO 9660, recognised by
/// the "CDROM" Volume Structure Standard Identifier at BP 10-14 of sector 16.
/// </summary>
/// <remarks>
/// <para>
/// Written from the normative text, the High Sierra Group "Working Paper for Information
/// Processing: Volume and File Structure of CD-ROM" (1986), cross-checked against
/// FreeBSD's BSD-licensed <c>cd9660</c> structure definitions; field-by-field notes in
/// <c>docs/HIGH-SIERRA-ON-DISK.md</c>.
/// </para>
/// <para>
/// WORM: Create writes a fresh volume (CD-ROM is read-only media, and no edit path is
/// offered). Proof: the Linux kernel <c>isofs</c> driver, which recognises High Sierra
/// on its own, mounts what Create writes and reads every file back byte-exact.
/// </para>
/// </remarks>
public sealed class HighSierraFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IFormatOptionsSchema {

  public IReadOnlyList<FormatOptionDescriptor> OptionsSchema { get; } = [
    new FormatOptionDescriptor(
      Key: "VolumeLabel", DisplayName: "Volume Label", Kind: FormatOptionKind.String, Default: "CDROM",
      Description: "Volume Identifier (d-characters A-Z, 0-9, _; 32 max).", IsOptimizationAxis: false),
  ];

  public string Id => "HighSierra";
  public string DisplayName => "High Sierra (CD-ROM)";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.CanCreate | FormatCapabilities.SupportsMultipleEntries | FormatCapabilities.SupportsDirectories;

  // ".iso" stays with ISO 9660 — the read side resolves it first-claim-wins when no magic
  // matches — so High Sierra answers to its own extension and to its descriptor magic.
  public string DefaultExtension => ".hsf";
  public IReadOnlyList<string> Extensions => [".hsf"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    // "CDROM" + Volume Structure Standard Version 1 at BP 10-15 of sector 16.
    new("CDROM\x01"u8.ToArray(), Offset: 16 * 2048 + 9, Confidence: 0.95),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description =>
    "High Sierra Group CD-ROM volume (1986, pre-ISO 9660): directories, multi-extent and " +
    "interleaved files read; fresh volumes written and verified by the Linux isofs driver.";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new HighSierraReader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(
      i, e.Path, e.Size, e.Size, "Stored", e.IsDirectory, false, e.LastModified)).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new HighSierraReader(stream);
    foreach (var e in r.Entries) {
      if (files != null && !MatchesFilter(e.Path, files)) continue;
      if (e.IsDirectory) {
        Directory.CreateDirectory(Path.Combine(outputDir, e.Path.Replace('/', Path.DirectorySeparatorChar)));
        continue;
      }
      WriteFile(outputDir, e.Path, r.Extract(e));
    }
  }

  public Stream OpenEntry(Stream archive, string entryName, string? password) {
    var bytes = this.ExtractEntryToMemory(archive, entryName, password);
    return new BoundedEntryStream(new MemoryStream(bytes, writable: false), bytes.Length, leaveOpen: false);
  }

  public byte[] ExtractEntryToMemory(Stream archive, string entryName, string? password) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryName);
    if (archive.CanSeek) archive.Position = 0;
    var r = new HighSierraReader(archive);
    var e = r.Entries.FirstOrDefault(x => !x.IsDirectory && string.Equals(x.Path, entryName.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase))
      ?? throw new FileNotFoundException($"High Sierra volume has no file '{entryName}'.", entryName);
    return r.Extract(e);
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(inputs);
    ArgumentNullException.ThrowIfNull(options);
    var w = new HighSierraWriter();
    if (options.HasOption("VolumeLabel")) w.VolumeId = options.GetOption("VolumeLabel", "CDROM");
    foreach (var input in inputs) {
      var when = input.Metadata?.LastWriteTimeUtc?.UtcDateTime;
      if (input.IsDirectory) w.AddDirectory(input.ArchiveName, when);
      else w.AddFile(input.ArchiveName, input.ReadContent(), when);
    }
    output.Write(w.Build());
  }
}
