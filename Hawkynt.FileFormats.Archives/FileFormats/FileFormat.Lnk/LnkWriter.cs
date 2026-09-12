#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileFormat.Lnk;

/// <summary>
/// WORM writer for Windows Shell Link (.lnk) files. Emits a minimal valid shortcut
/// pointing at a single target path (file or directory). The output contains the
/// 76-byte ShellLinkHeader, a LinkInfo block carrying the LocalBasePath (the
/// target's full path), Unicode StringData blocks for RelativePath / WorkingDir /
/// Arguments / IconLocation when supplied, and a 4-byte terminator block.
/// </summary>
/// <remarks>
/// Format reference: <c>[MS-SHLLINK]</c> §2.1 ShellLinkHeader, §2.3 LinkInfo,
/// §2.4 StringData. The LinkTargetIDList is intentionally omitted — it's
/// optional when the LinkInfo is sufficient to locate the target, and skipping
/// it keeps the writer free of the IDL/IDA itemid taxonomy. Shell32 / Explorer
/// still resolves the shortcut from the LinkInfo LocalBasePath.
/// </remarks>
public sealed class LnkWriter {

  // Header constants per MS-SHLLINK §2.1.
  private const uint HeaderSize = 0x0000004C;

  /// <summary>The fixed LinkCLSID GUID {00021401-0000-0000-C000-000000000046}.</summary>
  private static readonly byte[] LinkClsid = [
    0x01, 0x14, 0x02, 0x00, 0x00, 0x00, 0x00, 0x00,
    0xC0, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x46,
  ];

  [Flags]
  private enum LinkFlags : uint {
    HasLinkTargetIDList = 1u << 0,
    HasLinkInfo         = 1u << 1,
    HasName             = 1u << 2,
    HasRelativePath     = 1u << 3,
    HasWorkingDir       = 1u << 4,
    HasArguments        = 1u << 5,
    HasIconLocation     = 1u << 6,
    IsUnicode           = 1u << 7,
  }

  [Flags]
  private enum FileAttributes : uint {
    ReadOnly            = 0x00000001,
    Hidden              = 0x00000002,
    System              = 0x00000004,
    Directory           = 0x00000010,
    Archive             = 0x00000020,
    Normal              = 0x00000080,
  }

  /// <summary>
  /// Writes a .lnk pointing at <paramref name="targetPath"/>. Optional fields
  /// become Unicode StringData blocks; pass null to omit any of them.
  /// </summary>
  /// <param name="output">Target stream; not closed by this method.</param>
  /// <param name="targetPath">Absolute path the shortcut resolves to. Stored in
  ///   the LinkInfo's LocalBasePath as Latin1.</param>
  /// <param name="isDirectory">Sets the Directory bit in FileAttributes when the
  ///   target is a folder rather than a file.</param>
  /// <param name="targetSize">File size of the target (informational; 0 for
  ///   directories or when unknown).</param>
  /// <param name="relativePath">Optional relative path string block.</param>
  /// <param name="workingDir">Optional working-directory string block.</param>
  /// <param name="arguments">Optional command-line arguments string block.</param>
  /// <param name="iconLocation">Optional icon resource path string block.</param>
  public static void Write(
      Stream output,
      string targetPath,
      bool isDirectory = false,
      uint targetSize = 0,
      string? relativePath = null,
      string? workingDir = null,
      string? arguments = null,
      string? iconLocation = null) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(targetPath);

    using var ms = new MemoryStream();

    // ── Header (76 bytes). ────────────────────────────────────────────────
    var flags = LinkFlags.HasLinkInfo | LinkFlags.IsUnicode;
    if (relativePath != null) flags |= LinkFlags.HasRelativePath;
    if (workingDir != null) flags |= LinkFlags.HasWorkingDir;
    if (arguments != null) flags |= LinkFlags.HasArguments;
    if (iconLocation != null) flags |= LinkFlags.HasIconLocation;

    var fileAttrs = isDirectory ? FileAttributes.Directory : FileAttributes.Archive;

    Span<byte> header = stackalloc byte[76];
    BinaryPrimitives.WriteUInt32LittleEndian(header[..4], HeaderSize);
    LinkClsid.CopyTo(header[4..]);
    BinaryPrimitives.WriteUInt32LittleEndian(header[20..24], (uint)flags);
    BinaryPrimitives.WriteUInt32LittleEndian(header[24..28], (uint)fileAttrs);
    // CreationTime/AccessTime/WriteTime — leave zeroed (means "unset").
    BinaryPrimitives.WriteInt64LittleEndian(header[28..36], 0);
    BinaryPrimitives.WriteInt64LittleEndian(header[36..44], 0);
    BinaryPrimitives.WriteInt64LittleEndian(header[44..52], 0);
    BinaryPrimitives.WriteUInt32LittleEndian(header[52..56], targetSize);
    BinaryPrimitives.WriteInt32LittleEndian(header[56..60], 0); // IconIndex
    BinaryPrimitives.WriteInt32LittleEndian(header[60..64], 1); // ShowCommand = SW_SHOWNORMAL
    BinaryPrimitives.WriteUInt16LittleEndian(header[64..66], 0); // HotKey
    // Bytes 66..76 (Reserved1/2/3) left zero.
    ms.Write(header);

