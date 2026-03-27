using System.Text;
using Compression.Core.Checksums;
using Compression.Core.Dictionary.Lzma;

namespace FileFormat.SevenZip;

/// <summary>
/// Reads and writes the 7z archive header structure, including PackInfo,
/// UnpackInfo (folders/coders), SubStreamsInfo, and FilesInfo sections.
/// </summary>
internal static class SevenZipHeaderCodec {
  /// <summary>
  /// Reads a complete 7z header from a stream, handling both plain and encoded headers.
  /// </summary>
  /// <param name="stream">The stream positioned at the header data.</param>
  /// <param name="password">Optional password for decrypting encrypted headers.</param>
  /// <param name="archiveStream">The seekable archive stream for reading packed encoded-header data. Required when headers are encrypted.</param>
  /// <returns>The parsed header components.</returns>
  public static (SevenZipPackInfo PackInfo, List<SevenZipFolder> Folders,
    SevenZipSubStreamsInfo SubStreams, List<SevenZipFileInfo> Files)
    ReadHeader(Stream stream, string? password = null, Stream? archiveStream = null) {
    var id = ReadByte(stream);

    if (id == SevenZipConstants.IdEncodedHeader) {
      // Decompress the encoded header and parse the result
      var decodedHeader = DecodeEncodedHeader(stream, password, archiveStream);
      using var decodedStream = new MemoryStream(decodedHeader);
      return ReadHeader(decodedStream, password, archiveStream);
    }

    if (id != SevenZipConstants.IdHeader)
      throw new InvalidDataException($"Expected Header or EncodedHeader, got 0x{id:X2}.");

    SevenZipPackInfo? packInfo = null;
    List<SevenZipFolder>? folders = null;
    SevenZipSubStreamsInfo? subStreams = null;
    List<SevenZipFileInfo>? files = null;

    while (true) {
      id = ReadByte(stream);
      if (id == SevenZipConstants.IdEnd)
        break;

      switch (id) {
        case SevenZipConstants.IdMainStreams:
          (packInfo, folders, subStreams) = ReadMainStreams(stream);
          break;
        case SevenZipConstants.IdFilesInfo:
          files = ReadFilesInfo(stream);
          break;
        default:
          SkipData(stream);
          break;
      }
    }

    return (
      packInfo ?? new SevenZipPackInfo(),
      folders ?? [],
      subStreams ?? new SevenZipSubStreamsInfo(),
      files ?? []
    );
  }

  /// <summary>
  /// Writes a complete 7z header to a stream.
  /// </summary>
  /// <param name="stream">The stream to write to.</param>
  /// <param name="packInfo">The pack info describing compressed data.</param>
  /// <param name="folders">The folder definitions.</param>
  /// <param name="subStreams">The sub-stream info for per-file sizes.</param>
  /// <param name="files">The file metadata.</param>
  public static void WriteHeader(Stream stream, SevenZipPackInfo packInfo,
    List<SevenZipFolder> folders, SevenZipSubStreamsInfo subStreams,
    List<SevenZipFileInfo> files) {
    stream.WriteByte(SevenZipConstants.IdHeader);

    // Write MainStreams if there are folders
    if (folders.Count > 0)
      WriteMainStreams(stream, packInfo, folders, subStreams);

    // Write FilesInfo
    if (files.Count > 0)
      WriteFilesInfo(stream, files);

    stream.WriteByte(SevenZipConstants.IdEnd);
  }

  /// <summary>
  /// Writes an encoded header descriptor that references packed (compressed/encrypted) header data.
  /// </summary>
  public static void WriteEncodedHeader(Stream stream, SevenZipPackInfo packInfo,
      List<SevenZipFolder> folders) {
    stream.WriteByte(SevenZipConstants.IdEncodedHeader);
    WritePackInfo(stream, packInfo);
    WriteUnpackInfo(stream, folders);
    stream.WriteByte(SevenZipConstants.IdEnd);
  }

  // ---- MainStreams ----

  private static (SevenZipPackInfo PackInfo, List<SevenZipFolder> Folders,
    SevenZipSubStreamsInfo SubStreams) ReadMainStreams(Stream stream) {
    SevenZipPackInfo? packInfo = null;
    List<SevenZipFolder>? folders = null;
    SevenZipSubStreamsInfo? subStreams = null;

    while (true) {
      var id = ReadByte(stream);
      if (id == SevenZipConstants.IdEnd)
        break;

      switch (id) {
        case SevenZipConstants.IdPackInfo:
          packInfo = ReadPackInfo(stream);
          break;
        case SevenZipConstants.IdUnpackInfo:
          folders = ReadUnpackInfo(stream);
          break;
        case SevenZipConstants.IdSubStreamsInfo:
          subStreams = ReadSubStreamsInfo(stream, folders ?? []);
          break;
        default:
          SkipData(stream);
          break;
      }
    }

    return (
      packInfo ?? new SevenZipPackInfo(),
      folders ?? [],
      subStreams ?? new SevenZipSubStreamsInfo()
    );
  }

