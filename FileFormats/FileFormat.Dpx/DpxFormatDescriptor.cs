#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Dpx;

/// <summary>
/// DPX (SMPTE 268M digital moving-picture exchange). Magic "SDPX" (big-endian)
/// or "XPDS" (little-endian). Generic file header: magic(u32), offset-to-image-data
/// (u32 @4), version(8 bytes @8), total file size(u32 @16), … creator(100 bytes
/// @160). Image header at offset 768: orientation(u16 @768), number-of-elements
/// (u16 @770), pixels-per-line(u32 @772), lines-per-element(u32 @776), then per
/// image-element descriptors including bit depth at element 0
/// (descriptor @800, bit-depth @803 of the 72-byte element 0 block at offset 780).
///
/// <para>Surfaces <c>FULL.dpx</c> verbatim, a <c>metadata.ini</c> (width, height,
/// bit depth, descriptor, orientation, creator) and the image data region (from
/// offset-to-image-data to EOF) as <c>pixels.bin</c> (Kind="Raw"). Read-only;
/// malformed input degrades to FULL + partial metadata without throwing.</para>
/// </summary>
public sealed class DpxFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {
  public string Id => "Dpx";
  public string DisplayName => "DPX (SMPTE film)";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".dpx";
  public IReadOnlyList<string> Extensions => [".dpx"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new("SDPX"u8.ToArray(), Confidence: 0.95), // big-endian
    new("XPDS"u8.ToArray(), Confidence: 0.95), // little-endian
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description =>
    "DPX (SMPTE 268M) film frame: file/image headers + raw image data region as pixels.bin.";

  private sealed record DpxInfo(
    bool BigEndian,
    uint ImageOffset,
    string Version,
    uint Width,
    uint Height,
    ushort Orientation,
    ushort NumElements,
    byte Descriptor,
    byte BitDepth,
    string Creator,
    bool Valid);

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var fullSize = SafeLength(stream);
    var data = ReadAll(stream);
    var info = Parse(data);
    var entries = new List<ArchiveEntryInfo> {
      new(0, "FULL.dpx", fullSize, fullSize, "Stored", false, false, null),
      new(1, "metadata.ini", 0, 0, "Stored", false, false, null),
    };
    if (info.Valid && info.ImageOffset > 0 && info.ImageOffset < data.Length) {
      var len = data.Length - (int)info.ImageOffset;
      entries.Add(new ArchiveEntryInfo(2, "pixels.bin", len, len, "Stored", false, false, null, "Raw"));
    }
    return entries;
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var data = ReadAll(stream);
    if (Wants(files, "FULL.dpx"))
      WriteFile(outputDir, "FULL.dpx", data);

    var info = Parse(data);
    if (Wants(files, "metadata.ini"))
      WriteFile(outputDir, "metadata.ini", Encoding.UTF8.GetBytes(BuildMetadataIni(info)));

    if (info.Valid && info.ImageOffset > 0 && info.ImageOffset < data.Length && Wants(files, "pixels.bin")) {
      var len = data.Length - (int)info.ImageOffset;
      var pixels = new byte[len];
      Array.Copy(data, (int)info.ImageOffset, pixels, 0, len);
      WriteFile(outputDir, "pixels.bin", pixels);
    }
  }

  private static bool Wants(string[]? files, string name)
    => files == null || files.Length == 0 || MatchesFilter(name, files);

  private static DpxInfo Parse(byte[] data) {
    try {
      if (data.Length < 808) return Invalid();
      bool bigEndian;
      if (data[0] == 'S' && data[1] == 'D' && data[2] == 'P' && data[3] == 'X') bigEndian = true;
      else if (data[0] == 'X' && data[1] == 'P' && data[2] == 'D' && data[3] == 'S') bigEndian = false;
      else return Invalid();

      var imageOffset = ReadU32(data, 4, bigEndian);
      var version = ReadFixedString(data, 8, 8);
      // Image element header at offset 768.
      var orientation = ReadU16(data, 768, bigEndian);
      var numElements = ReadU16(data, 770, bigEndian);
      var width = ReadU32(data, 772, bigEndian);
      var height = ReadU32(data, 776, bigEndian);
      // First image element block begins at 780; descriptor @ +0, bit depth @ +23.
      var descriptor = data[780];
      var bitDepth = data[803];
      var creator = ReadFixedString(data, 160, 100);

      return new DpxInfo(bigEndian, imageOffset, version, width, height, orientation,
        numElements, descriptor, bitDepth, creator, Valid: true);
    } catch {
      return Invalid();
    }

    static DpxInfo Invalid() => new(false, 0, "", 0, 0, 0, 0, 0, 0, "", Valid: false);
  }

  private static uint ReadU32(byte[] d, int off, bool be)
    => be ? BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(off, 4))
          : BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(off, 4));

  private static ushort ReadU16(byte[] d, int off, bool be)
    => be ? BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(off, 2))
          : BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(off, 2));

  private static string ReadFixedString(byte[] d, int off, int len) {
    if (off + len > d.Length) len = Math.Max(0, d.Length - off);
    var s = Encoding.Latin1.GetString(d, off, len);
    var nul = s.IndexOf('\0');
    if (nul >= 0) s = s[..nul];
    return s.Trim();
  }

  private static string DescriptorName(byte d) => d switch {
    1 => "Red", 2 => "Green", 3 => "Blue", 4 => "Alpha",
    6 => "Luma", 50 => "RGB", 51 => "RGBA", 52 => "ABGR",
    100 => "CbYCrY", 102 => "CbYCr", _ => $"0x{d:X2}",
  };

  private static string BuildMetadataIni(DpxInfo info) {
    var sb = new StringBuilder();
    sb.Append("[Dpx]\n");
    sb.Append(CultureInfo.InvariantCulture, $"valid={(info.Valid ? 1 : 0)}\n");
    if (!info.Valid) {
      sb.Append("parse_status=partial\n");
      return sb.ToString();
    }
    sb.Append(CultureInfo.InvariantCulture, $"endian={(info.BigEndian ? "big" : "little")}\n");
    sb.Append(CultureInfo.InvariantCulture, $"version={info.Version}\n");
    sb.Append(CultureInfo.InvariantCulture, $"width={info.Width}\n");
    sb.Append(CultureInfo.InvariantCulture, $"height={info.Height}\n");
    sb.Append(CultureInfo.InvariantCulture, $"bit_depth={info.BitDepth}\n");
    sb.Append(CultureInfo.InvariantCulture, $"descriptor={DescriptorName(info.Descriptor)}\n");
    sb.Append(CultureInfo.InvariantCulture, $"orientation={info.Orientation}\n");
    sb.Append(CultureInfo.InvariantCulture, $"number_of_elements={info.NumElements}\n");
    sb.Append(CultureInfo.InvariantCulture, $"image_data_offset={info.ImageOffset}\n");
    sb.Append(CultureInfo.InvariantCulture, $"creator={info.Creator.Replace('\n', ' ').Replace('\r', ' ')}\n");
    sb.Append("parse_status=ok\n");
    return sb.ToString();
  }

  private static long SafeLength(Stream s) => s.CanSeek ? s.Length : 0;

  private static byte[] ReadAll(Stream stream) {
    if (stream.CanSeek) stream.Position = 0;
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    return ms.ToArray();
  }
}
