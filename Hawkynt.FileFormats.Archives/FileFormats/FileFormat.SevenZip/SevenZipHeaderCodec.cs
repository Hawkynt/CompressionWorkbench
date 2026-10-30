using System.Text;
using Compression.Core.Checksums;
using Compression.Registry;

namespace FileFormat.SevenZip;

/// <summary>Everything a 7z header describes, plus how the header itself was stored.</summary>
internal sealed class SevenZipDatabase {
  /// <summary>The packed streams of the main streams info.</summary>
  public SevenZipPackInfo PackInfo { get; init; } = new();

  /// <summary>The folders (solid blocks).</summary>
  public List<SevenZipFolder> Folders { get; init; } = [];

  /// <summary>How each folder's output divides into files.</summary>
  public SevenZipSubStreamsInfo SubStreams { get; init; } = new();

  /// <summary>The files, directories and anti-items, in header order.</summary>
  public List<SevenZipFileInfo> Files { get; init; } = [];

  /// <summary>Whether the header was stored packed (<c>kEncodedHeader</c>).</summary>
  public bool HeaderEncoded { get; init; }

  /// <summary>Whether the packed header was encrypted (7-Zip's <c>-mhe</c>).</summary>
  public bool HeaderEncrypted { get; init; }

  /// <summary>Deconstructs into the four parts the edit paths use.</summary>
  public void Deconstruct(out SevenZipPackInfo packInfo, out List<SevenZipFolder> folders, out SevenZipSubStreamsInfo subStreams, out List<SevenZipFileInfo> files) {
    packInfo = this.PackInfo;
    folders = this.Folders;
    subStreams = this.SubStreams;
    files = this.Files;
  }
}

/// <summary>
/// Reads and writes the 7z archive header structure, including PackInfo,
/// UnpackInfo (folders/coders), SubStreamsInfo, and FilesInfo sections.
/// </summary>
/// <remarks>
/// The reader follows 7zFormat.txt and the semantics of 7-Zip's 7zIn.cpp: properties the format
/// allows to be absent get the defaults 7-Zip gives them (one sub-stream per folder, sizes and
/// CRCs taken from the folder), unknown properties are skipped by their size, and a packed header
/// (<c>kEncodedHeader</c>) is decoded with the same coder graph as any folder.
/// </remarks>
internal static class SevenZipHeaderCodec {
  /// <summary>How deep packed headers may nest before the header is rejected as hostile.</summary>
  private const int MaxHeaderNesting = 4;

  /// <summary>
  /// Reads a complete 7z header from a stream, handling both plain and encoded headers.
  /// </summary>
  /// <param name="stream">The stream positioned at the header data.</param>
  /// <param name="password">Optional password for decrypting encrypted headers.</param>
  /// <param name="archiveStream">The archive (positions relative to its start); required for a packed header.</param>
  public static (SevenZipPackInfo PackInfo, List<SevenZipFolder> Folders,
    SevenZipSubStreamsInfo SubStreams, List<SevenZipFileInfo> Files)
    ReadHeader(Stream stream, string? password = null, Stream? archiveStream = null) {
    var (packInfo, folders, subStreams, files) = ReadDatabase(stream, archiveStream, new object(), new SevenZipKeyCache(password));
    return (packInfo, folders, subStreams, files);
  }

  /// <summary>Reads a complete 7z header, decoding a packed (and possibly encrypted) one.</summary>
  public static SevenZipDatabase ReadDatabase(Stream stream, Stream? archive, object gate, SevenZipKeyCache keys) {
    var encoded = false;
    var encrypted = false;
    for (var depth = 0; ; ++depth) {
      var id = ReadByte(stream);
      if (id == SevenZipConstants.IdHeader) {
        var database = ReadPlainHeader(stream);
        return new SevenZipDatabase {
          PackInfo = database.PackInfo, Folders = database.Folders, SubStreams = database.SubStreams, Files = database.Files,
          HeaderEncoded = encoded, HeaderEncrypted = encrypted,
        };
      }
      if (id != SevenZipConstants.IdEncodedHeader)
        throw new InvalidDataException($"7z: expected a header (0x01) or a packed header (0x17), found 0x{id:X2}.");
      if (depth >= MaxHeaderNesting)
        throw new InvalidDataException("7z: packed headers nest too deeply.");
      if (archive == null)
        throw new NotSupportedException("7z: a packed header needs the archive stream to be decoded.");

      var (decoded, wasEncrypted) = DecodeEncodedHeader(stream, archive, gate, keys);
      encoded = true;
      encrypted |= wasEncrypted;
      stream = new MemoryStream(decoded, writable: false);
    }
  }