  private static void WriteMainStreams(Stream stream, SevenZipPackInfo packInfo,
    List<SevenZipFolder> folders, SevenZipSubStreamsInfo subStreams) {
    stream.WriteByte(SevenZipConstants.IdMainStreams);

    WritePackInfo(stream, packInfo);
    WriteUnpackInfo(stream, folders);
    WriteSubStreamsInfo(stream, subStreams, folders);

    stream.WriteByte(SevenZipConstants.IdEnd);
  }

  // ---- PackInfo ----

  private static SevenZipPackInfo ReadPackInfo(Stream stream) {
    var packInfo = new SevenZipPackInfo();
    packInfo.PackPos = (long)SevenZipVarInt.Read(stream);
    var numPackStreams = (int)SevenZipVarInt.Read(stream);
    packInfo.PackSizes = new long[numPackStreams];
    packInfo.PackCrcs = new uint?[numPackStreams];

    while (true) {
      var id = ReadByte(stream);
      if (id == SevenZipConstants.IdEnd)
        break;

      switch (id) {
        case SevenZipConstants.IdSize:
          for (var i = 0; i < numPackStreams; ++i)
            packInfo.PackSizes[i] = (long)SevenZipVarInt.Read(stream);
          break;
        case SevenZipConstants.IdCrc:
          ReadCrcs(stream, numPackStreams, packInfo.PackCrcs);
          break;
        default:
          SkipData(stream);
          break;
      }
    }

    return packInfo;
  }

  private static void WritePackInfo(Stream stream, SevenZipPackInfo packInfo) {
    stream.WriteByte(SevenZipConstants.IdPackInfo);

    SevenZipVarInt.Write(stream, (ulong)packInfo.PackPos);
    SevenZipVarInt.Write(stream, (ulong)packInfo.PackSizes.Length);

    // Sizes
    stream.WriteByte(SevenZipConstants.IdSize);
    for (var i = 0; i < packInfo.PackSizes.Length; ++i)
      SevenZipVarInt.Write(stream, (ulong)packInfo.PackSizes[i]);

    stream.WriteByte(SevenZipConstants.IdEnd);
  }

  // ---- UnpackInfo (Folders) ----

  private static List<SevenZipFolder> ReadUnpackInfo(Stream stream) {
    var id = ReadByte(stream);
    if (id != SevenZipConstants.IdFolder)
      throw new InvalidDataException($"Expected IdFolder, got 0x{id:X2}.");

    var numFolders = (int)SevenZipVarInt.Read(stream);
    var external = ReadByte(stream);
    if (external != 0)
      throw new NotSupportedException("External folder references not supported.");

    var folders = new List<SevenZipFolder>(numFolders);
    for (var i = 0; i < numFolders; ++i)
      folders.Add(ReadFolder(stream));

    // Read additional properties
    while (true) {
      id = ReadByte(stream);
      if (id == SevenZipConstants.IdEnd)
        break;

      switch (id) {
        case SevenZipConstants.IdCodersUnpackSize:
          foreach (var folder in folders) {
            var numOutStreams = 0;
            foreach (var coder in folder.Coders)
              numOutStreams += coder.NumOutStreams;
            folder.UnpackSizes = new long[numOutStreams];
            for (var j = 0; j < numOutStreams; ++j)
              folder.UnpackSizes[j] = (long)SevenZipVarInt.Read(stream);
          }

          break;
        case SevenZipConstants.IdCrc:
          ReadFolderCrcs(stream, folders);
          break;
        default:
          SkipData(stream);
          break;
      }
    }

    return folders;
  }

