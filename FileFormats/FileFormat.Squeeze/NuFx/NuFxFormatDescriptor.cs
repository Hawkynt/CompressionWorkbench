using System.Buffers.Binary;
using System.Text;
using Compression.Core.Dictionary.Lzw;
using Compression.Registry;
using Compression.Registry.Streaming;
using FileFormat.Squeeze;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.NuFx;

/// <summary>
/// NuFX / ShrinkIt archive descriptor for Apple II and Apple IIgs archives.
/// Supports plain SHK/SDK archives, native Stored/Squeeze/NuLZW1/NuLZW2/LZC creation,
/// record-preserving direct add/replace/remove, and slack-compacting rebuilds.
/// </summary>
public sealed class NuFxFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations,
    IArchiveCreatable, IArchiveModifiable, IArchiveDefragmentable, IArchiveShrinkable,
    IArchiveLayoutMap, IFormatOptionsSchema, IArchiveWriteConstraints, IFormatValidator {
  public string Id => "NuFx";
  public string DisplayName => "NuFX / ShrinkIt";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract |
    FormatCapabilities.CanCreate | FormatCapabilities.CanModify |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".shk";
  public IReadOnlyList<string> Extensions => [".shk", ".sdk", ".bxy"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new(NuFxArchive.MasterSignature, Offset: 0, Confidence: 0.98f),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [
    new("nulzw2", "ShrinkIt LZW/2"),
    new("nulzw1", "ShrinkIt LZW/1"),
    new("lzc12", "Unix compress LZC-12"),
    new("lzc16", "Unix compress LZC-16"),
    new("squeeze", "Squeeze"),
    new("stored", "Stored"),
    new("auto", "Auto (smallest)"),
  ];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description =>
    "Apple II/IIgs NuFX (ShrinkIt) archive — SHK/SDK read/write with Stored, Squeeze, LZW/1, LZW/2, LZC-12 and LZC-16 threads.";

  public IReadOnlyList<FormatOptionDescriptor> OptionsSchema { get; } = [
    new("Mode", "Archive mode", FormatOptionKind.Enum, "Files", ["Files", "DiskImage"],
      "Files creates a normal .shk archive. DiskImage creates the single disk-image record used by .sdk."),
    new("FileType", "ProDOS file type", FormatOptionKind.Integer, "0", null,
      "Default ProDOS file type for newly created ordinary file records (0-255)."),
    new("AuxType", "ProDOS aux type", FormatOptionKind.Integer, "0", null,
      "Default ProDOS auxiliary type for newly created ordinary file records (0-65535)."),
    new("Access", "ProDOS access flags", FormatOptionKind.Integer, "227", null,
      "Default ProDOS access byte for newly created records. 227 (0xE3) is an unlocked file."),
  ];

  public long? MaxTotalArchiveSize => uint.MaxValue;
  public long? MinTotalArchiveSize => NuFxArchive.MasterHeaderLength;
  public string AcceptedInputsDescription =>
    "Regular files with slash-separated paths; SDK disk-image mode accepts exactly one file whose size is a multiple of 512 bytes.";

  public bool CanAccept(ArchiveInputInfo input, out string? reason) {
    ArgumentNullException.ThrowIfNull(input);
    if (input.IsDirectory) {
      reason = "NuFX has a directory-control thread, but ShrinkIt did not use it; empty directories cannot be represented portably.";
      return false;
    }
    var name = input.ArchiveName.Replace('\\', '/').Trim('/');
    if (name.Length == 0) {
      reason = "NuFX entries require a non-empty pathname.";
      return false;
    }
    if (name.Split('/').Any(p => p is "" or "." or "..")) {
      reason = "NuFX path components may not be empty, '.' or '..'.";
      return false;
    }
    if (NuFxArchive.EncodeMacRoman(name.Replace('/', ':')).Length > ushort.MaxValue) {
      reason = "NuFX pathnames are limited to 65535 encoded bytes.";
      return false;
    }
    reason = null;
    return true;
  }

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    RejectPassword(password);
    var archive = NuFxArchive.Parse(stream);
    return archive.Records.Select((record, index) => new ArchiveEntryInfo(
      index,
      record.Name,
      record.LogicalLength,
      record.DataThread?.CompressedLength ?? 0,
      NuFxArchive.MethodName(record.DataThread?.Format ?? 0),
      false,
      false,
      null,
      record.IsDiskImage ? "disk-image" : "file"
    )).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    RejectPassword(password);
    var archive = NuFxArchive.Parse(stream);
    foreach (var record in archive.Records) {
      if (files != null && !MatchesFilter(record.Name, files))
        continue;
      WriteFile(outputDir, record.Name, NuFxArchive.ExtractRecord(stream, record));
    }
  }

  public Stream OpenEntry(Stream archive, string entryName, string? password) {
    RejectPassword(password);
    var parsed = NuFxArchive.Parse(archive);
    var record = FindRecord(parsed, entryName);
    if (record == null)
      return new BoundedEntryStream(new MemoryStream([], writable: false), 0, leaveOpen: false);
    var data = NuFxArchive.ExtractRecord(archive, record);
    return new BoundedEntryStream(new MemoryStream(data, writable: false), data.Length, leaveOpen: false);
  }

  public byte[] ExtractEntryToMemory(Stream archive, string entryName, string? password) {
    using var input = this.OpenEntry(archive, entryName, password);
    using var output = new MemoryStream();
    input.CopyTo(output);
    return output.ToArray();
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(inputs);
    ArgumentNullException.ThrowIfNull(options);
    RejectEncryption(options);

    var mode = GetOption(options, "Mode", "Files");
    var method = NormalizeMethod(options.MethodName);
    var fileType = checked((byte)ParseBoundedInt(GetOption(options, "FileType", "0"), 0, 255, "FileType"));
    var auxType = checked((ushort)ParseBoundedInt(GetOption(options, "AuxType", "0"), 0, 65535, "AuxType"));
    var access = checked((byte)ParseBoundedInt(GetOption(options, "Access", "227"), 0, 255, "Access"));

    var files = inputs.Where(i => !i.IsDirectory).ToList();
    if (mode.Equals("DiskImage", StringComparison.OrdinalIgnoreCase)) {
      if (files.Count != 1)
        throw new InvalidDataException("NuFX SDK disk-image mode requires exactly one input file.");
      var bytes = files[0].ReadContent();
      if ((bytes.Length & 511) != 0)
        throw new InvalidDataException("NuFX SDK disk images must be a multiple of 512 bytes.");
      NuFxArchive.Create(output, [
        NuFxArchive.BuildNewRecord(files[0].ArchiveName, bytes, method, true, fileType, auxType, access)
      ]);
      return;
    }

    var records = new List<byte[]>(files.Count);
    foreach (var input in files) {
      if (!this.CanAccept(input, out var reason))
        throw new InvalidDataException(reason);
      records.Add(NuFxArchive.BuildNewRecord(input.ArchiveName, input.ReadContent(), method, false, fileType, auxType, access));
    }
    NuFxArchive.Create(output, records);
  }

  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(inputs);
    NuFxArchive.RequireWritablePlainArchive(archive);

    foreach (var input in inputs.Where(i => !i.IsDirectory)) {
      if (!this.CanAccept(input, out var reason))
        throw new InvalidDataException(reason);

      var parsed = NuFxArchive.Parse(archive);
      var existing = FindRecord(parsed, input.ArchiveName);
      var bytes = input.ReadContent();
      if (existing != null) {
        var replacement = NuFxArchive.ReplaceDataForkPreservingRecord(archive, existing, bytes);
        NuFxArchive.ReplaceRange(archive, existing.StartOffset, existing.RecordLength, replacement);
        NuFxArchive.PatchMaster(archive, parsed.RecordCount, checked(parsed.NuFxLength - existing.RecordLength + replacement.LongLength));
      } else {
        var record = NuFxArchive.BuildNewRecord(input.ArchiveName, bytes, "nulzw2", false, 0, 0, 0xE3);
        var insertAt = checked(parsed.StartOffset + parsed.NuFxLength);
        NuFxArchive.ReplaceRange(archive, insertAt, 0, record);
        NuFxArchive.PatchMaster(archive, checked(parsed.RecordCount + 1), checked(parsed.NuFxLength + record.LongLength));
      }
    }
  }

  public void Remove(Stream archive, string[] entryNames) {
    ArgumentNullException.ThrowIfNull(archive);
    entryNames ??= [];
    NuFxArchive.RequireWritablePlainArchive(archive);

    foreach (var requested in entryNames) {
      while (true) {
        var parsed = NuFxArchive.Parse(archive);
        var record = FindRecord(parsed, requested);
        if (record == null)
          break;
        NuFxArchive.ReplaceRange(archive, record.StartOffset, record.RecordLength, []);
        NuFxArchive.PatchMaster(archive, checked(parsed.RecordCount - 1), checked(parsed.NuFxLength - record.RecordLength));
      }
    }
  }

  public void Defragment(Stream archive)
    => this.Defragment(archive, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });

  public void Defragment(Stream archive, DefragOptions options) {
    ArgumentNullException.ThrowIfNull(options);
    NuFxArchive.RequireWritablePlainArchive(archive);
    if (options.Mode != DefragMode.ConsolidateAtStart)
      throw new NotSupportedException("NuFX compaction supports ConsolidateAtStart; records already form one contiguous sequence.");

    var parsed = NuFxArchive.Parse(archive);
    var records = parsed.Records.Select(record => NuFxArchive.CompactRecord(archive, record)).ToList();
    using var rebuilt = new MemoryStream();
    NuFxArchive.Create(rebuilt, records);
    NuFxArchive.ReplaceRange(archive, parsed.StartOffset, parsed.NuFxLength, rebuilt.ToArray());
  }

  public IEnumerable<DefragBlockInfo> EnumerateLayout(Stream archive) {
    var parsed = NuFxArchive.Parse(archive);
    yield return new DefragBlockInfo(parsed.StartOffset, NuFxArchive.MasterHeaderLength, DefragBlockKind.Used, FileName: "<NuFX master>");
    foreach (var record in parsed.Records)
      yield return new DefragBlockInfo(record.StartOffset, record.RecordLength, DefragBlockKind.Used, FileName: record.Name);
  }

  public void Shrink(Stream input, Stream output) {
    ArgumentNullException.ThrowIfNull(input);
    ArgumentNullException.ThrowIfNull(output);
    var parsed = NuFxArchive.Parse(input);
    if (parsed.StartOffset != 0 || parsed.NuFxLength != input.Length) {
      input.Position = 0;
      output.Position = 0;
      output.SetLength(0);
      input.CopyTo(output);
      return;
    }

    var records = parsed.Records.Select(record => NuFxArchive.CompactRecord(input, record)).ToList();
    using var rebuilt = new MemoryStream();
    NuFxArchive.Create(rebuilt, records);

    output.Position = 0;
    output.SetLength(0);
    if (rebuilt.Length < input.Length) {
      rebuilt.Position = 0;
      rebuilt.CopyTo(output);
    } else {
      input.Position = 0;
      input.CopyTo(output);
    }
  }

  public ValidationResult ValidateHeader(ReadOnlySpan<byte> header, long fileSize) {
    var issues = new List<ValidationIssue>();
    if (header.Length < NuFxArchive.MasterHeaderLength) {
      issues.Add(new ValidationIssue(ValidationLevel.Header, IssueSeverity.Error, "NUFX_SHORT_HEADER",
        "NuFX master header is shorter than 48 bytes."));
      return Validation(false, 0.10, FormatHealth.Uncertain, ValidationLevel.Header, issues);
    }
    if (!header[..NuFxArchive.MasterSignature.Length].SequenceEqual(NuFxArchive.MasterSignature)) {
      issues.Add(new ValidationIssue(ValidationLevel.Header, IssueSeverity.Error, "NUFX_BAD_MAGIC",
        "NuFX master signature is missing."));
      return Validation(false, 0.05, FormatHealth.Uncertain, ValidationLevel.Header, issues);
    }

    var stored = BinaryPrimitives.ReadUInt16LittleEndian(header.Slice(6, 2));
    var calculated = NuLzwCodec.Crc16Xmodem(header.Slice(8, NuFxArchive.MasterHeaderLength - 8), 0);
    if (stored != calculated)
      issues.Add(new ValidationIssue(ValidationLevel.Header, IssueSeverity.Error, "NUFX_MASTER_CRC",
        $"Master CRC mismatch: stored 0x{stored:X4}, calculated 0x{calculated:X4}.", 6));

    var version = BinaryPrimitives.ReadUInt16LittleEndian(header.Slice(0x1C, 2));
    var eof = BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(0x26, 4));
    if (version > 2)
      issues.Add(new ValidationIssue(ValidationLevel.Header, IssueSeverity.Warning, "NUFX_MASTER_VERSION",
        $"Unknown NuFX master version {version}.", 0x1C));
    if (version > 0 && eof != 0 && eof > fileSize)
      issues.Add(new ValidationIssue(ValidationLevel.Header, IssueSeverity.Error, "NUFX_MASTER_EOF",
        $"Master EOF {eof} exceeds physical file size {fileSize}.", 0x26));

    var valid = issues.All(i => i.Severity != IssueSeverity.Error);
    return Validation(valid, valid ? 0.99 : 0.85,
      valid ? (issues.Count == 0 ? FormatHealth.Perfect : FormatHealth.Good) : FormatHealth.Damaged,
      ValidationLevel.Header, issues);
  }

  public ValidationResult ValidateStructure(Stream stream) {
    try {
      var parsed = NuFxArchive.Parse(stream);
      return Validation(true, 1.0, FormatHealth.Perfect, ValidationLevel.Structure, [],
        parsed.Records.Count, parsed.Records.Count);
    } catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or EndOfStreamException) {
      return Validation(false, 0.95, FormatHealth.Damaged, ValidationLevel.Structure, [
        new ValidationIssue(ValidationLevel.Structure, IssueSeverity.Error, "NUFX_STRUCTURE", ex.Message)
      ]);
    }
  }

  public ValidationResult ValidateIntegrity(Stream stream) {
    try {
      var parsed = NuFxArchive.Parse(stream);
      var issues = new List<ValidationIssue>();
      var validEntries = 0;
      foreach (var record in parsed.Records) {
        var format = record.DataThread?.Format ?? (ushort)0;
        if (format > 5) {
          issues.Add(new ValidationIssue(ValidationLevel.Integrity, IssueSeverity.Warning,
            "NUFX_UNCHECKED_METHOD", $"'{record.Name}' uses compression format {format}, which is structurally preserved but not decoded by this implementation.",
            record.StartOffset));
          continue;
        }
        _ = NuFxArchive.ExtractRecord(stream, record);
        validEntries++;
      }
      return Validation(true, 1.0, issues.Count == 0 ? FormatHealth.Perfect : FormatHealth.Degraded,
        ValidationLevel.Integrity, issues, validEntries, parsed.Records.Count);
    } catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or EndOfStreamException) {
      return Validation(false, 1.0, FormatHealth.Damaged, ValidationLevel.Integrity, [
        new ValidationIssue(ValidationLevel.Integrity, IssueSeverity.Error, "NUFX_INTEGRITY", ex.Message)
      ]);
    }
  }

  private static ValidationResult Validation(bool valid, double confidence, FormatHealth health,
      ValidationLevel level, IReadOnlyList<ValidationIssue> issues, int? validEntries = null, int? totalEntries = null)
    => new() {
      IsValid = valid,
      Confidence = confidence,
      Health = health,
      Level = level,
      Issues = issues,
      ValidEntries = validEntries,
      TotalEntries = totalEntries,
    };

  private static NuFxRecord? FindRecord(NuFxParsedArchive archive, string name) {
    var normalized = NuFxArchive.NormalizePath(name);
    var exact = archive.Records.FirstOrDefault(r => r.Name.Equals(normalized, StringComparison.OrdinalIgnoreCase));
    if (exact != null || normalized.Contains('/'))
      return exact;
    return archive.Records.FirstOrDefault(r =>
      Path.GetFileName(r.Name).Equals(normalized, StringComparison.OrdinalIgnoreCase));
  }

  private static void RejectPassword(string? password) {
    if (!string.IsNullOrEmpty(password))
      throw new NotSupportedException("NuFX does not define password encryption.");
  }

  private static void RejectEncryption(FormatCreateOptions options) {
    if (!string.IsNullOrEmpty(options.Password) || options.EncryptFilenames ||
        !string.IsNullOrEmpty(options.EncryptionMethod))
      throw new NotSupportedException("NuFX does not define password encryption.");
  }

  private static string GetOption(FormatCreateOptions options, string key, string fallback)
    => options.FormatSpecific != null && options.FormatSpecific.TryGetValue(key, out var value) ? value : fallback;

  private static int ParseBoundedInt(string text, int min, int max, string name) {
    if (!int.TryParse(text, out var value) || value < min || value > max)
      throw new InvalidDataException($"{name} must be in the range {min}..{max}.");
    return value;
  }

  private static string NormalizeMethod(string? method) {
    if (string.IsNullOrWhiteSpace(method))
      return "nulzw2";
    var normalized = method.Trim().ToLowerInvariant();
    return normalized switch {
      "stored" or "store" => "stored",
      "squeeze" or "sq" => "squeeze",
      "nulzw1" or "lzw1" => "nulzw1",
      "nulzw2" or "lzw2" => "nulzw2",
      "lzc12" or "lzc-12" => "lzc12",
      "lzc16" or "lzc-16" => "lzc16",
      "auto" => "auto",
      _ => throw new NotSupportedException($"NuFX creation method '{method}' is not supported."),
    };
  }
}

