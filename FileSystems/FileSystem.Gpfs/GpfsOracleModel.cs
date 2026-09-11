#pragma warning disable CS1591
using System.Globalization;
using System.Text.RegularExpressions;

namespace FileSystem.Gpfs;

/// <summary>
/// Clean-room semantic model extracted from IBM Storage Scale diagnostic output.
/// This is deliberately not an on-disk parser: it records facts that the IBM
/// tools expose so raw-image deductions can be compared against an independent
/// oracle instead of being promoted from plausible guesses.
/// </summary>
internal static class GpfsOracleParser {
  private static readonly Regex NumberField = new(@"\b(?<name>[A-Za-z][A-Za-z0-9]*) is (?<value>0x[0-9A-Fa-f]+|[0-9]+)", RegexOptions.CultureInvariant);
  private static readonly Regex Address = new(@"(?<![0-9])(?<disk>[0-9]+):(?<sector>[0-9]+)(?![0-9])", RegexOptions.CultureInvariant);
  private static readonly Regex HexWord = new(@"0x(?<word>[0-9A-Fa-f]+)", RegexOptions.CultureInvariant);
  private static readonly Regex TsdbfsHeader = new(@"^Inode (?<inode>[0-9]+) \[(?<bracket>[0-9]+)\] snap (?<snap>[0-9]+) \(index (?<index>[0-9]+) in block (?<block>[0-9]+)\):$", RegexOptions.CultureInvariant);

  internal static GpfsFsckOracle ParseMmfsckx(string text) {
    ArgumentNullException.ThrowIfNull(text);

    string? formatVersion = null;
    string? allocationLayout = null;
    string? allocationType = null;
    var subblocksPerFullBlock = 0;
    long sectorBytes = 0;
    long metadataSubblockBytes = 0;
    long metadataFullblockBytes = 0;
    long dataSubblockBytes = 0;
    long dataFullblockBytes = 0;
    long inodeBytes = 0;
    long indirectBytes = 0;
    long eaOverflowBytes = 0;
    long directoryBytes = 0;

    var reserved = new List<GpfsReservedFileOracle>();
    var mismatches = new List<GpfsAllocationMapMismatch>();
    MutableReservedFile? current = null;

    var lines = text.ReplaceLineEndings("\n").Split('\n');
    for (var i = 0; i < lines.Length; ++i) {
      var line = lines[i].Trim();
      if (line.Length == 0)
        continue;

      if (line.StartsWith("format version is ", StringComparison.Ordinal)) {
        var value = line["format version is ".Length..];
        formatVersion = value.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries)[0];
        continue;
      }

      if (line.StartsWith("allocation map layout is ", StringComparison.Ordinal)) {
        var tail = line["allocation map layout is ".Length..];
        var marker = tail.IndexOf("  type is ", StringComparison.Ordinal);
        if (marker >= 0) {
          allocationLayout = tail[..marker].Trim();
          allocationType = tail[(marker + "  type is ".Length)..].Trim();
        }
        continue;
      }

      if (line.StartsWith("subblocksPerFullBlock is ", StringComparison.Ordinal)) {
        subblocksPerFullBlock = ParseDecimalAfter(line, "subblocksPerFullBlock is ");
        continue;
      }

      sectorBytes = ParseSizeIfPresent(line, "sector", sectorBytes);
      metadataSubblockBytes = ParseSizeIfPresent(line, "metadata subblock", metadataSubblockBytes);
      metadataFullblockBytes = ParseSizeIfPresent(line, "metadata fullblock", metadataFullblockBytes);
      dataSubblockBytes = ParseSizeIfPresent(line, "data subblock", dataSubblockBytes);
      dataFullblockBytes = ParseSizeIfPresent(line, "data fullblock", dataFullblockBytes);
      inodeBytes = ParseSizeIfPresent(line, "inode", inodeBytes);
      indirectBytes = ParseSizeIfPresent(line, "indirect block", indirectBytes);
      eaOverflowBytes = ParseSizeIfPresent(line, "ea overflow block", eaOverflowBytes);
      directoryBytes = ParseSizeIfPresent(line, "directory block", directoryBytes);

      if (TryReservedKind(line, out var kind)) {
        FlushCurrent();
        current = new MutableReservedFile(kind);
        continue;
      }

      if (current != null) {
        foreach (Match match in NumberField.Matches(line)) {
          var name = match.Groups["name"].Value;
          var value = match.Groups["value"].Value;
          current.Fields[name] = value;
        }
        if (line.Contains("holdsData yes", StringComparison.Ordinal))
          current.HoldsData = true;
        else if (line.Contains("holdsData no", StringComparison.Ordinal))
          current.HoldsData = false;
      }

      if (TryParseAllocationMismatch(lines, ref i, out var mismatch))
        mismatches.Add(mismatch);
    }

