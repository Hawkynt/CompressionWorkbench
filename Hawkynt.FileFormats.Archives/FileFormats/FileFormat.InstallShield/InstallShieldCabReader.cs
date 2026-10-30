using System.Buffers.Binary;
using System.Text;
using Compression.Core.Checksums;
using Compression.Core.Deflate;

namespace FileFormat.InstallShield;

/// <summary>A file of an InstallShield cabinet set.</summary>
/// <param name="FileName">The path as unshield writes it: file group, directory, name.</param>
/// <param name="Index">The file's index in the cabinet's file table.</param>
/// <param name="Size">The expanded size.</param>
/// <param name="CompressedSize">The stored size.</param>
public sealed record InstallShieldCabEntry(string FileName, int Index, long Size, long CompressedSize);

/// <summary>
/// Reads InstallShield cabinet sets (data1.hdr + data1.cab, data2.cab, ...; "ISc(" signature),
/// InstallShield 5 to 2012+.
/// </summary>
/// <remarks>
/// Converted from unshield (https://github.com/twogood/unshield, MIT licence), lib/*.c: the
/// common and volume headers, the cabinet descriptor, file table, file groups, file descriptors
/// for both header generations, split files across volumes, the byte obfuscation, and both
/// chunked Deflate layouts ("new": 16-bit length per chunk; "old": chunks ending in 00 00 FF FF).
/// File naming follows unshield's <c>x</c> command.
/// </remarks>
public sealed class InstallShieldCabReader {

  private const uint CabSignature = 0x28635349; // "ISc("
  private const int MaxFileGroupCount = 71;
  private const int MaxComponentCount = 71;
  private const ushort FileSplit = 1, FileObfuscated = 2, FileCompressed = 4, FileInvalid = 8;
  private const byte LinkPrev = 1;

  private readonly byte[] _header;
  private readonly Func<int, Stream?> _openVolume;
  private readonly Func<string, Stream?> _openExternal;
  private readonly int _headerIndex;
  private readonly uint _descriptorOffset;
  private readonly uint _fileTableOffset, _fileTableOffset2, _directoryCount, _fileCount;
  private readonly uint[] _fileTable;
  private readonly Dictionary<int, FileDescriptor> _descriptors = [];
  private readonly List<InstallShieldCabEntry> _entries = [];

  /// <summary>Gets the major InstallShield version the header was written by (0 for 5.x-era v0).</summary>
  public int MajorVersion { get; }

  /// <summary>Gets the file groups, in cabinet order.</summary>
  public IReadOnlyList<string> FileGroups { get; }

  /// <summary>Gets the components.</summary>
  public IReadOnlyList<string> Components { get; }

  /// <summary>Gets the files, grouped and named as unshield extracts them.</summary>
  public IReadOnlyList<InstallShieldCabEntry> Entries => this._entries;

