using System.Buffers.Binary;
using System.Text;

namespace FileFormat.Uc2;

/// <summary>One file or directory of a UC2 archive (the newest revision of its name).</summary>
public sealed class Uc2Entry {
  /// <summary>Gets the path inside the archive, '/'-separated (long names where the archive records them).</summary>
  public string Path { get; init; } = "";
  /// <summary>Gets the DOS 8.3 name as stored.</summary>
  public string ShortName { get; init; } = "";
  /// <summary>Gets whether this is a directory.</summary>
  public bool IsDirectory { get; init; }
  /// <summary>Gets the uncompressed size.</summary>
  public long Size { get; init; }
  /// <summary>Gets the compressed stream size (excluding its master).</summary>
  public long CompressedSize { get; init; }
  /// <summary>Gets the compression method number.</summary>
  public int Method { get; init; }
  /// <summary>Gets the DOS attribute byte.</summary>
  public byte Attributes { get; init; }
  /// <summary>Gets the modification time (DOS local time, 2-second resolution).</summary>
  public DateTime? LastModified { get; init; }

  internal uint MasterPrefix { get; init; }
  internal long Offset { get; init; }
  internal ushort Fletch { get; init; }
}

/// <summary>
/// UltraCompressor II (.uc2) archives: FHEAD + XHEAD, headerless compressed streams for files and
/// masters (shared dictionaries), and a compressed central directory; every stream is checked with
/// UC2's 16-bit XOR checksum. Reads every UC2 release's archives (methods 1-9, 21-29, 30-37,
/// 40-47 and turbo 80; supermaster, no-master and custom masters; delta filter) and writes archives
/// UC2 r2 reads: one 512-byte custom master compressed with the supermaster, files with method 3.
/// </summary>
/// <remarks>
/// Converted from klauspost/uc2 (LGPL-3.0), whose format.md documents the format from the
/// published UC2 r2 source; SUPER.DAT is the supermaster from that LGPL-3.0 UC2 release.
/// </remarks>
public static class Uc2Archive {
  private const uint Magic = 0x1A324355; // "UC2\x1A"
  private const uint AMag = 0x01B2C3D4;
  private const int FHeadSize = 13, XHeadSize = 16, CompressSize = 10;
  private const uint SuperMaster = 0, NoMaster = 1, LegacyPrefix = 0xDEDEDEDE;
  private const byte BoDir = 1, BoFile = 2, BoMast = 3, BoEol = 4;
  private const int MetaSize = 22, ExtSize = 21;
  private const string LongNameTag = "AIP:Win95 LongN";
  private const string Utf8NameTag = "UC2X:UTF8Name";

  private static readonly Lazy<byte[]> SuperDat = new(LoadSuper);

  private static byte[] LoadSuper() {
    using var s = typeof(Uc2Archive).Assembly.GetManifestResourceStream("FileFormat.Uc2.super.dat")
      ?? throw new InvalidOperationException("UC2 supermaster resource is missing.");
    var b = new byte[s.Length];
    s.ReadExactly(b);
    if (Fletch(b) != 0x1E55) throw new InvalidOperationException("UC2 supermaster resource is damaged.");
    return b;
  }

  private static readonly Lazy<Encoding> OemEncoding = new(() => {
    Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    return Encoding.GetEncoding(437); // UC2 does not record the code page; CP437 is the DOS default
  });

  private static Encoding Oem => OemEncoding.Value;

  /// <summary>UC2's "Fletcher": 0xA55A XOR the XOR of all little-endian 16-bit words (odd tail byte = low byte).</summary>
  public static ushort Fletch(ReadOnlySpan<byte> data) {
    ushort x = 0;
    var i = 0;
    for (; i + 1 < data.Length; i += 2) x ^= (ushort)(data[i] | (data[i + 1] << 8));
    if (i < data.Length) x ^= data[i];
    return (ushort)(0xA55A ^ x);
  }

  // ── reading ─────────────────────────────────────────────────────────

  /// <summary>A parsed archive: entries plus what is needed to extract them.</summary>
  public sealed class Parsed {
    internal Parsed(byte[] data, List<Uc2Entry> entries, Dictionary<uint, (int Method, uint Prefix, long Offset, long CompLen, int Length)> masters) {
      this.Data = data; this.Entries = entries; this.Masters = masters;
    }
    internal byte[] Data { get; }
    /// <summary>Gets the entries, directories included.</summary>
    public IReadOnlyList<Uc2Entry> Entries { get; }
    internal Dictionary<uint, (int Method, uint Prefix, long Offset, long CompLen, int Length)> Masters { get; }
    internal Dictionary<uint, byte[]> MasterCache { get; } = [];
  }