internal sealed record NuFxParsedArchive(long StartOffset, long NuFxLength, uint RecordCount, IReadOnlyList<NuFxRecord> Records);

internal sealed record NuFxThread(
  ushort Class,
  ushort Format,
  ushort Kind,
  ushort Crc,
  uint UncompressedLength,
  uint CompressedLength,
  int HeaderOffset,
  long DataOffset
);

internal sealed record NuFxRecord(
  long StartOffset,
  long RecordLength,
  ushort Version,
  byte FileSystemSeparator,
  uint FileType,
  uint ExtraType,
  ushort StorageType,
  string Name,
  bool IsDiskImage,
  long LogicalLength,
  byte[] RawHeader,
  IReadOnlyList<NuFxThread> Threads,
  NuFxThread? DataThread
);

internal static class NuFxArchive {
  internal const int MasterHeaderLength = 48;
  private const int FixedRecordHeaderLength = 56;
  private const int ThreadHeaderLength = 16;
  private const ushort RecordVersion = 3;
  private const ushort ProDosFileSystem = 1;
  private const ushort ThreadClassMessage = 0;
  private const ushort ThreadClassData = 2;
  private const ushort ThreadClassFilename = 3;
  private const ushort KindDataFork = 0;
  private const ushort KindDiskImage = 1;
  private const ushort KindResourceFork = 2;
  private const ushort KindComment = 1;
  private const int MaxRecordThreads = 4096;
  private static readonly byte[] RecordSignature = [0x4E, 0xF5, 0x46, 0xD8];