  private static SevenZipFolder ReadFolder(Stream stream) {
    var folder = new SevenZipFolder();
    var numCoders = (int)SevenZipVarInt.Read(stream);

    var totalInStreams = 0;
    var totalOutStreams = 0;

    for (var i = 0; i < numCoders; ++i) {
      var coder = new SevenZipCoder();
      var flags = ReadByte(stream);

      var codecIdSize = flags & 0x0F;
      coder.CodecId = new byte[codecIdSize];
      ReadExact(stream, coder.CodecId, 0, codecIdSize);

      if ((flags & 0x10) != 0) {
        coder.NumInStreams = (int)SevenZipVarInt.Read(stream);
        coder.NumOutStreams = (int)SevenZipVarInt.Read(stream);
      }

      if ((flags & 0x20) != 0) {
        var propsSize = (int)SevenZipVarInt.Read(stream);
        coder.Properties = new byte[propsSize];
        ReadExact(stream, coder.Properties, 0, propsSize);
      }

      folder.Coders.Add(coder);
      totalInStreams += coder.NumInStreams;
      totalOutStreams += coder.NumOutStreams;
    }

    // Bind pairs
    var numBindPairs = totalOutStreams - 1;
    for (var i = 0; i < numBindPairs; ++i) {
      var inIndex = (int)SevenZipVarInt.Read(stream);
      var outIndex = (int)SevenZipVarInt.Read(stream);
      folder.BindPairs.Add((inIndex, outIndex));
    }

    // Pack streams (if more than one unbound input stream)
    var numPackStreams = totalInStreams - numBindPairs;
    if (numPackStreams > 1) {
      for (var i = 0; i < numPackStreams; ++i) {
        _ = SevenZipVarInt.Read(stream); // pack stream index
      }
    }

    return folder;
  }

  private static void WriteUnpackInfo(Stream stream, List<SevenZipFolder> folders) {
    stream.WriteByte(SevenZipConstants.IdUnpackInfo);

    stream.WriteByte(SevenZipConstants.IdFolder);
    SevenZipVarInt.Write(stream, (ulong)folders.Count);
    stream.WriteByte(0); // not external

    foreach (var folder in folders)
      WriteFolder(stream, folder);

    // Coders unpack sizes
    stream.WriteByte(SevenZipConstants.IdCodersUnpackSize);
    foreach (var folder in folders) {
      foreach (var size in folder.UnpackSizes)
        SevenZipVarInt.Write(stream, (ulong)size);
    }

    // Note: Folder CRCs are NOT written in UnpackInfo. They are only stored in
    // SubStreamsInfo as per-file digests. Writing them here causes 7-Zip 26+ to
    // reject the archive when the folder has only one stream, because 7-Zip
    // expects the folder CRC in SubStreamsInfo, not duplicated in UnpackInfo.

    stream.WriteByte(SevenZipConstants.IdEnd);
  }

  private static void WriteFolder(Stream stream, SevenZipFolder folder) {
    SevenZipVarInt.Write(stream, (ulong)folder.Coders.Count);

    foreach (var coder in folder.Coders) {
      var flags = (byte)(coder.CodecId.Length & 0x0F);
      var hasStreamCounts = coder.NumInStreams != 1 || coder.NumOutStreams != 1;
      if (hasStreamCounts) flags |= 0x10;
      if (coder.Properties != null && coder.Properties.Length > 0) flags |= 0x20;

      stream.WriteByte(flags);
      stream.Write(coder.CodecId, 0, coder.CodecId.Length);

      if (hasStreamCounts) {
        SevenZipVarInt.Write(stream, (ulong)coder.NumInStreams);
        SevenZipVarInt.Write(stream, (ulong)coder.NumOutStreams);
      }

      if (coder.Properties != null && coder.Properties.Length > 0) {
        SevenZipVarInt.Write(stream, (ulong)coder.Properties.Length);
        stream.Write(coder.Properties, 0, coder.Properties.Length);
      }
    }

    // Bind pairs (totalOutStreams - 1)
    var totalInStreams = 0;
    var totalOutStreams = 0;
    foreach (var coder in folder.Coders) {
      totalInStreams += coder.NumInStreams;
      totalOutStreams += coder.NumOutStreams;
    }
    var numBindPairs = totalOutStreams - 1;
    for (var i = 0; i < numBindPairs; ++i) {
      SevenZipVarInt.Write(stream, (ulong)folder.BindPairs[i].InIndex);
      SevenZipVarInt.Write(stream, (ulong)folder.BindPairs[i].OutIndex);
    }

    // Pack stream indices (when more than one unbound input stream)
    var numPackStreams = totalInStreams - numBindPairs;
    if (numPackStreams > 1) {
      // Find unbound input streams (not targets of any bind pair)
      var boundIn = new HashSet<int>();
      foreach (var (inIdx, _) in folder.BindPairs)
        boundIn.Add(inIdx);
      for (var i = 0; i < totalInStreams; ++i) {
        if (!boundIn.Contains(i))
          SevenZipVarInt.Write(stream, (ulong)i);
      }
    }
  }

