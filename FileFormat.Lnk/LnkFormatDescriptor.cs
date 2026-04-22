#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Lnk;

/// <summary>
/// Windows Shell Link (.lnk) shortcut file surfaced as a read-only archive.
/// Parses the 76-byte ShellLinkHeader, the optional LinkTargetIDList,
/// LinkInfo block, StringData entries (Name/RelativePath/WorkingDir/Arguments/IconLocation),
/// and any trailing ExtraData blocks. Writes each as a distinct entry.
/// </summary>
public sealed class LnkFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {
  public string Id => "Lnk";
  public string DisplayName => "Windows Shell Link";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".lnk";
  public IReadOnlyList<string> Extensions => [".lnk"];
  public IReadOnlyList<string> CompoundExtensions => [];

  // Full 20-byte magic: HeaderSize (0x0000004C LE) + LinkCLSID.
  private static readonly byte[] Magic20 = [
    0x4C, 0x00, 0x00, 0x00,
    0x01, 0x14, 0x02, 0x00, 0x00, 0x00, 0x00, 0x00,
    0xC0, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x46,
  ];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new(Magic20, Offset: 0, Confidence: 0.99),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description =>
    "Windows Shell Link (.lnk) shortcut. Surfaces header, ID list, LinkInfo, " +
    "UTF-16/ANSI string blocks, and any extra data blocks.";

