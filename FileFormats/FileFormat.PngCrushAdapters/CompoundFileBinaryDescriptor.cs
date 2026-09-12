#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Registry;

namespace FileFormat.PngCrushAdapters;

/// <summary>
/// Structural reader for Microsoft Compound Binary File (CFB/OLE Structured Storage) containers.
/// </summary>
/// <remarks>
/// This exposes storage objects as directories and stream objects as their exact logical byte streams.
/// It implements the sector, DIFAT/FAT, MiniFAT/mini-stream and directory-tree mechanics from MS-CFB;
/// application-specific stream contents are intentionally left uninterpreted so callers can recurse into
/// embedded payloads with CompressionWorkbench's normal format detection.
/// </remarks>
public sealed class CompoundFileBinaryDescriptor
  : IFormatDescriptor, IArchiveFormatOperations, IArchiveInMemoryExtract, IFormatValidator {

  internal static readonly byte[] Signature = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1];

  public string Id => "CompoundFileBinary";
  public string DisplayName => "Compound File Binary / OLE Structured Storage";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries | FormatCapabilities.SupportsDirectories;
  public string DefaultExtension => ".cfb";
  public IReadOnlyList<string> Extensions => [".cfb", ".ole"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [new(Signature, Confidence: 0.99)];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description =>
    "Physical CFB/OLE Structured Storage view exposing original storages and stream bytes without interpreting application payloads.";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var compound = CompoundFileBinary.Read(stream);
    return compound.Entries.Select((entry, index) => new ArchiveEntryInfo(
      Index: index,
      Name: entry.Path,
      OriginalSize: checked((long)entry.Size),
      CompressedSize: checked((long)entry.Size),
      Method: "Stored",
      IsDirectory: entry.IsStorage,
      IsEncrypted: false,
      LastModified: entry.LastModified,
      Kind: entry.IsStorage ? "storage" : "stream")).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    ArgumentException.ThrowIfNullOrWhiteSpace(outputDir);
    var compound = CompoundFileBinary.Read(stream);
    var selected = files is { Length: > 0 }
      ? new HashSet<string>(files, StringComparer.OrdinalIgnoreCase)
      : null;
    var root = Path.GetFullPath(outputDir);
    Directory.CreateDirectory(root);

    foreach (var entry in compound.Entries) {
      if (selected is not null && !selected.Contains(entry.Path))
        continue;

      var path = _SafeOutputPath(root, entry.Path);
      if (entry.IsStorage) {
        Directory.CreateDirectory(path);
        continue;
      }

      Directory.CreateDirectory(Path.GetDirectoryName(path)!);
      File.WriteAllBytes(path, compound.ReadStream(entry.DirectoryId));
    }
  }

  public Stream OpenEntry(Stream archive, string entryName, string? password) {
    ArgumentException.ThrowIfNullOrWhiteSpace(entryName);
    var compound = CompoundFileBinary.Read(archive);
    var entry = compound.Find(entryName)
      ?? throw new FileNotFoundException($"CFB stream '{entryName}' was not found.", entryName);
    if (entry.IsStorage)
      throw new InvalidOperationException($"CFB entry '{entryName}' is a storage object, not a stream.");
    return new MemoryStream(compound.ReadStream(entry.DirectoryId), writable: false);
  }

  public byte[] ExtractEntryToMemory(Stream archive, string entryName, string? password) {
    using var stream = this.OpenEntry(archive, entryName, password);
    using var memory = new MemoryStream();
    stream.CopyTo(memory);
    return memory.ToArray();
  }

  public void ExtractEntry(Stream input, string entryName, Stream output, string? password) {
    ArgumentNullException.ThrowIfNull(output);
    using var stream = this.OpenEntry(input, entryName, password);
    stream.CopyTo(output);
  }

  public ValidationResult ValidateHeader(ReadOnlySpan<byte> header, long fileSize) {
    var issues = new List<ValidationIssue>();
    try {
      CompoundFileBinary.ValidateHeader(header, fileSize);
      return new() {
        IsValid = true,
        Confidence = 0.99,
        Health = FormatHealth.Good,
        Level = ValidationLevel.Header,
        Issues = issues,
      };
    } catch (InvalidDataException exception) {
      issues.Add(new(ValidationLevel.Header, IssueSeverity.Error, "CFB_HEADER_INVALID", exception.Message));
      return new() {
        IsValid = false,
        Confidence = header.Length >= 8 && header[..8].SequenceEqual(Signature) ? 0.95 : 0.05,
        Health = FormatHealth.Damaged,
        Level = ValidationLevel.Header,
        Issues = issues,
      };
    }
  }

  public ValidationResult ValidateStructure(Stream stream) => _Validate(stream, ValidationLevel.Structure);

  public ValidationResult ValidateIntegrity(Stream stream) => _Validate(stream, ValidationLevel.Integrity);

  private static ValidationResult _Validate(Stream stream, ValidationLevel level) {
    var issues = new List<ValidationIssue>();
    try {
      var compound = CompoundFileBinary.Read(stream);
      // Force every stream chain to be resolved at integrity depth; structure parsing already validates
      // directory/DIFAT/FAT/MiniFAT references and hierarchy cycles.
      if (level == ValidationLevel.Integrity)
        foreach (var entry in compound.Entries.Where(entry => !entry.IsStorage))
          _ = compound.ReadStream(entry.DirectoryId);

      return new() {
        IsValid = true,
        Confidence = 0.99,
        Health = FormatHealth.Good,
        Level = level,
        Issues = issues,
        ValidEntries = compound.Entries.Count,
        TotalEntries = compound.Entries.Count,
      };
    } catch (Exception exception) when (exception is InvalidDataException or IOException or NotSupportedException) {
      issues.Add(new(level, IssueSeverity.Error, "CFB_STRUCTURE_INVALID", exception.Message));
      return new() {
        IsValid = false,
        Confidence = 0.90,
        Health = FormatHealth.Damaged,
        Level = level,
        Issues = issues,
      };
    }
  }

  private static string _SafeOutputPath(string root, string entryPath) {
    if (Path.IsPathRooted(entryPath))
      throw new InvalidDataException($"CFB entry path '{entryPath}' is rooted.");

    var parts = entryPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
    if (parts.Length == 0 || parts.Any(part => part is "." or ".." || part.Contains(Path.DirectorySeparatorChar) || part.Contains(Path.AltDirectorySeparatorChar)))
      throw new InvalidDataException($"CFB entry path '{entryPath}' is unsafe.");

    var candidate = Path.GetFullPath(Path.Combine([root, .. parts]));
    var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
    if (!candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && !string.Equals(candidate, root, StringComparison.OrdinalIgnoreCase))
      throw new InvalidDataException($"CFB entry path '{entryPath}' escapes the extraction root.");
    return candidate;
  }
}