  // ---- SubStreamsInfo ----

  private static SevenZipSubStreamsInfo ReadSubStreamsInfo(Stream stream,
    List<SevenZipFolder> folders) {
    var subStreams = new SevenZipSubStreamsInfo();
    subStreams.NumUnpackStreams = new int[folders.Count];
    for (var i = 0; i < folders.Count; ++i)
      subStreams.NumUnpackStreams[i] = 1; // default

    var unpackSizes = new List<long>();
    var digests = new List<uint>();

    while (true) {
      var id = ReadByte(stream);
      if (id == SevenZipConstants.IdEnd)
        break;

      switch (id) {
        case SevenZipConstants.IdNumUnpackStreams:
          for (var i = 0; i < folders.Count; ++i)
            subStreams.NumUnpackStreams[i] = (int)SevenZipVarInt.Read(stream);
          break;
        case SevenZipConstants.IdSize:
          for (var i = 0; i < folders.Count; ++i) {
            var numStreams = subStreams.NumUnpackStreams[i];
            if (numStreams <= 1) {
              // Single-stream folder: size is implied from folder unpack size
              if (numStreams == 1 && folders[i].UnpackSizes.Length > 0)
                unpackSizes.Add(folders[i].UnpackSizes[^1]);
              continue;
            }

            var sum = 0L;
            for (var j = 0; j < numStreams - 1; ++j) {
              var size = (long)SevenZipVarInt.Read(stream);
              unpackSizes.Add(size);
              sum += size;
            }

            // Last stream size is implied (folder unpack size - sum)
            var folder = folders[i];
            var lastSize = folder.UnpackSizes.Length > 0
              ? folder.UnpackSizes[^1] - sum
              : 0;
            unpackSizes.Add(lastSize);
          }

          break;
        case SevenZipConstants.IdCrc:
          var totalStreams = 0;
          for (var i = 0; i < folders.Count; ++i)
            totalStreams += subStreams.NumUnpackStreams[i];
          var crcs = new uint?[totalStreams];
          ReadCrcs(stream, totalStreams, crcs);
          foreach (var crc in crcs) {
            if (crc.HasValue)
              digests.Add(crc.Value);
          }

          break;
        default:
          SkipData(stream);
          break;
      }
    }

    // If no explicit sizes were read, derive from folder unpack sizes (1 file per folder)
    if (unpackSizes.Count == 0) {
      for (var i = 0; i < folders.Count; ++i) {
        var numStreams = subStreams.NumUnpackStreams[i];
        if (numStreams == 1 && folders[i].UnpackSizes.Length > 0)
          unpackSizes.Add(folders[i].UnpackSizes[^1]);
        else if (numStreams == 0) {
          // No streams in this folder
        }
      }
    }

    subStreams.UnpackSizes = [.. unpackSizes];
    subStreams.Digests = [.. digests];

    return subStreams;
  }

  private static void WriteSubStreamsInfo(Stream stream, SevenZipSubStreamsInfo subStreams,
    List<SevenZipFolder> folders) {
    stream.WriteByte(SevenZipConstants.IdSubStreamsInfo);

    // NumUnpackStreams (omit when all folders have exactly 1 stream — that's the default)
    var allSingle = true;
    for (var i = 0; i < folders.Count; ++i) {
      if (subStreams.NumUnpackStreams[i] != 1) {
        allSingle = false;
        break;
      }
    }

    if (!allSingle) {
      stream.WriteByte(SevenZipConstants.IdNumUnpackStreams);
      for (var i = 0; i < folders.Count; ++i)
        SevenZipVarInt.Write(stream, (ulong)subStreams.NumUnpackStreams[i]);
    }

    // Sizes (for folders with more than 1 stream, write all but the last)
    var hasSizes = false;
    for (var i = 0; i < folders.Count; ++i) {
      if (subStreams.NumUnpackStreams[i] > 1) {
        hasSizes = true;
        break;
      }
    }

    if (hasSizes) {
      stream.WriteByte(SevenZipConstants.IdSize);
      var sizeIndex = 0;
      for (var i = 0; i < folders.Count; ++i) {
        var numStreams = subStreams.NumUnpackStreams[i];
        for (var j = 0; j < numStreams - 1; ++j) {
          SevenZipVarInt.Write(stream, (ulong)subStreams.UnpackSizes[sizeIndex]);
          ++sizeIndex;
        }

        ++sizeIndex; // skip the last (implied) size
      }
    }

    // CRCs
    if (subStreams.Digests.Length > 0) {
      stream.WriteByte(SevenZipConstants.IdCrc);
      // All defined
      WriteBoolVector(stream, subStreams.Digests.Length, _ => true);
      foreach (var crc in subStreams.Digests)
        WriteUInt32Le(stream, crc);
    }

    stream.WriteByte(SevenZipConstants.IdEnd);
  }

