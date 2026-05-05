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

  public static MzImage Read(ReadOnlySpan<byte> data) {
    if (data.Length < 28) throw new InvalidDataException("MZ: file shorter than 28-byte header.");
    if (data[0] != 'M' || data[1] != 'Z')
      if (data[0] != 'Z' || data[1] != 'M') // old Borland-style swap — accepted by COMMAND.COM
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

    // Extended header dispatch — e_lfanew at 0x3C (only meaningful when the MZ
    // header is large enough to reach that offset).
    uint eLfanew = 0;
    var extSig = "";
    if (data.Length >= 64 && headerParagraphs * 16 >= 64) {
      eLfanew = BinaryPrimitives.ReadUInt32LittleEndian(data[0x3C..]);
      if (eLfanew > 0 && eLfanew + 4 <= data.Length) {
        if (data[(int)eLfanew] == 'P' && data[(int)eLfanew + 1] == 'E') extSig = "PE";
        else if (data[(int)eLfanew] == 'N' && data[(int)eLfanew + 1] == 'E') extSig = "NE";
        else if (data[(int)eLfanew] == 'L' && data[(int)eLfanew + 1] == 'E') extSig = "LE";
        else if (data[(int)eLfanew] == 'L' && data[(int)eLfanew + 1] == 'X') extSig = "LX";
      }
    }

    var headerSize = Math.Min(data.Length, headerParagraphs * 16);
    // Image size per DOS loader rules: blocks_in_file * 512, with bytes_in_last subtracted
    // when it's non-zero (a non-zero value says the last block isn't full).
    long declaredImageSize = (long)blocks * 512;
    if (bytesInLast > 0) declaredImageSize -= 512 - bytesInLast;
    if (declaredImageSize > data.Length) declaredImageSize = data.Length;
    if (declaredImageSize < headerSize) declaredImageSize = headerSize;

    var header = data[..headerSize].ToArray();
    var body = data.Slice(headerSize, (int)(declaredImageSize - headerSize)).ToArray();
    var overlay = data[(int)declaredImageSize..].ToArray();

    return new MzImage(
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
      Header: header,
      Body: body,
      Overlay: overlay);
  }
}