/// <summary>Legacy binary Microsoft Office documents exposed as their original CFB storages and streams.</summary>
public sealed class LegacyOfficeCompoundFileDescriptor
  : IFormatDescriptor, IArchiveFormatOperations, IArchiveInMemoryExtract, IFormatValidator {

  private static readonly CompoundFileBinaryDescriptor _Cfb = new();
  private static readonly string[] _OfficeMainStreams = ["WordDocument", "Workbook", "Book", "PowerPoint Document"];

  public string Id => "LegacyOfficeCompoundFile";
  public string DisplayName => "Legacy Microsoft Office compound file";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities => _Cfb.Capabilities;
  public string DefaultExtension => ".doc";
  public IReadOnlyList<string> Extensions => [".doc", ".dot", ".xls", ".xlt", ".ppt", ".pps", ".pot"];
  public IReadOnlyList<string> CompoundExtensions => [];

  // The CFB signature is shared by many non-Office formats. Content-only detection belongs to the
  // generic CompoundFileBinary descriptor; legacy Office extensions select this more specific view.
  public IReadOnlyList<MagicSignature> MagicSignatures => [];
  public IReadOnlyList<FormatMethodInfo> Methods => _Cfb.Methods;
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description =>
    "Structural view of legacy .doc/.dot/.xls/.xlt/.ppt/.pps/.pot files preserving their original OLE Structured Storage hierarchy and stream bytes.";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var entries = _Cfb.List(stream, password);
    _RequireLegacyOffice(entries);
    return entries;
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    _RequireLegacyOffice(_Cfb.List(stream, password));
    _Rewind(stream);
    _Cfb.Extract(stream, outputDir, password, files);
  }

  public Stream OpenEntry(Stream archive, string entryName, string? password) {
    _RequireLegacyOffice(_Cfb.List(archive, password));
    _Rewind(archive);
    return _Cfb.OpenEntry(archive, entryName, password);
  }

  public byte[] ExtractEntryToMemory(Stream archive, string entryName, string? password) {
    using var stream = this.OpenEntry(archive, entryName, password);
    using var memory = new MemoryStream();
    stream.CopyTo(memory);
    return memory.ToArray();
  }

  public void ExtractEntry(Stream input, string entryName, Stream output, string? password) {
    ArgumentNullException.ThrowIfNull(output);
    using var stream = this.OpenEntry(input, entryName, password);
    stream.CopyTo(output);
  }

  public ValidationResult ValidateHeader(ReadOnlySpan<byte> header, long fileSize) => _Cfb.ValidateHeader(header, fileSize);

  public ValidationResult ValidateStructure(Stream stream) => _ValidateOffice(stream, ValidationLevel.Structure);

  public ValidationResult ValidateIntegrity(Stream stream) => _ValidateOffice(stream, ValidationLevel.Integrity);

  private static ValidationResult _ValidateOffice(Stream stream, ValidationLevel level) {
    _Rewind(stream);
    var cfb = level == ValidationLevel.Integrity ? _Cfb.ValidateIntegrity(stream) : _Cfb.ValidateStructure(stream);
    if (!cfb.IsValid)
      return cfb;

    try {
      _Rewind(stream);
      var entries = _Cfb.List(stream, password: null);
      _RequireLegacyOffice(entries);
      return cfb;
    } catch (InvalidDataException exception) {
      var issues = cfb.Issues.ToList();
      issues.Add(new(level, IssueSeverity.Error, "OFFICE_MAIN_STREAM_MISSING", exception.Message));
      return new() {
        IsValid = false,
        Confidence = 0.40,
        Health = FormatHealth.Damaged,
        Level = level,
        Issues = issues,
        ValidEntries = cfb.ValidEntries,
        TotalEntries = cfb.TotalEntries,
      };
    }
  }

  private static void _RequireLegacyOffice(IReadOnlyCollection<ArchiveEntryInfo> entries) {
    if (entries.Any(entry => !entry.IsDirectory && _OfficeMainStreams.Contains(entry.Name, StringComparer.OrdinalIgnoreCase)))
      return;
    throw new InvalidDataException(
      "The CFB container does not contain a legacy Office main stream (WordDocument, Workbook/Book, or PowerPoint Document)." );
  }

  private static void _Rewind(Stream stream) {
    if (!stream.CanSeek)
      throw new NotSupportedException("Legacy Office CFB access requires a seekable stream.");
    stream.Position = 0;
  }
}