  // ---- FilesInfo ----

  private static List<SevenZipFileInfo> ReadFilesInfo(Stream stream) {
    var numFiles = (int)SevenZipVarInt.Read(stream);
    var files = new List<SevenZipFileInfo>(numFiles);
    for (var i = 0; i < numFiles; ++i)
      files.Add(new SevenZipFileInfo());

    while (true) {
      var id = ReadByte(stream);
      if (id == SevenZipConstants.IdEnd)
        break;

      var propSize = (long)SevenZipVarInt.Read(stream);
      var startPos = stream.Position;

      switch (id) {
        case SevenZipConstants.IdName:
          ReadNames(stream, files);
          break;
        case SevenZipConstants.IdEmptyStream:
          ReadBoolVectorInto(stream, files, (f, v) => f.IsEmptyStream = v);
          // Mark empty streams as directories by default;
          // IdEmptyFile will override for actual empty files
          foreach (var file in files) {
            if (file.IsEmptyStream)
              file.IsDirectory = true;
          }

          break;
        case SevenZipConstants.IdEmptyFile: {
          // EmptyFile applies only to entries that are EmptyStreams
          var emptyStreamFiles = files.Where(f => f.IsEmptyStream).ToList();
          var emptyFileBits = (byte)0;
          for (var i = 0; i < emptyStreamFiles.Count; ++i) {
            if (i % 8 == 0)
              emptyFileBits = ReadByte(stream);
            var isEmptyFile = (emptyFileBits & (0x80 >> (i % 8))) != 0;
            if (isEmptyFile) {
              emptyStreamFiles[i].IsEmptyFile = true;
              emptyStreamFiles[i].IsDirectory = false;
            }
          }

          break;
        }

        case SevenZipConstants.IdMTime:
          ReadTimes(stream, files, (f, t) => f.LastWriteTime = t);
          break;
        case SevenZipConstants.IdCTime:
          ReadTimes(stream, files, (f, t) => f.CreationTime = t);
          break;
        case SevenZipConstants.IdATime:
          ReadTimes(stream, files, (f, t) => f.LastAccessTime = t);
          break;
        case SevenZipConstants.IdAttributes:
          ReadAttributes(stream, files);
          break;
        default:
          // Skip unknown property
          var remaining = propSize - (stream.Position - startPos);
          if (remaining > 0)
            stream.Position += remaining;
          break;
      }

      // Ensure we consumed exactly propSize bytes
      var consumed = stream.Position - startPos;
      if (consumed < propSize)
        stream.Position = startPos + propSize;
    }

    return files;
  }

  private static void ReadNames(Stream stream, List<SevenZipFileInfo> files) {
    var external = ReadByte(stream);
    if (external != 0)
      throw new NotSupportedException("External name references not supported.");

    foreach (var file in files) {
      var nameBytes = new List<byte>();
      while (true) {
        var lo = stream.ReadByte();
        var hi = stream.ReadByte();
        if (lo < 0 || hi < 0)
          throw new EndOfStreamException("Unexpected end of stream reading file name.");
        if (lo == 0 && hi == 0)
          break;
        nameBytes.Add((byte)lo);
        nameBytes.Add((byte)hi);
      }

      file.Name = Encoding.Unicode.GetString([.. nameBytes]);
    }
  }

  private static void ReadTimes(Stream stream, List<SevenZipFileInfo> files,
    Action<SevenZipFileInfo, DateTime> setter) {
    // "AllDefined" bit + optional bit vector + data
    var defined = ReadOptionalBoolVector(stream, files.Count);

    var external = ReadByte(stream);
    if (external != 0)
      throw new NotSupportedException("External time references not supported.");

    for (var i = 0; i < files.Count; ++i) {
      if (defined[i]) {
        var buf = new byte[8];
        ReadExact(stream, buf, 0, 8);
        var fileTime = BitConverter.ToInt64(buf, 0);
        setter(files[i], DateTime.FromFileTimeUtc(fileTime));
      }
    }
  }

  private static void ReadAttributes(Stream stream, List<SevenZipFileInfo> files) {
    var defined = ReadOptionalBoolVector(stream, files.Count);

    var external = ReadByte(stream);
    if (external != 0)
      throw new NotSupportedException("External attribute references not supported.");

    for (var i = 0; i < files.Count; ++i) {
      if (defined[i]) {
        var buf = new byte[4];
        ReadExact(stream, buf, 0, 4);
        files[i].Attributes = BitConverter.ToUInt32(buf, 0);
      }
    }
  }

