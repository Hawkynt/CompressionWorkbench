#pragma warning disable CS1591
using System.Globalization;
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.UefiFv;

/// <summary>
/// UEFI PI Firmware Volume (<c>.fv</c>/<c>.fd</c>) archive surface. FFS files are
/// exposed as <c>{GUID}_{TYPE_TAG}.bin</c>; standalone volumes can be created and
/// ordinary FFS2 records can be added/replaced/removed through erased free space.
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://uefi.org/specifications</c> — UEFI Platform Initialization (PI) Specification, Volume 3: Firmware Storage Design</description></item>
///   <item><description><c>https://github.com/LongSoft/UEFITool</c> — UEFITool firmware-volume parser/editor</description></item>
/// </list>
/// </summary>
public sealed class UefiFvFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations,
  IArchiveCreatable, IArchiveModifiable {

  /// <summary>
  /// Gets the id.
  /// </summary>
  public string Id => "UefiFv";
  /// <summary>
  /// Gets the display name.
  /// </summary>
  public string DisplayName => "UEFI Firmware Volume";
  /// <summary>
  /// Gets the category.
  /// </summary>
  public FormatCategory Category => FormatCategory.Archive;
  /// <summary>
  /// Gets the capabilities.
  /// </summary>
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract |
    FormatCapabilities.CanCreate | FormatCapabilities.CanModify |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
  /// <summary>
  /// Gets the default extension.
  /// </summary>
  public string DefaultExtension => ".fv";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
  public IReadOnlyList<string> Extensions => [".fv", ".fd"];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
  public IReadOnlyList<string> CompoundExtensions => [];
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new([(byte)'_', (byte)'F', (byte)'V', (byte)'H'],
      Offset: UefiFvReader.SignatureOffset, Confidence: 0.95),
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
    "UEFI PI Firmware Volume — create and offline R/W for ordinary FFS2 files in fixed-capacity volumes.";

  /// <summary>
  /// Lists the entries in the supplied container.
  /// </summary>
  public List<ArchiveEntryInfo> List(Stream stream, string? password) =>
    BuildEntries(stream).Select((e, i) => new ArchiveEntryInfo(
      Index: i, Name: e.Name,
      OriginalSize: e.Data.LongLength, CompressedSize: e.Data.LongLength,
      Method: e.Method, IsDirectory: false, IsEncrypted: false, LastModified: null)).ToList();

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    foreach (var e in BuildEntries(stream)) {
      if (files != null && files.Length > 0 && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, e.Data);
    }
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(inputs);
    var files = FilesOnly(inputs).Where(f => !string.Equals(f.Name, "metadata.ini", StringComparison.OrdinalIgnoreCase));
    output.Write(UefiFvWriter.Build(files));
  }

  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs)
    => UefiFvInPlaceModifier.Add(archive, inputs);

  public void Remove(Stream archive, string[] entryNames)
    => UefiFvInPlaceModifier.Remove(archive,
      entryNames.Where(n => !string.Equals(n, "metadata.ini", StringComparison.OrdinalIgnoreCase)).ToArray());

  private static List<(string Name, byte[] Data, string Method)> BuildEntries(Stream stream) {
    if (stream.CanSeek) stream.Position = 0;
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    var data = ms.GetBuffer().AsSpan(0, checked((int)ms.Length));
    var fvStart = UefiFvReader.FindFirst(data) ?? 0;
    var fv = UefiFvReader.Read(data, fvStart);

    var entries = new List<(string, byte[], string)> {
      ("metadata.ini", BuildMetadata(fv), "stored"),
    };
    foreach (var f in fv.Files) {
      if (f.Type == 0xF0) continue;
      entries.Add((UefiFvWriter.EntryName(f.Name, f.Type), f.Contents, "stored"));
    }
    return entries;
  }

  private static byte[] BuildMetadata(UefiFvReader.FirmwareVolume fv) {
    var sb = new StringBuilder();
    sb.AppendLine("[uefi_fv]");
    sb.Append(CultureInfo.InvariantCulture, $"fv_start_offset = 0x{fv.StartOffset:X}\n");
    sb.Append(CultureInfo.InvariantCulture, $"file_system_guid = {fv.Header.FileSystemGuid:D}\n");
    sb.Append(CultureInfo.InvariantCulture, $"fv_length = {fv.Header.FvLength}\n");
    sb.Append(CultureInfo.InvariantCulture, $"attributes = 0x{fv.Header.Attributes:X8}\n");
    sb.Append(CultureInfo.InvariantCulture, $"header_length = {fv.Header.HeaderLength}\n");
    sb.Append(CultureInfo.InvariantCulture, $"checksum = 0x{fv.Header.Checksum:X4}\n");
    sb.Append(CultureInfo.InvariantCulture, $"ext_header_offset = 0x{fv.Header.ExtHeaderOffset:X}\n");
    sb.Append(CultureInfo.InvariantCulture, $"revision = {fv.Header.Revision}\n");
    sb.Append(CultureInfo.InvariantCulture, $"file_count = {fv.Files.Count}\n");

    sb.AppendLine();
    sb.AppendLine("[block_map]");
    for (var i = 0; i < fv.Header.BlockMap.Count; i++) {
      var (nb, bl) = fv.Header.BlockMap[i];
      sb.Append(CultureInfo.InvariantCulture,
        $"block_{i} = {nb} blocks x {bl} bytes\n");
    }

    sb.AppendLine();
    sb.AppendLine("[files]");
    for (var i = 0; i < fv.Files.Count; i++) {
      var f = fv.Files[i];
      sb.Append(CultureInfo.InvariantCulture,
        $"file_{i} = {f.Name:D} type=0x{f.Type:X2} ({UefiFvReader.FileTypeName(f.Type)}) size={f.Size}\n");
    }
    return Encoding.UTF8.GetBytes(sb.ToString());
  }
}
