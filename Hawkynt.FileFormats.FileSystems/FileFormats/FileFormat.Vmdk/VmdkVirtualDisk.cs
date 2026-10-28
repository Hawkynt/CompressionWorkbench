using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Compression.Core.Deflate;

namespace FileFormat.Vmdk;

/// <summary>
/// Read-only view of the guest disk a VMDK describes, whatever its layout: a hosted
/// sparse extent (monolithicSparse, the pieces of twoGbMaxExtentSparse), a
/// streamOptimized extent (compressed grains, metadata at the end), flat and zero
/// extents named by a text descriptor, and delta disks that defer unallocated grains
/// to their parent.
/// </summary>
/// <remarks>
/// <para>Implemented from VMware's <i>Virtual Disk Format 1.1</i> specification
/// (SparseExtentHeader, grain directory / grain tables, the descriptor file, and
/// stream-optimized compressed sparse extents with grain and metadata markers).</para>
/// <para>Files a descriptor names are resolved relative to the descriptor, so a
/// multi-file disk can only be opened from a <see cref="FileStream" />. Anything that
/// cannot be resolved — a missing extent, a missing or mismatched parent, an extent type
/// this reader does not implement — is refused with an exception rather than read as
/// zeros.</para>
/// </remarks>
public sealed class VmdkVirtualDisk : Stream {
  private const uint SparseMagic = 0x564D444B; // "KDMV"
  private const ulong GdAtEnd = 0xFFFF_FFFF_FFFF_FFFFUL;
  private const uint FlagZeroedGte = 1u << 2;
  private const uint FlagCompressed = 1u << 16;
  private const uint FlagMarkers = 1u << 17;
  private const ushort CompressionDeflate = 1;
  private const int MaxChainDepth = 16;

  private readonly List<Extent> _extents;
  private readonly List<Stream> _owned;
  private readonly long _length;
  private long _position;

  private VmdkVirtualDisk(List<Extent> extents, List<Stream> owned) {
    this._extents = extents;
    this._owned = owned;
    this._length = extents.Sum(e => e.Length);
  }

  /// <summary>
  /// Opens the guest disk of <paramref name="stream" />. Returns <c>null</c> when the
  /// stream is not a VMDK at all.
  /// </summary>
  /// <exception cref="NotSupportedException">The disk uses a feature this reader does not implement.</exception>
  /// <exception cref="InvalidDataException">The disk is damaged, or a file it depends on is missing or does not match.</exception>
  public static VmdkVirtualDisk? Open(Stream stream) {
    ArgumentNullException.ThrowIfNull(stream);
    var owned = new List<Stream>();
    try {
      var extents = OpenChain(stream, PathOf(stream), owned, 0);
      if (extents is null) {
        foreach (var s in owned) s.Dispose();
        return null;
      }
      return new VmdkVirtualDisk(extents, owned);
    } catch {
      foreach (var s in owned) s.Dispose();
      throw;
    }
  }