    // ── LinkInfo block (MS-SHLLINK §2.3). ─────────────────────────────────
    // Layout: LinkInfoSize(4) LinkInfoHeaderSize(4) LinkInfoFlags(4)
    //         VolumeIDOffset(4) LocalBasePathOffset(4) CommonNetworkRelativeLinkOffset(4)
    //         CommonPathSuffixOffset(4) VolumeID + LocalBasePath + CommonPathSuffix.
    // LinkInfoFlags = 0x00000001 (VolumeIDAndLocalBasePath).
    var localBasePathBytes = Encoding.Latin1.GetBytes(targetPath);
    const uint LinkInfoHeaderSize = 28;
    const uint VolumeIDSize = 17; // type(4) + serial(4) + offset_to_label(4) + drive_type(4) + label(1 NUL)

    var volumeIdOffset = LinkInfoHeaderSize;
    var localBasePathOffset = volumeIdOffset + VolumeIDSize;
    var commonNetworkRelativeLinkOffset = 0u;
    var commonPathSuffixOffset = localBasePathOffset + (uint)localBasePathBytes.Length + 1; // +1 NUL
    var linkInfoSize = commonPathSuffixOffset + 1; // CommonPathSuffix = "" (just NUL)

    var linkInfo = new byte[linkInfoSize];
    BinaryPrimitives.WriteUInt32LittleEndian(linkInfo.AsSpan(0, 4), linkInfoSize);
    BinaryPrimitives.WriteUInt32LittleEndian(linkInfo.AsSpan(4, 4), LinkInfoHeaderSize);
    BinaryPrimitives.WriteUInt32LittleEndian(linkInfo.AsSpan(8, 4), 0x00000001); // VolumeIDAndLocalBasePath
    BinaryPrimitives.WriteUInt32LittleEndian(linkInfo.AsSpan(12, 4), volumeIdOffset);
    BinaryPrimitives.WriteUInt32LittleEndian(linkInfo.AsSpan(16, 4), localBasePathOffset);
    BinaryPrimitives.WriteUInt32LittleEndian(linkInfo.AsSpan(20, 4), commonNetworkRelativeLinkOffset);
    BinaryPrimitives.WriteUInt32LittleEndian(linkInfo.AsSpan(24, 4), commonPathSuffixOffset);

    // VolumeID: minimal "fixed disk, unknown" record.
    var vol = linkInfo.AsSpan((int)volumeIdOffset);
    BinaryPrimitives.WriteUInt32LittleEndian(vol[..4], VolumeIDSize);
    BinaryPrimitives.WriteUInt32LittleEndian(vol[4..8], 3); // DRIVE_FIXED
    BinaryPrimitives.WriteUInt32LittleEndian(vol[8..12], 0); // DriveSerialNumber
    BinaryPrimitives.WriteUInt32LittleEndian(vol[12..16], 16); // VolumeLabelOffset → label region (1 NUL byte)
    vol[16] = 0; // empty label

    localBasePathBytes.CopyTo(linkInfo.AsSpan((int)localBasePathOffset));
    // Trailing NUL (already zero) and CommonPathSuffix NUL (already zero).

    ms.Write(linkInfo);

    // ── StringData blocks (Unicode). ──────────────────────────────────────
    WriteUnicodeString(ms, relativePath);
    WriteUnicodeString(ms, workingDir);
    WriteUnicodeString(ms, arguments);
    WriteUnicodeString(ms, iconLocation);

    // ── ExtraData terminator (BlockSize < 4). ─────────────────────────────
    Span<byte> term = stackalloc byte[4];
    ms.Write(term);

    var blob = ms.ToArray();
    output.Write(blob, 0, blob.Length);
  }

  /// <summary>
  /// Writes a CountedString StringData entry per MS-SHLLINK §2.4: u16 character
  /// count followed by that many UTF-16LE characters. Null inputs are skipped
  /// (the corresponding HasXxx flag must be clear in the header).
  /// </summary>
  private static void WriteUnicodeString(Stream ms, string? text) {
    if (text == null) return;
    var bytes = Encoding.Unicode.GetBytes(text);
    Span<byte> count = stackalloc byte[2];
    BinaryPrimitives.WriteUInt16LittleEndian(count, (ushort)text.Length);
    ms.Write(count);
    ms.Write(bytes);
  }
}
