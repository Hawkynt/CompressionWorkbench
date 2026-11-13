#pragma warning disable CS1591
using Compression.Core.DiskImage;
using System.Text;

namespace FileSystem.Iso;

/// <summary>
/// Reads ISO 9660 (ECMA-119) disc images with optional Joliet and Rock Ridge support.
/// </summary>
public sealed class IsoReader : IDisposable {
  private const int SectorSize = 2048;
  private readonly ImageAccessor _data;
  private readonly List<IsoEntry> _entries = [];
  private readonly bool _preferJoliet;
  private readonly HashSet<long> _visitedDirectories = [];
  private bool _joliet;

  /// <summary>All entries found in the image.</summary>
  public IReadOnlyList<IsoEntry> Entries => _entries;

  /// <summary>
  /// Opens an ISO 9660 image from the given stream. When <paramref name="useJoliet"/>
  /// is <see langword="true"/> (the default) and the image carries a Joliet
  /// Supplementary Volume Descriptor, the long UCS-2 names from the Joliet
  /// directory tree are returned; otherwise the primary ECMA-119 tree is read.
  /// </summary>
  public IsoReader(Stream stream, bool leaveOpen = false, bool useJoliet = true) {
    ArgumentNullException.ThrowIfNull(stream);
    _preferJoliet = useJoliet;
    if (stream.CanSeek) stream.Position = 0;
    _data = new ImageAccessor(stream, leaveOpen);
    Parse();
  }

  private void Parse() {
    if (_data.Length < 17L * SectorSize)
      throw new InvalidDataException("ISO9660: image too small.");

    long pvdOffset = -1;
    long jolietOffset = -1;
    for (var sector = 16; sector < 256; ++sector) {
      var off = (long)sector * SectorSize;
      if (off + SectorSize > _data.Length) break;

      var type = _data.ReadByte(off);
      if (type == 0xFF) break;
      if (!IsCD001(off)) continue;

      if (type == 1 && pvdOffset < 0) {
        pvdOffset = off;
      } else if (type == 2 && jolietOffset < 0 && _preferJoliet) {
        var esc = _data.Read(off + 88, 3);
        if (esc is [0x25, 0x2F, 0x40 or 0x43 or 0x45])
          jolietOffset = off;
      }
    }

    if (pvdOffset < 0)
      throw new InvalidDataException("ISO9660: no Primary Volume Descriptor found.");

    var descOff = pvdOffset;
    // Rock Ridge carries full POSIX names, modes and times; like Linux, prefer it over
    // Joliet, whose names stop at 64 characters.
    if (jolietOffset >= 0 && HasRockRidge(pvdOffset)) jolietOffset = -1;
    if (jolietOffset >= 0) {
      _joliet = true;
      descOff = jolietOffset;
    }

    // zisofs data is compressed whichever tree names the file, but only the Rock
    // Ridge records of the primary tree say so: map those by extent first.
    if (_joliet)
      CollectPrimaryZisofs(pvdOffset);

    var rootRec = descOff + 156;
    var rootExtent = _data.ReadUInt32(rootRec + 2);
    var rootLength = (long)_data.ReadUInt32(rootRec + 10);
    var rootExtendedAttributeBlocks = _data.ReadByte(rootRec + 1);
    var rootOffset = DataOffset(rootExtent, rootExtendedAttributeBlocks);
    ReadDirectory(rootOffset, rootLength, "");

    // The folder relocated directories were moved into (rr_moved, as mkisofs names it, or
    // .rr_moved) is bookkeeping once they are listed at their real places: like the
    // relocated directories themselves it is not listed, as long as it holds nothing else.
    if (_sawRelocation)
      _entries.RemoveAll(e => e.IsDirectory && e.Name is "rr_moved" or ".rr_moved" or "RR_MOVED"
        && !_entries.Any(x => x.Name.StartsWith(e.Name + "/", StringComparison.Ordinal)));
  }

  private bool _sawRelocation;

  private readonly Dictionary<long, ZisofsInfo> _zisofsByExtent = [];