internal sealed class CompoundFileBinary {
  private const uint FreeSector = 0xFFFFFFFF;
  private const uint EndOfChain = 0xFFFFFFFE;
  private const uint FatSector = 0xFFFFFFFD;
  private const uint DifatSector = 0xFFFFFFFC;
  private const uint NoStream = 0xFFFFFFFF;
  private const int HeaderLength = 512;
  private const int DirectoryEntryLength = 128;

  private readonly byte[] _data;
  private readonly int _sectorSize;
  private readonly int _miniSectorSize;
  private readonly uint _miniStreamCutoff;
  private readonly uint[] _fat;
  private readonly uint[] _miniFat;
  private readonly DirectoryEntry[] _directory;
  private readonly byte[] _miniStream;

  private CompoundFileBinary(
    byte[] data,
    int sectorSize,
    int miniSectorSize,
    uint miniStreamCutoff,
    uint[] fat,
    uint[] miniFat,
    DirectoryEntry[] directory,
    byte[] miniStream,
    List<Entry> entries) {
    this._data = data;
    this._sectorSize = sectorSize;
    this._miniSectorSize = miniSectorSize;
    this._miniStreamCutoff = miniStreamCutoff;
    this._fat = fat;
    this._miniFat = miniFat;
    this._directory = directory;
    this._miniStream = miniStream;
    this.Entries = entries;
  }