  private static SevenZipDatabase ReadPlainHeader(Stream stream) {
    var skipped = new HashSet<string>(StringComparer.Ordinal);
    var id = ReadNumber(stream);
    if (id == SevenZipConstants.IdArchiveProperties) {
      // Name/value pairs 7-Zip never writes; remembered so a rewrite refuses instead of dropping them.
      for (ulong type; (type = ReadNumber(stream)) != 0;) {
        skipped.Add($"archive property 0x{type:X2}");
        SkipData(stream);
      }
      id = ReadNumber(stream);
    }
    if (id == SevenZipConstants.IdAdditionalStreams) {
      skipped.Add("additional streams");
      ReadStreamsInfo(stream);
      id = ReadNumber(stream);
    }

    var (packInfo, folders, subStreams) = (new SevenZipPackInfo(), new List<SevenZipFolder>(), new SevenZipSubStreamsInfo());
    if (id == SevenZipConstants.IdMainStreams) {
      (packInfo, folders, subStreams) = ReadStreamsInfo(stream);
      id = ReadNumber(stream);
    }

    var files = new List<SevenZipFileInfo>();
    if (id == SevenZipConstants.IdFilesInfo) {
      files = ReadFilesInfo(stream, skipped);
      id = ReadNumber(stream);
    }
    if (id != SevenZipConstants.IdEnd)
      throw new InvalidDataException($"7z: unexpected header property 0x{id:X2}.");

    AttachStreams(folders, subStreams, files);
    return new SevenZipDatabase { PackInfo = packInfo, Folders = folders, SubStreams = subStreams, Files = files };
  }

  /// <summary>Checks that the files with data use up exactly the sub-streams the folders declare.</summary>
  private static void AttachStreams(List<SevenZipFolder> folders, SevenZipSubStreamsInfo subStreams, List<SevenZipFileInfo> files) {
    var withData = files.Count(static f => !f.IsEmptyStream);
    var streams = subStreams.UnpackSizes.Length;
    if (files.Count == 0 && streams > 0) {
      // A header without FilesInfo: one nameless file per sub-stream, as 7-Zip lists it.
      for (var i = 0; i < streams; ++i)
        files.Add(new SevenZipFileInfo());
      return;
    }
    if (withData != streams)
      throw new InvalidDataException($"7z: the header lists {withData} files with data for {streams} sub-streams.");
  }

  /// <summary>Writes a complete 7z header to a stream.</summary>
  public static void WriteHeader(Stream stream, SevenZipPackInfo packInfo,
    List<SevenZipFolder> folders, SevenZipSubStreamsInfo subStreams,
    List<SevenZipFileInfo> files) {
    stream.WriteByte(SevenZipConstants.IdHeader);

    if (folders.Count > 0) {
      stream.WriteByte(SevenZipConstants.IdMainStreams);
      WriteStreamsInfoBody(stream, packInfo, folders, subStreams);
    }

    if (files.Count > 0)
      WriteFilesInfo(stream, files);

    stream.WriteByte(SevenZipConstants.IdEnd);
  }

  /// <summary>Writes an encoded header descriptor that references packed (compressed/encrypted) header data.</summary>
  public static void WriteEncodedHeader(Stream stream, SevenZipPackInfo packInfo, List<SevenZipFolder> folders) {
    stream.WriteByte(SevenZipConstants.IdEncodedHeader);
    WritePackInfo(stream, packInfo);
    WriteUnpackInfo(stream, folders, writeCrcs: true);
    stream.WriteByte(SevenZipConstants.IdEnd);
  }

  // ---- StreamsInfo ----

  private static (SevenZipPackInfo PackInfo, List<SevenZipFolder> Folders, SevenZipSubStreamsInfo SubStreams) ReadStreamsInfo(Stream stream) {
    var packInfo = new SevenZipPackInfo();
    var folders = new List<SevenZipFolder>();
    var id = ReadNumber(stream);
    if (id == SevenZipConstants.IdPackInfo) {
      packInfo = ReadPackInfo(stream);
      id = ReadNumber(stream);
    }
    if (id == SevenZipConstants.IdUnpackInfo) {
      folders = ReadUnpackInfo(stream);
      id = ReadNumber(stream);
    }

    SevenZipSubStreamsInfo subStreams;
    if (id == SevenZipConstants.IdSubStreamsInfo) {
      subStreams = ReadSubStreamsInfo(stream, folders);
      id = ReadNumber(stream);
    } else
      subStreams = DefaultSubStreams(folders);

    if (id != SevenZipConstants.IdEnd)
      throw new InvalidDataException($"7z: unexpected streams-info property 0x{id:X2}.");

    var packStreamsNeeded = folders.Sum(static f => f.NumPackStreams);
    if (packStreamsNeeded != packInfo.PackSizes.Length)
      throw new InvalidDataException($"7z: the folders read {packStreamsNeeded} packed streams, the header has {packInfo.PackSizes.Length}.");
    return (packInfo, folders, subStreams);
  }