  private static void WriteFilesInfo(Stream stream, List<SevenZipFileInfo> files) {
    stream.WriteByte(SevenZipConstants.IdFilesInfo);
    SevenZipVarInt.Write(stream, (ulong)files.Count);

    // Names
    WriteNames(stream, files);

    // EmptyStream
    var hasEmptyStreams = files.Any(f => f.IsEmptyStream);
    if (hasEmptyStreams) {
      using var emptyStreamData = new MemoryStream();
      WriteBoolVectorBits(emptyStreamData, files.Count, i => files[i].IsEmptyStream);
      WritePropWithSize(stream, SevenZipConstants.IdEmptyStream, emptyStreamData);

      // EmptyFile (among empty streams, which are empty files vs directories)
      var emptyStreamFiles = files.Where(f => f.IsEmptyStream).ToList();
      var hasEmptyFiles = emptyStreamFiles.Any(f => f.IsEmptyFile);
      if (hasEmptyFiles) {
        using var emptyFileData = new MemoryStream();
        WriteBoolVectorBits(emptyFileData, emptyStreamFiles.Count,
          i => emptyStreamFiles[i].IsEmptyFile);
        WritePropWithSize(stream, SevenZipConstants.IdEmptyFile, emptyFileData);
      }
    }

    // MTime
    var hasMTimes = files.Any(f => f.LastWriteTime.HasValue);
    if (hasMTimes) {
      using var mtimeData = new MemoryStream();
      WriteTimes(mtimeData, files, f => f.LastWriteTime);
      WritePropWithSize(stream, SevenZipConstants.IdMTime, mtimeData);
    }

    // CTime
    var hasCTimes = files.Any(f => f.CreationTime.HasValue);
    if (hasCTimes) {
      using var ctimeData = new MemoryStream();
      WriteTimes(ctimeData, files, f => f.CreationTime);
      WritePropWithSize(stream, SevenZipConstants.IdCTime, ctimeData);
    }

    // Attributes
    var hasAttrs = files.Any(f => f.Attributes.HasValue);
    if (hasAttrs) {
      using var attrData = new MemoryStream();
      WriteAttributes(attrData, files);
      WritePropWithSize(stream, SevenZipConstants.IdAttributes, attrData);
    }

    stream.WriteByte(SevenZipConstants.IdEnd);
  }

  private static void WriteNames(Stream stream, List<SevenZipFileInfo> files) {
    using var nameData = new MemoryStream();
    nameData.WriteByte(0); // external = false

    foreach (var file in files) {
      var nameBytes = Encoding.Unicode.GetBytes(file.Name);
      nameData.Write(nameBytes, 0, nameBytes.Length);
      nameData.WriteByte(0); // null terminator (2 bytes, UTF-16LE)
      nameData.WriteByte(0);
    }

    WritePropWithSize(stream, SevenZipConstants.IdName, nameData);
  }

  private static void WriteTimes(Stream stream, List<SevenZipFileInfo> files,
    Func<SevenZipFileInfo, DateTime?> getter) {
    // Write "AllDefined" or bit vector
    var allDefined = files.All(f => getter(f).HasValue);
    if (allDefined) {
      stream.WriteByte(1); // AllDefined = true
    }
    else {
      stream.WriteByte(0); // AllDefined = false, write bit vector
      WriteBoolVectorBits(stream, files.Count, i => getter(files[i]).HasValue);
    }

    stream.WriteByte(0); // external = false

    for (var i = 0; i < files.Count; ++i) {
      var time = getter(files[i]);
      if (time.HasValue) {
        var fileTime = time.Value.ToFileTimeUtc();
        var buf = BitConverter.GetBytes(fileTime);
        stream.Write(buf, 0, 8);
      }
    }
  }

  private static void WriteAttributes(Stream stream, List<SevenZipFileInfo> files) {
    var allDefined = files.All(f => f.Attributes.HasValue);
    if (allDefined)
      stream.WriteByte(1);
    else {
      stream.WriteByte(0);
      WriteBoolVectorBits(stream, files.Count, i => files[i].Attributes.HasValue);
    }

    stream.WriteByte(0); // external = false

    for (var i = 0; i < files.Count; ++i) {
      var attrs = files[i].Attributes;
      if (attrs.HasValue) {
        var buf = BitConverter.GetBytes(attrs.Value);
        stream.Write(buf, 0, 4);
      }
    }
  }

