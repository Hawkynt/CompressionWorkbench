#pragma warning disable CS1591
using Compression.Registry;

namespace FileFormat.Cab;

/// <summary>
/// Walks CAB CFHEADER, CFFOLDER entries, CFFILE entries, and CFDATA blocks to emit
/// the byte-level layout of the cabinet archive as <see cref="DefragBlockInfo"/> tiles.
/// </summary>
public static class CabLayoutMap {

  public static IEnumerable<DefragBlockInfo> Enumerate(Stream archive) {
    ArgumentNullException.ThrowIfNull(archive);
    archive.Position = 0;

    if (archive.Length < CabConstants.HeaderSize)
      yield break;

    using var reader = new BinaryReader(archive, System.Text.Encoding.UTF8, leaveOpen: true);

    // Validate signature "MSCF"
    var sig = reader.ReadBytes(4);
    if (sig.Length < 4 || sig[0] != 0x4D || sig[1] != 0x53 || sig[2] != 0x43 || sig[3] != 0x46)
      yield break;

    var reserved1 = reader.ReadUInt32();
    var cbCabinet = reader.ReadUInt32();
    var reserved2 = reader.ReadUInt32();
    var coffFiles = reader.ReadUInt32();
    var reserved3 = reader.ReadUInt32();
    var verMinor = reader.ReadByte();
    var verMajor = reader.ReadByte();
    var cFolders = reader.ReadUInt16();
    var cFiles = reader.ReadUInt16();
    var flags = reader.ReadUInt16();
    var setId = reader.ReadUInt16();
    var iCabinet = reader.ReadUInt16();

    // Skip reserve fields if present
    byte cbFolderReserve = 0, cbDataReserve = 0;
    if ((flags & CabConstants.FlagReserveFields) != 0) {
      var cbCabinetReserve = reader.ReadUInt16();
      cbFolderReserve = reader.ReadByte();
      cbDataReserve = reader.ReadByte();
      reader.ReadBytes(cbCabinetReserve);
    }

    var headerEnd = archive.Position;

    // Emit CFHEADER tile
    yield return new DefragBlockInfo(0, headerEnd, DefragBlockKind.MetadataReserved,
      FileName: "CFHEADER");

    // Read CFFOLDER entries
    var folders = new List<(uint DataOffset, ushort DataCount, ushort CompType)>();
    var foldersStart = archive.Position;
    for (var i = 0; i < cFolders; ++i) {
      var coffCabStart = reader.ReadUInt32();
      var cCFData = reader.ReadUInt16();
      var typeCompress = reader.ReadUInt16();
      if (cbFolderReserve > 0)
        reader.ReadBytes(cbFolderReserve);
      folders.Add((coffCabStart, cCFData, typeCompress));
    }
    var foldersEnd = archive.Position;

    if (foldersEnd > foldersStart) {
      yield return new DefragBlockInfo(foldersStart, foldersEnd - foldersStart,
        DefragBlockKind.MetadataReserved, FileName: $"CFFOLDER entries ({cFolders})");
    }

    // Seek to first CFFILE
    archive.Position = coffFiles;
    var cfFileStart = (long)coffFiles;
    for (var i = 0; i < cFiles; ++i) {
      var entryStart = archive.Position;
      try {
        reader.ReadUInt32(); // cbFile
        reader.ReadUInt32(); // uoffFolderStart
        reader.ReadUInt16(); // iFolderIdx
        reader.ReadUInt16(); // date
        reader.ReadUInt16(); // time
        reader.ReadUInt16(); // attribs

        // Read null-terminated filename
        byte b;
        while ((b = reader.ReadByte()) != 0) { }
      } catch {
        break;
      }
    }
    var cfFileEnd = archive.Position;

    if (cfFileEnd > cfFileStart) {
      yield return new DefragBlockInfo(cfFileStart, cfFileEnd - cfFileStart,
        DefragBlockKind.MetadataReserved, FileName: $"CFFILE entries ({cFiles})");
    }

    // Emit CFDATA blocks per folder
    for (var fi = 0; fi < folders.Count; ++fi) {
      var (dataOffset, dataCount, compType) = folders[fi];
      archive.Position = dataOffset;

      var methodName = (CabCompressionType)(compType & 0x000F) switch {
        CabCompressionType.None => "Store",
        CabCompressionType.MsZip => "MSZIP",
        CabCompressionType.Quantum => "Quantum",
        CabCompressionType.Lzx => "LZX",
        _ => $"Method {compType & 0x000F}",
      };

      var dataTiles = new List<DefragBlockInfo>();
      for (var di = 0; di < dataCount; ++di) {
        var blockStart = archive.Position;
        if (blockStart + 8 > archive.Length) break;

        try {
          reader.ReadUInt32(); // checksum
          var cbData = reader.ReadUInt16();
          reader.ReadUInt16(); // cbUncomp
          if (cbDataReserve > 0)
            reader.ReadBytes(cbDataReserve);

          // Accumulate 8-byte CFDATA header as metadata
          var dataHeaderSize = 8 + cbDataReserve;
          dataTiles.Add(new DefragBlockInfo(blockStart, dataHeaderSize,
            DefragBlockKind.MetadataReserved,
            FileName: $"CFDATA header (folder {fi}, block {di})"));

          // Accumulate compressed data as Used
          if (cbData > 0) {
            var dataStart = blockStart + dataHeaderSize;
            dataTiles.Add(new DefragBlockInfo(dataStart, cbData,
              DefragBlockKind.Used,
              FileName: $"Folder {fi} data ({methodName})",
              Classification: ClassifyMethod((CabCompressionType)(compType & 0x000F))));
            archive.Position = dataStart + cbData;
          }
        } catch {
          break;
        }
      }
      foreach (var tile in dataTiles)
        yield return tile;
    }
  }

  private static DefragBlockClass ClassifyMethod(CabCompressionType method) => method switch {
    CabCompressionType.None => DefragBlockClass.Frozen,
    CabCompressionType.MsZip => DefragBlockClass.Normal,
    CabCompressionType.Quantum => DefragBlockClass.Cold,
    CabCompressionType.Lzx => DefragBlockClass.Hot,
    _ => DefragBlockClass.Normal,
  };
}
