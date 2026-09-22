#pragma warning disable CS1591
using System.Buffers.Binary;

namespace FileFormat.Mz;

/// <summary>
/// Reader for pure DOS MZ executables. Splits the file into its three logical
/// regions — the fixed-size header + relocation table, the program image, and any
/// trailing overlay (installer payloads, appended archives, SCUMM resources, etc.).
/// </summary>
/// <remarks>
/// PE/NE/LE/LX executables also start with 'MZ'; consumers should prefer the
/// format-specific readers for those (PE at <c>e_lfanew</c>). This reader is the
/// fallback for files where <c>e_lfanew</c> is zero, points past the file end,
/// or points to a signature other than "PE\0\0" / "NE" / "LE" / "LX".
/// </remarks>
public sealed class MzReader {

  /// <summary>
  /// Represents a mz image.
  /// </summary>
  public sealed record MzImage(
    ushort BytesInLastBlock,
    ushort BlocksInFile,
    ushort NumRelocs,
    ushort HeaderParagraphs,
    ushort MinExtraParagraphs,
    ushort MaxExtraParagraphs,
    ushort InitialSs,
    ushort InitialSp,
    ushort Checksum,
    ushort InitialIp,
    ushort InitialCs,
    ushort RelocTableOffset,
    ushort OverlayNumber,
    uint ExtendedHeaderOffset,  // e_lfanew — 0 when the file is a pure MZ
    string ExtendedSignature,    // "" for pure MZ, "PE", "NE", "LE", or "LX" otherwise
    byte[] Header,               // bytes 0 .. headerSize
    byte[] Body,                 // bytes headerSize .. imageSize
    byte[] Overlay               // bytes imageSize .. eof (can be empty)
  );

  internal readonly record struct MzLayout(
    ushort BytesInLastBlock,
    ushort BlocksInFile,
    ushort NumRelocs,
    ushort HeaderParagraphs,
    ushort MinExtraParagraphs,
    ushort MaxExtraParagraphs,
    ushort InitialSs,
    ushort InitialSp,
    ushort Checksum,
    ushort InitialIp,
    ushort InitialCs,
    ushort RelocTableOffset,
    ushort OverlayNumber,
    uint ExtendedHeaderOffset,
    string ExtendedSignature,
    int HeaderLength,
    int BodyOffset,
    int BodyLength,
    int OverlayOffset,
    int OverlayLength);

  /// <summary>
  /// Reads the value from the supplied input.
  /// </summary>
  public static MzImage Read(ReadOnlySpan<byte> data) {
    var layout = ReadLayout(data);
    return new MzImage(
      BytesInLastBlock: layout.BytesInLastBlock,
      BlocksInFile: layout.BlocksInFile,
      NumRelocs: layout.NumRelocs,
      HeaderParagraphs: layout.HeaderParagraphs,
      MinExtraParagraphs: layout.MinExtraParagraphs,
      MaxExtraParagraphs: layout.MaxExtraParagraphs,
      InitialSs: layout.InitialSs,
      InitialSp: layout.InitialSp,
      Checksum: layout.Checksum,
      InitialIp: layout.InitialIp,
      InitialCs: layout.InitialCs,
      RelocTableOffset: layout.RelocTableOffset,
      OverlayNumber: layout.OverlayNumber,
      ExtendedHeaderOffset: layout.ExtendedHeaderOffset,
      ExtendedSignature: layout.ExtendedSignature,
      Header: data[..layout.HeaderLength].ToArray(),
      Body: data.Slice(layout.BodyOffset, layout.BodyLength).ToArray(),
      Overlay: data.Slice(layout.OverlayOffset, layout.OverlayLength).ToArray());
  }

  internal static MzLayout ReadLayout(ReadOnlySpan<byte> data) {
    if (data.Length < 28) throw new InvalidDataException("MZ: file shorter than 28-byte header.");
    if (data[0] != 'M' || data[1] != 'Z')
      if (data[0] != 'Z' || data[1] != 'M')
        throw new InvalidDataException($"MZ: unexpected magic 0x{data[0]:X2}{data[1]:X2}");

    var bytesInLast = BinaryPrimitives.ReadUInt16LittleEndian(data[2..]);
    var blocks = BinaryPrimitives.ReadUInt16LittleEndian(data[4..]);
    var numRelocs = BinaryPrimitives.ReadUInt16LittleEndian(data[6..]);
    var headerParagraphs = BinaryPrimitives.ReadUInt16LittleEndian(data[8..]);
    var minExtra = BinaryPrimitives.ReadUInt16LittleEndian(data[10..]);
    var maxExtra = BinaryPrimitives.ReadUInt16LittleEndian(data[12..]);
    var ss = BinaryPrimitives.ReadUInt16LittleEndian(data[14..]);
    var sp = BinaryPrimitives.ReadUInt16LittleEndian(data[16..]);
    var checksum = BinaryPrimitives.ReadUInt16LittleEndian(data[18..]);
    var ip = BinaryPrimitives.ReadUInt16LittleEndian(data[20..]);
    var cs = BinaryPrimitives.ReadUInt16LittleEndian(data[22..]);
    var relocOffset = BinaryPrimitives.ReadUInt16LittleEndian(data[24..]);
    var overlayNum = BinaryPrimitives.ReadUInt16LittleEndian(data[26..]);

    uint eLfanew = 0;
    var extSig = "";
    if (data.Length >= 64 && headerParagraphs * 16 >= 64) {
      eLfanew = BinaryPrimitives.ReadUInt32LittleEndian(data[0x3C..]);
      if (eLfanew is > 0 and <= int.MaxValue) {
        var offset = (int)eLfanew;
        if (offset <= data.Length - 4) {
          if (data[offset] == 'P' && data[offset + 1] == 'E') extSig = "PE";
          else if (data[offset] == 'N' && data[offset + 1] == 'E') extSig = "NE";
          else if (data[offset] == 'L' && data[offset + 1] == 'E') extSig = "LE";
          else if (data[offset] == 'L' && data[offset + 1] == 'X') extSig = "LX";
        }
      }
    }

    var headerLength = Math.Min(data.Length, headerParagraphs * 16);
    long declaredImageSize = (long)blocks * 512;
    if (bytesInLast > 0) declaredImageSize -= 512 - bytesInLast;
    declaredImageSize = Math.Clamp(declaredImageSize, headerLength, data.Length);

    var imageEnd = checked((int)declaredImageSize);
    return new MzLayout(
      BytesInLastBlock: bytesInLast,
      BlocksInFile: blocks,
      NumRelocs: numRelocs,
      HeaderParagraphs: headerParagraphs,
      MinExtraParagraphs: minExtra,
      MaxExtraParagraphs: maxExtra,
      InitialSs: ss,
      InitialSp: sp,
      Checksum: checksum,
      InitialIp: ip,
      InitialCs: cs,
      RelocTableOffset: relocOffset,
      OverlayNumber: overlayNum,
      ExtendedHeaderOffset: eLfanew,
      ExtendedSignature: extSig,
      HeaderLength: headerLength,
      BodyOffset: headerLength,
      BodyLength: imageEnd - headerLength,
      OverlayOffset: imageEnd,
      OverlayLength: data.Length - imageEnd);
  }
}