  // ---- Encoded Header Support ----

  private static byte[] DecodeEncodedHeader(Stream stream, string? password, Stream? archiveStream) {
    // Read the streams info that describes how the header is packed
    var (packInfo, folders, _) = ReadMainStreams(stream);

    if (folders.Count == 0)
      throw new InvalidDataException("Encoded header has no folders.");

    var folder = folders[0];
    if (folder.Coders.Count == 0)
      throw new InvalidDataException("Encoded header folder has no coders.");

    // Read packed data — may be in the archive stream at PackPos, not in the descriptor stream
    var packedSize = packInfo.PackSizes.Length > 0 ? packInfo.PackSizes[0] : 0;
    var packedData = new byte[packedSize];
    if (archiveStream != null) {
      archiveStream.Position = SevenZipConstants.SignatureHeaderSize + packInfo.PackPos;
      ReadExact(archiveStream, packedData, 0, (int)packedSize);
    }
    else {
      ReadExact(stream, packedData, 0, (int)packedSize);
    }

    // Find compression and AES coders
    SevenZipCoder? lzma2Coder = null;
    SevenZipCoder? lzmaCoder = null;
    SevenZipCoder? aesCoder = null;
    foreach (var c in folder.Coders) {
      if (c.CodecId.AsSpan().SequenceEqual(SevenZipConstants.CodecLzma2))
        lzma2Coder = c;
      else if (c.CodecId.AsSpan().SequenceEqual(SevenZipConstants.CodecLzma))
        lzmaCoder = c;
      else if (c.CodecId.AsSpan().SequenceEqual(SevenZipConstants.CodecAes))
        aesCoder = c;
    }

    var data = packedData;

    // Decrypt AES if present (outer coder in chain)
    if (aesCoder != null) {
      if (string.IsNullOrEmpty(password))
        throw new InvalidDataException("Encrypted header requires a password.");
      data = DecryptAesHeader(data, aesCoder, password);
    }

    // Decompress LZMA2 if present
    if (lzma2Coder != null) {
      var dictByte = lzma2Coder.Properties is { Length: > 0 } ? lzma2Coder.Properties[0] : (byte)0;
      var dictSize = DecodeDictionarySize(dictByte);
      using var packedStream = new MemoryStream(data);
      var decoder = new Lzma2Decoder(packedStream, dictSize);
      return decoder.Decode();
    }

    // Decompress LZMA if present (used by official 7-Zip tool)
    if (lzmaCoder != null) {
      if (lzmaCoder.Properties == null || lzmaCoder.Properties.Length < 5)
        throw new InvalidDataException("LZMA coder missing properties.");
      var unpackSize = folder.UnpackSizes.Length > 0 ? folder.UnpackSizes[^1] : -1;
      using var packedStream = new MemoryStream(data);
      var decoder = new LzmaDecoder(packedStream, lzmaCoder.Properties, unpackSize);
      return decoder.Decode();
    }

    if (folder.Coders.Count == 1 && aesCoder != null)
      return data; // AES-only (unusual but valid)

    throw new NotSupportedException(
      $"Encoded header codec not supported: [{string.Join(", ", folder.Coders[0].CodecId.Select(b => $"0x{b:X2}"))}]");
  }

  private static byte[] DecryptAesHeader(byte[] data, SevenZipCoder aesCoder, string password) {
    var props = aesCoder.Properties ?? throw new InvalidDataException("AES coder missing properties.");
    if (props.Length < 2) throw new InvalidDataException("AES properties too short.");

    var firstByte = props[0];
    var sizesByte = props[1];
    var numCyclesPower = firstByte & 0x3F;
    var hasSalt = (firstByte & 0x40) != 0;
    var hasIv = (firstByte & 0x80) != 0;
    var saltSize = hasSalt ? ((sizesByte >> 4) & 0x0F) + 1 : 0;
    var ivSize = hasIv ? (sizesByte & 0x0F) + 1 : 0;

    var salt = saltSize > 0 ? props[2..(2 + saltSize)] : [];
    var iv = new byte[16];
    if (ivSize > 0)
      props.AsSpan(2 + saltSize, ivSize).CopyTo(iv);

    var key = Compression.Core.Crypto.KeyDerivation.SevenZipDeriveKey(password, salt, numCyclesPower);
    return Compression.Core.Crypto.AesCryptor.DecryptCbcNoPadding(data, key, iv);
  }

  private static int DecodeDictionarySize(byte encoded) {
    if (encoded == 0) return 4096;
    var bits = encoded / 2 + 12;
    if ((encoded & 1) == 0) return 1 << bits;
    return 3 << (bits - 1);
  }

