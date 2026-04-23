#pragma warning disable CS1591
using System.Globalization;
using System.IO.Compression;
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Nifti;

/// <summary>
/// Pseudo-archive descriptor for NIfTI-1 and NIfTI-2 medical imaging files
/// (single-file <c>.nii</c> variant, optionally gzip-framed as <c>.nii.gz</c>).
/// Emits a <c>metadata.ini</c> with dimensionality + datatype + pixel spacing,
/// the raw header bytes, and the voxel payload following <c>vox_offset</c>.
/// </summary>
/// <remarks>
/// Detects the version via the <c>sizeof_hdr</c> sentinel: 348 → NIfTI-1,
/// 540 → NIfTI-2, in either byte order. If the file begins with <c>0x1F 0x8B</c>
/// it is transparently inflated (via <see cref="GZipStream"/>) before parsing —
/// this covers the common <c>.nii.gz</c> distribution format.
/// </remarks>
public sealed class NiftiFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {

  public string Id => "Nifti";
  public string DisplayName => "NIfTI";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".nii";
  public IReadOnlyList<string> Extensions => [".nii"];
  public IReadOnlyList<string> CompoundExtensions => [".nii.gz"];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    // NIfTI-1 single-file magic at offset 344. Confidence moderate because the
    // leading bytes of the file are an i16/i32 header that doesn't have a bit
    // pattern that can be tested with a fixed-offset magic check.
    new([(byte)'n', (byte)'+', (byte)'1', 0x00], Offset: 344, Confidence: 0.90),
    new([(byte)'n', (byte)'i', (byte)'1', 0x00], Offset: 344, Confidence: 0.85),
    // NIfTI-2 magic at offset 4.
    new([(byte)'n', (byte)'+', (byte)'2', 0x00], Offset: 4, Confidence: 0.90),
    new([(byte)'n', (byte)'i', (byte)'2', 0x00], Offset: 4, Confidence: 0.85),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description =>
    "NIfTI-1 / NIfTI-2 medical imaging (single-file .nii, optionally gzip-framed as .nii.gz). " +
    "Surfaces header + voxel payload with parsed dimensionality + datatype.";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) =>
    this.BuildEntries(stream)
      .Select((e, i) => new ArchiveEntryInfo(i, e.Name, e.Data.LongLength, e.Data.LongLength,
        "stored", false, false, null, e.Kind))
      .ToList();

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    foreach (var e in this.BuildEntries(stream)) {
      if (files != null && files.Length > 0 && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, e.Data);
    }
  }

  private List<(string Name, byte[] Data, string Kind)> BuildEntries(Stream stream) {
    var raw = ReadAll(stream);
    var fileSize = raw.Length;

    // Gzip framing — transparently inflate .nii.gz before parsing.
    if (raw.Length >= 2 && raw[0] == 0x1F && raw[1] == 0x8B) {
      using var src = new MemoryStream(raw);
      using var gz = new GZipStream(src, CompressionMode.Decompress);
      using var dst = new MemoryStream();
      gz.CopyTo(dst);
      raw = dst.ToArray();
    }

    var result = new List<(string, byte[], string)>();
    try {
      var img = NiftiReader.Read(raw);
      result.Add(("metadata.ini", BuildMetadata(img, fileSize), "Metadata"));
      result.Add(("header.bin", img.HeaderBytes, "Header"));
      if (img.VoxelBytes.Length > 0) result.Add(("voxels.bin", img.VoxelBytes, "Payload"));
    } catch (Exception ex) {
      var sb = new StringBuilder();
      sb.Append("[nifti]\r\n");
      sb.Append("parse_status=error\r\n");
      sb.Append(CultureInfo.InvariantCulture, $"file_size={fileSize}\r\n");
      sb.Append("error=").Append(ex.Message).Append("\r\n");
      result.Add(("metadata.ini", Encoding.UTF8.GetBytes(sb.ToString()), "Metadata"));
    }
    return result;
  }

  private static byte[] ReadAll(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    return ms.ToArray();
  }

  private static byte[] BuildMetadata(NiftiReader.NiftiImage i, long onDiskSize) {
    var sb = new StringBuilder();
    sb.Append("[nifti]\r\n");
    sb.Append("parse_status=ok\r\n");
    sb.Append(CultureInfo.InvariantCulture, $"file_size={onDiskSize}\r\n");
    sb.Append(CultureInfo.InvariantCulture, $"version=NIfTI-{(int)i.Version}\r\n");
    sb.Append("endian=").Append(i.LittleEndian ? "little" : "big").Append("\r\n");
    sb.Append("magic=").Append(i.Magic).Append("\r\n");
    sb.Append(CultureInfo.InvariantCulture, $"sizeof_hdr={i.SizeOfHeader}\r\n");
    sb.Append(CultureInfo.InvariantCulture, $"datatype={i.Datatype} ({NiftiReader.DatatypeName(i.Datatype)})\r\n");
    sb.Append(CultureInfo.InvariantCulture, $"bitpix={i.Bitpix}\r\n");
    sb.Append(CultureInfo.InvariantCulture, $"vox_offset={i.VoxOffset}\r\n");
    sb.Append(CultureInfo.InvariantCulture, $"scl_slope={i.SclSlope}\r\n");
    sb.Append(CultureInfo.InvariantCulture, $"scl_inter={i.SclInter}\r\n");
    sb.Append(CultureInfo.InvariantCulture, $"ndim={i.Dim[0]}\r\n");
    for (var d = 1; d < 8; d++)
      sb.Append(CultureInfo.InvariantCulture, $"dim{d}={i.Dim[d]}\r\n");
    for (var d = 0; d < 8; d++)
      sb.Append(CultureInfo.InvariantCulture, $"pixdim{d}={i.Pixdim[d]}\r\n");
    sb.Append("description=").Append(i.Description).Append("\r\n");
    sb.Append("intent_name=").Append(i.IntentName).Append("\r\n");
    sb.Append(CultureInfo.InvariantCulture, $"voxel_bytes={i.VoxelBytes.Length}\r\n");
    return Encoding.UTF8.GetBytes(sb.ToString());
  }
}