  [Flags]
  private enum LinkFlags : uint {
    HasLinkTargetIDList = 1 << 0,
    HasLinkInfo         = 1 << 1,
    HasName             = 1 << 2,
    HasRelativePath     = 1 << 3,
    HasWorkingDir       = 1 << 4,
    HasArguments        = 1 << 5,
    HasIconLocation     = 1 << 6,
    IsUnicode           = 1 << 7,
  }

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    List<(string Name, byte[] Data)> entries;
    try {
      entries = BuildEntries(stream);
    } catch {
      entries = [];
    }
    return entries.Select((e, i) => new ArchiveEntryInfo(
      Index: i, Name: e.Name,
      OriginalSize: e.Data.Length, CompressedSize: e.Data.Length,
      Method: "stored", IsDirectory: false, IsEncrypted: false, LastModified: null
    )).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    List<(string Name, byte[] Data)> entries;
    try {
      entries = BuildEntries(stream);
    } catch {
      entries = [];
    }
    foreach (var e in entries) {
      if (files != null && files.Length > 0 && !MatchesFilter(e.Name, files))
        continue;
      WriteFile(outputDir, e.Name, e.Data);
    }
  }

  private static List<(string Name, byte[] Data)> BuildEntries(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    var blob = ms.ToArray();

    var entries = new List<(string Name, byte[] Data)> {
      ("FULL.lnk", blob),
    };

    var meta = new StringBuilder();
    meta.AppendLine("; Windows Shell Link metadata");

    if (blob.Length < 76) {
      meta.AppendLine("error=truncated_header");
      entries.Add(("metadata.ini", Encoding.UTF8.GetBytes(meta.ToString())));
      return entries;
    }

    // Header (76 bytes).
    var header = blob.AsSpan(0, 76).ToArray();
    entries.Add(("header.bin", header));

    var flags = (LinkFlags)BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(20, 4));
    var attrs = BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(24, 4));
    var creation = BinaryPrimitives.ReadInt64LittleEndian(blob.AsSpan(28, 8));
    var access = BinaryPrimitives.ReadInt64LittleEndian(blob.AsSpan(36, 8));
    var write = BinaryPrimitives.ReadInt64LittleEndian(blob.AsSpan(44, 8));
    var fileSize = BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(52, 4));
    var showCmd = BinaryPrimitives.ReadInt32LittleEndian(blob.AsSpan(60, 4));

    meta.Append("link_flags_hex=0x").AppendLine(((uint)flags).ToString("X8", CultureInfo.InvariantCulture));
    meta.Append("file_attributes_hex=0x").AppendLine(attrs.ToString("X8", CultureInfo.InvariantCulture));
    meta.Append("show_command=").AppendLine(showCmd.ToString(CultureInfo.InvariantCulture));
    meta.Append("file_size_target=").AppendLine(fileSize.ToString(CultureInfo.InvariantCulture));
    meta.Append("creation_time_utc=").AppendLine(FormatFileTime(creation));
    meta.Append("access_time_utc=").AppendLine(FormatFileTime(access));
    meta.Append("write_time_utc=").AppendLine(FormatFileTime(write));

    var pos = 76;
    var isUnicode = (flags & LinkFlags.IsUnicode) != 0;

    // LinkTargetIDList: uint16 size + IDList.
    if ((flags & LinkFlags.HasLinkTargetIDList) != 0 && pos + 2 <= blob.Length) {
      var idListSize = BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(pos, 2));
      // Total bytes consumed: size field + payload.
      var total = 2 + idListSize;
      if (pos + total <= blob.Length) {
        var idlist = blob.AsSpan(pos, total).ToArray();
        entries.Add(("idlist.bin", idlist));
        pos += total;
      } else {
        pos = blob.Length;
      }
    }

    // LinkInfo: uint32 size.
    string? targetPath = null;
    if ((flags & LinkFlags.HasLinkInfo) != 0 && pos + 4 <= blob.Length) {
      var linkInfoSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(pos, 4));
      if (linkInfoSize >= 4 && pos + linkInfoSize <= blob.Length) {
        var linkInfo = blob.AsSpan(pos, linkInfoSize).ToArray();
        entries.Add(("linkinfo.bin", linkInfo));
        targetPath = TryReadLocalBasePath(linkInfo);
        pos += linkInfoSize;
      } else {
        pos = blob.Length;
      }
    }

    // StringData blocks: each has a uint16 character count, then characters
    // (UTF-16LE if IsUnicode else single-byte).
    var stringOrder = new (LinkFlags Flag, string FileName, string MetaKey)[] {
      (LinkFlags.HasName,         "strings/name.txt",          "name_if_set"),
      (LinkFlags.HasRelativePath, "strings/relative_path.txt", "relative_path_if_set"),
      (LinkFlags.HasWorkingDir,   "strings/working_dir.txt",   "working_dir_if_set"),
      (LinkFlags.HasArguments,    "strings/arguments.txt",     "arguments_if_set"),
      (LinkFlags.HasIconLocation, "strings/icon_location.txt", "icon_location_if_set"),
    };

    foreach (var (flag, fileName, metaKey) in stringOrder) {
      if ((flags & flag) == 0) continue;
      if (pos + 2 > blob.Length) break;
      var charCount = BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(pos, 2));
      pos += 2;
      var byteCount = isUnicode ? charCount * 2 : charCount;
      if (pos + byteCount > blob.Length) break;
      var raw = blob.AsSpan(pos, byteCount);
      var text = isUnicode
        ? Encoding.Unicode.GetString(raw)
        : Encoding.Latin1.GetString(raw);
      entries.Add((fileName, Encoding.UTF8.GetBytes(text)));
      meta.Append(metaKey).Append('=').AppendLine(text);
      pos += byteCount;
    }

    if (targetPath != null)
      meta.Append("target_path_from_linkinfo_if_set=").AppendLine(targetPath);

    // Extra data blocks: each uint32 BlockSize + uint32 Signature + body.
    // Terminated by a BlockSize < 4.
    while (pos + 8 <= blob.Length) {
      var blockSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(pos, 4));
      if (blockSize < 4) break;
      if (pos + blockSize > blob.Length) break;
      var sig = BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(pos + 4, 4));
      var body = blob.AsSpan(pos, blockSize).ToArray();
      var name = $"extra_data/{sig:X8}.bin";
      entries.Add((name, body));
      pos += blockSize;
    }

    entries.Insert(1, ("metadata.ini", Encoding.UTF8.GetBytes(meta.ToString())));
    return entries;
  }

  private static string FormatFileTime(long filetime) {
    if (filetime <= 0) return "";
    try {
      return DateTime.FromFileTimeUtc(filetime).ToString("O", CultureInfo.InvariantCulture);
    } catch {
      return "";
    }
  }

  /// <summary>
  /// Best-effort extraction of the LocalBasePath field from a LinkInfo block.
  /// The block layout: LinkInfoSize(4) LinkInfoHeaderSize(4) LinkInfoFlags(4)
  /// VolumeIDOffset(4) LocalBasePathOffset(4) CNRLinkOffset(4) CommonPathSuffixOffset(4)
  /// [LocalBasePathOffsetUnicode(4)] [CommonPathSuffixOffsetUnicode(4)] ...
  /// </summary>
  private static string? TryReadLocalBasePath(byte[] linkInfo) {
    if (linkInfo.Length < 28) return null;
    var linkInfoFlags = BinaryPrimitives.ReadUInt32LittleEndian(linkInfo.AsSpan(8, 4));
    // VolumeIDAndLocalBasePath = bit 0
    if ((linkInfoFlags & 1) == 0) return null;
    var localBasePathOffset = (int)BinaryPrimitives.ReadUInt32LittleEndian(linkInfo.AsSpan(16, 4));
    if (localBasePathOffset <= 0 || localBasePathOffset >= linkInfo.Length) return null;
    // Null-terminated single-byte (ANSI/Latin1) string at offset localBasePathOffset.
    var end = localBasePathOffset;
    while (end < linkInfo.Length && linkInfo[end] != 0) end++;
    return Encoding.Latin1.GetString(linkInfo, localBasePathOffset, end - localBasePathOffset);
  }
}