  private void CollectPrimaryZisofs(long pvdOffset) {
    var rootRec = pvdOffset + 156;
    var visited = new HashSet<long>();
    Walk(DataOffset(_data.ReadUInt32(rootRec + 2), _data.ReadByte(rootRec + 1)), _data.ReadUInt32(rootRec + 10), 0);

    void Walk(long offset, long length, int depth) {
      if (depth > 64 || offset < 0 || length < 0 || offset + length > _data.Length || !visited.Add(offset)) return;
      var end = offset + length;
      var pos = offset;
      while (pos < end) {
        var recLen = _data.ReadByte(pos);
        if (recLen == 0) { pos = ((pos / SectorSize) + 1) * SectorSize; continue; }
        if (recLen < 34 || pos > end - recLen) return;
        var nameLen = _data.ReadByte(pos + 32);
        var special = nameLen == 1 && _data.ReadByte(pos + 33) is 0 or 1;
        var extent = DataOffset(_data.ReadUInt32(pos + 2), _data.ReadByte(pos + 1));
        if (!special) {
          if ((_data.ReadByte(pos + 25) & 0x02) != 0) {
            Walk(extent, _data.ReadUInt32(pos + 10), depth + 1);
          } else if (RockRidgeAt(pos, recLen, nameLen)?.Zisofs is { } zf) {
            _zisofsByExtent[extent] = zf;
          }
        }
        pos += recLen;
      }
    }
  }

  /// <summary>
  /// Rock Ridge is in use when the first record of the primary root directory starts its
  /// System Use area with the SUSP "SP" entry (IEEE P1281 5.3: check bytes BE EF).
  /// </summary>
  private bool HasRockRidge(long pvdOffset) {
    var rootRec = pvdOffset + 156;
    var root = DataOffset(_data.ReadUInt32(rootRec + 2), _data.ReadByte(rootRec + 1));
    if (root < 0 || root + 40 > _data.Length) return false;
    var recLen = _data.ReadByte(root);
    var nameLen = _data.ReadByte(root + 32);
    var su = root + 33 + nameLen + ((nameLen & 1) == 0 ? 1 : 0);
    return recLen >= 40 && su + 7 <= root + recLen
      && _data.ReadByte(su) == 'S' && _data.ReadByte(su + 1) == 'P'
      && _data.ReadByte(su + 4) == 0xBE && _data.ReadByte(su + 5) == 0xEF;
  }

  private RockRidgeInfo? RockRidgeAt(long pos, int recLen, int nameLen) {
    var suOffset = 33 + nameLen;
    if ((nameLen & 1) == 0) ++suOffset;
    return suOffset < recLen ? IsoRockRidge.Parse(_data, pos + suOffset, pos + recLen, SectorSize) : null;
  }

  private bool IsCD001(long vdOffset) =>
    _data.Length > vdOffset + 5 &&
    _data.ReadByte(vdOffset + 1) == 'C' && _data.ReadByte(vdOffset + 2) == 'D' &&
    _data.ReadByte(vdOffset + 3) == '0' && _data.ReadByte(vdOffset + 4) == '0' && _data.ReadByte(vdOffset + 5) == '1';