  /// <summary>Parses an archive and verifies its central directory.</summary>
  /// <exception cref="InvalidDataException">Not a UC2 archive, or its directory is damaged.</exception>
  /// <exception cref="NotSupportedException">UltraCrypt-encrypted or private-profile (PCP) archive.</exception>
  public static Parsed Read(Stream stream) {
    ArgumentNullException.ThrowIfNull(stream);
    if (stream.CanSeek) stream.Position = 0;
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    var data = ms.ToArray();
    if (data.Length >= 3 && data[0] == 'U' && data[1] == 'E' && data[2] == '2')
      throw new NotSupportedException("UC2 archive is encrypted with UltraCrypt.");
    if (data.Length < FHeadSize + XHeadSize || BinaryPrimitives.ReadUInt32LittleEndian(data) != Magic)
      throw new InvalidDataException("Not an UltraCompressor II archive.");
    var compLen = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4));
    if (BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(8)) - AMag != compLen) {
      // the spare FHEAD at the end carries the same values
      var spare = data.AsSpan(data.Length - FHeadSize);
      if (BinaryPrimitives.ReadUInt32LittleEndian(spare) != Magic || BinaryPrimitives.ReadUInt32LittleEndian(spare[8..]) - AMag != BinaryPrimitives.ReadUInt32LittleEndian(spare[4..]))
        throw new InvalidDataException("UC2 header length check fails.");
    }
    var x = data.AsSpan(FHeadSize, XHeadSize);
    var cdirVol = BinaryPrimitives.ReadUInt32LittleEndian(x);
    var cdirOff = BinaryPrimitives.ReadUInt32LittleEndian(x[4..]);
    var cdirFletch = BinaryPrimitives.ReadUInt16LittleEndian(x[8..]);
    var needed = BinaryPrimitives.ReadUInt16LittleEndian(x[13..]);
    if (needed == 203) throw new NotSupportedException("UC2 2.3 private compression profile (PCP) archives are not supported.");
    if (needed > 204) throw new NotSupportedException($"UC2 archive needs UltraCompressor version {needed}.");
    long cdirAt = needed == 204 ? ((long)(cdirVol - 1) << 32) | cdirOff : cdirOff;
    if (needed != 204 && cdirVol != 1 || cdirAt < FHeadSize + XHeadSize || cdirAt + CompressSize > data.Length)
      throw new InvalidDataException("UC2 central directory location is invalid.");
    var cdirMethod = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan((int)cdirAt + 4));
    if (cdirMethod is < 1 or > 9)
      throw new InvalidDataException($"UC2 central directory method {cdirMethod} is invalid.");
    var start = (int)cdirAt + CompressSize;
    var raw = Uc2Ultra.Decode(data, start, data.Length - start, new byte[512], -1);
    if (Fletch(raw) != cdirFletch)
      throw new InvalidDataException("UC2 central directory fails its checksum.");
    return ParseCdir(data, raw, needed == 204);
  }

  private static Parsed ParseCdir(byte[] data, byte[] raw, bool extended) {
    var p = 0;
    ReadOnlySpan<byte> Take(int n) {
      if (p + n > raw.Length) throw new InvalidDataException("UC2 central directory is truncated.");
      var s = raw.AsSpan(p, n);
      p += n;
      return s;
    }
    long Loc(ReadOnlySpan<byte> b) {
      var vol = BinaryPrimitives.ReadUInt32LittleEndian(b);
      var off = BinaryPrimitives.ReadUInt32LittleEndian(b[4..]);
      return extended ? ((long)(vol - 1) << 32) | off : off;
    }

    var dirs = new Dictionary<uint, string> { [0] = "" };
    var masters = new Dictionary<uint, (int, uint, long, long, int)>();
    var byPath = new Dictionary<string, Uc2Entry>(StringComparer.Ordinal); // newest revision wins
    var order = new List<string>();
    while (true) {
      var type = Take(1)[0];
      if (type == BoEol) break;
      if (type is BoDir or BoFile) {
        var m = Take(MetaSize);
        var parent = BinaryPrimitives.ReadUInt32LittleEndian(m);
        var attr = m[4];
        var time = BinaryPrimitives.ReadUInt16LittleEndian(m[5..]);
        var date = BinaryPrimitives.ReadUInt16LittleEndian(m[7..]);
        var shortName = ShortName(m.Slice(9, 11));
        var hasTags = m[21] != 0;
        uint index = 0, size = 0, prefix = 0;
        ushort fletch = 0;
        int method = 0;
        long compLen = 0, offset = 0;
        if (type == BoDir) {
          index = BinaryPrimitives.ReadUInt32LittleEndian(Take(4));
        } else {
          var f = Take(6 + CompressSize + 8);
          size = BinaryPrimitives.ReadUInt32LittleEndian(f);
          fletch = BinaryPrimitives.ReadUInt16LittleEndian(f[4..]);
          compLen = BinaryPrimitives.ReadUInt32LittleEndian(f[6..]);
          method = BinaryPrimitives.ReadUInt16LittleEndian(f[10..]);
          prefix = BinaryPrimitives.ReadUInt32LittleEndian(f[12..]);
          offset = Loc(f[16..]);
        }
        string? longName = null;
        long size64 = size;
        while (hasTags) {
          var h = Take(ExtSize);
          var tagSize = BinaryPrimitives.ReadUInt32LittleEndian(h[16..]);
          if (tagSize > 1_000_000) throw new InvalidDataException("UC2 tag is implausibly large.");
          var nul = h[..16].IndexOf((byte)0);
          var tagName = Encoding.ASCII.GetString(h[..(nul < 0 ? 16 : nul)]);
          var payload = Take((int)tagSize);
          if (tagName == Utf8NameTag) longName = Encoding.UTF8.GetString(payload);
          else if (tagName == LongNameTag && longName is null) {
            var z = payload.IndexOf((byte)0);
            longName = Oem.GetString(payload[..(z < 0 ? payload.Length : z)]);
          } else if (tagName == "UC2X:Size64" && payload.Length == 8) size64 = BinaryPrimitives.ReadInt64LittleEndian(payload);
          hasTags = h[20] != 0;
        }
        if (!dirs.TryGetValue(parent, out var parentPath))
          throw new InvalidDataException($"UC2 record refers to undefined directory {parent}.");
        var leaf = Sanitize(longName ?? shortName);
        var path = parentPath.Length == 0 ? leaf : parentPath + "/" + leaf;
        if (type == BoDir) dirs[index] = path;
        if (!byPath.ContainsKey(path)) order.Add(path);
        byPath[path] = new Uc2Entry {
          Path = path, ShortName = shortName, IsDirectory = type == BoDir, Size = size64, CompressedSize = compLen,
          Method = method, Attributes = attr, LastModified = DosTime(date, time), MasterPrefix = prefix, Offset = offset, Fletch = fletch,
        };
        continue;
      }
      if (type == BoMast) {
        var m = Take(20 + CompressSize + 8);
        var index = BinaryPrimitives.ReadUInt32LittleEndian(m);
        var length = BinaryPrimitives.ReadUInt16LittleEndian(m[16..]);
        var compLen = BinaryPrimitives.ReadUInt32LittleEndian(m[20..]);
        var method = BinaryPrimitives.ReadUInt16LittleEndian(m[24..]);
        var prefix = BinaryPrimitives.ReadUInt32LittleEndian(m[26..]);
        masters[index] = (method, prefix, Loc(m[30..]), compLen, length);
        continue;
      }
      throw new InvalidDataException($"UC2 central directory record type {type} is invalid.");
    }
    // Reserved U$~ members (comment, banners, locks) are UC2's own, not archive content.
    var entries = order.Select(k => byPath[k]).Where(e => !e.ShortName.StartsWith("U$~", StringComparison.Ordinal)).ToList();
    return new Parsed(data, entries, masters);
  }

  /// <summary>Extracts one file, verifying its checksum.</summary>
  public static byte[] Extract(Parsed archive, Uc2Entry entry) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entry);
    if (entry.IsDirectory || entry.Size == 0) return [];
    if (entry.MasterPrefix < 2 && entry.MasterPrefix != SuperMaster && entry.MasterPrefix != NoMaster)
      throw new InvalidDataException($"'{entry.Path}' uses an invalid master.");
    var dict = Dictionary(archive, entry.MasterPrefix);
    return DecodeStream(archive.Data, entry.Offset, entry.CompressedSize, entry.Method, dict, entry.Size, entry.Fletch, entry.Path);
  }

  private static byte[] Dictionary(Parsed archive, uint prefix) {
    switch (prefix) {
      case SuperMaster or LegacyPrefix: return SuperDat.Value;
      case NoMaster: return new byte[512];
    }
    if (archive.MasterCache.TryGetValue(prefix, out var cached)) return cached;
    if (!archive.Masters.TryGetValue(prefix, out var m))
      throw new InvalidDataException($"UC2 master {prefix} is missing.");
    if (m.Prefix is not (SuperMaster or NoMaster or LegacyPrefix))
      throw new InvalidDataException($"UC2 master {prefix} is chained to another master.");
    var dict = m.Prefix == NoMaster ? new byte[512] : SuperDat.Value;
    var content = DecodeStream(archive.Data, m.Offset, m.CompLen, m.Method, dict, m.Length, null, $"master {prefix}");
    archive.MasterCache[prefix] = content;
    return content;
  }

  private static (int Delta, bool Turbo) MethodInfo(int method) => method switch {
    >= 1 and <= 9 => (0, false),
    >= 21 and <= 29 => (1, false),
    >= 30 and <= 37 => (method - 29, false),
    >= 40 and <= 47 => (method - 39, false),
    80 => (0, true),
    _ => throw new InvalidDataException($"UC2 method {method} is invalid."),
  };

  private static byte[] DecodeStream(byte[] data, long offset, long compLen, int method, byte[] dictionary, long size, ushort? fletch, string what) {
    if (offset < 0 || offset >= data.Length) throw new InvalidDataException($"UC2 {what} points outside the archive.");
    var available = (int)Math.Min(compLen > 0 ? compLen : data.Length - offset, data.Length - offset);
    var (delta, turbo) = MethodInfo(method);
    if (turbo) {
      var t = DecodeTurbo(data, (int)offset, available, dictionary, size);
      if (fletch is { } ft && Fletch(t) != ft) throw new InvalidDataException($"UC2 checksum mismatch for {what}.");
      return t;
    }
    // For delta methods the dictionary (unless it is the supermaster) is delta'd too.
    var dict = delta > 0 && !ReferenceEquals(dictionary, SuperDat.Value) ? ForwardDelta(dictionary, delta) : dictionary;
    var output = Uc2Ultra.Decode(data, (int)offset, available, dict, size);
    if (fletch is { } f && Fletch(output) != f) throw new InvalidDataException($"UC2 checksum mismatch for {what}.");
    if (delta > 0) InverseDelta(output, delta);
    return output;
  }

  private static byte[] ForwardDelta(byte[] input, int size) {
    var o = new byte[input.Length];
    Span<byte> arr = stackalloc byte[8];
    arr.Clear();
    var ctr = 0;
    for (var i = 0; i < input.Length; ++i) {
      o[i] = (byte)(input[i] - arr[ctr]);
      arr[ctr] = input[i];
      ctr = (ctr + 1) % size;
    }
    return o;
  }

  private static void InverseDelta(byte[] buffer, int size) {
    Span<byte> arr = stackalloc byte[8];
    arr.Clear();
    var ctr = 0;
    for (var i = 0; i < buffer.Length; ++i) {
      buffer[i] = (byte)(buffer[i] + arr[ctr]);
      arr[ctr] = buffer[i];
      ctr = (ctr + 1) % size;
    }
  }

  private static byte[] DecodeTurbo(byte[] data, int offset, int length, byte[] dict, long size) {
    var table = new byte[32768];
    for (var i = 0; i + 10 < dict.Length; ++i) table[(dict[i] << 7) ^ dict[i + 1]] = dict[i + 2];
    var output = new byte[size];
    var o = 0;
    byte p1 = 0, p2 = 0;
    var pos = offset;
    var end = offset + length;
    while (o < size) {
      if (pos + 2 > end) throw new InvalidDataException("UC2 turbo stream ended early.");
      var n = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(pos));
      pos += 2;
      if (n == 0 || pos + n > end) throw new InvalidDataException("UC2 turbo stream ended early.");
      var blockEnd = pos + n;
      while (pos < blockEnd) {
        int ctl = data[pos++];
        for (var i = 0; i < 8 && (pos < blockEnd || (ctl & 0xFF) != 0); ++i) {
          var h = (p2 << 7) ^ p1;
          var c = table[h];
          if ((ctl & 0x80) == 0) {
            if (pos >= blockEnd) throw new InvalidDataException("UC2 turbo block is truncated.");
            c = data[pos++];
            table[h] = c;
          }
          if (o < size) output[o++] = c;
          p2 = p1; p1 = c;
          ctl = (ctl << 1) & 0xFF;
        }
      }
    }
    return output;
  }

  private static string ShortName(ReadOnlySpan<byte> fcb) {
    var oem = Oem;
    var name = oem.GetString(fcb[..8]).TrimEnd(' ');
    var ext = oem.GetString(fcb[8..11]).TrimEnd(' ');
    return ext.Length == 0 ? name : name + "." + ext;
  }

  private static string Sanitize(string name) {
    var s = name.Replace('/', '_').Replace('\\', '_').Replace(':', '_');
    return s is "" or "." or ".." ? "_" : s;
  }

  private static DateTime? DosTime(ushort date, ushort time) {
    if (date == 0) return null;
    try {
      return new DateTime(1980 + (date >> 9), (date >> 5) & 15, date & 31, time >> 11, (time >> 5) & 63, (time & 31) * 2, DateTimeKind.Local);
    } catch (ArgumentOutOfRangeException) {
      return null;
    }
  }

  // ── writing ─────────────────────────────────────────────────────────

  /// <summary>A file to write: its archive path ('/'-separated), content and time.</summary>
  public readonly record struct Input(string Path, byte[] Data, DateTime LastModified);

  /// <summary>
  /// Writes a UC2 archive that UC2 r2 reads (wVersionNeededToExtract 200): one custom master of
  /// 512 zero bytes compressed with the supermaster, every file compressed with method 3 against
  /// it, directories implied by the paths, 8.3 aliases plus "AIP:Win95 LongN" tags for names that
  /// do not fit 8.3, and the CDIR compressed with no master.
  /// </summary>
  public static void Write(Stream output, IEnumerable<Input> inputs) {
    ArgumentNullException.ThrowIfNull(output);
    var files = inputs.ToList();
    var body = new MemoryStream();
    body.Write(new byte[FHeadSize + XHeadSize]);

    // directories
    var dirIndex = new Dictionary<string, uint>(StringComparer.Ordinal) { [""] = 0 };
    var dirRecords = new List<(uint Parent, uint Index, string Leaf)>();
    var aliases = new Dictionary<uint, HashSet<string>>();
    uint nextDir = 1;
    uint DirOf(string dirPath) {
      if (dirIndex.TryGetValue(dirPath, out var idx)) return idx;
      var slash = dirPath.LastIndexOf('/');
      var parent = DirOf(slash < 0 ? "" : dirPath[..slash]);
      idx = nextDir++;
      dirIndex[dirPath] = idx;
      dirRecords.Add((parent, idx, slash < 0 ? dirPath : dirPath[(slash + 1)..]));
      return idx;
    }

    // master: 512 zeros compressed with the supermaster (method 3)
    var master = new byte[512];
    var masterStream = Uc2Ultra.Encode(master, SuperDat.Value);

    var fileRecords = new List<(uint Parent, string Leaf, byte[] Data, DateTime Time, long Offset, int CompLen)>();
    foreach (var f in files) {
      var path = f.Path.Replace('\\', '/').Trim('/');
      if (path.Length == 0) continue;
      var slash = path.LastIndexOf('/');
      var parent = DirOf(slash < 0 ? "" : path[..slash]);
      var stream = Uc2Ultra.Encode(f.Data, master);
      var offset = body.Position;
      body.Write(stream);
      fileRecords.Add((parent, slash < 0 ? path : path[(slash + 1)..], f.Data, f.LastModified, offset, stream.Length));
    }
    var masterOffset = body.Position;
    body.Write(masterStream);

    // central directory (canonical order: per directory, child dirs, then files, then recurse)
    var raw = new MemoryStream();
    var children = dirRecords.GroupBy(d => d.Parent).ToDictionary(g => g.Key, g => g.ToList());
    var filesByDir = fileRecords.GroupBy(f => f.Parent).ToDictionary(g => g.Key, g => g.ToList());
    void Emit(uint dir) {
      foreach (var c in children.GetValueOrDefault(dir) ?? []) WriteMeta(raw, BoDir, c.Parent, 0x10, null, Alias(aliases, c.Parent, c.Leaf), c.Leaf, w => { Span<byte> ib = stackalloc byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(ib, c.Index); w.Write(ib); });
      foreach (var f in filesByDir.GetValueOrDefault(dir) ?? []) {
        WriteMeta(raw, BoFile, f.Parent, 0x20, f.Time, Alias(aliases, f.Parent, f.Leaf), f.Leaf, w => {
          Span<byte> b = stackalloc byte[6 + CompressSize + 8];
          BinaryPrimitives.WriteUInt32LittleEndian(b, (uint)f.Data.Length);
          BinaryPrimitives.WriteUInt16LittleEndian(b[4..], Fletch(f.Data));
          BinaryPrimitives.WriteUInt32LittleEndian(b[6..], (uint)f.CompLen);
          BinaryPrimitives.WriteUInt16LittleEndian(b[10..], 3);
          BinaryPrimitives.WriteUInt32LittleEndian(b[12..], 2);
          BinaryPrimitives.WriteUInt32LittleEndian(b[16..], 1);
          BinaryPrimitives.WriteUInt32LittleEndian(b[20..], checked((uint)f.Offset));
          w.Write(b);
        });
      }
      foreach (var c in children.GetValueOrDefault(dir) ?? []) Emit(c.Index);
    }
    Emit(0);
    if (fileRecords.Count > 0) {
      Span<byte> m = stackalloc byte[1 + 20 + CompressSize + 8];
      m[0] = BoMast;
      BinaryPrimitives.WriteUInt32LittleEndian(m[1..], 2);       // index
      BinaryPrimitives.WriteUInt32LittleEndian(m[5..], 0);       // key: one UC2 never generates, so it is not reused
      BinaryPrimitives.WriteUInt32LittleEndian(m[9..], (uint)fileRecords.Sum(f => (long)f.Data.Length));
      BinaryPrimitives.WriteUInt32LittleEndian(m[13..], (uint)fileRecords.Count);
      BinaryPrimitives.WriteUInt16LittleEndian(m[17..], 512);
      BinaryPrimitives.WriteUInt16LittleEndian(m[19..], 0xDEDE);
      BinaryPrimitives.WriteUInt32LittleEndian(m[21..], (uint)masterStream.Length);
      BinaryPrimitives.WriteUInt16LittleEndian(m[25..], 3);
      BinaryPrimitives.WriteUInt32LittleEndian(m[27..], SuperMaster);
      BinaryPrimitives.WriteUInt32LittleEndian(m[31..], 1);
      BinaryPrimitives.WriteUInt32LittleEndian(m[35..], checked((uint)masterOffset));
      raw.Write(m);
    }
    raw.WriteByte(BoEol);
    raw.Write(new byte[17 + 4]); // XTAIL + serial
    var rawCdir = raw.ToArray();
    var cdirStream = Uc2Ultra.Encode(rawCdir, new byte[512]);

    var cdirAt = body.Position;
    Span<byte> comp = stackalloc byte[CompressSize];
    BinaryPrimitives.WriteUInt32LittleEndian(comp, (uint)cdirStream.Length);
    BinaryPrimitives.WriteUInt16LittleEndian(comp[4..], 3);
    BinaryPrimitives.WriteUInt32LittleEndian(comp[6..], NoMaster);
    body.Write(comp);
    body.Write(cdirStream);
    var componentLength = checked((uint)(body.Position - FHeadSize));

    var head = new byte[FHeadSize + XHeadSize];
    BinaryPrimitives.WriteUInt32LittleEndian(head, Magic);
    BinaryPrimitives.WriteUInt32LittleEndian(head.AsSpan(4), componentLength);
    BinaryPrimitives.WriteUInt32LittleEndian(head.AsSpan(8), componentLength + AMag);
    head[12] = 0;
    BinaryPrimitives.WriteUInt32LittleEndian(head.AsSpan(13), 1);
    BinaryPrimitives.WriteUInt32LittleEndian(head.AsSpan(17), checked((uint)cdirAt));
    BinaryPrimitives.WriteUInt16LittleEndian(head.AsSpan(21), Fletch(rawCdir));
    head[23] = 0;
    BinaryPrimitives.WriteUInt16LittleEndian(head.AsSpan(24), 202); // made by
    BinaryPrimitives.WriteUInt16LittleEndian(head.AsSpan(26), 200); // needed
    head[28] = 0;
    body.Position = 0;
    body.Write(head);
    body.Position = body.Length;
    body.Write(head.AsSpan(0, FHeadSize)); // spare FHEAD
    if (body.Length > int.MaxValue) throw new NotSupportedException("UC2 archives this writer produces stay below 2 GiB.");
    output.Write(body.GetBuffer(), 0, (int)body.Length);
  }

  private static void WriteMeta(MemoryStream raw, byte type, uint parent, byte attr, DateTime? time, byte[] fcb, string leaf, Action<MemoryStream> tail) {
    raw.WriteByte(type);
    Span<byte> m = stackalloc byte[MetaSize];
    BinaryPrimitives.WriteUInt32LittleEndian(m, parent);
    m[4] = attr;
    var (dosDate, dosTime) = ToDos(time);
    BinaryPrimitives.WriteUInt16LittleEndian(m[5..], dosTime);
    BinaryPrimitives.WriteUInt16LittleEndian(m[7..], dosDate);
    fcb.CopyTo(m[9..]);
    m[20] = 0xDE; // bHidden: UC2's own fill byte
    var needsLongName = !string.Equals(ShortName(fcb), leaf, StringComparison.Ordinal);
    m[21] = needsLongName ? (byte)1 : (byte)0;
    raw.Write(m);
    tail(raw);
    if (needsLongName) {
      Span<byte> h = stackalloc byte[ExtSize];
      h.Clear();
      Encoding.ASCII.GetBytes(LongNameTag).CopyTo(h);
      var name = Oem.GetBytes(leaf);
      BinaryPrimitives.WriteUInt32LittleEndian(h[16..], (uint)name.Length + 1);
      h[20] = 0;
      raw.Write(h);
      raw.Write(name);
      raw.WriteByte(0);
    }
  }

  private static (ushort Date, ushort Time) ToDos(DateTime? t) {
    if (t is not { } v) return (0, 0);
    v = v.Kind == DateTimeKind.Utc ? v.ToLocalTime() : v;
    if (v.Year is < 1980 or > 2107) return (0x21, 0); // 1980-01-01
    return ((ushort)(((v.Year - 1980) << 9) | (v.Month << 5) | v.Day), (ushort)((v.Hour << 11) | (v.Minute << 5) | (v.Second / 2)));
  }

  /// <summary>An 8.3 FCB name unique within the directory: A-Z 0-9 _ ~, never a device name or U$~.</summary>
  private static byte[] Alias(Dictionary<uint, HashSet<string>> used, uint dir, string leaf) {
    if (!used.TryGetValue(dir, out var set)) used[dir] = set = [];
    static string Clean(string s, int max) {
      var sb = new StringBuilder();
      foreach (var ch in s.ToUpperInvariant()) {
        if (sb.Length == max) break;
        if (ch is >= 'A' and <= 'Z' or >= '0' and <= '9' or '_' or '~') sb.Append(ch);
        else if (ch == '-' && sb.Length > 0) sb.Append(ch);
      }
      return sb.ToString();
    }
    var dot = leaf.LastIndexOf('.');
    var baseRaw = dot > 0 ? leaf[..dot] : leaf;
    var extRaw = dot > 0 ? leaf[(dot + 1)..] : "";
    var b = Clean(baseRaw, 8);
    var e = Clean(extRaw, 3);
    if (b.Length == 0 || IsDevice(b) || b.StartsWith("U$~", StringComparison.Ordinal)) b = "FILE";
    var candidate = e.Length == 0 ? b : b + "." + e;
    for (var n = 1; set.Contains(candidate); ++n) {
      var suffix = "~" + n;
      var stem = b[..Math.Min(b.Length, 8 - suffix.Length)] + suffix;
      candidate = e.Length == 0 ? stem : stem + "." + e;
    }
    set.Add(candidate);
    var fcb = new byte[11];
    fcb.AsSpan().Fill((byte)' ');
    var cdot = candidate.IndexOf('.');
    Encoding.ASCII.GetBytes(cdot < 0 ? candidate : candidate[..cdot]).CopyTo(fcb, 0);
    if (cdot >= 0) Encoding.ASCII.GetBytes(candidate[(cdot + 1)..]).CopyTo(fcb, 8);
    return fcb;
  }

  private static bool IsDevice(string b) => b is "CON" or "PRN" or "AUX" or "NUL" or "CLOCK$"
    || (b.Length == 4 && (b.StartsWith("COM", StringComparison.Ordinal) || b.StartsWith("LPT", StringComparison.Ordinal)) && b[3] is >= '1' and <= '9');
}