  // ---- CRC Helpers ----

  private static void ReadCrcs(Stream stream, int count, uint?[] crcs) {
    var defined = ReadOptionalBoolVector(stream, count);
    for (var i = 0; i < count; ++i) {
      if (defined[i]) {
        var buf = new byte[4];
        ReadExact(stream, buf, 0, 4);
        crcs[i] = BitConverter.ToUInt32(buf, 0);
      }
    }
  }

  private static void ReadFolderCrcs(Stream stream, List<SevenZipFolder> folders) {
    var defined = ReadOptionalBoolVector(stream, folders.Count);
    for (var i = 0; i < folders.Count; ++i) {
      if (defined[i]) {
        var buf = new byte[4];
        ReadExact(stream, buf, 0, 4);
        folders[i].UnpackCrc = BitConverter.ToUInt32(buf, 0);
      }
    }
  }

  // ---- Bool Vector Helpers ----

  private static bool[] ReadBoolVector(Stream stream, int count) {
    var result = new bool[count];
    var currentByte = (byte)0;
    var bitIndex = 0;
    for (var i = 0; i < count; ++i) {
      if (bitIndex == 0)
        currentByte = ReadByte(stream);
      result[i] = (currentByte & (0x80 >> bitIndex)) != 0;
      bitIndex = (bitIndex + 1) % 8;
    }

    return result;
  }

  private static bool[] ReadOptionalBoolVector(Stream stream, int count) {
    var allDefined = ReadByte(stream);
    if (allDefined != 0) {
      var all = new bool[count];
      all.AsSpan().Fill(true);
      return all;
    }

    return ReadBoolVector(stream, count);
  }

  private static void ReadBoolVectorInto(Stream stream, List<SevenZipFileInfo> files,
    Action<SevenZipFileInfo, bool> setter) {
    var currentByte = (byte)0;
    var bitIndex = 0;
    for (var i = 0; i < files.Count; ++i) {
      if (bitIndex == 0)
        currentByte = ReadByte(stream);
      var value = (currentByte & (0x80 >> bitIndex)) != 0;
      setter(files[i], value);
      bitIndex = (bitIndex + 1) % 8;
    }
  }

  private static void WriteBoolVector(Stream stream, int count, Func<int, bool> predicate) {
    // Check if all are defined
    var allDefined = true;
    for (var i = 0; i < count; ++i) {
      if (!predicate(i)) {
        allDefined = false;
        break;
      }
    }

    if (allDefined) {
      stream.WriteByte(1); // AllDefined
      return;
    }

    stream.WriteByte(0); // not all defined
    WriteBoolVectorBits(stream, count, predicate);
  }

  private static void WriteBoolVectorBits(Stream stream, int count, Func<int, bool> predicate) {
    var currentByte = (byte)0;
    var bitIndex = 0;
    for (var i = 0; i < count; ++i) {
      if (predicate(i))
        currentByte |= (byte)(0x80 >> bitIndex);
      ++bitIndex;
      if (bitIndex == 8) {
        stream.WriteByte(currentByte);
        currentByte = 0;
        bitIndex = 0;
      }
    }

    if (bitIndex > 0)
      stream.WriteByte(currentByte);
  }

  // ---- Property size helpers ----

  private static void WritePropWithSize(Stream stream, byte propertyId, MemoryStream data) {
    stream.WriteByte(propertyId);
    SevenZipVarInt.Write(stream, (ulong)data.Length);
    data.Position = 0;
    data.CopyTo(stream);
  }

  // ---- I/O Helpers ----

  private static byte ReadByte(Stream stream) {
    var b = stream.ReadByte();
    if (b < 0)
      throw new EndOfStreamException("Unexpected end of 7z stream.");
    return (byte)b;
  }

  private static void ReadExact(Stream stream, byte[] buffer, int offset, int count) {
    var totalRead = 0;
    while (totalRead < count) {
      var read = stream.Read(buffer, offset + totalRead, count - totalRead);
      if (read == 0)
        throw new EndOfStreamException("Unexpected end of 7z stream.");
      totalRead += read;
    }
  }

  private static void SkipData(Stream stream) {
    var size = (long)SevenZipVarInt.Read(stream);
    stream.Position += size;
  }

  private static void WriteUInt32Le(Stream stream, uint value) {
    stream.WriteByte((byte)value);
    stream.WriteByte((byte)(value >> 8));
    stream.WriteByte((byte)(value >> 16));
    stream.WriteByte((byte)(value >> 24));
  }
}