  private void ReadDirectory(long offset, long length, string basePath) {
    if (offset < 0 || length < 0 || offset > _data.Length || length > _data.Length - offset)
      throw new InvalidDataException($"ISO9660: directory '{basePath}' extent lies outside the image.");
    if (!_visitedDirectories.Add(offset))
      throw new InvalidDataException($"ISO9660: directory '{basePath}' reuses an already visited extent.");

    var end = checked(offset + length);
    var pos = offset;
    PendingFile? pending = null;

    while (pos < end) {
      var recLen = _data.ReadByte(pos);
      if (recLen == 0) {
        pos = ((pos / SectorSize) + 1) * SectorSize;
        continue;
      }
      if (recLen < 34 || pos > end - recLen)
        throw new InvalidDataException($"ISO9660: malformed directory record in '{basePath}'.");

      var extendedAttributeBlocks = _data.ReadByte(pos + 1);
      var extentLba = _data.ReadUInt32(pos + 2);
      var dataLength = (long)_data.ReadUInt32(pos + 10);
      var flags = _data.ReadByte(pos + 25);
      var fileUnitSize = _data.ReadByte(pos + 26);
      var interleaveGapSize = _data.ReadByte(pos + 27);
      var nameLen = _data.ReadByte(pos + 32);
      var isDir = (flags & 0x02) != 0;
      var hasMoreExtents = (flags & 0x80) != 0;

      if (33 + nameLen > recLen)
        throw new InvalidDataException($"ISO9660: directory record in '{basePath}' has a truncated file identifier.");

      var isSpecial = nameLen == 1 && (_data.ReadByte(pos + 33) is 0 or 1);
      var rr = _joliet || isSpecial ? null : RockRidgeAt(pos, recLen, nameLen);
      var name = DecodeName(pos, nameLen, rr);
      // RE: a directory moved here (rr_moved) from deeper than ECMA-119 allows; it is
      // listed at its real place, where a CL entry points to it.
      if (rr is { Relocated: true } && isDir && pending is null) {
        _sawRelocation = true;
        pos += recLen;
        continue;
      }
      if (rr?.ChildLink is { } childLink && !isDir && pending is null) {
        isDir = true;
        extentLba = childLink;
        extendedAttributeBlocks = 0;
        var target = DataOffset(childLink, 0);
        if (target < 0 || target + 34 > _data.Length)
          throw new InvalidDataException($"ISO9660: Rock Ridge child link of '{name}' points outside the image.");
        dataLength = _data.ReadUInt32(target + 10); // the relocated directory's own "." record
        hasMoreExtents = false;
        fileUnitSize = 0;
        interleaveGapSize = 0;
      }
      if (!isSpecial) {
        var semi = name.IndexOf(';');
        if (semi >= 0) name = name[..semi];
        name = name.TrimEnd('.');
      }

      if (isSpecial) {
        if (pending is not null)
          throw new InvalidDataException($"ISO9660: multi-extent file '{pending.Name}' is interrupted by a special directory record.");
        pos += recLen;
        continue;
      }
      if (string.IsNullOrEmpty(name)) {
        if (pending is not null)
          throw new InvalidDataException($"ISO9660: multi-extent file '{pending.Name}' is interrupted by an empty identifier.");
        pos += recLen;
        continue;
      }

      var fullPath = string.IsNullOrEmpty(basePath) ? name : $"{basePath}/{name}";
      var physicalOffset = DataOffset(extentLba, extendedAttributeBlocks);
      var lastModified = rr?.Modified ?? DecodeTimestamp(pos, end);
      var mode = rr?.Mode is { } posixMode ? (int)posixMode : (int?)null;

      if (isDir) {
        if (pending is not null)
          throw new InvalidDataException($"ISO9660: multi-extent file '{pending.Name}' is interrupted by directory '{fullPath}'.");
        if (hasMoreExtents)
          throw new InvalidDataException($"ISO9660: directory '{fullPath}' illegally advertises the Multi-Extent flag.");
        if (fileUnitSize != 0 || interleaveGapSize != 0)
          throw new NotSupportedException($"ISO9660: interleaved directory '{fullPath}' is not supported.");

        _entries.Add(new IsoEntry {
          Name = fullPath,
          IsDirectory = true,
          LastModified = lastModified,
          UnixMode = mode,
          DataOffset = physicalOffset,
        });
        ReadDirectory(physicalOffset, dataLength, fullPath);
        pos += recLen;
        continue;
      }

      string? limitation = null;
      var interleaved = fileUnitSize != 0;
      if (fileUnitSize == 0 && interleaveGapSize != 0)
        limitation = $"ISO9660 file section has an interleave gap of {interleaveGapSize} without a file unit size.";
      else if (interleaved && extendedAttributeBlocks != 0)
        limitation = "ISO9660 interleaved file section with an extended attribute record is not supported.";

      // SL: a symbolic link has no data of its own; its content is the target path.
      if (pending is null && rr?.SymlinkTarget is { } linkTarget) {
        _entries.Add(new IsoEntry {
          Name = fullPath,
          Size = Encoding.UTF8.GetByteCount(linkTarget),
          IsSymlink = true,
          LinkTarget = linkTarget,
          LastModified = lastModified,
          UnixMode = mode,
          DataOffset = physicalOffset,
        });
        pos += recLen;
        continue;
      }

      var zisofs = rr?.Zisofs ?? (_joliet && _zisofsByExtent.TryGetValue(physicalOffset, out var mapped) ? mapped : null);
      if (rr?.UnsupportedCompression is { } algorithm)
        limitation ??= $"Rock Ridge ZF compression '{algorithm}' is not supported.";
      if (zisofs is not null && (hasMoreExtents || pending is not null))
        limitation ??= "zisofs compression of a multi-extent file is not supported.";
      if (zisofs is not null && interleaved)
        limitation ??= "zisofs compression of an interleaved file is not supported.";

      if (pending is null) {
        var segments = new List<IsoDataSegment>();
        AddSection(segments, 0, physicalOffset, dataLength, interleaved ? fileUnitSize : 0, interleaveGapSize);
        if (hasMoreExtents) {
          pending = new PendingFile(fullPath, lastModified, segments, dataLength, limitation) { Mode = mode, Interleaved = interleaved };
        } else if (zisofs is { } zf && limitation is null) {
          _entries.Add(new IsoEntry {
            Name = fullPath,
            Size = zf.UncompressedSize,
            StoredSize = dataLength,
            Zisofs = zf,
            LastModified = lastModified,
            UnixMode = mode,
            DataOffset = physicalOffset,
            DataSegments = segments,
          });
        } else {
          AddFile(fullPath, lastModified, segments, dataLength, limitation, mode, interleaved);
        }
      } else {
        if (!string.Equals(pending.Name, fullPath, StringComparison.Ordinal))
          throw new InvalidDataException(
            $"ISO9660: multi-extent file '{pending.Name}' is followed by unrelated record '{fullPath}'.");
        AddSection(pending.Segments, pending.Length, physicalOffset, dataLength, interleaved ? fileUnitSize : 0, interleaveGapSize);
        pending.Length = checked(pending.Length + dataLength);
        pending.Limitation ??= limitation;
        pending.Interleaved |= interleaved;
        if (!hasMoreExtents) {
          AddFile(pending.Name, pending.LastModified, pending.Segments, pending.Length, pending.Limitation, pending.Mode, pending.Interleaved);
          pending = null;
        }
      }

      pos += recLen;
    }

    if (pending is not null)
      throw new InvalidDataException($"ISO9660: multi-extent file '{pending.Name}' is missing its final directory record.");
  }

