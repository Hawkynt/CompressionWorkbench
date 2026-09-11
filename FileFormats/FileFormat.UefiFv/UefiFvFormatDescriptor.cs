#pragma warning disable CS1591
using System.Globalization;
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.UefiFv;

/// <summary>
/// UEFI PI Firmware Volume archive surface. Standard FFS2/FFS3 files are exposed as
/// <c>{GUID}_{TYPE_TAG}.bin</c>; mutable unsigned volumes support transactional edits,
/// erase-aware wiping, purge, and offline consolidation.
///
/// References: UEFI PI Specification 1.10 Volume III (Firmware Storage) and EDK II
/// as an interoperability oracle. The implementation is independently written.
/// </summary>
public sealed class UefiFvFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations,
  IArchiveCreatable, IArchiveModifiable, IArchiveDefragmentable, IArchiveLayoutMap, IArchivePurgeable {

  public string Id => "UefiFv";
  public string DisplayName => "UEFI Firmware Volume";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract |
    FormatCapabilities.CanCreate | FormatCapabilities.CanModify |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".fv";
  public IReadOnlyList<string> Extensions => [".fv", ".fd"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new([(byte)'_', (byte)'F', (byte)'V', (byte)'H'], Offset: UefiFvReader.SignatureOffset, Confidence: 0.95),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description =>
    "UEFI PI Firmware Volume — FFS2/FFS3 create and transactional offline R/W, purge, wipe and defrag.";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) =>
    BuildEntries(stream).Select((e, i) => new ArchiveEntryInfo(
      Index: i, Name: e.Name,
      OriginalSize: e.Data.LongLength, CompressedSize: e.Data.LongLength,
      Method: e.Method, IsDirectory: false, IsEncrypted: false, LastModified: null)).ToList();

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

  public void Defragment(Stream archive)
    => UefiFvMaintenance.Defragment(archive, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });

  public void Defragment(Stream archive, DefragOptions options)
    => UefiFvMaintenance.Defragment(archive, options);

  public IEnumerable<DefragBlockInfo> EnumerateLayout(Stream archive)
    => UefiFvLayoutMap.Enumerate(archive);

  /// <summary>
  /// Restores unused bytes to the FV's declared erase value. For erase-polarity-one
  /// flash this is 0xFF rather than zero; writing zero would turn free space into
  /// apparently programmed data and make the firmware volume structurally invalid.
  /// </summary>
  public long WipeUnusedSpace(Stream image, bool wipeClusterTips = true, bool wipeDeletedEntries = true)
    => UefiFvLayoutMap.Wipe(image, wipeDeletedEntries);

  public void Purge(Stream archive) => UefiFvMaintenance.Purge(archive);

  private static List<(string Name, byte[] Data, string Method)> BuildEntries(Stream stream) {
    if (stream.CanSeek) stream.Position = 0;
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    var data = ms.GetBuffer().AsSpan(0, checked((int)ms.Length));
    var fvStart = UefiFvReader.FindFirst(data) ?? 0;
    var fv = UefiFvReader.Read(data, fvStart);

    var entries = new List<(string, byte[], string)> { ("metadata.ini", BuildMetadata(fv), "stored") };
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
    sb.Append(CultureInfo.InvariantCulture, $"erase_byte = 0x{fv.Header.EraseByte:X2}\n");
    sb.Append(CultureInfo.InvariantCulture, $"header_length = {fv.Header.HeaderLength}\n");
    sb.Append(CultureInfo.InvariantCulture, $"checksum = 0x{fv.Header.Checksum:X4}\n");
    sb.Append(CultureInfo.InvariantCulture, $"ext_header_offset = 0x{fv.Header.ExtHeaderOffset:X}\n");
    sb.Append(CultureInfo.InvariantCulture, $"revision = {fv.Header.Revision}\n");
    sb.Append(CultureInfo.InvariantCulture, $"file_count = {fv.Files.Count}\n");
    sb.AppendLine();
    sb.AppendLine("[block_map]");
    for (var i = 0; i < fv.Header.BlockMap.Count; i++) {
      var (blocks, length) = fv.Header.BlockMap[i];
      sb.Append(CultureInfo.InvariantCulture, $"block_{i} = {blocks} blocks x {length} bytes\n");
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