  internal sealed record Entry(uint DirectoryId, string Path, ulong Size, bool IsStorage, DateTime? LastModified);

  private sealed record DirectoryEntry(
    string Name,
    byte ObjectType,
    uint LeftSiblingId,
    uint RightSiblingId,
    uint ChildId,
    uint StartingSector,
    ulong StreamSize,
    DateTime? LastModified);

  internal IReadOnlyList<Entry> Entries { get; }

  internal Entry? Find(string path)
    => this.Entries.FirstOrDefault(entry => string.Equals(entry.Path, path.TrimEnd('/'), StringComparison.OrdinalIgnoreCase));

  internal static CompoundFileBinary Read(Stream stream) {
    ArgumentNullException.ThrowIfNull(stream);
    if (!stream.CanRead)
      throw new NotSupportedException("CFB input stream is not readable.");
    if (!stream.CanSeek)
      throw new NotSupportedException("CFB access requires a seekable stream.");
    if (stream.Length > int.MaxValue)
      throw new NotSupportedException("CFB files larger than 2 GiB are not supported by the in-memory structural reader.");

    stream.Position = 0;
    var data = new byte[checked((int)stream.Length)];
    stream.ReadExactly(data);
    ValidateHeader(data, data.LongLength);

    var header = data.AsSpan(0, HeaderLength);
    var majorVersion = BinaryPrimitives.ReadUInt16LittleEndian(header[26..28]);
    var sectorSize = 1 << BinaryPrimitives.ReadUInt16LittleEndian(header[30..32]);
    var miniSectorSize = 1 << BinaryPrimitives.ReadUInt16LittleEndian(header[32..34]);
    var fatSectorCount = BinaryPrimitives.ReadUInt32LittleEndian(header[44..48]);
    var firstDirectorySector = BinaryPrimitives.ReadUInt32LittleEndian(header[48..52]);
    var miniStreamCutoff = BinaryPrimitives.ReadUInt32LittleEndian(header[56..60]);
    var firstMiniFatSector = BinaryPrimitives.ReadUInt32LittleEndian(header[60..64]);
    var miniFatSectorCount = BinaryPrimitives.ReadUInt32LittleEndian(header[64..68]);
    var firstDifatSector = BinaryPrimitives.ReadUInt32LittleEndian(header[68..72]);
    var difatSectorCount = BinaryPrimitives.ReadUInt32LittleEndian(header[72..76]);

    if (data.Length < sectorSize)
      throw new InvalidDataException("CFB file is shorter than its header sector.");
    if (data.Length % sectorSize != 0)
      throw new InvalidDataException("CFB file length is not aligned to the declared sector size.");

    var sectorCount = data.Length / sectorSize - 1;
    var fatSectorIds = new List<uint>(checked((int)Math.Min(fatSectorCount, int.MaxValue)));
    for (var i = 0; i < 109 && fatSectorIds.Count < fatSectorCount; ++i) {
      var id = BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(76 + i * 4, 4));
      if (id != FreeSector)
        fatSectorIds.Add(id);
    }

    var difatSeen = new HashSet<uint>();
    var difatId = firstDifatSector;
    var difatEntriesPerSector = sectorSize / 4 - 1;
    for (uint sectorIndex = 0; sectorIndex < difatSectorCount; ++sectorIndex) {
      if (difatId is EndOfChain or FreeSector)
        throw new InvalidDataException("CFB DIFAT chain ended before the declared DIFAT sector count.");
      if (!difatSeen.Add(difatId))
        throw new InvalidDataException("CFB DIFAT chain contains a cycle.");
      var sector = _Sector(data, sectorSize, sectorCount, difatId);
      for (var i = 0; i < difatEntriesPerSector && fatSectorIds.Count < fatSectorCount; ++i) {
        var id = BinaryPrimitives.ReadUInt32LittleEndian(sector.Slice(i * 4, 4));
        if (id != FreeSector)
          fatSectorIds.Add(id);
      }
      difatId = BinaryPrimitives.ReadUInt32LittleEndian(sector[^4..]);
    }