  private void AddFile(
      string name,
      DateTime? lastModified,
      IReadOnlyList<IsoDataSegment> segments,
      long length,
      string? limitation,
      int? mode,
      bool interleaved = false) {
    _entries.Add(new IsoEntry {
      Name = name,
      Size = length,
      IsDirectory = false,
      LastModified = lastModified,
      UnixMode = mode,
      DataOffset = segments.Count == 1 ? segments[0].PhysicalOffset : 0,
      DataSegments = segments,
      MountLimitation = limitation,
      IsInterleaved = interleaved,
    });
  }

  /// <summary>
  /// Adds one file section's data. An interleaved section (ECMA-119 6.4.3) is recorded in file
  /// units of <paramref name="unitBlocks"/> logical blocks, each followed by
  /// <paramref name="gapBlocks"/> blocks that belong to something else; a plain section is one run.
  /// </summary>
  internal static void AddSection(List<IsoDataSegment> segments, long logicalOffset, long physicalOffset, long length, int unitBlocks, int gapBlocks) {
    if (unitBlocks == 0) {
      segments.Add(new IsoDataSegment(logicalOffset, physicalOffset, length));
      return;
    }
    var unitBytes = (long)unitBlocks * SectorSize;
    var stride = (long)(unitBlocks + gapBlocks) * SectorSize;
    for (long done = 0, at = physicalOffset; done < length; done += unitBytes, at += stride)
      segments.Add(new IsoDataSegment(logicalOffset + done, at, Math.Min(unitBytes, length - done)));
  }

  private string DecodeName(long pos, int nameLen, RockRidgeInfo? rr) {
    if (_joliet)
      return Encoding.BigEndianUnicode.GetString(_data.Read(pos + 33, nameLen));
    return rr?.Name ?? Encoding.ASCII.GetString(_data.Read(pos + 33, nameLen));
  }