    FlushCurrent();

    if (formatVersion == null || allocationLayout == null || allocationType == null || subblocksPerFullBlock <= 0)
      throw new InvalidDataException("GPFS mmfsckx output does not contain a complete file-system geometry header.");

    var geometry = new GpfsFileSystemGeometry(
      formatVersion,
      allocationLayout,
      allocationType,
      subblocksPerFullBlock,
      sectorBytes,
      metadataSubblockBytes,
      metadataFullblockBytes,
      dataSubblockBytes,
      dataFullblockBytes,
      inodeBytes,
      indirectBytes,
      eaOverflowBytes,
      directoryBytes);

    return new GpfsFsckOracle(geometry, reserved, mismatches);

    void FlushCurrent() {
      if (current == null)
        return;
      if (!current.TryBuild(out var item))
        throw new InvalidDataException($"Incomplete mmfsckx reserved-file section '{current.Kind}'.");
      reserved.Add(item);
      current = null;
    }
  }

  internal static GpfsInodeOracle ParseTsdbfsInode(string text) {
    ArgumentNullException.ThrowIfNull(text);
    var lines = text.ReplaceLineEndings("\n").Split('\n').Select(static line => line.Trim()).Where(static line => line.Length > 0).ToArray();
    if (lines.Length == 0)
      throw new InvalidDataException("Empty tsdbfs inode output.");

    var header = TsdbfsHeader.Match(lines[0]);
    if (!header.Success)
      throw new InvalidDataException("Unrecognized tsdbfs inode header.");

    var inode = ParseInt(header, "inode");
    var snap = ParseInt(header, "snap");
    var index = ParseInt(header, "index");
    var inodeBlock = ParseInt(header, "block");
    var addresses = new List<GpfsDiskAddress>();
    var inodeSize = 0;
    var addressSlots = 0;
    string? indirectionLevel = null;
    string? status = null;
    long objectVersion = 0;
    ulong generation = 0;
    var linkCount = 0;
    var blockSizeCode = 0;
    var lastBlockSubblocks = 0;
    uint checksum = 0;
    var checksumValid = false;
    long fileSize = 0;
    long fullBlocks = 0;
    var currentMetadataReplicas = 0;
    var maxMetadataReplicas = 0;
    var currentDataReplicas = 0;
    var maxDataReplicas = 0;
    var dataPoolIndex = 0;

    foreach (var line in lines[1..]) {
      if (line.StartsWith("Inode address: ", StringComparison.Ordinal)) {
        var beforeSize = line.AsSpan("Inode address: ".Length);
        var sizeMarker = beforeSize.IndexOf(" size ", StringComparison.Ordinal);
        if (sizeMarker < 0)
          throw new InvalidDataException("tsdbfs inode address line has no size field.");
        foreach (Match match in Address.Matches(beforeSize[..sizeMarker].ToString()))
          addresses.Add(ParseAddress(match));
        inodeSize = ParseIntField(line, "size");
        addressSlots = ParseIntField(line, "nAddrs");
      } else if (line.StartsWith("indirectionLevel=", StringComparison.Ordinal)) {
        indirectionLevel = ParseEqualsField(line, "indirectionLevel");
        status = ParseEqualsField(line, "status");
      } else if (line.StartsWith("objectVersion=", StringComparison.Ordinal)) {
        objectVersion = ParseLongEqualsField(line, "objectVersion");
        generation = ParseUlongEqualsField(line, "generation");
        linkCount = checked((int)ParseLongEqualsField(line, "nlink"));
      } else if (line.StartsWith("blocksize code=", StringComparison.Ordinal)) {
        blockSizeCode = ParseDecimalAfter(line, "blocksize code=");
      } else if (line.StartsWith("lastBlockSubblocks=", StringComparison.Ordinal)) {
        lastBlockSubblocks = ParseDecimalAfter(line, "lastBlockSubblocks=");
      } else if (line.StartsWith("checksum=", StringComparison.Ordinal)) {
        var value = ParseEqualsField(line, "checksum");
        checksum = uint.Parse(value.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
        checksumValid = line.Contains(" is Valid", StringComparison.OrdinalIgnoreCase);
      } else if (line.StartsWith("fileSize=", StringComparison.Ordinal)) {
        fileSize = ParseLongEqualsField(line, "fileSize");
        fullBlocks = ParseLongEqualsField(line, "nFullBlocks");
      } else if (line.StartsWith("currentMetadataReplicas=", StringComparison.Ordinal)) {
        currentMetadataReplicas = checked((int)ParseLongEqualsField(line, "currentMetadataReplicas"));
        maxMetadataReplicas = checked((int)ParseLongEqualsField(line, "maxMetadataReplicas"));
      } else if (line.StartsWith("currentDataReplicas=", StringComparison.Ordinal)) {
        currentDataReplicas = checked((int)ParseLongEqualsField(line, "currentDataReplicas"));
        maxDataReplicas = checked((int)ParseLongEqualsField(line, "maxDataReplicas"));
      } else if (line.StartsWith("dataPoolIndex=", StringComparison.Ordinal)) {
        dataPoolIndex = ParseDecimalAfter(line, "dataPoolIndex=");
      }
    }

    if (addresses.Count == 0 || inodeSize <= 0 || addressSlots <= 0 || indirectionLevel == null || status == null)
      throw new InvalidDataException("Incomplete tsdbfs inode output.");

    return new GpfsInodeOracle(
      inode,
      snap,
      index,
      inodeBlock,
      addresses,
      inodeSize,
      addressSlots,
      indirectionLevel,
      status,
      objectVersion,
      generation,
      linkCount,
      blockSizeCode,
      lastBlockSubblocks,
      checksum,
      checksumValid,
      fileSize,
      fullBlocks,
      currentMetadataReplicas,
      maxMetadataReplicas,
      currentDataReplicas,
      maxDataReplicas,
      dataPoolIndex);
  }

  internal static IReadOnlyList<GpfsSectorOwnerOracle> ParseMmfileid(string text, int diskId) {
    ArgumentNullException.ThrowIfNull(text);
    if (diskId < 0)
      throw new ArgumentOutOfRangeException(nameof(diskId));

    var result = new List<GpfsSectorOwnerOracle>();
    foreach (var raw in text.ReplaceLineEndings("\n").Split('\n')) {
      var line = raw.Trim();
      if (line.Length == 0)
        continue;

      if (line.StartsWith("Address ", StringComparison.Ordinal)) {
        var afterAddress = line.AsSpan("Address ".Length);
        var end = afterAddress.IndexOf(' ');
        if (end <= 0 || !long.TryParse(afterAddress[..end], NumberStyles.None, CultureInfo.InvariantCulture, out var sector))
          continue;
        var inode = TryParseParenthesizedInode(line);
        result.Add(new GpfsSectorOwnerOracle(new GpfsDiskAddress(diskId, sector), inode, null, null, line));
        continue;
      }

      var parts = line.Split(' ', 4, StringSplitOptions.RemoveEmptyEntries);
      if (parts.Length == 4
          && int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var inodeNumber)
          && long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var address)
          && int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var snapId))
        result.Add(new GpfsSectorOwnerOracle(new GpfsDiskAddress(diskId, address), inodeNumber, snapId, parts[3], "user-file"));
    }

    return result;
  }

  internal static IReadOnlyList<GpfsFileLocationOracle> ParseMmgetlocation(string text) {
    ArgumentNullException.ThrowIfNull(text);
    var result = new List<GpfsFileLocationOracle>();
    foreach (var raw in text.ReplaceLineEndings("\n").Split('\n')) {
      var line = raw.Trim();
      if (!line.StartsWith("mmgetlocation:fileDataInfor:", StringComparison.Ordinal))
        continue;

      var cells = line.Split(':');
      if (cells.Length < 9
          || !long.TryParse(cells[2], NumberStyles.None, CultureInfo.InvariantCulture, out var chunkIndex))
        continue;

      var offsetText = cells[3].TrimEnd(')');
      if (!long.TryParse(offsetText, NumberStyles.None, CultureInfo.InvariantCulture, out var offset))
        continue;

      var replicas = new List<GpfsLocationReplica>();
      for (var i = 4; i + 4 < cells.Length; i += 5) {
        var nsdName = cells[i];
        var server = cells[i + 1];
        if (nsdName.Length == 0 || !int.TryParse(cells[i + 2], NumberStyles.None, CultureInfo.InvariantCulture, out var diskId))
          continue;
        replicas.Add(new GpfsLocationReplica(nsdName, server, diskId, cells[i + 3]));
      }

      result.Add(new GpfsFileLocationOracle(chunkIndex, offset, replicas));
    }

    return result;
  }

  private static bool TryParseAllocationMismatch(string[] lines, ref int index, out GpfsAllocationMapMismatch mismatch) {
    mismatch = null!;
    var line = lines[index].Trim();
    if (!line.StartsWith("!Block ", StringComparison.Ordinal) || !line.Contains(" has map status:", StringComparison.Ordinal))
      return false;

    var addressText = line.AsSpan("!Block ".Length, line.IndexOf(" has map status:", StringComparison.Ordinal) - "!Block ".Length);
    if (!GpfsDiskAddress.TryParse(addressText, out var address))
      return false;

    var actual = ParseHexWords(line);
    var expected = Array.Empty<ulong>();
    if (line.Contains(" expected:", StringComparison.Ordinal)) {
      var expectedIndex = line.IndexOf(" expected:", StringComparison.Ordinal);
      actual = ParseHexWords(line[..expectedIndex]);
      expected = ParseHexWords(line[expectedIndex..]);
    } else {
      if (actual.Length == 0 && index + 1 < lines.Length)
        actual = ParseHexWords(lines[++index]);
      if (index + 1 < lines.Length && lines[index + 1].Trim().Equals("expected:", StringComparison.Ordinal)) {
        index += 2;
        expected = ParseHexWords(lines[index]);
      }
    }

    if (actual.Length == 0 || expected.Length == 0)
      return false;

    mismatch = new GpfsAllocationMapMismatch(address, actual, expected);
    return true;
  }

  private static ulong[] ParseHexWords(string line)
    => HexWord.Matches(line)
      .Select(static match => ulong.Parse(match.Groups["word"].Value, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture))
      .ToArray();

  private static bool TryReservedKind(string line, out GpfsReservedFileKind kind) {
    kind = line switch {
      "inode0 file" => GpfsReservedFileKind.InodeFile,
      "block alloc map file of system pool" => GpfsReservedFileKind.BlockAllocationMap,
      "inode alloc map file" => GpfsReservedFileKind.InodeAllocationMap,
      "access control list file" => GpfsReservedFileKind.AccessControlList,
      "extended attribute file" => GpfsReservedFileKind.ExtendedAttribute,
      "fileset metadata file" => GpfsReservedFileKind.FilesetMetadata,
      _ => GpfsReservedFileKind.Unknown,
    };
    return kind != GpfsReservedFileKind.Unknown;
  }

  private static long ParseSizeIfPresent(string line, string label, long existing) {
    var prefix = label + " is ";
    if (!line.StartsWith(prefix, StringComparison.Ordinal))
      return existing;
    var parts = line[prefix.Length..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
    if (parts.Length < 2 || !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var value))
      return existing;
    return parts[1] switch {
      "B" => value,
      "KB" => checked(value * 1024),
      "MB" => checked(value * 1024 * 1024),
      "GB" => checked(value * 1024 * 1024 * 1024),
      _ => existing,
    };
  }

  private static int ParseDecimalAfter(string line, string prefix) {
    var tail = line.AsSpan(prefix.Length);
    var end = tail.IndexOfAny(' ', '\t', '(');
    if (end >= 0)
      tail = tail[..end];
    return int.Parse(tail, NumberStyles.None, CultureInfo.InvariantCulture);
  }

  private static int ParseIntField(string line, string name)
    => checked((int)ParseLongWordField(line, name));

  private static long ParseLongWordField(string line, string name) {
    var marker = name + " ";
    var start = line.IndexOf(marker, StringComparison.Ordinal);
    if (start < 0)
      throw new InvalidDataException($"Missing field '{name}'.");
    var tail = line.AsSpan(start + marker.Length);
    var end = tail.IndexOf(' ');
    if (end >= 0)
      tail = tail[..end];
    return long.Parse(tail, NumberStyles.Integer, CultureInfo.InvariantCulture);
  }

  private static string ParseEqualsField(string line, string name) {
    var marker = name + "=";
    var start = line.IndexOf(marker, StringComparison.Ordinal);
    if (start < 0)
      throw new InvalidDataException($"Missing field '{name}'.");
    var tail = line.AsSpan(start + marker.Length);
    var end = tail.IndexOf(' ');
    return (end >= 0 ? tail[..end] : tail).ToString();
  }

  private static long ParseLongEqualsField(string line, string name) {
    var value = ParseEqualsField(line, name);
    return value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
      ? checked((long)ulong.Parse(value.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture))
      : long.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture);
  }

  private static ulong ParseUlongEqualsField(string line, string name) {
    var value = ParseEqualsField(line, name);
    return value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
      ? ulong.Parse(value.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture)
      : ulong.Parse(value, NumberStyles.None, CultureInfo.InvariantCulture);
  }

  private static int ParseInt(Match match, string group)
    => int.Parse(match.Groups[group].Value, NumberStyles.None, CultureInfo.InvariantCulture);

  private static GpfsDiskAddress ParseAddress(Match match)
    => new(
      int.Parse(match.Groups["disk"].Value, NumberStyles.None, CultureInfo.InvariantCulture),
      long.Parse(match.Groups["sector"].Value, NumberStyles.None, CultureInfo.InvariantCulture));

  private static int? TryParseParenthesizedInode(string line) {
    var marker = "(inode ";
    var start = line.IndexOf(marker, StringComparison.Ordinal);
    if (start < 0)
      return null;
    var tail = line.AsSpan(start + marker.Length);
    var end = tail.IndexOfAny(',', ')');
    if (end >= 0)
      tail = tail[..end];
    return int.TryParse(tail, NumberStyles.None, CultureInfo.InvariantCulture, out var inode) ? inode : null;
  }

  private sealed class MutableReservedFile(GpfsReservedFileKind kind) {
    internal GpfsReservedFileKind Kind { get; } = kind;
    internal Dictionary<string, string> Fields { get; } = new(StringComparer.Ordinal);
    internal bool? HoldsData { get; set; }

    internal bool TryBuild(out GpfsReservedFileOracle value) {
      value = null!;
      if (!TryDecimal("inodeNum", out var inodeNum) || !TryDecimal("subblocksPerFullBlock", out var subblocks) || !TryDecimal("recordsPerBlock", out var records))
        return false;

      value = new GpfsReservedFileOracle(
        Kind,
        inodeNum,
        subblocks,
        records,
        TryHex("inodeSpaceMask"),
        TryHex("inodeBlockMask"),
        TryHex("iallocSpaceMask"),
        TryHex("iallocSegmtMask"),
        TryNullableDecimal("disks"),
        TryNullableDecimal("regions"),
        TryNullableDecimal("segments"),
        HoldsData);
      return true;
    }

    private int? TryNullableDecimal(string name)
      => TryDecimal(name, out var value) ? value : null;

    private bool TryDecimal(string name, out int value)
      => Fields.TryGetValue(name, out var text)
         && int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);

    private uint? TryHex(string name) {
      if (!Fields.TryGetValue(name, out var text))
        return null;
      var span = text.AsSpan();
      if (span.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        span = span[2..];
      return uint.TryParse(span, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var value) ? value : null;
    }
  }
}

