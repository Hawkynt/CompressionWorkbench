#pragma warning disable CS1591
using System.Globalization;
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.UefiFv;

/// <summary>
/// Pseudo-archive descriptor for UEFI PI Firmware Volumes (<c>.fv</c>/<c>.fd</c>).
/// Locates the FV by scanning for the <c>_FVH</c> signature at offset 40 and
/// emits one entry per FFS file, named <c>{GUID}_{TYPE_TAG}.bin</c>.
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://uefi.org/specifications</c> — UEFI Platform Initialization (PI) Specification — Volume 3 defines Firmware Volumes and FFS</description></item>
///   <item><description><c>https://github.com/LongSoft/UEFITool</c> — UEFITool — canonical firmware-volume parser/editor</description></item>
/// </list>
/// </summary>
public sealed class UefiFvFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {

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
    // '_FVH' signature is at offset 40 (not 0) — callers must respect the Offset.
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
    "UEFI PI Firmware Volume — container for FFS files (PEI/DXE/driver modules).";

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

  private static List<(string Name, byte[] Data, string Method)> BuildEntries(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    var data = ms.GetBuffer().AsSpan(0, (int)ms.Length);
    var fvStart = UefiFvReader.FindFirst(data) ?? 0;
    var fv = UefiFvReader.Read(data, fvStart);

    var entries = new List<(string, byte[], string)> {
      ("metadata.ini", BuildMetadata(fv), "stored"),
    };
    foreach (var f in fv.Files) {
      var tag = UefiFvReader.ShortTypeTag(f.Type);
      entries.Add(($"{f.Name:D}_{tag}.bin", f.Contents, "stored"));
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