  private DateTime? DecodeTimestamp(long pos, long end) {
    if (pos + 24 >= end) return null;
    try {
      var y = _data.ReadByte(pos + 18) + 1900;
      var m = _data.ReadByte(pos + 19);
      var d = _data.ReadByte(pos + 20);
      var h = _data.ReadByte(pos + 21);
      var mi = _data.ReadByte(pos + 22);
      var s = _data.ReadByte(pos + 23);
      return new DateTime(y, m, d, h, mi, s, DateTimeKind.Utc);
    } catch (ArgumentOutOfRangeException) {
      return null;
    }
  }

  private static long DataOffset(uint extentLba, byte extendedAttributeBlocks)
    => checked(((long)extentLba + extendedAttributeBlocks) * SectorSize);

  /// <summary>
  /// Copies an entry's logical bytes into <paramref name="destination"/>. Multi-extent
  /// file sections are concatenated in directory-record order as required by ECMA-119.
  /// </summary>
  public void ExtractTo(IsoEntry entry, Stream destination) {
    ArgumentNullException.ThrowIfNull(entry);
    ArgumentNullException.ThrowIfNull(destination);
    if (entry.IsDirectory) return;
    if (entry.IsSymlink) {
      var target = Encoding.UTF8.GetBytes(entry.LinkTarget ?? "");
      destination.Write(target, 0, target.Length);
      return;
    }
    if (entry.MountLimitation is { } limitation)
      throw new NotSupportedException($"ISO9660: '{entry.Name}' cannot be decoded safely: {limitation}");
    if (entry.Zisofs is { } zf) {
      var segment = Segments(entry)[0];
      if (entry.StoredSize > Array.MaxLength || segment.PhysicalOffset < 0 || segment.PhysicalOffset > _data.Length - entry.StoredSize)
        throw new InvalidDataException($"ISO9660: zisofs file '{entry.Name}' lies outside the image.");
      var decoded = IsoRockRidge.DecodeZisofs(_data.Read(segment.PhysicalOffset, (int)entry.StoredSize), zf, entry.Name);
      destination.Write(decoded, 0, decoded.Length);
      return;
    }

    long logical = 0;
    foreach (var segment in Segments(entry)) {
      if (segment.LogicalOffset != logical)
        throw new InvalidDataException($"ISO9660: '{entry.Name}' has a discontinuous file-section map.");
      if (segment.PhysicalOffset < 0 || segment.PhysicalOffset > _data.Length - segment.Length)
        throw new InvalidDataException($"ISO9660: '{entry.Name}' has a file section outside the image.");
      _data.CopyTo(segment.PhysicalOffset, destination, segment.Length);
      logical = checked(logical + segment.Length);
    }
    if (logical != entry.Size)
      throw new InvalidDataException($"ISO9660: '{entry.Name}' file sections produce {logical} of {entry.Size} bytes.");
  }

  /// <summary>Extracts the logical data for the given entry.</summary>
  public byte[] Extract(IsoEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    if (entry.IsDirectory || entry.Size <= 0) return [];
    if (entry.Size > Array.MaxLength)
      throw new IOException($"ISO9660: '{entry.Name}' is {entry.Size:N0} bytes, past the array limit; use ExtractTo.");
    using var output = new MemoryStream(checked((int)entry.Size));
    ExtractTo(entry, output);
    return output.ToArray();
  }

  internal static IReadOnlyList<IsoDataSegment> Segments(IsoEntry entry)
    => entry.DataSegments.Count != 0
      ? entry.DataSegments
      : entry.Size <= 0 || entry.IsSymlink
        ? []
        : [new(0, entry.DataOffset, entry.Size)];

  /// <inheritdoc/>
  public void Dispose() => _data.Dispose();

  private sealed class PendingFile(
    string name,
    DateTime? lastModified,
    List<IsoDataSegment> segments,
    long length,
    string? limitation
  ) {
    public string Name { get; } = name;
    public DateTime? LastModified { get; } = lastModified;
    public List<IsoDataSegment> Segments { get; } = segments;
    public long Length { get; set; } = length;
    public bool Interleaved { get; set; }
    public string? Limitation { get; set; } = limitation;
    public int? Mode { get; init; }
  }
}