  internal static readonly byte[] MasterSignature = [0x4E, 0xF5, 0x46, 0xE9, 0x6C, 0xE5];

  private const string MacRomanHigh =
    "ÄÅÇÉÑÖÜáàâäãåçéèêëíìîïñóòôöõúùûü†°¢£§•¶ß®©™´¨≠ÆØ∞±≤≥¥µ∂∑∏π∫ªºΩæø¿¡¬√ƒ≈∆«»… ÀÃÕŒœ–—“”‘’÷◊ÿŸ⁄€‹›ﬁﬂ‡·‚„‰ÂÊÁËÈÍÎÏÌÓÔÒÚÛÙıˆ˜¯˘˙˚¸˝˛ˇ";

  internal static NuFxParsedArchive Parse(Stream stream) {
    ArgumentNullException.ThrowIfNull(stream);
    if (!stream.CanRead || !stream.CanSeek)
      throw new NotSupportedException("NuFX parsing requires a readable, seekable stream.");

    var start = FindMaster(stream);
    if (start < 0)
      throw new InvalidDataException("NuFX master signature not found.");

    stream.Position = start;
    var master = ReadExactly(stream, MasterHeaderLength);
    var storedMasterCrc = BinaryPrimitives.ReadUInt16LittleEndian(master.AsSpan(6, 2));
    var calculatedMasterCrc = NuLzwCodec.Crc16Xmodem(master.AsSpan(8), 0);
    if (storedMasterCrc != calculatedMasterCrc)
      throw new InvalidDataException($"NuFX master CRC mismatch: stored 0x{storedMasterCrc:X4}, calculated 0x{calculatedMasterCrc:X4}.");

    var recordCount = BinaryPrimitives.ReadUInt32LittleEndian(master.AsSpan(8, 4));
    var masterVersion = BinaryPrimitives.ReadUInt16LittleEndian(master.AsSpan(0x1C, 2));
    var declaredLength = BinaryPrimitives.ReadUInt32LittleEndian(master.AsSpan(0x26, 4));
    var nufxLength = masterVersion > 0 && declaredLength >= MasterHeaderLength
      ? declaredLength
      : checked(stream.Length - start);
    if (start + nufxLength > stream.Length)
      throw new InvalidDataException("NuFX master EOF extends beyond the physical stream.");

    var records = new List<NuFxRecord>(checked((int)Math.Min(recordCount, 100000u)));
    stream.Position = start + MasterHeaderLength;
    for (uint index = 0; index < recordCount; index++) {
      if (stream.Position >= start + nufxLength)
        throw new InvalidDataException("NuFX archive ended before the declared record count.");
      records.Add(ReadRecord(stream, start + nufxLength));
    }

    if (stream.Position > start + nufxLength)
      throw new InvalidDataException("NuFX records extend beyond the master EOF.");
    return new NuFxParsedArchive(start, nufxLength, recordCount, records);
  }