  private static void WriteStreamsInfoBody(Stream stream, SevenZipPackInfo packInfo, List<SevenZipFolder> folders, SevenZipSubStreamsInfo subStreams) {
    WritePackInfo(stream, packInfo);
    WriteUnpackInfo(stream, folders, writeCrcs: false);
    WriteSubStreamsInfo(stream, subStreams, folders);
    stream.WriteByte(SevenZipConstants.IdEnd);
  }

  // ---- PackInfo ----

  private static SevenZipPackInfo ReadPackInfo(Stream stream) {
    var packInfo = new SevenZipPackInfo { PackPos = ReadLength(stream) };
    var numPackStreams = ReadCount(stream, 1 << 24);
    packInfo.PackSizes = new long[numPackStreams];
    packInfo.PackCrcs = new uint?[numPackStreams];

    WaitFor(stream, SevenZipConstants.IdSize);
    for (var i = 0; i < numPackStreams; ++i)
      packInfo.PackSizes[i] = ReadLength(stream);

    for (ulong id; (id = ReadNumber(stream)) != SevenZipConstants.IdEnd;) {
      if (id == SevenZipConstants.IdCrc)
        ReadDigests(stream, numPackStreams, packInfo.PackCrcs);
      else
        SkipData(stream);
    }
    return packInfo;
  }

  private static void WritePackInfo(Stream stream, SevenZipPackInfo packInfo) {
    stream.WriteByte(SevenZipConstants.IdPackInfo);
    SevenZipVarInt.Write(stream, (ulong)packInfo.PackPos);
    SevenZipVarInt.Write(stream, (ulong)packInfo.PackSizes.Length);

    stream.WriteByte(SevenZipConstants.IdSize);
    foreach (var size in packInfo.PackSizes)
      SevenZipVarInt.Write(stream, (ulong)size);

    stream.WriteByte(SevenZipConstants.IdEnd);
  }

  // ---- UnpackInfo (Folders) ----

  private static List<SevenZipFolder> ReadUnpackInfo(Stream stream) {
    WaitFor(stream, SevenZipConstants.IdFolder);
    var numFolders = ReadCount(stream, 1 << 24);
    if (ReadByte(stream) != 0)
      throw new NotSupportedException("7z: folders stored in an additional stream are not supported.");

    var folders = new List<SevenZipFolder>(numFolders);
    for (var i = 0; i < numFolders; ++i)
      folders.Add(ReadFolder(stream));

    WaitFor(stream, SevenZipConstants.IdCodersUnpackSize);
    foreach (var folder in folders) {
      folder.UnpackSizes = new long[folder.Coders.Count];
      for (var j = 0; j < folder.UnpackSizes.Length; ++j)
        folder.UnpackSizes[j] = ReadLength(stream);
    }

    for (ulong id; (id = ReadNumber(stream)) != SevenZipConstants.IdEnd;) {
      if (id != SevenZipConstants.IdCrc) {
        SkipData(stream);
        continue;
      }
      var crcs = new uint?[folders.Count];
      ReadDigests(stream, folders.Count, crcs);
      for (var i = 0; i < folders.Count; ++i)
        folders[i].UnpackCrc = crcs[i];
    }
    return folders;
  }

  private static SevenZipFolder ReadFolder(Stream stream) {
    var folder = new SevenZipFolder();
    var numCoders = ReadCount(stream, 64);
    if (numCoders == 0)
      throw new InvalidDataException("7z: a folder without coders.");

    for (var i = 0; i < numCoders; ++i) {
      var flags = ReadByte(stream);
      if ((flags & 0xC0) != 0)
        throw new NotSupportedException("7z: coders with alternative methods (flag 0x80) are not supported.");
      var coder = new SevenZipCoder { CodecId = new byte[flags & 0x0F] };
      stream.ReadExactly(coder.CodecId);

      if ((flags & 0x10) != 0) {
        coder.NumInStreams = ReadCount(stream, 64);
        coder.NumOutStreams = ReadCount(stream, 64);
      }
      if ((flags & 0x20) != 0) {
        coder.Properties = new byte[ReadCount(stream, 1 << 16)];
        stream.ReadExactly(coder.Properties);
      }
      folder.Coders.Add(coder);
    }

    var totalIn = folder.Coders.Sum(static c => c.NumInStreams);
    var totalOut = folder.Coders.Sum(static c => c.NumOutStreams);
    for (var i = 0; i < totalOut - 1; ++i) {
      var inIndex = ReadCount(stream, totalIn - 1);
      var outIndex = ReadCount(stream, totalOut - 1);
      folder.BindPairs.Add((inIndex, outIndex));
    }

    var numPackStreams = totalIn - (totalOut - 1);
    if (numPackStreams < 1)
      throw new InvalidDataException("7z: a folder without a packed stream.");
    if (numPackStreams > 1) {
      folder.PackedStreams = new int[numPackStreams];
      for (var i = 0; i < numPackStreams; ++i)
        folder.PackedStreams[i] = ReadCount(stream, totalIn - 1);
    } else {
      for (var i = 0; i < totalIn; ++i)
        if (!folder.BindPairs.Exists(b => b.InIndex == i)) {
          folder.PackedStreams = [i];
          break;
        }
    }
    return folder;
  }

