#pragma warning disable CS1591
using System.Globalization;
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Vhdx;

/// <summary>
/// Pseudo-archive descriptor for Hyper-V VHDX virtual hard-disk images
/// (MS-VHDX v1). Surfaces the File Type Identifier, both header copies, and
/// both region tables as separate entries so the structure can be inspected and
/// compared against reference parsers. Full disk-image extraction (metadata
/// region walk + BAT + block decode) is deferred.
/// </summary>
public sealed class VhdxFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable {
  public string Id => "Vhdx";
  public string DisplayName => "VHDX";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract |
    FormatCapabilities.CanTest | FormatCapabilities.CanCreate |
    FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".vhdx";
  public IReadOnlyList<string> Extensions => [".vhdx"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures =>
    [new("vhdxfile"u8.ToArray(), Offset: 0, Confidence: 0.95)];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Microsoft Hyper-V VHDX virtual hard disk (MS-VHDX v1)";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) =>
    BuildEntries(stream).Select((e, i) => new ArchiveEntryInfo(
      i, e.Name, e.Data.LongLength, e.Data.LongLength, "stored", false, false, null
    )).ToList();

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    foreach (var e in BuildEntries(stream)) {
      if (files != null && files.Length > 0 && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, e.Data);
    }
  }

  /// <summary>
  /// Wraps the supplied input files into a fixed-payload VHDX container.
  /// The inputs are first written into an embedded FAT filesystem image, then
  /// that image is wrapped in a spec-compliant VHDX (16 MiB blocks, 512 B logical
  /// sectors, 4096 B physical sectors, no log, no parent).
  /// </summary>
  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    var fat = FileSystem.Fat.FatWriter.BuildFromFiles(FlatFiles(inputs));
    var w = new VhdxWriter();
    w.SetDiskData(fat);
    output.Write(w.Build());
  }

  private static List<(string Name, byte[] Data)> BuildEntries(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    var img = VhdxReader.Read(ms.GetBuffer().AsSpan(0, (int)ms.Length));

    var entries = new List<(string, byte[])> {
      ("metadata.ini", BuildMetadata(img)),
      ("file_type_identifier.bin", img.FileTypeIdentifier),
    };
    if (img.HeaderPrimary.Length > 0) entries.Add(("header_primary.bin", img.HeaderPrimary));
    if (img.HeaderBackup.Length > 0) entries.Add(("header_backup.bin", img.HeaderBackup));
    if (img.RegionTablePrimary.Length > 0) entries.Add(("region_table_primary.bin", img.RegionTablePrimary));
    if (img.RegionTableBackup.Length > 0) entries.Add(("region_table_backup.bin", img.RegionTableBackup));
    return entries;
  }

  private static byte[] BuildMetadata(VhdxReader.VhdxImage img) {
    var sb = new StringBuilder();
    sb.AppendLine("[vhdx]");
    sb.Append(CultureInfo.InvariantCulture, $"file_size = {img.TotalFileSize}\n");
    sb.Append("signature = vhdxfile\n");
    sb.Append(CultureInfo.InvariantCulture, $"creator = {img.Creator}\n");
    AppendHeader(sb, "header_primary", img.PrimaryHeaderInfo);
    AppendHeader(sb, "header_backup", img.BackupHeaderInfo);
    return Encoding.UTF8.GetBytes(sb.ToString());
  }

  private static void AppendHeader(StringBuilder sb, string prefix, VhdxReader.HeaderInfo? info) {
    if (info is null) {
      sb.Append(CultureInfo.InvariantCulture, $"{prefix}_valid = false\n");
      return;
    }
    sb.Append(CultureInfo.InvariantCulture, $"{prefix}_valid = true\n");
    sb.Append(CultureInfo.InvariantCulture, $"{prefix}_checksum = 0x{info.Checksum:X8}\n");
    sb.Append(CultureInfo.InvariantCulture, $"{prefix}_sequence_number = {info.SequenceNumber}\n");
    sb.Append(CultureInfo.InvariantCulture, $"{prefix}_file_write_guid = {info.FileWriteGuid:D}\n");
    sb.Append(CultureInfo.InvariantCulture, $"{prefix}_data_write_guid = {info.DataWriteGuid:D}\n");
    sb.Append(CultureInfo.InvariantCulture, $"{prefix}_log_guid = {info.LogGuid:D}\n");
    sb.Append(CultureInfo.InvariantCulture, $"{prefix}_log_version = {info.LogVersion}\n");
    sb.Append(CultureInfo.InvariantCulture, $"{prefix}_version = {info.Version}\n");
    sb.Append(CultureInfo.InvariantCulture, $"{prefix}_log_length = {info.LogLength}\n");
    sb.Append(CultureInfo.InvariantCulture, $"{prefix}_log_offset = 0x{info.LogOffset:X16}\n");
  }
}