  internal static void Create(Stream output, IReadOnlyList<byte[]> records) {
    ArgumentNullException.ThrowIfNull(output);
    if (!output.CanWrite || !output.CanSeek)
      throw new NotSupportedException("NuFX creation requires a writable, seekable output stream.");

    output.Position = 0;
    output.SetLength(0);
    output.Position = MasterHeaderLength;
    foreach (var record in records)
      output.Write(record);
    var length = output.Position;
    if (length > uint.MaxValue)
      throw new InvalidDataException("NuFX archives are limited by the 32-bit master EOF field.");
    PatchMaster(output, checked((uint)records.Count), length);
    output.Position = length;
    output.SetLength(length);
  }

  internal static byte[] BuildNewRecord(string name, byte[] data, string method, bool diskImage,
      byte fileType, ushort auxType, byte access) {
    ArgumentNullException.ThrowIfNull(data);
    var storedPath = NormalizePath(name).Replace('/', ':');
    var nameBytes = EncodeMacRoman(storedPath);
    if (nameBytes.Length == 0 || nameBytes.Length > ushort.MaxValue)
      throw new InvalidDataException("NuFX filename length is invalid.");

    var selected = CompressBest(data, method);
    var filenameFieldLength = Math.Max(nameBytes.Length, 32);
    const int attribCount = FixedRecordHeaderLength + 4;
    const int threadCount = 2;
    var headerLength = checked(attribCount + threadCount * ThreadHeaderLength);
    var header = new byte[headerLength];

    RecordSignature.CopyTo(header, 0);
    BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(6, 2), attribCount);
    BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(8, 2), RecordVersion);
    BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0x0A, 4), threadCount);
    BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(0x0E, 2), ProDosFileSystem);
    header[0x10] = (byte)':';
    header[0x12] = access;
    BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0x16, 4), diskImage ? 0u : fileType);
    BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0x1A, 4),
      diskImage ? checked((uint)(data.Length / 512)) : auxType);
    BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(0x1E, 2), diskImage ? (ushort)512 : (ushort)1);
    BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(0x38, 2), 0);
    BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(0x3A, 2), 0);

    var threadOffset = attribCount;
    WriteThreadHeader(header.AsSpan(threadOffset, ThreadHeaderLength),
      ThreadClassFilename, 0, 0, 0, checked((uint)nameBytes.Length), checked((uint)filenameFieldLength));
    threadOffset += ThreadHeaderLength;
    var threadCrc = NuLzwCodec.Crc16Xmodem(data, 0xFFFF);
    WriteThreadHeader(header.AsSpan(threadOffset, ThreadHeaderLength),
      ThreadClassData, selected.Format, diskImage ? KindDiskImage : KindDataFork, threadCrc,
      diskImage ? 0u : checked((uint)data.Length), checked((uint)selected.Bytes.Length));

    BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(4, 2), NuLzwCodec.Crc16Xmodem(header.AsSpan(6), 0));

    using var result = new MemoryStream(header.Length + filenameFieldLength + selected.Bytes.Length);
    result.Write(header);
    result.Write(nameBytes);
    if (filenameFieldLength > nameBytes.Length)
      result.Write(new byte[filenameFieldLength - nameBytes.Length]);
    result.Write(selected.Bytes);
    return result.ToArray();
  }

  internal static byte[] ExtractRecord(Stream archive, NuFxRecord record) {
    var thread = record.DataThread;
    if (thread == null || thread.CompressedLength == 0)
      return [];
    archive.Position = thread.DataOffset;
    var compressed = ReadExactly(archive, checked((int)thread.CompressedLength));
    var logicalLength = checked((int)record.LogicalLength);
    byte[] expanded = thread.Format switch {
      0 => compressed.Length == logicalLength ? compressed : compressed.AsSpan(0, Math.Min(compressed.Length, logicalLength)).ToArray(),
      1 => DecompressSqueeze(compressed),
      2 => NuLzwCodec.Decompress(compressed, NuLzwVariant.Lzw1, logicalLength),
      3 => NuLzwCodec.Decompress(compressed, NuLzwVariant.Lzw2, logicalLength),
      4 => NuFxLzcCodec.Decompress(compressed, 12, logicalLength),
      5 => NuFxLzcCodec.Decompress(compressed, 16, logicalLength),
      _ => throw new NotSupportedException($"NuFX thread compression format {thread.Format} is not supported for extraction."),
    };

    if (expanded.Length < logicalLength)
      throw new InvalidDataException($"NuFX entry '{record.Name}' expanded to {expanded.Length} bytes, expected {logicalLength}.");
    if (expanded.Length != logicalLength)
      expanded = expanded.AsSpan(0, logicalLength).ToArray();

    if (record.Version == 3) {
      var actual = NuLzwCodec.Crc16Xmodem(expanded, 0xFFFF);
      if (actual != thread.Crc)
        throw new InvalidDataException($"NuFX thread CRC mismatch for '{record.Name}': stored 0x{thread.Crc:X4}, calculated 0x{actual:X4}.");
    }
    return expanded;
  }

  internal static byte[] ReplaceDataForkPreservingRecord(Stream archive, NuFxRecord record, byte[] newData) {
    if (record.Version == 2)
      throw new NotSupportedException("Direct replacement of rare NuFX v2 records is refused because v2 thread CRC semantics differ.");
    var target = record.DataThread;
    if (target == null)
      return AddDataForkPreservingRecord(archive, record, newData);
    if (record.IsDiskImage && (newData.Length & 511) != 0)
      throw new InvalidDataException("Replacing an SDK disk image requires a multiple-of-512 byte payload.");

    var method = target.Format switch {
      0 => "stored",
      1 => "squeeze",
      2 => "nulzw1",
      3 => "nulzw2",
      4 => "lzc12",
      5 => "lzc16",
      _ => "stored",
    };
    var selected = CompressBest(newData, method);
    var header = (byte[])record.RawHeader.Clone();
    var threadHeader = header.AsSpan(target.HeaderOffset, ThreadHeaderLength);
    BinaryPrimitives.WriteUInt16LittleEndian(threadHeader.Slice(2, 2), selected.Format);
    var crc = record.Version == 3 ? NuLzwCodec.Crc16Xmodem(newData, 0xFFFF) : (ushort)0;
    BinaryPrimitives.WriteUInt16LittleEndian(threadHeader.Slice(6, 2), crc);
    BinaryPrimitives.WriteUInt32LittleEndian(threadHeader.Slice(8, 4),
      record.IsDiskImage ? 0u : checked((uint)newData.Length));
    BinaryPrimitives.WriteUInt32LittleEndian(threadHeader.Slice(12, 4), checked((uint)selected.Bytes.Length));

    if (record.IsDiskImage) {
      BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0x1A, 4), checked((uint)(newData.Length / 512)));
      BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(0x1E, 2), 512);
    }

    BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(4, 2), NuLzwCodec.Crc16Xmodem(header.AsSpan(6), 0));
    return AssembleRecord(archive, record, header, target, selected.Bytes, trimSlack: false);
  }

  private static byte[] AddDataForkPreservingRecord(Stream archive, NuFxRecord record, byte[] newData) {
    var selected = CompressBest(newData, "nulzw2");
    var oldHeader = record.RawHeader;
    var header = new byte[checked(oldHeader.Length + ThreadHeaderLength)];
    oldHeader.CopyTo(header, 0);
    BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0x0A, 4), checked((uint)record.Threads.Count + 1));

    var newThreadOffset = oldHeader.Length;
    var crc = record.Version == 3 ? NuLzwCodec.Crc16Xmodem(newData, 0xFFFF) : (ushort)0;
    WriteThreadHeader(header.AsSpan(newThreadOffset, ThreadHeaderLength),
      ThreadClassData, selected.Format, KindDataFork, crc,
      checked((uint)newData.Length), checked((uint)selected.Bytes.Length));

    if (record.Threads.Any(t => t.Class == ThreadClassData && t.Kind == KindResourceFork))
      BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(0x1E, 2), 5);
    else if (record.StorageType == 0)
      BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(0x1E, 2), 1);

    BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(4, 2), NuLzwCodec.Crc16Xmodem(header.AsSpan(6), 0));

    using var output = new MemoryStream();
    output.Write(header);
    foreach (var thread in record.Threads) {
      if (thread.CompressedLength == 0)
        continue;
      archive.Position = thread.DataOffset;
      CopyExactly(archive, output, thread.CompressedLength);
    }
    output.Write(selected.Bytes);
    return output.ToArray();
  }

  internal static byte[] CompactRecord(Stream archive, NuFxRecord record) {
    var header = (byte[])record.RawHeader.Clone();
    var changed = false;
    foreach (var thread in record.Threads) {
      if (!IsSlackThread(thread) || thread.CompressedLength <= thread.UncompressedLength)
        continue;
      var span = header.AsSpan(thread.HeaderOffset, ThreadHeaderLength);
      BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(12, 4), thread.UncompressedLength);
      changed = true;
    }
    if (!changed)
      return ReadRange(archive, record.StartOffset, record.RecordLength);
    BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(4, 2), NuLzwCodec.Crc16Xmodem(header.AsSpan(6), 0));
    return AssembleRecord(archive, record, header, null, null, trimSlack: true);
  }

  internal static void RequireWritablePlainArchive(Stream archive) {
    if (!archive.CanRead || !archive.CanWrite || !archive.CanSeek)
      throw new NotSupportedException("NuFX direct mutation requires a readable, writable, seekable stream.");
    var parsed = Parse(archive);
    if (parsed.StartOffset != 0)
      throw new NotSupportedException("Direct mutation of wrapped BXY/SEA NuFX archives is not enabled; plain SHK/SDK archives are fully R/W.");
  }

  internal static void PatchMaster(Stream stream, uint count, long nufxLength) {
    if (nufxLength is < MasterHeaderLength or > uint.MaxValue)
      throw new InvalidDataException("NuFX master EOF is outside its 32-bit representable range.");
    const long start = 0;

    var master = new byte[MasterHeaderLength];
    MasterSignature.CopyTo(master, 0);
    if (stream.Length >= start + MasterHeaderLength) {
      stream.Position = start;
      var old = ReadExactly(stream, MasterHeaderLength);
      old.CopyTo(master, 0);
      MasterSignature.CopyTo(master, 0);
    }
    BinaryPrimitives.WriteUInt32LittleEndian(master.AsSpan(8, 4), count);
    BinaryPrimitives.WriteUInt16LittleEndian(master.AsSpan(0x1C, 2), 2);
    BinaryPrimitives.WriteUInt32LittleEndian(master.AsSpan(0x26, 4), checked((uint)nufxLength));
    BinaryPrimitives.WriteUInt16LittleEndian(master.AsSpan(6, 2), NuLzwCodec.Crc16Xmodem(master.AsSpan(8), 0));
    stream.Position = start;
    stream.Write(master);
  }

  internal static void ReplaceRange(Stream stream, long offset, long oldLength, ReadOnlySpan<byte> replacement) {
    if (!stream.CanRead || !stream.CanWrite || !stream.CanSeek)
      throw new NotSupportedException("NuFX mutation requires a readable, writable, seekable stream.");
    if (offset < 0 || oldLength < 0 || offset + oldLength > stream.Length)
      throw new ArgumentOutOfRangeException(nameof(offset));

    var replacementLength = replacement.Length;
    var delta = replacementLength - oldLength;
    var tailStart = offset + oldLength;
    var originalLength = stream.Length;
    var buffer = new byte[64 * 1024];

    if (delta > 0) {
      stream.SetLength(checked(originalLength + delta));
      var remaining = originalLength - tailStart;
      while (remaining > 0) {
        var chunk = (int)Math.Min(buffer.Length, remaining);
        var readAt = tailStart + remaining - chunk;
        stream.Position = readAt;
        stream.ReadExactly(buffer.AsSpan(0, chunk));
        stream.Position = readAt + delta;
        stream.Write(buffer, 0, chunk);
        remaining -= chunk;
      }
    } else if (delta < 0) {
      var readAt = tailStart;
      var writeAt = offset + replacementLength;
      while (readAt < originalLength) {
        var chunk = (int)Math.Min(buffer.Length, originalLength - readAt);
        stream.Position = readAt;
        stream.ReadExactly(buffer.AsSpan(0, chunk));
        stream.Position = writeAt;
        stream.Write(buffer, 0, chunk);
        readAt += chunk;
        writeAt += chunk;
      }
      Array.Clear(buffer);
      var wipeAt = originalLength + delta;
      var wipeRemaining = -delta;
      while (wipeRemaining > 0) {
        var chunk = (int)Math.Min(buffer.Length, wipeRemaining);
        stream.Position = wipeAt;
        stream.Write(buffer, 0, chunk);
        wipeAt += chunk;
        wipeRemaining -= chunk;
      }
      stream.SetLength(originalLength + delta);
    }

    stream.Position = offset;
    if (!replacement.IsEmpty)
      stream.Write(replacement);
  }

  internal static string MethodName(ushort format) => format switch {
    0 => "Stored",
    1 => "Squeeze",
    2 => "NuLZW/1",
    3 => "NuLZW/2",
    4 => "LZC-12",
    5 => "LZC-16",
    _ => $"NuFX-{format}",
  };

  internal static string NormalizePath(string path)
    => path.Replace('\\', '/').Trim('/');

  internal static byte[] EncodeMacRoman(string text) {
    using var output = new MemoryStream(text.Length);
    foreach (var ch in text) {
      if (ch <= 0x7F) {
        output.WriteByte((byte)ch);
        continue;
      }
      var index = MacRomanHigh.IndexOf(ch);
      output.WriteByte(index >= 0 ? checked((byte)(0x80 + index)) : (byte)'?');
    }
    return output.ToArray();
  }

  private static string DecodeMacRoman(ReadOnlySpan<byte> bytes) {
    var sb = new StringBuilder(bytes.Length);
    foreach (var value in bytes)
      sb.Append(value < 0x80 ? (char)value : MacRomanHigh[value - 0x80]);
    return sb.ToString();
  }

  private static long FindMaster(Stream stream) {
    var original = stream.Position;
    try {
      var max = (int)Math.Min(1024, Math.Max(0, stream.Length - MasterSignature.Length));
      var probe = new byte[max + MasterSignature.Length];
      stream.Position = 0;
      var read = stream.Read(probe, 0, probe.Length);
      for (var offset = 0; offset <= read - MasterSignature.Length; offset++) {
        if (probe.AsSpan(offset, MasterSignature.Length).SequenceEqual(MasterSignature))
          return offset;
      }
      return -1;
    } finally {
      stream.Position = original;
    }
  }

  private static NuFxRecord ReadRecord(Stream stream, long archiveEnd) {
    var start = stream.Position;
    var fixedHeader = ReadExactly(stream, FixedRecordHeaderLength);
    if (!fixedHeader.AsSpan(0, 4).SequenceEqual(RecordSignature))
      throw new InvalidDataException($"NuFX record signature missing at 0x{start:X}.");

    var storedCrc = BinaryPrimitives.ReadUInt16LittleEndian(fixedHeader.AsSpan(4, 2));
    var attribCount = BinaryPrimitives.ReadUInt16LittleEndian(fixedHeader.AsSpan(6, 2));
    var version = BinaryPrimitives.ReadUInt16LittleEndian(fixedHeader.AsSpan(8, 2));
    var threadCount = BinaryPrimitives.ReadUInt32LittleEndian(fixedHeader.AsSpan(0x0A, 4));
    // attrib_count is a 16-bit field, so its own width is the upper bound; the
    // megabyte ceiling it was compared against could never be reached.
    if (attribCount < FixedRecordHeaderLength + 2)
      throw new InvalidDataException($"NuFX record attribute count {attribCount} is invalid.");
    if (threadCount > MaxRecordThreads)
      throw new InvalidDataException($"NuFX record thread count {threadCount} is unreasonable.");
    if (version > 3)
      throw new NotSupportedException($"NuFX record version {version} is not supported.");

    var variable = ReadExactly(stream, attribCount - FixedRecordHeaderLength);
    var deprecatedNameLength = BinaryPrimitives.ReadUInt16LittleEndian(variable.AsSpan(variable.Length - 2, 2));
    var deprecatedName = ReadExactly(stream, deprecatedNameLength);
    var threadHeadersLength = checked((int)threadCount * ThreadHeaderLength);
    var threadHeaders = ReadExactly(stream, threadHeadersLength);

    var rawHeader = new byte[checked(attribCount + deprecatedNameLength + threadHeadersLength)];
    fixedHeader.CopyTo(rawHeader, 0);
    variable.CopyTo(rawHeader, FixedRecordHeaderLength);
    deprecatedName.CopyTo(rawHeader, attribCount);
    threadHeaders.CopyTo(rawHeader, attribCount + deprecatedNameLength);

    var calculatedCrc = NuLzwCodec.Crc16Xmodem(rawHeader.AsSpan(6), 0);
    if (storedCrc != calculatedCrc)
      throw new InvalidDataException($"NuFX record CRC mismatch at 0x{start:X}: stored 0x{storedCrc:X4}, calculated 0x{calculatedCrc:X4}.");

    var separator = fixedHeader[0x10] == 0 ? (byte)':' : fixedHeader[0x10];
    var fileType = BinaryPrimitives.ReadUInt32LittleEndian(fixedHeader.AsSpan(0x16, 4));
    var extraType = BinaryPrimitives.ReadUInt32LittleEndian(fixedHeader.AsSpan(0x1A, 4));
    var storageType = BinaryPrimitives.ReadUInt16LittleEndian(fixedHeader.AsSpan(0x1E, 2));

    var dataStart = checked(start + rawHeader.LongLength);
    var dataOffset = dataStart;
    var threads = new List<NuFxThread>(checked((int)threadCount));
    for (var index = 0; index < threadCount; index++) {
      var headerOffset = checked(attribCount + deprecatedNameLength + (int)index * ThreadHeaderLength);
      var span = rawHeader.AsSpan(headerOffset, ThreadHeaderLength);
      var thread = new NuFxThread(
        BinaryPrimitives.ReadUInt16LittleEndian(span),
        BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(2, 2)),
        BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(4, 2)),
        BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(6, 2)),
        BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(8, 4)),
        BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(12, 4)),
        headerOffset,
        dataOffset
      );
      threads.Add(thread);
      dataOffset = checked(dataOffset + thread.CompressedLength);
      if (dataOffset > archiveEnd)
        throw new InvalidDataException($"NuFX record at 0x{start:X} extends past the master EOF.");
    }

    byte[] nameBytes = deprecatedName;
    var filenameThread = threads.FirstOrDefault(t => t.Class == ThreadClassFilename && t.Kind == 0);
    if (filenameThread != null && filenameThread.UncompressedLength > 0) {
      if (filenameThread.UncompressedLength > filenameThread.CompressedLength)
        throw new InvalidDataException("NuFX filename thread logical length exceeds its allocated field.");
      stream.Position = filenameThread.DataOffset;
      nameBytes = ReadExactly(stream, checked((int)filenameThread.UncompressedLength));
    }

    var storedName = DecodeMacRoman(nameBytes);
    if (separator != 0)
      storedName = storedName.Replace((char)separator, '/');
    var name = NormalizePath(storedName);
    var dataThread = threads.FirstOrDefault(t =>
      t.Class == ThreadClassData && (t.Kind == KindDataFork || t.Kind == KindDiskImage));
    var diskImage = dataThread?.Kind == KindDiskImage;
    var logicalLength = dataThread == null
      ? 0
      : diskImage
        ? checked((long)extraType * 512)
        : dataThread.UncompressedLength;
    var recordEnd = dataOffset;
    stream.Position = recordEnd;
    return new NuFxRecord(start, recordEnd - start, version, separator, fileType, extraType,
      storageType, name, diskImage, logicalLength, rawHeader, threads, dataThread);
  }

  private static byte[] AssembleRecord(Stream archive, NuFxRecord record, byte[] header,
      NuFxThread? replacementThread, byte[]? replacementBytes, bool trimSlack) {
    using var output = new MemoryStream();
    output.Write(header);
    foreach (var thread in record.Threads) {
      if (replacementThread != null && ReferenceEquals(thread, replacementThread)) {
        output.Write(replacementBytes!);
        continue;
      }
      var length = thread.CompressedLength;
      if (trimSlack && IsSlackThread(thread))
        length = Math.Min(thread.CompressedLength, thread.UncompressedLength);
      if (length == 0)
        continue;
      archive.Position = thread.DataOffset;
      CopyExactly(archive, output, length);
    }
    return output.ToArray();
  }

  private static bool IsSlackThread(NuFxThread thread)
    => (thread.Class == ThreadClassFilename && thread.Kind == 0) ||
       (thread.Class == ThreadClassMessage && thread.Kind == KindComment);

  private static (ushort Format, byte[] Bytes) CompressBest(byte[] data, string method) {
    if (method == "stored")
      return (0, data);
    if (method == "squeeze")
      return (1, CompressSqueeze(data));
    if (method == "nulzw1")
      return (2, NuLzwCodec.Compress(data, NuLzwVariant.Lzw1));
    if (method == "nulzw2")
      return (3, NuLzwCodec.Compress(data, NuLzwVariant.Lzw2));
    if (method == "lzc12")
      return (4, NuFxLzcCodec.Compress(data, 12));
    if (method == "lzc16")
      return (5, NuFxLzcCodec.Compress(data, 16));
    if (method != "auto")
      throw new NotSupportedException($"Unsupported NuFX method '{method}'.");

    var candidates = new (ushort Format, byte[] Bytes)[] {
      (0, data),
      (1, CompressSqueeze(data)),
      (2, NuLzwCodec.Compress(data, NuLzwVariant.Lzw1)),
      (3, NuLzwCodec.Compress(data, NuLzwVariant.Lzw2)),
      (4, NuFxLzcCodec.Compress(data, 12)),
      (5, NuFxLzcCodec.Compress(data, 16)),
    };
    return candidates.OrderBy(c => c.Bytes.Length).ThenBy(c => c.Format).First();
  }

  private static byte[] CompressSqueeze(byte[] data) {
    using var input = new MemoryStream(data, writable: false);
    using var output = new MemoryStream();
    SqueezeStream.Compress(input, output, string.Empty);
    return output.ToArray();
  }

  private static byte[] DecompressSqueeze(byte[] data) {
    using var input = new MemoryStream(data, writable: false);
    using var output = new MemoryStream();
    SqueezeStream.Decompress(input, output);
    return output.ToArray();
  }

  private static void WriteThreadHeader(Span<byte> destination, ushort cls, ushort format, ushort kind,
      ushort crc, uint eof, uint compressedEof) {
    BinaryPrimitives.WriteUInt16LittleEndian(destination, cls);
    BinaryPrimitives.WriteUInt16LittleEndian(destination.Slice(2, 2), format);
    BinaryPrimitives.WriteUInt16LittleEndian(destination.Slice(4, 2), kind);
    BinaryPrimitives.WriteUInt16LittleEndian(destination.Slice(6, 2), crc);
    BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(8, 4), eof);
    BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(12, 4), compressedEof);
  }

  private static byte[] ReadExactly(Stream stream, int length) {
    if (length < 0)
      throw new InvalidDataException("Negative NuFX field length.");
    var data = new byte[length];
    if (length != 0)
      stream.ReadExactly(data);
    return data;
  }

  private static byte[] ReadRange(Stream stream, long offset, long length) {
    if (length > int.MaxValue)
      throw new NotSupportedException("NuFX record is too large to materialize.");
    stream.Position = offset;
    return ReadExactly(stream, checked((int)length));
  }

  private static void CopyExactly(Stream input, Stream output, long length) {
    var buffer = new byte[64 * 1024];
    var remaining = length;
    while (remaining > 0) {
      var count = (int)Math.Min(buffer.Length, remaining);
      input.ReadExactly(buffer.AsSpan(0, count));
      output.Write(buffer, 0, count);
      remaining -= count;
    }
  }
}