    if (fatSectorIds.Count != fatSectorCount)
      throw new InvalidDataException($"CFB declares {fatSectorCount} FAT sectors but DIFAT resolves {fatSectorIds.Count}.");

    var fat = new uint[checked(fatSectorIds.Count * (sectorSize / 4))];
    var fatOffset = 0;
    foreach (var id in fatSectorIds) {
      var sector = _Sector(data, sectorSize, sectorCount, id);
      for (var i = 0; i < sectorSize; i += 4)
        fat[fatOffset++] = BinaryPrimitives.ReadUInt32LittleEndian(sector.Slice(i, 4));
    }

    var directoryBytes = _ReadFatChain(data, sectorSize, sectorCount, fat, firstDirectorySector, size: null);
    if (directoryBytes.Length == 0 || directoryBytes.Length % DirectoryEntryLength != 0)
      throw new InvalidDataException("CFB directory stream length is invalid.");

    var directory = new DirectoryEntry[directoryBytes.Length / DirectoryEntryLength];
    for (var i = 0; i < directory.Length; ++i)
      directory[i] = _ReadDirectoryEntry(directoryBytes.AsSpan(i * DirectoryEntryLength, DirectoryEntryLength), majorVersion);

    if (directory.Length == 0 || directory[0].ObjectType != 5)
      throw new InvalidDataException("CFB stream ID 0 is not the root storage entry.");

    uint[] miniFat;
    if (miniFatSectorCount == 0) {
      miniFat = [];
    } else {
      if (firstMiniFatSector is EndOfChain or FreeSector)
        throw new InvalidDataException("CFB declares MiniFAT sectors but has no MiniFAT start sector.");
      var miniFatBytes = _ReadFatChain(
        data, sectorSize, sectorCount, fat, firstMiniFatSector,
        checked((ulong)miniFatSectorCount * (ulong)sectorSize));
      miniFat = new uint[miniFatBytes.Length / 4];
      for (var i = 0; i < miniFat.Length; ++i)
        miniFat[i] = BinaryPrimitives.ReadUInt32LittleEndian(miniFatBytes.AsSpan(i * 4, 4));
    }

    var root = directory[0];
    var miniStream = root.StreamSize == 0
      ? []
      : _ReadFatChain(data, sectorSize, sectorCount, fat, root.StartingSector, root.StreamSize);

    var entries = new List<Entry>();
    var placed = new HashSet<uint>();
    _WalkDirectoryTree(directory, root.ChildId, parentPath: "", entries, placed, new HashSet<uint>());