internal readonly record struct GpfsDiskAddress(int DiskId, long Sector) {
  internal static bool TryParse(ReadOnlySpan<char> text, out GpfsDiskAddress address) {
    address = default;
    var colon = text.IndexOf(':');
    if (colon <= 0 || colon == text.Length - 1)
      return false;
    if (!int.TryParse(text[..colon], NumberStyles.None, CultureInfo.InvariantCulture, out var diskId)
        || !long.TryParse(text[(colon + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var sector)
        || diskId < 0 || sector < 0)
      return false;
    address = new GpfsDiskAddress(diskId, sector);
    return true;
  }

  public override string ToString()
    => string.Create(CultureInfo.InvariantCulture, $"{this.DiskId}:{this.Sector}");
}

internal enum GpfsReservedFileKind {
  Unknown,
  InodeFile,
  BlockAllocationMap,
  InodeAllocationMap,
  AccessControlList,
  ExtendedAttribute,
  FilesetMetadata,
}

internal sealed record GpfsFileSystemGeometry(
  string FormatVersion,
  string AllocationMapLayout,
  string AllocationMapType,
  int SubblocksPerFullBlock,
  long SectorBytes,
  long MetadataSubblockBytes,
  long MetadataFullblockBytes,
  long DataSubblockBytes,
  long DataFullblockBytes,
  long InodeBytes,
  long IndirectBlockBytes,
  long EaOverflowBlockBytes,
  long DirectoryBlockBytes);

internal sealed record GpfsReservedFileOracle(
  GpfsReservedFileKind Kind,
  int InodeNumber,
  int SubblocksPerFullBlock,
  int RecordsPerBlock,
  uint? InodeSpaceMask,
  uint? InodeBlockMask,
  uint? IallocSpaceMask,
  uint? IallocSegmentMask,
  int? DiskCount,
  int? RegionCount,
  int? SegmentCount,
  bool? HoldsData);

internal sealed record GpfsAllocationMapMismatch(
  GpfsDiskAddress Address,
  IReadOnlyList<ulong> ActualWords,
  IReadOnlyList<ulong> ExpectedWords);

internal sealed record GpfsFsckOracle(
  GpfsFileSystemGeometry Geometry,
  IReadOnlyList<GpfsReservedFileOracle> ReservedFiles,
  IReadOnlyList<GpfsAllocationMapMismatch> AllocationMapMismatches);

internal sealed record GpfsInodeOracle(
  int InodeNumber,
  int SnapshotId,
  int IndexInBlock,
  int InodeBlock,
  IReadOnlyList<GpfsDiskAddress> PhysicalAddresses,
  int InodeSize,
  int AddressSlots,
  string IndirectionLevel,
  string Status,
  long ObjectVersion,
  ulong Generation,
  int LinkCount,
  int BlockSizeCode,
  int LastBlockSubblocks,
  uint Checksum,
  bool ChecksumValid,
  long FileSize,
  long FullBlocks,
  int CurrentMetadataReplicas,
  int MaxMetadataReplicas,
  int CurrentDataReplicas,
  int MaxDataReplicas,
  int DataPoolIndex);

internal sealed record GpfsSectorOwnerOracle(
  GpfsDiskAddress Address,
  int? InodeNumber,
  int? SnapshotId,
  string? Path,
  string Description);

internal sealed record GpfsLocationReplica(
  string NsdName,
  string Server,
  int DiskId,
  string FailureGroup);

internal sealed record GpfsFileLocationOracle(
  long ChunkIndex,
  long Offset,
  IReadOnlyList<GpfsLocationReplica> Replicas);