  private static void WriteUnpackInfo(Stream stream, List<SevenZipFolder> folders, bool writeCrcs) {
    stream.WriteByte(SevenZipConstants.IdUnpackInfo);

    stream.WriteByte(SevenZipConstants.IdFolder);
    SevenZipVarInt.Write(stream, (ulong)folders.Count);
    stream.WriteByte(0); // not external

    foreach (var folder in folders)
      WriteFolder(stream, folder);

    stream.WriteByte(SevenZipConstants.IdCodersUnpackSize);
    foreach (var folder in folders)
      foreach (var size in folder.UnpackSizes)
        SevenZipVarInt.Write(stream, (ulong)size);

    // Folder CRCs go here only for the packed header, as 7-Zip does; for the main streams the
    // per-file CRCs live in SubStreamsInfo, and 7-Zip then expects no folder CRC for a folder
    // holding a single file.
    if (writeCrcs && folders.Exists(static f => f.UnpackCrc.HasValue)) {
      stream.WriteByte(SevenZipConstants.IdCrc);
      WriteDigests(stream, [.. folders.Select(static f => f.UnpackCrc)]);
    }

    stream.WriteByte(SevenZipConstants.IdEnd);
  }

  private static void WriteFolder(Stream stream, SevenZipFolder folder) {
    SevenZipVarInt.Write(stream, (ulong)folder.Coders.Count);

    foreach (var coder in folder.Coders) {
      var flags = (byte)(coder.CodecId.Length & 0x0F);
      var hasStreamCounts = coder.NumInStreams != 1 || coder.NumOutStreams != 1;
      if (hasStreamCounts) flags |= 0x10;
      if (coder.Properties is { Length: > 0 }) flags |= 0x20;

      stream.WriteByte(flags);
      stream.Write(coder.CodecId, 0, coder.CodecId.Length);

      if (hasStreamCounts) {
        SevenZipVarInt.Write(stream, (ulong)coder.NumInStreams);
        SevenZipVarInt.Write(stream, (ulong)coder.NumOutStreams);
      }

      if (coder.Properties is { Length: > 0 }) {
        SevenZipVarInt.Write(stream, (ulong)coder.Properties.Length);
        stream.Write(coder.Properties, 0, coder.Properties.Length);
      }
    }

    foreach (var (inIndex, outIndex) in folder.BindPairs) {
      SevenZipVarInt.Write(stream, (ulong)inIndex);
      SevenZipVarInt.Write(stream, (ulong)outIndex);
    }

    var totalIn = folder.Coders.Sum(static c => c.NumInStreams);
    var numPackStreams = totalIn - folder.BindPairs.Count;
    if (numPackStreams <= 1)
      return;

    if (folder.PackedStreams.Length == numPackStreams) {
      foreach (var p in folder.PackedStreams)
        SevenZipVarInt.Write(stream, (ulong)p);
      return;
    }
    for (var i = 0; i < totalIn; ++i)
      if (!folder.BindPairs.Exists(b => b.InIndex == i))
        SevenZipVarInt.Write(stream, (ulong)i);
  }

  // ---- SubStreamsInfo ----

  /// <summary>No SubStreamsInfo: one file per folder, its size and CRC the folder's.</summary>
  private static SevenZipSubStreamsInfo DefaultSubStreams(List<SevenZipFolder> folders) => new() {
    NumUnpackStreams = [.. folders.Select(static _ => 1)],
    UnpackSizes = [.. folders.Select(static f => f.MainUnpackSize)],
    Digests = [.. folders.Select(static f => f.UnpackCrc)],
  };