    return new(data, sectorSize, miniSectorSize, miniStreamCutoff, fat, miniFat, directory, miniStream, entries);
  }

  internal byte[] ReadStream(uint directoryId) {
    if (directoryId >= this._directory.Length)
      throw new InvalidDataException($"CFB directory ID {directoryId} is out of bounds.");
    var entry = this._directory[directoryId];
    if (entry.ObjectType != 2)
      throw new InvalidOperationException($"CFB directory ID {directoryId} is not a stream.");
    if (entry.StreamSize > int.MaxValue)
      throw new NotSupportedException("CFB streams larger than 2 GiB are not supported by the in-memory entry API.");
    if (entry.StreamSize == 0)
      return [];

    return entry.StreamSize < this._miniStreamCutoff
      ? this._ReadMiniChain(entry.StartingSector, entry.StreamSize)
      : _ReadFatChain(
        this._data,
        this._sectorSize,
        this._data.Length / this._sectorSize - 1,
        this._fat,
        entry.StartingSector,
        entry.StreamSize);
  }

  internal static void ValidateHeader(ReadOnlySpan<byte> header, long fileSize) {
    if (header.Length < HeaderLength)
      throw new InvalidDataException("CFB header requires at least 512 bytes.");
    if (!header[..8].SequenceEqual(CompoundFileBinaryDescriptor.Signature))
      throw new InvalidDataException("CFB signature is invalid.");

    var major = BinaryPrimitives.ReadUInt16LittleEndian(header[26..28]);
    var byteOrder = BinaryPrimitives.ReadUInt16LittleEndian(header[28..30]);
    var sectorShift = BinaryPrimitives.ReadUInt16LittleEndian(header[30..32]);
    var miniSectorShift = BinaryPrimitives.ReadUInt16LittleEndian(header[32..34]);
    var directorySectorCount = BinaryPrimitives.ReadUInt32LittleEndian(header[40..44]);
    var miniCutoff = BinaryPrimitives.ReadUInt32LittleEndian(header[56..60]);

    if (major is not (3 or 4))
      throw new InvalidDataException($"Unsupported CFB major version {major}.");
    if (byteOrder != 0xFFFE)
      throw new InvalidDataException("CFB byte order marker must be 0xFFFE.");
    if (sectorShift != (major == 3 ? 9 : 12))
      throw new InvalidDataException($"CFB version {major} has invalid sector shift {sectorShift}.");
    if (miniSectorShift != 6)
      throw new InvalidDataException($"CFB mini sector shift must be 6, not {miniSectorShift}.");
    if (major == 3 && directorySectorCount != 0)
      throw new InvalidDataException("CFB version 3 must declare zero directory-sector count.");
    if (miniCutoff != 0x1000)
      throw new InvalidDataException($"CFB mini-stream cutoff must be 4096 bytes, not {miniCutoff}.");

    var sectorSize = 1L << sectorShift;
    if (fileSize < sectorSize)
      throw new InvalidDataException("CFB file is shorter than its header sector.");
  }

  private byte[] _ReadMiniChain(uint startSector, ulong size) {
    var required = checked((int)size);
    var result = new byte[required];
    var written = 0;
    var current = startSector;
    var seen = new HashSet<uint>();

    while (written < required) {
      if (current is EndOfChain or FreeSector)
        throw new InvalidDataException("CFB MiniFAT chain ended before the declared stream size.");
      if (current >= this._miniFat.Length)
        throw new InvalidDataException($"CFB mini sector {current} is outside the MiniFAT.");
      if (!seen.Add(current))
        throw new InvalidDataException("CFB MiniFAT chain contains a cycle.");

      var offset = checked((long)current * this._miniSectorSize);
      if (offset < 0 || offset + this._miniSectorSize > this._miniStream.LongLength)
        throw new InvalidDataException($"CFB mini sector {current} points outside the root mini stream.");

      var take = Math.Min(this._miniSectorSize, required - written);
      Buffer.BlockCopy(this._miniStream, checked((int)offset), result, written, take);
      written += take;
      current = this._miniFat[current];
    }

    return result;
  }

  private static byte[] _ReadFatChain(
    byte[] data,
    int sectorSize,
    int sectorCount,
    uint[] fat,
    uint startSector,
    ulong? size) {

    if (startSector is EndOfChain or FreeSector)
      return size is null or 0 ? [] : throw new InvalidDataException("CFB FAT chain has no start sector.");

    var expected = size is null ? -1 : checked((int)size.Value);
    using var output = expected >= 0 ? new MemoryStream(expected) : new MemoryStream();
    var current = startSector;
    var seen = new HashSet<uint>();

    while (current != EndOfChain) {
      if (current is FreeSector or FatSector or DifatSector)
        throw new InvalidDataException($"CFB FAT chain references reserved sector marker 0x{current:X8}.");
      if (current >= fat.Length)
        throw new InvalidDataException($"CFB sector {current} is outside the FAT.");
      if (!seen.Add(current))
        throw new InvalidDataException("CFB FAT chain contains a cycle.");

      var sector = _Sector(data, sectorSize, sectorCount, current);
      if (expected >= 0) {
        var remaining = expected - checked((int)output.Length);
        if (remaining <= 0)
          break;
        output.Write(sector[..Math.Min(sector.Length, remaining)]);
        if (output.Length == expected)
          break;
      } else {
        output.Write(sector);
      }

      current = fat[current];
    }

    if (expected >= 0 && output.Length != expected)
      throw new InvalidDataException($"CFB FAT chain provides {output.Length} bytes for a {expected}-byte stream.");
    return output.ToArray();
  }

  private static ReadOnlySpan<byte> _Sector(byte[] data, int sectorSize, int sectorCount, uint sectorId) {
    if (sectorId >= sectorCount)
      throw new InvalidDataException($"CFB sector {sectorId} is outside the file ({sectorCount} sectors).");
    var offset = checked((long)(sectorId + 1) * sectorSize);
    return data.AsSpan(checked((int)offset), sectorSize);
  }

  private static DirectoryEntry _ReadDirectoryEntry(ReadOnlySpan<byte> entry, ushort majorVersion) {
    var objectType = entry[66];
    if (objectType == 0)
      return new("", 0, NoStream, NoStream, NoStream, EndOfChain, 0, null);
    if (objectType is not (1 or 2 or 5))
      throw new InvalidDataException($"CFB directory entry has unsupported object type 0x{objectType:X2}.");

    var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(entry[64..66]);
    if (nameLength is < 2 or > 64 || (nameLength & 1) != 0)
      throw new InvalidDataException($"CFB directory entry has invalid UTF-16 name length {nameLength}.");
    var nameBytes = entry[..(nameLength - 2)];
    var name = Encoding.Unicode.GetString(nameBytes);
    if (name.IndexOf('\0') >= 0)
      throw new InvalidDataException("CFB directory entry name contains an embedded NUL.");

    var left = BinaryPrimitives.ReadUInt32LittleEndian(entry[68..72]);
    var right = BinaryPrimitives.ReadUInt32LittleEndian(entry[72..76]);
    var child = BinaryPrimitives.ReadUInt32LittleEndian(entry[76..80]);
    var modifiedRaw = BinaryPrimitives.ReadUInt64LittleEndian(entry[108..116]);
    var start = BinaryPrimitives.ReadUInt32LittleEndian(entry[116..120]);
    var size = BinaryPrimitives.ReadUInt64LittleEndian(entry[120..128]);
    if (majorVersion == 3)
      size &= uint.MaxValue;

    return new(name, objectType, left, right, child, start, size, _FileTime(modifiedRaw));
  }

  private static DateTime? _FileTime(ulong raw) {
    if (raw == 0 || raw > long.MaxValue)
      return null;
    try {
      return DateTime.FromFileTimeUtc((long)raw);
    } catch (ArgumentOutOfRangeException) {
      return null;
    }
  }

  private static void _WalkDirectoryTree(
    DirectoryEntry[] directory,
    uint directoryId,
    string parentPath,
    List<Entry> output,
    HashSet<uint> placed,
    HashSet<uint> siblingStack) {

    if (directoryId == NoStream)
      return;
    if (directoryId >= directory.Length)
      throw new InvalidDataException($"CFB directory ID {directoryId} is out of bounds.");
    if (!siblingStack.Add(directoryId))
      throw new InvalidDataException("CFB directory sibling tree contains a cycle.");

    var entry = directory[directoryId];
    if (entry.ObjectType == 0)
      throw new InvalidDataException($"CFB directory tree references unallocated stream ID {directoryId}.");

    _WalkDirectoryTree(directory, entry.LeftSiblingId, parentPath, output, placed, siblingStack);

    if (!placed.Add(directoryId))
      throw new InvalidDataException($"CFB directory stream ID {directoryId} is referenced more than once.");
    var path = string.IsNullOrEmpty(parentPath) ? entry.Name : $"{parentPath}/{entry.Name}";
    if (entry.ObjectType == 1) {
      output.Add(new(directoryId, path, 0, true, entry.LastModified));
      _WalkDirectoryTree(directory, entry.ChildId, path, output, placed, new HashSet<uint>());
    } else if (entry.ObjectType == 2) {
      output.Add(new(directoryId, path, entry.StreamSize, false, entry.LastModified));
    } else {
      throw new InvalidDataException("CFB root storage may only appear at stream ID 0.");
    }

    _WalkDirectoryTree(directory, entry.RightSiblingId, parentPath, output, placed, siblingStack);
    siblingStack.Remove(directoryId);
  }
}