  /// <summary>As <see cref="Open" />, but returns <c>null</c> instead of throwing.</summary>
  public static VmdkVirtualDisk? TryOpen(Stream stream, out string? reason) {
    reason = null;
    try {
      return Open(stream);
    } catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or IOException or UnauthorizedAccessException) {
      reason = ex.Message;
      return null;
    }
  }

  private static string? PathOf(Stream stream) => stream is FileStream fs ? fs.Name : null;

  // ── Opening ───────────────────────────────────────────────────────

  private static List<Extent>? OpenChain(Stream stream, string? path, List<Stream> owned, int depth) {
    if (depth > MaxChainDepth)
      throw new InvalidDataException("VMDK: parent chain is too deep (or circular).");
    if (stream.Length < 4) return null;

    stream.Position = 0;
    Span<byte> magic = stackalloc byte[4];
    stream.ReadExactly(magic);
    stream.Position = 0;

    if (BinaryPrimitives.ReadUInt32LittleEndian(magic) == SparseMagic) {
      var sparse = SparseExtent.Open(stream, 0, (SubStream?)null);
      var descriptor = sparse.EmbeddedDescriptor is { } text ? Descriptor.Parse(text) : null;
      if (descriptor is not null && descriptor.HasParent) {
        var parent = OpenParent(descriptor, path, owned, depth);
        sparse = SparseExtent.Open(stream, 0, parent);
      }
      return [sparse];
    }

    // A text descriptor: small, ASCII, carrying the "# Disk DescriptorFile" banner or createType.
    if (stream.Length > 1 << 20) return null;
    var bytes = new byte[(int)stream.Length];
    stream.ReadExactly(bytes);
    stream.Position = 0;
    var desc = Encoding.UTF8.GetString(bytes);
    if (!desc.Contains("createType", StringComparison.Ordinal)) return null;
    var d = Descriptor.Parse(desc);
    if (d.Extents.Count == 0)
      throw new InvalidDataException("VMDK: descriptor names no extents.");

    var parentDisk = d.HasParent ? OpenParent(d, path, owned, depth) : null;
    var result = new List<Extent>();
    long start = 0;
    foreach (var line in d.Extents) {
      var length = checked(line.Sectors * 512);
      Extent extent;
      switch (line.Type) {
        case "ZERO":
          extent = new ZeroExtent(length);
          break;
        case "FLAT":
        case "VMFS":
        case "VMFSRAW":
        case "VMFSRDM": {
          var file = OpenSibling(path, line.FileName!, owned);
          extent = new FlatExtent(file, checked(line.Offset * 512), length);
          break;
        }
        case "SPARSE": {
          var file = OpenSibling(path, line.FileName!, owned);
          var sub = parentDisk is null ? null : new SubStream(parentDisk, start, length);
          extent = SparseExtent.Open(file, length, sub);
          break;
        }
        default:
          throw new NotSupportedException($"VMDK: extent type {line.Type} is not supported.");
      }
      result.Add(extent);
      start += length;
    }
    return result;
  }

  private static VmdkVirtualDisk OpenParent(Descriptor child, string? childPath, List<Stream> owned, int depth) {
    var hint = child.ParentFileNameHint
      ?? throw new InvalidDataException("VMDK: delta disk has no parentFileNameHint; its parent cannot be located.");
    var file = OpenSibling(childPath, hint, owned);
    var extents = OpenChain(file, ((FileStream)file).Name, owned, depth + 1)
      ?? throw new InvalidDataException($"VMDK: parent '{hint}' is not a VMDK.");
    var parentDescriptor = ReadDescriptorOf(file);
    if (parentDescriptor?.Cid is { } cid && child.ParentCid is { } expected && cid != expected)
      throw new InvalidDataException(
        $"VMDK: parent '{hint}' has CID {cid:x8}, the delta disk expects {expected:x8}; the parent changed after the delta was taken.");
    return new VmdkVirtualDisk(extents, []);
  }

  private static Descriptor? ReadDescriptorOf(Stream stream) {
    stream.Position = 0;
    Span<byte> magic = stackalloc byte[4];
    stream.ReadExactly(magic);
    if (BinaryPrimitives.ReadUInt32LittleEndian(magic) == SparseMagic)
      return SparseExtent.Open(stream, 0, (SubStream?)null).EmbeddedDescriptor is { } t ? Descriptor.Parse(t) : null;
    if (stream.Length > 1 << 20) return null;
    var bytes = new byte[(int)stream.Length];
    stream.Position = 0;
    stream.ReadExactly(bytes);
    return Descriptor.Parse(Encoding.UTF8.GetString(bytes));
  }

  private static Stream OpenSibling(string? descriptorPath, string fileName, List<Stream> owned) {
    if (descriptorPath is null)
      throw new NotSupportedException(
        $"VMDK: this disk needs the separate file '{fileName}'; open the descriptor from a file path so it can be found.");
    var candidate = Path.IsPathRooted(fileName)
      ? fileName
      : Path.Combine(Path.GetDirectoryName(Path.GetFullPath(descriptorPath)) ?? ".", fileName);
    if (!File.Exists(candidate))
      throw new InvalidDataException($"VMDK: referenced file '{fileName}' was not found next to the descriptor.");
    var stream = new FileStream(candidate, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
    owned.Add(stream);
    return stream;
  }

  // ── Stream ────────────────────────────────────────────────────────

  public override bool CanRead => true;
  public override bool CanSeek => true;
  public override bool CanWrite => false;
  public override long Length => this._length;

  public override long Position {
    get => this._position;
    set {
      ArgumentOutOfRangeException.ThrowIfNegative(value);
      this._position = value;
    }
  }

  public override int Read(byte[] buffer, int offset, int count) => this.Read(buffer.AsSpan(offset, count));

  public override int Read(Span<byte> buffer) {
    if (this._position >= this._length || buffer.IsEmpty) return 0;
    var total = (int)Math.Min(buffer.Length, this._length - this._position);
    var done = 0;
    long start = 0;
    foreach (var extent in this._extents) {
      if (done >= total) break;
      var end = start + extent.Length;
      var pos = this._position + done;
      if (pos < end) {
        var take = (int)Math.Min(total - done, end - pos);
        extent.ReadAt(pos - start, buffer.Slice(done, take));
        done += take;
      }
      start = end;
    }
    this._position += done;
    return done;
  }

  public override long Seek(long offset, SeekOrigin origin) {
    var next = origin switch {
      SeekOrigin.Begin => offset,
      SeekOrigin.Current => this._position + offset,
      SeekOrigin.End => this._length + offset,
      _ => throw new ArgumentOutOfRangeException(nameof(origin)),
    };
    if (next < 0) throw new IOException("Seek before beginning of stream.");
    return this._position = next;
  }

  public override void Flush() { }
  public override void SetLength(long value) => throw new NotSupportedException("A VMDK guest disk view is read-only.");
  public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException("A VMDK guest disk view is read-only.");

  protected override void Dispose(bool disposing) {
    if (disposing)
      foreach (var s in this._owned) s.Dispose();
    base.Dispose(disposing);
  }

  // ── Extents ───────────────────────────────────────────────────────

  private abstract class Extent {
    public abstract long Length { get; }
    public abstract void ReadAt(long offset, Span<byte> destination);
  }

  private sealed class ZeroExtent(long length) : Extent {
    public override long Length => length;
    public override void ReadAt(long offset, Span<byte> destination) => destination.Clear();
  }

  private sealed class FlatExtent(Stream file, long baseOffset, long length) : Extent {
    public override long Length => length;
    public override void ReadAt(long offset, Span<byte> destination) {
      var at = baseOffset + offset;
      if (at + destination.Length > file.Length)
        throw new InvalidDataException("VMDK: flat extent file is shorter than the descriptor says.");
      file.Position = at;
      file.ReadExactly(destination);
    }
  }

  /// <summary>Reads a window of another disk (the parent's bytes behind one child extent).</summary>
  private sealed class SubStream(VmdkVirtualDisk disk, long start, long length) {
    public long Length => length;
    public void ReadAt(long offset, Span<byte> destination) {
      destination.Clear();
      if (offset >= length) return;
      var take = (int)Math.Min(destination.Length, length - offset);
      disk.Position = start + offset;
      var done = 0;
      while (done < take) {
        var n = disk.Read(destination.Slice(done, take - done));
        if (n <= 0) break;
        done += n;
      }
    }
  }

  /// <summary>A hosted sparse extent (KDMV), optionally stream-optimized, optionally a delta over a parent.</summary>
  private sealed class SparseExtent : Extent {
    private readonly Stream _file;
    private readonly long _length;
    private readonly long _grainBytes;
    private readonly long[] _grains;  // byte offset of grain (or its marker); 0 = unallocated; -1 = zero grain
    private readonly bool _compressed;
    private readonly SubStream? _parent;
    private long _cachedGrain = -1;
    private byte[]? _cachedData;

    public string? EmbeddedDescriptor { get; }
    public override long Length => this._length;

    private SparseExtent(Stream file, long length, long grainBytes, long[] grains, bool compressed, SubStream? parent, string? descriptor) {
      this._file = file;
      this._length = length;
      this._grainBytes = grainBytes;
      this._grains = grains;
      this._compressed = compressed;
      this._parent = parent;
      this.EmbeddedDescriptor = descriptor;
    }

    public static SparseExtent Open(Stream file, long descriptorLength, VmdkVirtualDisk? parent)
      => Open(file, descriptorLength, parent is null ? null : new SubStream(parent, 0, parent.Length));

    public static SparseExtent Open(Stream file, long descriptorLength, SubStream? parent) {
      var header = ReadHeader(file, 0);
      var gdOffset = BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(56));
      var flags = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8));
      if (gdOffset == GdAtEnd) {
        // Stream-optimized: the authoritative header is the footer, one sector before the end-of-stream marker.
        if (file.Length < 1536)
          throw new InvalidDataException("VMDK: stream-optimized extent has no footer.");
        var footer = ReadHeader(file, file.Length - 1024);
        gdOffset = BinaryPrimitives.ReadUInt64LittleEndian(footer.AsSpan(56));
        if (gdOffset == GdAtEnd || gdOffset == 0)
          throw new InvalidDataException("VMDK: stream-optimized footer carries no grain directory.");
        header = footer;
        flags = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8));
      }

      // Flag bit 1 (redundant grain table in use): the redundant directory is the one
      // read, as VMware's and qemu's readers do; the two are written as copies.
      var rgdOffset = BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(48));
      if ((flags & 0x2u) != 0 && rgdOffset != 0 && rgdOffset != GdAtEnd)
        gdOffset = rgdOffset;

      var capacity = BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(12));
      var grainSectors = BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(20));
      var descriptorSector = BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(28));
      var descriptorSectors = BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(36));
      var gtes = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(44));
      var compressAlgorithm = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(77));
      var compressed = (flags & FlagCompressed) != 0;
      if (compressed && compressAlgorithm != CompressionDeflate)
        throw new NotSupportedException($"VMDK: grain compression algorithm {compressAlgorithm} is not supported.");
      if (grainSectors is 0 or > 1u << 20 || (grainSectors & (grainSectors - 1)) != 0)
        throw new InvalidDataException($"VMDK: invalid grain size of {grainSectors} sectors.");
      if (capacity > long.MaxValue / 512)
        throw new InvalidDataException("VMDK: capacity exceeds the supported range.");
      if (gtes == 0) gtes = 512;

      var grainBytes = (long)grainSectors * 512;
      var length = (long)capacity * 512;
      if (descriptorLength > 0 && descriptorLength != length)
        throw new InvalidDataException(
          $"VMDK: extent holds {length} bytes, the descriptor says {descriptorLength}.");
      var grainCount = checked((int)((length + grainBytes - 1) / grainBytes));
      var gdEntries = (grainCount + (int)gtes - 1) / (int)gtes;
      var zeroedGte = (flags & FlagZeroedGte) != 0 || (flags & FlagMarkers) != 0;

      var grains = new long[grainCount];
      if (grainCount > 0) {
        var gd = new byte[checked(gdEntries * 4)];
        ReadAt(file, checked((long)gdOffset * 512), gd, "grain directory");
        var gt = new byte[checked((int)gtes * 4)];
        for (var d = 0; d < gdEntries; d++) {
          var gtSector = BinaryPrimitives.ReadUInt32LittleEndian(gd.AsSpan(d * 4));
          if (gtSector == 0) continue;
          var count = Math.Min((int)gtes, grainCount - d * (int)gtes);
          ReadAt(file, (long)gtSector * 512, gt.AsSpan(0, count * 4), "grain table");
          for (var t = 0; t < count; t++) {
            var sector = BinaryPrimitives.ReadUInt32LittleEndian(gt.AsSpan(t * 4));
            grains[d * (int)gtes + t] = sector switch {
              0 => 0,
              1 when zeroedGte => -1,
              _ => (long)sector * 512,
            };
          }
        }
      }

      string? descriptor = null;
      if (descriptorSector != 0 && descriptorSectors is > 0 and <= 2048) {
        var bytes = new byte[(int)descriptorSectors * 512];
        ReadAt(file, checked((long)descriptorSector * 512), bytes, "embedded descriptor");
        var end = Array.IndexOf(bytes, (byte)0);
        descriptor = Encoding.UTF8.GetString(bytes, 0, end < 0 ? bytes.Length : end);
      }

      return new SparseExtent(file, length, grainBytes, grains, compressed, parent, descriptor);
    }

    private static byte[] ReadHeader(Stream file, long at) {
      var header = new byte[512];
      ReadAt(file, at, header, "sparse header");
      if (BinaryPrimitives.ReadUInt32LittleEndian(header) != SparseMagic)
        throw new InvalidDataException($"VMDK: no sparse extent header at offset {at}.");
      return header;
    }

    private static void ReadAt(Stream file, long at, Span<byte> destination, string what) {
      if (at < 0 || at + destination.Length > file.Length)
        throw new InvalidDataException($"VMDK: {what} lies beyond the end of the extent file.");
      file.Position = at;
      file.ReadExactly(destination);
    }

    public override void ReadAt(long offset, Span<byte> destination) {
      while (!destination.IsEmpty) {
        var grain = offset / this._grainBytes;
        var inGrain = (int)(offset % this._grainBytes);
        var take = (int)Math.Min(destination.Length, this._grainBytes - inGrain);
        var slice = destination[..take];
        var location = grain < this._grains.Length ? this._grains[grain] : 0;
        if (location == -1)
          slice.Clear();
        else if (location == 0) {
          if (this._parent is null) slice.Clear();
          else this._parent.ReadAt(offset, slice);
        } else if (this._compressed)
          this.Grain(grain, location).AsSpan(inGrain, take).CopyTo(slice);
        else
          ReadAt(this._file, location + inGrain, slice, "grain");
        destination = destination[take..];
        offset += take;
      }
    }

    /// <summary>Inflates a compressed grain: <c>u64 lba, u32 size</c>, then <c>size</c> bytes of deflate data.</summary>
    private byte[] Grain(long grain, long location) {
      if (this._cachedGrain == grain) return this._cachedData!;
      Span<byte> marker = stackalloc byte[12];
      ReadAt(this._file, location, marker, "grain marker");
      var lba = BinaryPrimitives.ReadUInt64LittleEndian(marker);
      var size = BinaryPrimitives.ReadUInt32LittleEndian(marker[8..]);
      if (lba != (ulong)(grain * (this._grainBytes / 512)))
        throw new InvalidDataException($"VMDK: grain marker at {location} names LBA {lba}, the grain table expects {grain * (this._grainBytes / 512)}.");
      if (size == 0 || size > this._grainBytes * 2 + 1024)
        throw new InvalidDataException($"VMDK: compressed grain of {size} bytes is implausible.");
      var data = new byte[size];
      ReadAt(this._file, location + 12, data, "compressed grain");
      // RFC 1950 wrapper (what VMware and qemu write); tolerate a bare RFC 1951 stream.
      var payload = data.Length >= 2 && (data[0] & 0x0F) == 8 && ((data[0] << 8) | data[1]) % 31 == 0
        ? data.AsSpan(2)
        : data.AsSpan();
      var inflated = DeflateDecompressor.Decompress(payload);
      var expected = (int)Math.Min(this._grainBytes, this._length - grain * this._grainBytes);
      if (inflated.Length < expected)
        throw new InvalidDataException($"VMDK: grain {grain} inflated to {inflated.Length} bytes, expected {expected}.");
      var full = new byte[this._grainBytes];
      inflated.AsSpan(0, Math.Min(inflated.Length, full.Length)).CopyTo(full);
      this._cachedGrain = grain;
      this._cachedData = full;
      return full;
    }
  }

  // ── Descriptor ────────────────────────────────────────────────────

  private sealed record ExtentLine(string Access, long Sectors, string Type, string? FileName, long Offset);

  private sealed class Descriptor {
    public List<ExtentLine> Extents { get; } = [];
    public uint? Cid { get; private set; }
    public uint? ParentCid { get; private set; }
    public string? ParentFileNameHint { get; private set; }
    public bool HasParent => this.ParentCid is { } p && p != 0xFFFF_FFFF;

    public static Descriptor Parse(string text) {
      var d = new Descriptor();
      foreach (var raw in text.Split('\n')) {
        var line = raw.Trim().TrimEnd('\r');
        if (line.Length == 0 || line[0] == '#') continue;
        if (line.StartsWith("RW ", StringComparison.Ordinal) || line.StartsWith("RDONLY ", StringComparison.Ordinal)
            || line.StartsWith("NOACCESS ", StringComparison.Ordinal)) {
          d.Extents.Add(ParseExtent(line));
          continue;
        }
        var eq = line.IndexOf('=');
        if (eq <= 0) continue;
        var key = line[..eq].Trim();
        var value = line[(eq + 1)..].Trim().Trim('"');
        switch (key) {
          case "CID" when uint.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var cid):
            d.Cid = cid;
            break;
          case "parentCID" when uint.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var pcid):
            d.ParentCid = pcid;
            break;
          case "parentFileNameHint":
            d.ParentFileNameHint = value;
            break;
        }
      }
      return d;
    }

    private static ExtentLine ParseExtent(string line) {
      // ACCESS SIZE TYPE ["FILENAME" [OFFSET]]
      var q1 = line.IndexOf('"');
      var head = (q1 < 0 ? line : line[..q1]).Split(' ', StringSplitOptions.RemoveEmptyEntries);
      if (head.Length < 3 || !long.TryParse(head[1], NumberStyles.None, CultureInfo.InvariantCulture, out var sectors))
        throw new InvalidDataException($"VMDK: malformed extent line '{line}'.");
      string? file = null;
      long offset = 0;
      if (q1 >= 0) {
        var q2 = line.IndexOf('"', q1 + 1);
        if (q2 < 0) throw new InvalidDataException($"VMDK: malformed extent line '{line}'.");
        file = line[(q1 + 1)..q2];
        var tail = line[(q2 + 1)..].Trim();
        if (tail.Length > 0 && !long.TryParse(tail.Split(' ')[0], NumberStyles.None, CultureInfo.InvariantCulture, out offset))
          throw new InvalidDataException($"VMDK: malformed extent offset in '{line}'.");
      }
      var type = head[2].ToUpperInvariant();
      if (type != "ZERO" && file is null)
        throw new InvalidDataException($"VMDK: extent line '{line}' names no file.");
      return new ExtentLine(head[0], sectors, type, file, offset);
    }
  }
}