  private static SevenZipSubStreamsInfo ReadSubStreamsInfo(Stream stream, List<SevenZipFolder> folders) {
    var numStreams = new int[folders.Count];
    numStreams.AsSpan().Fill(1);

    ulong id;
    for (;;) {
      id = ReadNumber(stream);
      if (id == SevenZipConstants.IdNumUnpackStreams) {
        for (var i = 0; i < folders.Count; ++i)
          numStreams[i] = ReadCount(stream, 1 << 24);
        continue;
      }
      if (id is SevenZipConstants.IdCrc or SevenZipConstants.IdSize or SevenZipConstants.IdEnd)
        break;
      SkipData(stream);
    }

    var sizes = new List<long>();
    if (id == SevenZipConstants.IdSize) {
      for (var i = 0; i < folders.Count; ++i) {
        if (numStreams[i] == 0)
          continue;
        var folderSize = folders[i].MainUnpackSize;
        var sum = 0L;
        for (var j = 1; j < numStreams[i]; ++j) {
          var size = ReadLength(stream);
          sum += size;
          if (sum > folderSize || sum < 0)
            throw new InvalidDataException("7z: the sub-stream sizes exceed their folder.");
          sizes.Add(size);
        }
        sizes.Add(folderSize - sum);
      }
      id = ReadNumber(stream);
    } else
      for (var i = 0; i < folders.Count; ++i) {
        if (numStreams[i] > 1)
          throw new InvalidDataException("7z: a folder with several files but no sub-stream sizes.");
        if (numStreams[i] == 1)
          sizes.Add(folders[i].MainUnpackSize);
      }

    // CRCs are listed only for sub-streams whose folder CRC does not already cover them.
    var digests = new uint?[sizes.Count];
    var unknown = 0;
    for (var i = 0; i < folders.Count; ++i)
      if (numStreams[i] != 1 || !folders[i].UnpackCrc.HasValue)
        unknown += numStreams[i];

    uint?[]? listed = null;
    for (; id != SevenZipConstants.IdEnd; id = ReadNumber(stream)) {
      if (id == SevenZipConstants.IdCrc) {
        listed = new uint?[unknown];
        ReadDigests(stream, unknown, listed);
      } else
        SkipData(stream);
    }

    for (int i = 0, s = 0, l = 0; i < folders.Count; ++i) {
      if (numStreams[i] == 1 && folders[i].UnpackCrc.HasValue) {
        digests[s++] = folders[i].UnpackCrc;
        continue;
      }
      for (var j = 0; j < numStreams[i]; ++j)
        digests[s++] = listed?[l++];
    }

    return new SevenZipSubStreamsInfo { NumUnpackStreams = numStreams, UnpackSizes = [.. sizes], Digests = digests };
  }

  private static void WriteSubStreamsInfo(Stream stream, SevenZipSubStreamsInfo subStreams, List<SevenZipFolder> folders) {
    stream.WriteByte(SevenZipConstants.IdSubStreamsInfo);

    if (subStreams.NumUnpackStreams.Any(static n => n != 1)) {
      stream.WriteByte(SevenZipConstants.IdNumUnpackStreams);
      foreach (var n in subStreams.NumUnpackStreams)
        SevenZipVarInt.Write(stream, (ulong)n);
    }

    if (subStreams.NumUnpackStreams.Any(static n => n > 1)) {
      stream.WriteByte(SevenZipConstants.IdSize);
      var sizeIndex = 0;
      foreach (var n in subStreams.NumUnpackStreams) {
        for (var j = 0; j < n - 1; ++j)
          SevenZipVarInt.Write(stream, (ulong)subStreams.UnpackSizes[sizeIndex + j]);
        sizeIndex += n;
      }
    }

    // Every sub-stream's CRC is listed: the main-streams folders carry none of their own.
    if (subStreams.Digests.Any(static d => d.HasValue)) {
      stream.WriteByte(SevenZipConstants.IdCrc);
      WriteDigests(stream, subStreams.Digests);
    }

    stream.WriteByte(SevenZipConstants.IdEnd);
  }

  // ---- FilesInfo ----

  private static List<SevenZipFileInfo> ReadFilesInfo(Stream stream, HashSet<string> skipped) {
    var numFiles = ReadCount(stream, 1 << 26);
    var files = new List<SevenZipFileInfo>(numFiles);
    for (var i = 0; i < numFiles; ++i)
      files.Add(new SevenZipFileInfo { SkippedProperties = skipped });

    var emptyStreams = new List<SevenZipFileInfo>();
    for (ulong id; (id = ReadNumber(stream)) != SevenZipConstants.IdEnd;) {
      var size = ReadLength(stream);
      var start = stream.Position;
      if (start + size > stream.Length)
        throw new InvalidDataException("7z: a file property runs past the end of the header.");

      switch (id) {
        case SevenZipConstants.IdName:
          ReadNames(stream, files, start + size);
          break;
        case SevenZipConstants.IdEmptyStream: {
          var vector = ReadBitVector(stream, numFiles);
          emptyStreams.Clear();
          for (var i = 0; i < numFiles; ++i) {
            files[i].IsEmptyStream = vector[i];
            files[i].IsDirectory = vector[i];
            files[i].IsEmptyFile = false;
            files[i].IsAnti = false;
            if (vector[i])
              emptyStreams.Add(files[i]);
          }
          break;
        }
        case SevenZipConstants.IdEmptyFile: {
          var vector = ReadBitVector(stream, emptyStreams.Count);
          for (var i = 0; i < emptyStreams.Count; ++i) {
            emptyStreams[i].IsEmptyFile = vector[i];
            emptyStreams[i].IsDirectory = !vector[i];
          }
          break;
        }
        case SevenZipConstants.IdAnti: {
          var vector = ReadBitVector(stream, emptyStreams.Count);
          for (var i = 0; i < emptyStreams.Count; ++i)
            emptyStreams[i].IsAnti = vector[i];
          break;
        }
        case SevenZipConstants.IdMTime:
          ReadUInt64Vector(stream, files, (f, v) => f.LastWriteTime = FileTime(v));
          break;
        case SevenZipConstants.IdCTime:
          ReadUInt64Vector(stream, files, (f, v) => f.CreationTime = FileTime(v));
          break;
        case SevenZipConstants.IdATime:
          ReadUInt64Vector(stream, files, (f, v) => f.LastAccessTime = FileTime(v));
          break;
        case SevenZipConstants.IdStartPos:
          skipped.Add($"file property 0x{id:X2}");
          ReadUInt64Vector(stream, files, (f, v) => f.StartPos = v);
          break;
        case SevenZipConstants.IdAttributes:
          ReadAttributes(stream, files);
          break;
        case SevenZipConstants.IdDummy:
          break;
        default:
          skipped.Add($"file property 0x{id:X2}");
          break;
      }

      // Every property is exactly as long as it says; padding and unknown ones are skipped whole.
      stream.Position = start + size;
    }
    return files;
  }