  /// <summary>
  /// Opens a cabinet set from its header (data1.hdr, or data1.cab when it carries the header).
  /// </summary>
  /// <param name="header">The header file.</param>
  /// <param name="headerIndex">The volume number of the header (1 for data1.hdr).</param>
  /// <param name="openVolume">Opens dataN.cab for a volume number, or returns null when missing.</param>
  /// <param name="openExternal">
  /// Opens a file kept beside the cabinets (directory, backslash, name), for entries whose data the
  /// cabinet does not hold; null when there are no siblings to look at.
  /// </param>
  public InstallShieldCabReader(Stream header, int headerIndex, Func<int, Stream?> openVolume, Func<string, Stream?>? openExternal = null) {
    this._openExternal = openExternal ?? (_ => null);
    ArgumentNullException.ThrowIfNull(header);
    ArgumentNullException.ThrowIfNull(openVolume);
    this._openVolume = openVolume;
    this._headerIndex = headerIndex;
    using (var ms = new MemoryStream()) {
      header.Position = 0;
      header.CopyTo(ms);
      this._header = ms.ToArray();
    }
    var h = this._header;
    if (h.Length < 20 || BinaryPrimitives.ReadUInt32LittleEndian(h) != CabSignature)
      throw new InvalidDataException("Not an InstallShield cabinet (no \"ISc(\" signature).");

    var version = BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(4));
    this._descriptorOffset = BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(12));
    var descriptorSize = BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(16));
    this.MajorVersion = (version >> 24) switch {
      1 => (int)((version >> 12) & 0xF),
      2 or 4 => (int)(version & 0xFFFF) / 100,
      _ => 0,
    };
    if (descriptorSize == 0)
      throw new InvalidDataException("This cabinet carries no header; open its .hdr file.");

    var d = (int)this._descriptorOffset;
    this.Need(d, 0x30 + 0xE + 4 * (MaxFileGroupCount + MaxComponentCount));
    this._fileTableOffset = U32(h, d + 0x0C);
    this._directoryCount = U32(h, d + 0x1C);
    this._fileCount = U32(h, d + 0x28);
    this._fileTableOffset2 = U32(h, d + 0x2C);

    var start = (long)this._descriptorOffset + this._fileTableOffset;
    var count = (long)this._directoryCount + this._fileCount;
    if (count == 0 || start + count * 4 > h.Length)
      throw new InvalidDataException("InstallShield file table lies outside the header.");
    this._fileTable = new uint[count];
    for (var i = 0; i < count; ++i)
      this._fileTable[i] = U32(h, (int)(start + 4 * i));

    var groupOffsets = new uint[MaxFileGroupCount];
    var componentOffsets = new uint[MaxComponentCount];
    for (var i = 0; i < MaxFileGroupCount; ++i) groupOffsets[i] = U32(h, d + 0x3E + 4 * i);
    for (var i = 0; i < MaxComponentCount; ++i) componentOffsets[i] = U32(h, d + 0x3E + 4 * (MaxFileGroupCount + i));

    var groups = new List<(string Name, int First, int Last)>();
    foreach (var descriptor in this.WalkLists(groupOffsets)) {
      var p = this.Buffer(descriptor);
      var name = this.HeaderString(U32(h, p));
      p += 4 + (this.MajorVersion <= 5 ? 0x48 : 0x12);
      groups.Add((name, (int)U32(h, p), (int)U32(h, p + 4)));
    }
    this.FileGroups = groups.Select(g => g.Name).ToList();
    this.Components = this.WalkLists(componentOffsets).Select(o => this.HeaderString(U32(h, this.Buffer(o)))).ToList();

    foreach (var (name, first, last) in groups) {
      if (first == -1)
        continue;
      for (var i = first; i <= last && i < this._fileCount; ++i) {
        if (i < 0 || !this.IsValid(i))
          continue;
        var fd = this.Descriptor(i);
        var dir = fd.DirectoryIndex < this._directoryCount ? this.DirectoryName(fd.DirectoryIndex) : "";
        this._entries.Add(new InstallShieldCabEntry(BuildPath(name, dir, this.FileName(fd)), i, (long)fd.ExpandedSize, (long)fd.CompressedSize));
      }
    }
  }

  private IEnumerable<uint> WalkLists(uint[] heads) {
    var seen = new HashSet<uint>();
    foreach (var head in heads) {
      var next = head;
      while (next != 0 && seen.Add(next)) {
        var p = this.Buffer(next);
        this.Need(p, 12);
        yield return U32(this._header, p + 4);
        next = U32(this._header, p + 8);
      }
    }
  }

  // ── naming (unshield src/unshield.c extract_file) ───────────────────

  private static string BuildPath(string group, string directory, string file) {
    static string Clean(string s, bool dirPart) {
      var sb = new StringBuilder(s.Length);
      foreach (var c in s)
        sb.Append(c switch {
          '\\' when dirPart => '/',
          ' ' or '<' or '>' or '[' or ']' when dirPart => '_',
          < ' ' or (char)127 => '_',
          _ => c,
        });
      return sb.ToString();
    }
    var parts = new List<string>();
    if (group.Length > 0) parts.Add(Clean(group, true));
    if (directory.Length > 0) parts.Add(Clean(directory, true));
    parts.Add(Clean(file, false));
    // "." and ".." segments are dropped: a cabinet must not place files outside its own tree
    // (unshield refuses such names as a directory traversal, CVE-2015-1386).
    return string.Join('/', string.Join('/', parts).Split('/', StringSplitOptions.RemoveEmptyEntries).Where(s => s is not ("." or "..")));
  }

  // ── header access ────────────────────────────────────────────────────

  private static uint U32(byte[] b, int at) => BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(at));

  private void Need(long at, long length) {
    if (at < 0 || at + length > this._header.Length)
      throw new InvalidDataException("InstallShield header structure lies outside the header.");
  }

  private int Buffer(uint offset) {
    var at = (long)this._descriptorOffset + offset;
    this.Need(at, 4);
    return (int)at;
  }

  private string HeaderString(uint offset) => this.StringAt(this.Buffer(offset));

  private string StringAt(long at) {
    var h = this._header;
    if (at < 0 || at >= h.Length)
      return "";
    if (this.MajorVersion >= 17) {
      var end = (int)at;
      while (end + 1 < h.Length && (h[end] != 0 || h[end + 1] != 0))
        end += 2;
      return Encoding.Unicode.GetString(h, (int)at, end - (int)at);
    } else {
      var end = Array.IndexOf(h, (byte)0, (int)at);
      return Encoding.Latin1.GetString(h, (int)at, (end < 0 ? h.Length : end) - (int)at);
    }
  }

  private string DirectoryName(uint index)
    => this.StringAt((long)this._descriptorOffset + this._fileTableOffset + this._fileTable[index]);

  private string FileName(FileDescriptor fd)
    => this.StringAt((long)this._descriptorOffset + this._fileTableOffset + fd.NameOffset);

  private sealed class FileDescriptor {
    public uint NameOffset;
    public uint DirectoryIndex;
    public ushort Flags;
    public ulong ExpandedSize, CompressedSize, DataOffset;
    public byte[] Md5 = new byte[16];
    public int Volume;
    public uint LinkPrevious;
    public byte LinkFlags;
  }

  private FileDescriptor Descriptor(int index) {
    if (this._descriptors.TryGetValue(index, out var cached))
      return cached;
    var h = this._header;
    var fd = new FileDescriptor();
    if (this.MajorVersion is 0 or 5) {
      var p = (long)this._descriptorOffset + this._fileTableOffset + this._fileTable[this._directoryCount + index];
      this.Need(p, this.MajorVersion == 5 ? 0x3A : 0x2A);
      var at = (int)p;
      fd.Volume = this._headerIndex;
      fd.NameOffset = U32(h, at);
      fd.DirectoryIndex = BinaryPrimitives.ReadUInt16LittleEndian(h.AsSpan(at + 4));
      fd.Flags = BinaryPrimitives.ReadUInt16LittleEndian(h.AsSpan(at + 8));
      fd.ExpandedSize = U32(h, at + 10);
      fd.CompressedSize = U32(h, at + 14);
      fd.DataOffset = U32(h, at + 38);
      if (this.MajorVersion == 5)
        h.AsSpan(at + 42, 16).CopyTo(fd.Md5);
    } else {
      var p = (long)this._descriptorOffset + this._fileTableOffset + this._fileTableOffset2 + (long)index * 0x57;
      this.Need(p, 0x57);
      var at = (int)p;
      fd.Flags = BinaryPrimitives.ReadUInt16LittleEndian(h.AsSpan(at));
      fd.ExpandedSize = BinaryPrimitives.ReadUInt64LittleEndian(h.AsSpan(at + 2));
      fd.CompressedSize = BinaryPrimitives.ReadUInt64LittleEndian(h.AsSpan(at + 10));
      fd.DataOffset = BinaryPrimitives.ReadUInt64LittleEndian(h.AsSpan(at + 18));
      h.AsSpan(at + 26, 16).CopyTo(fd.Md5);
      fd.NameOffset = U32(h, at + 0x3A);
      fd.DirectoryIndex = BinaryPrimitives.ReadUInt16LittleEndian(h.AsSpan(at + 0x3E));
      fd.LinkPrevious = U32(h, at + 0x4C);
      fd.LinkFlags = h[at + 0x54];
      fd.Volume = BinaryPrimitives.ReadUInt16LittleEndian(h.AsSpan(at + 0x55));
    }
    this._descriptors[index] = fd;
    return fd;
  }

  private bool IsValid(int index) {
    var fd = this.Descriptor(index);
    return (fd.Flags & FileInvalid) == 0 && fd.NameOffset != 0 && fd.DataOffset != 0;
  }

  // ── extraction (unshield lib/file.c) ─────────────────────────────────

  /// <summary>
  /// Returns the file's bytes. Compressed files are tried in the chunk layout with 16-bit
  /// lengths first and the 00 00 FF FF-terminated layout second; both are checked against the
  /// expanded size, and against the stored MD5 from InstallShield 6 on.
  /// </summary>
  public byte[] Extract(InstallShieldCabEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    return this.Extract(entry.Index, 0);
  }

  private byte[] Extract(int index, int depth) {
    var fd = this.Descriptor(index);
    if ((fd.LinkFlags & LinkPrev) != 0 && depth < 16)
      return this.Extract((int)fd.LinkPrevious, depth + 1);

    byte[] data;
    if ((fd.Flags & FileCompressed) == 0) {
      using var r = new VolumeReader(this, index, fd);
      data = r.Read((int)fd.ExpandedSize);
    } else {
      try {
        data = this.ExtractNew(index, fd);
      } catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException) {
        data = this.ExtractOld(index, fd);
      }
    }
    if ((ulong)data.Length != fd.ExpandedSize)
      throw new InvalidDataException($"InstallShield file {index}: expanded to {data.Length} bytes, the header says {fd.ExpandedSize}.");
    if (this.MajorVersion >= 6 && !Md5.Compute(data).AsSpan().SequenceEqual(fd.Md5))
      throw new InvalidDataException($"InstallShield file {index}: MD5 mismatch.");
    return data;
  }

  // Each chunk: u16 length, then a Deflate fragment (no zlib header) that ends in a sync flush.
  private byte[] ExtractNew(int index, FileDescriptor fd) {
    using var r = new VolumeReader(this, index, fd);
    using var output = new MemoryStream((int)Math.Min(fd.ExpandedSize, int.MaxValue));
    var left = (long)fd.CompressedSize;
    while (left > 0) {
      var length = BinaryPrimitives.ReadUInt16LittleEndian(r.Read(2));
      if (length == 0)
        throw new InvalidDataException("InstallShield chunk of length zero.");
      output.Write(Inflate(r.Read(length), (long)fd.ExpandedSize - output.Length));
      left -= 2 + length;
    }
    return output.ToArray();
  }

  /// <summary>
  /// Inflates one fragment, producing at most <paramref name="limit"/> bytes. Stopping at the
  /// limit matters for the smallest files: their single fragment may end before its block's
  /// end-of-block code, and zlib (unshield's inflater) simply stops there too.
  /// </summary>
  private static byte[] Inflate(ReadOnlySpan<byte> fragment, long limit) {
    if (limit <= 0)
      return [];
    var inflater = new DeflateDecompressor(new MemoryStream(fragment.ToArray(), writable: false));
    var buffer = new byte[(int)Math.Min(limit, int.MaxValue)];
    var n = 0;
    while (n < buffer.Length) {
      var got = inflater.Decompress(buffer.AsSpan(n));
      if (got <= 0)
        break;
      n += got;
    }
    return n == buffer.Length ? buffer : buffer[..n];
  }

  private static ReadOnlySpan<byte> EndOfChunk => [0x00, 0x00, 0xFF, 0xFF];

  // Older layout: the volume holds the fragments back to back, each ending in 00 00 FF FF.
  private byte[] ExtractOld(int index, FileDescriptor fd) {
    using var r = new VolumeReader(this, index, fd);
    using var output = new MemoryStream((int)Math.Min(fd.ExpandedSize, int.MaxValue));
    var outLeft = (long)fd.ExpandedSize;
    while (outLeft > 0) {
      var input = r.ReadRestOfVolume();
      var at = 0;
      while (at < input.Length && outLeft > 0) {
        var span = input.AsSpan(at);
        var chunk = span.IndexOf(EndOfChunk);
        if (chunk < 0)
          throw new InvalidDataException("InstallShield chunk without end-of-chunk marker.");
        // A marker followed by a byte with its low bit set is inside the data (a Deflate block
        // never starts with BFINAL set here); keep looking past it.
        while (chunk + 4 < span.Length && (span[chunk + 4] & 1) != 0) {
          var further = span[(chunk + 4)..].IndexOf(EndOfChunk);
          if (further < 0)
            throw new InvalidDataException("InstallShield chunk without end-of-chunk marker.");
          chunk += 4 + further;
        }
        // The marker is the LEN/NLEN of the empty stored block that flushes the fragment.
        var piece = Inflate(span[..(chunk + 4)], outLeft);
        output.Write(piece);
        outLeft -= piece.Length;
        at += chunk + 4;
      }
      if (outLeft > 0 && !r.NextVolume())
        throw new InvalidDataException("InstallShield file runs past the last volume.");
    }
    return output.ToArray();
  }

  /// <summary>Reads one file's stored bytes across volumes, undoing the obfuscation.</summary>
  private sealed class VolumeReader : IDisposable {
    private readonly InstallShieldCabReader _owner;
    private readonly int _index;
    private readonly FileDescriptor _fd;
    private Stream? _volume;
    private int _volumeNumber;
    private long _volumeLeft;
    private uint _seed;

    public VolumeReader(InstallShieldCabReader owner, int index, FileDescriptor fd) {
      this._owner = owner;
      this._index = index;
      this._fd = fd;
      var volume = fd.Volume;
      for (var guard = 0; ; ++guard) {
        var lastIndex = this.Open(volume);
        // InstallShield 5: the header names the first volume; walk to the one holding the file.
        if (owner.MajorVersion <= 5 && index > lastIndex && guard < 1000) {
          ++volume;
          continue;
        }
        break;
      }

      // The cabinet ends exactly where the file's data would start: the file is kept beside
      // the cabinet as a loose file (unshield: "not inside the cabinet, trying external file").
      if (this._volume!.Length == (long)fd.DataOffset)
        this.OpenExternal();
    }

    private void OpenExternal() {
      var dir = this._fd.DirectoryIndex < this._owner._directoryCount ? this._owner.DirectoryName(this._fd.DirectoryIndex) : "";
      var relative = dir.Length == 0 ? this._owner.FileName(this._fd) : dir + "\\" + this._owner.FileName(this._fd);
      using var external = this._owner._openExternal(relative) ?? throw new InvalidDataException($"InstallShield file '{relative}' is neither in the cabinet nor beside it.");
      var bytes = new MemoryStream();
      external.CopyTo(bytes);
      if ((this._fd.Flags & FileCompressed) != 0) {
        // Small compressed files may lack (part of) the closing 00 00 FF FF; complete it.
        var missing = (long)this._fd.CompressedSize - bytes.Length;
        if (missing > 0)
          bytes.Write(EndOfChunk[(int)(4 - Math.Min(4, missing))..]);
        this._volumeLeft = Math.Min((long)this._fd.CompressedSize, bytes.Length);
      } else
        this._volumeLeft = (long)this._fd.ExpandedSize;
      bytes.Position = 0;
      this._volume?.Dispose();
      this._volume = bytes;
      this._volumeNumber = int.MaxValue; // no further volumes
    }

    /// <summary>Opens a volume and positions on this file's data; returns the volume's last file index.</summary>
    private uint Open(int number) {
      this._volume?.Dispose();
      this._volume = this._owner._openVolume(number) ?? throw new InvalidDataException($"InstallShield volume data{number}.cab is missing.");
      this._volumeNumber = number;
      var s = this._volume;
      Span<byte> common = stackalloc byte[20];
      s.Position = 0;
      s.ReadExactly(common);
      if (BinaryPrimitives.ReadUInt32LittleEndian(common) != CabSignature)
        throw new InvalidDataException($"data{number}.cab is not an InstallShield cabinet volume.");

      uint firstIndex, lastIndex;
      ulong firstOffset, firstExpanded, firstCompressed, lastOffset, lastExpanded, lastCompressed;
      if (this._owner.MajorVersion is 0 or 5) {
        // data offset, an unknown word, then first/last file index, offset, expanded and compressed size.
        var w = new byte[40];
        s.ReadExactly(w);
        uint W(int i) => BinaryPrimitives.ReadUInt32LittleEndian(w.AsSpan(8 + 4 * i));
        firstIndex = W(0); lastIndex = W(1);
        firstOffset = W(2); firstExpanded = W(3); firstCompressed = W(4);
        lastOffset = W(5); lastExpanded = W(6); lastCompressed = W(7);
        if (lastOffset == 0)
          lastOffset = int.MaxValue;
      } else {
        var v = new byte[64];
        s.ReadExactly(v);
        ulong Q(int i) => BinaryPrimitives.ReadUInt32LittleEndian(v.AsSpan(4 * i)) | ((ulong)BinaryPrimitives.ReadUInt32LittleEndian(v.AsSpan(4 * i + 4)) << 32);
        firstIndex = BinaryPrimitives.ReadUInt32LittleEndian(v.AsSpan(8));
        lastIndex = BinaryPrimitives.ReadUInt32LittleEndian(v.AsSpan(12));
        firstOffset = Q(4); firstExpanded = Q(6); firstCompressed = Q(8);
        lastOffset = Q(10); lastExpanded = Q(12); lastCompressed = Q(14);
      }

      if (this._owner.MajorVersion == 5) {
        if (this._index < this._owner._fileCount - 1 && this._index == lastIndex && lastCompressed != this._fd.CompressedSize)
          this._fd.Flags |= FileSplit;
        else if (this._index > 0 && this._index == firstIndex && firstCompressed != this._fd.CompressedSize)
          this._fd.Flags |= FileSplit;
      }

      ulong offset, expanded, compressed;
      if ((this._fd.Flags & FileSplit) != 0) {
        if (this._index == lastIndex && lastOffset != 0x7FFFFFFF)
          (offset, expanded, compressed) = (lastOffset, lastExpanded, lastCompressed);
        else if (this._index == firstIndex)
          (offset, expanded, compressed) = (firstOffset, firstExpanded, firstCompressed);
        else {
          this._volumeLeft = 0;
          return lastIndex;
        }
      } else
        (offset, expanded, compressed) = (this._fd.DataOffset, this._fd.ExpandedSize, this._fd.CompressedSize);

      this._volumeLeft = (long)((this._fd.Flags & FileCompressed) != 0 ? compressed : expanded);
      // A volume that does not hold the file yet is walked past by the caller; reading from
      // past its end fails on its own.
      s.Position = (long)Math.Min(offset, (ulong)s.Length);
      return lastIndex;
    }

    public bool NextVolume() {
      if (this._volumeNumber == int.MaxValue)
        return false;
      try {
        this.Open(this._volumeNumber + 1);
        return true;
      } catch (InvalidDataException) {
        return false;
      }
    }

    public byte[] Read(int count) {
      var result = new byte[count];
      var done = 0;
      while (done < count) {
        if (this._volumeLeft == 0 && !this.NextVolume())
          throw new InvalidDataException("InstallShield file runs past the last volume.");
        var n = (int)Math.Min(count - done, this._volumeLeft);
        if (n == 0)
          throw new InvalidDataException("InstallShield volume holds no more of this file.");
        this._volume!.ReadExactly(result, done, n);
        done += n;
        this._volumeLeft -= n;
      }
      this.Deobfuscate(result);
      return result;
    }

    public byte[] ReadRestOfVolume() {
      if (this._volumeLeft == 0 && !this.NextVolume())
        throw new InvalidDataException("InstallShield file runs past the last volume.");
      return this.Read((int)this._volumeLeft);
    }

    private void Deobfuscate(Span<byte> data) {
      if ((this._fd.Flags & FileObfuscated) == 0)
        return;
      foreach (ref var b in data) {
        var x = (byte)(b ^ 0xD5);
        b = (byte)((byte)((x >> 2) | (x << 6)) - (this._seed % 0x47));
        ++this._seed;
      }
    }

    public void Dispose() => this._volume?.Dispose();
  }
}