  /// <summary>A FILETIME, or <see langword="null"/> when it lies outside what <see cref="DateTime"/> can hold.</summary>
  private static DateTime? FileTime(ulong value)
    => value <= (ulong)DateTime.MaxValue.ToFileTimeUtc() ? DateTime.FromFileTimeUtc((long)value) : null;

  private static void ReadNames(Stream stream, List<SevenZipFileInfo> files, long end) {
    if (ReadByte(stream) != 0)
      throw new NotSupportedException("7z: names stored in an additional stream are not supported.");

    var length = end - stream.Position;
    if (length < 0 || (length & 1) != 0)
      throw new InvalidDataException("7z: the name list has an odd length.");
    var bytes = new byte[length];
    stream.ReadExactly(bytes);
    var chars = Encoding.Unicode.GetString(bytes);
    var start = 0;
    foreach (var file in files) {
      var terminator = chars.IndexOf('\0', start);
      if (terminator < 0)
        throw new InvalidDataException("7z: fewer names than files.");
      file.Name = chars[start..terminator];
      start = terminator + 1;
    }
    if (start != chars.Length)
      throw new InvalidDataException("7z: more names than files.");
  }

  private static void ReadUInt64Vector(Stream stream, List<SevenZipFileInfo> files, Action<SevenZipFileInfo, ulong> setter) {
    var defined = ReadOptionalBitVector(stream, files.Count);
    if (ReadByte(stream) != 0)
      throw new NotSupportedException("7z: times stored in an additional stream are not supported.");

    Span<byte> buffer = stackalloc byte[8];
    for (var i = 0; i < files.Count; ++i)
      if (defined[i]) {
        stream.ReadExactly(buffer);
        setter(files[i], BitConverter.ToUInt64(buffer));
      }
  }

  private static void ReadAttributes(Stream stream, List<SevenZipFileInfo> files) {
    var defined = ReadOptionalBitVector(stream, files.Count);
    if (ReadByte(stream) != 0)
      throw new NotSupportedException("7z: attributes stored in an additional stream are not supported.");

    Span<byte> buffer = stackalloc byte[4];
    for (var i = 0; i < files.Count; ++i)
      if (defined[i]) {
        stream.ReadExactly(buffer);
        files[i].Attributes = BitConverter.ToUInt32(buffer);
      }
  }

  private static void WriteFilesInfo(Stream stream, List<SevenZipFileInfo> files) {
    stream.WriteByte(SevenZipConstants.IdFilesInfo);
    SevenZipVarInt.Write(stream, (ulong)files.Count);

    var emptyStreamFiles = files.Where(static f => f.IsEmptyStream).ToList();
    if (emptyStreamFiles.Count > 0) {
      using var emptyStreamData = new MemoryStream();
      WriteBitVector(emptyStreamData, files.Count, i => files[i].IsEmptyStream);
      WritePropWithSize(stream, SevenZipConstants.IdEmptyStream, emptyStreamData);

      if (emptyStreamFiles.Exists(static f => f.IsEmptyFile)) {
        using var emptyFileData = new MemoryStream();
        WriteBitVector(emptyFileData, emptyStreamFiles.Count, i => emptyStreamFiles[i].IsEmptyFile);
        WritePropWithSize(stream, SevenZipConstants.IdEmptyFile, emptyFileData);
      }

      if (emptyStreamFiles.Exists(static f => f.IsAnti)) {
        using var antiData = new MemoryStream();
        WriteBitVector(antiData, emptyStreamFiles.Count, i => emptyStreamFiles[i].IsAnti);
        WritePropWithSize(stream, SevenZipConstants.IdAnti, antiData);
      }
    }

    WriteNames(stream, files);

    WriteTimes(stream, files, SevenZipConstants.IdCTime, static f => f.CreationTime);
    WriteTimes(stream, files, SevenZipConstants.IdATime, static f => f.LastAccessTime);
    WriteTimes(stream, files, SevenZipConstants.IdMTime, static f => f.LastWriteTime);

    if (files.Exists(static f => f.Attributes.HasValue)) {
      using var attrData = new MemoryStream();
      WriteDefinedVector(attrData, files.Count, i => files[i].Attributes.HasValue);
      attrData.WriteByte(0); // not external
      foreach (var file in files)
        if (file.Attributes is { } attributes)
          attrData.Write(BitConverter.GetBytes(attributes));
      WritePropWithSize(stream, SevenZipConstants.IdAttributes, attrData);
    }

    stream.WriteByte(SevenZipConstants.IdEnd);
  }

  /// <summary>
  /// Refuses an edit of a header that holds properties this codec skips: they are tied to the
  /// file list (one bit or value per entry), so re-emitting them verbatim after the list changed
  /// would attach them to the wrong entries, and dropping them would lose them.
  /// </summary>
  internal static void RequireCarriable(IReadOnlyList<SevenZipFileInfo> files, string operation) {
    if (files.Count > 0 && files[0].SkippedProperties is { Count: > 0 } skipped)
      throw new NotSupportedException(
        $"7z {operation}: the header holds {string.Join(", ", skipped.Order(StringComparer.Ordinal))}, which a rewrite cannot carry. Refused, nothing was changed.");
    if (files.Any(static f => f.IsAnti))
      throw new NotSupportedException($"7z {operation}: the archive holds anti-items (an update archive), which a rewrite cannot carry. Refused, nothing was changed.");
  }

  private static void WriteNames(Stream stream, List<SevenZipFileInfo> files) {
    using var nameData = new MemoryStream();
    nameData.WriteByte(0); // not external
    foreach (var file in files) {
      nameData.Write(Encoding.Unicode.GetBytes(file.Name));
      nameData.WriteByte(0);
      nameData.WriteByte(0);
    }
    WritePropWithSize(stream, SevenZipConstants.IdName, nameData);
  }

  private static void WriteTimes(Stream stream, List<SevenZipFileInfo> files, byte propertyId, Func<SevenZipFileInfo, DateTime?> getter) {
    if (!files.Exists(f => getter(f).HasValue))
      return;

    using var data = new MemoryStream();
    WriteDefinedVector(data, files.Count, i => getter(files[i]).HasValue);
    data.WriteByte(0); // not external
    foreach (var file in files)
      if (getter(file) is { } time)
        data.Write(BitConverter.GetBytes(time.ToFileTimeUtc()));
    WritePropWithSize(stream, propertyId, data);
  }

  // ---- Encoded Header Support ----

  private static (byte[] Header, bool Encrypted) DecodeEncodedHeader(Stream stream, Stream archive, object gate, SevenZipKeyCache keys) {
    var (packInfo, folders, _) = ReadStreamsInfo(stream);
    if (folders.Count == 0)
      throw new InvalidDataException("7z: a packed header without a folder.");

    var folder = folders[0];
    var encrypted = folder.IsEncrypted;
    var size = folder.MainUnpackSize;
    if (size is < 0 or > 1L << 30)
      throw new InvalidDataException("7z: implausible packed header size.");

    var offsets = PackStreamOffsets(packInfo);
    var decoder = new SevenZipFolderDecoder(folder,
      p => new SevenZipWindowStream(archive, gate, SevenZipConstants.SignatureHeaderSize + offsets[p], packInfo.PackSizes[p]), keys);
    var header = new MemoryStream((int)size);
    try {
      decoder.DecodeMain(header);
    } catch (Exception e) when (encrypted && keys.HasPassword && IsDataError(e)) {
      throw new ArchivePasswordException("7z: the archive header does not decrypt with this password (wrong password?).", passwordWasSupplied: true);
    }

    var bytes = header.ToArray();
    if (folder.UnpackCrc is { } crc && Crc32.Compute(bytes) != crc) {
      if (encrypted)
        throw new ArchivePasswordException("7z: the archive header does not decrypt with this password (wrong password?).", passwordWasSupplied: true);
      throw new InvalidDataException("7z: the packed header fails its CRC.");
    }
    return (bytes, encrypted);
  }

  /// <summary>
  /// Where a packed header keeps its packed streams, read without decoding it (so without a
  /// password); <see langword="null"/> for a plain header.
  /// </summary>
  internal static SevenZipPackInfo? ReadEncodedHeaderPackInfo(byte[] rawHeader) {
    using var stream = new MemoryStream(rawHeader, writable: false);
    return ReadByte(stream) == SevenZipConstants.IdEncodedHeader ? ReadStreamsInfo(stream).PackInfo : null;
  }

  /// <summary>Whether <paramref name="e"/> is how a decoder reports corrupt input.</summary>
  internal static bool IsDataError(Exception e)
    => e is InvalidDataException or EndOfStreamException or ArgumentException or IndexOutOfRangeException or OverflowException or NotSupportedException;

  /// <summary>The offset of every packed stream relative to the end of the signature header.</summary>
  internal static long[] PackStreamOffsets(SevenZipPackInfo packInfo) {
    var offsets = new long[packInfo.PackSizes.Length];
    var position = packInfo.PackPos;
    for (var i = 0; i < offsets.Length; ++i) {
      offsets[i] = position;
      position += packInfo.PackSizes[i];
    }
    return offsets;
  }

  // ---- Digest and bit vector helpers ----

  private static void ReadDigests(Stream stream, int count, uint?[] digests) {
    var defined = ReadOptionalBitVector(stream, count);
    Span<byte> buffer = stackalloc byte[4];
    for (var i = 0; i < count; ++i)
      if (defined[i]) {
        stream.ReadExactly(buffer);
        digests[i] = BitConverter.ToUInt32(buffer);
      }
  }

  private static void WriteDigests(Stream stream, IReadOnlyList<uint?> digests) {
    WriteDefinedVector(stream, digests.Count, i => digests[i].HasValue);
    foreach (var digest in digests)
      if (digest is { } value)
        stream.Write(BitConverter.GetBytes(value));
  }

  private static bool[] ReadBitVector(Stream stream, int count) {
    var result = new bool[count];
    var current = 0;
    for (var i = 0; i < count; ++i) {
      if ((i & 7) == 0)
        current = ReadByte(stream);
      result[i] = (current & (0x80 >> (i & 7))) != 0;
    }
    return result;
  }

  private static bool[] ReadOptionalBitVector(Stream stream, int count) {
    if (ReadByte(stream) == 0)
      return ReadBitVector(stream, count);
    var all = new bool[count];
    all.AsSpan().Fill(true);
    return all;
  }

  private static void WriteDefinedVector(Stream stream, int count, Func<int, bool> predicate) {
    var allDefined = true;
    for (var i = 0; i < count && allDefined; ++i)
      allDefined = predicate(i);
    if (allDefined) {
      stream.WriteByte(1);
      return;
    }
    stream.WriteByte(0);
    WriteBitVector(stream, count, predicate);
  }

  private static void WriteBitVector(Stream stream, int count, Func<int, bool> predicate) {
    var current = 0;
    for (var i = 0; i < count; ++i) {
      if (predicate(i))
        current |= 0x80 >> (i & 7);
      if ((i & 7) == 7) {
        stream.WriteByte((byte)current);
        current = 0;
      }
    }
    if ((count & 7) != 0)
      stream.WriteByte((byte)current);
  }

  private static void WritePropWithSize(Stream stream, byte propertyId, MemoryStream data) {
    stream.WriteByte(propertyId);
    SevenZipVarInt.Write(stream, (ulong)data.Length);
    data.Position = 0;
    data.CopyTo(stream);
  }

  // ---- Primitive readers ----

  private static byte ReadByte(Stream stream) {
    var b = stream.ReadByte();
    return b < 0 ? throw new EndOfStreamException("7z: the header ends early.") : (byte)b;
  }

  private static ulong ReadNumber(Stream stream) => SevenZipVarInt.Read(stream);

  /// <summary>A count no larger than <paramref name="max"/>, refused otherwise (hostile headers ask for billions).</summary>
  private static int ReadCount(Stream stream, int max) {
    var value = ReadNumber(stream);
    return value > (ulong)max ? throw new InvalidDataException($"7z: a header count of {value} is out of range.") : (int)value;
  }

  /// <summary>A size or offset, which must fit a signed 64-bit integer.</summary>
  private static long ReadLength(Stream stream) {
    var value = ReadNumber(stream);
    return value > long.MaxValue ? throw new InvalidDataException("7z: a header size does not fit 63 bits.") : (long)value;
  }

  /// <summary>Skips properties until <paramref name="id"/>; 7-Zip's WaitId.</summary>
  private static void WaitFor(Stream stream, byte id) {
    for (;;) {
      var type = ReadNumber(stream);
      if (type == id)
        return;
      if (type == SevenZipConstants.IdEnd)
        throw new InvalidDataException($"7z: the header lacks property 0x{id:X2}.");
      SkipData(stream);
    }
  }

  private static void SkipData(Stream stream) {
    var size = ReadLength(stream);
    if (stream.Position + size > stream.Length)
      throw new InvalidDataException("7z: a header property runs past the end of the header.");
    stream.Position += size;
  }
}
