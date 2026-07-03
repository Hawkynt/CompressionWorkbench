using System.IO.Compression;
using System.Text;

namespace FileFormat.BitRock;

/// <summary>
/// Reader for BitRock / InstallBuilder self-extracting installers.
///
/// Layout (recovered by binary inspection of real installers):
///   [PE stub] [content region] [embedded Metakit VFS] [16-byte trailer]
///   ["bitrock-lzma-4.0" 16 bytes] ["mFC3acAOJrQinu5aEHu0uH7N5XSQ3Z14" 32-byte magic]
///
/// The installer runtime is a tclkit whose virtual file system is a Metakit
/// (Mk4) datafile using the mk4vfs schema
/// "dirs[name:S,parent:I,files[name:S,size:I,date:I,contents:B]]".
///
/// Locating the VFS (mirrors the runtime's own logic):
///   off   = EOF - offset_of("bitrock-lzma-4.0")            (= 48: 16 id + 32 magic)
///   a,b,c,d = four big-endian int32 read at EOF-16-off
///           a == 0x80000000, top byte of c == 0x80, b == VFS byte length
///   start = EOF - 16 - b - off                             (VFS begins here, "JL" magic)
///
/// Metakit metadata encodes column data as segments described by
/// base-128 big-endian integers whose HIGH bit marks the final byte
/// (verified: the schema-string length prefix "80 bc" decodes to 60, the exact
/// schema length). Each view column is described by a (rowCount, byteSize,
/// position) triple; positions are absolute file offsets. The top-level "dirs"
/// view stores its directory names as a NUL-separated pool at VFS offset 8,
/// followed by a per-name length index and a per-directory parent index.
///
/// Per-directory file catalogues (name pool + size/date columns) and the file
/// contents are stored in the content region. File contents are stored as
/// self-delimiting zlib (or gzip) streams; tiny files may be stored verbatim.
/// </summary>
public sealed class BitRockReader {

  /// <summary>The 32-byte end-of-file magic that identifies a BitRock installer.</summary>
  public static ReadOnlySpan<byte> EndMagic => "mFC3acAOJrQinu5aEHu0uH7N5XSQ3Z14"u8;

  /// <summary>The compressor identifier stored just before the end magic.</summary>
  public static ReadOnlySpan<byte> CompressorId => "bitrock-lzma-4.0"u8;

  private readonly byte[] _vfs;                       // the embedded Metakit datafile
  private readonly List<string> _dirPaths = [];       // full path per directory row
  private readonly List<BitRockFile> _files = [];     // extracted file entries

  /// <summary>Full paths of every directory in the tclkit runtime virtual file system.</summary>
  public IReadOnlyList<string> DirectoryPaths => this._dirPaths;

  /// <summary>Runtime file entries recovered from the Metakit VFS (name + decompressed content).</summary>
  public IReadOnlyList<BitRockFile> Files => this._files;

  /// <summary>
  /// Offset at which the tclkit runtime's Metakit VFS begins — i.e. the end of the cookfs content
  /// region that carries the application payload (see <see cref="BitRockContentScanner"/>).
  /// </summary>
  public long VfsStart { get; }

  private BitRockReader(byte[] vfs, long vfsStart) {
    this._vfs = vfs;
    this.VfsStart = vfsStart;
    this.ParseDirectories();
    this.ExtractContents();
  }

  /// <summary>
  /// Returns true if the stream is a BitRock / InstallBuilder installer, i.e. the
  /// end magic or compressor id and mk4vfs schema are present near the tail.
  /// </summary>
  public static bool IsBitRock(Stream stream) {
    try {
      var tail = ReadTail(stream, out _);
      return tail.AsSpan().IndexOf(EndMagic) >= 0
          || (tail.AsSpan().IndexOf(CompressorId) >= 0
              && tail.AsSpan().IndexOf("dirs[name:S,parent:I"u8) >= 0);
    } catch {
      return false;
    }
  }

  /// <summary>Opens a BitRock installer stream, or throws if it is not one.</summary>
  public static BitRockReader Open(Stream stream) {
    if (!TryLocateVfs(stream, out var start, out var size))
      throw new InvalidDataException("Not a BitRock/InstallBuilder installer (VFS not found).");
    var vfs = new byte[size];
    stream.Position = start;
    stream.ReadExactly(vfs, 0, (int)size);
    if (vfs.Length < 2 || vfs[0] != (byte)'J' || vfs[1] != (byte)'L')
      throw new InvalidDataException("BitRock VFS is missing the Metakit 'JL' magic.");

    // The application payload lives in the cookfs content region ahead of the VFS; the VFS start is
    // that archive's end offset. Payload reconstruction/extraction is driven by BitRockContentScanner.
    return new BitRockReader(vfs, start);
  }

  // ── VFS localisation ───────────────────────────────────────────────────────

  private static byte[] ReadTail(Stream stream, out long fileLength) {
    fileLength = stream.Length;
    var tailLen = (int)Math.Min(fileLength, 65536L);
    var tail = new byte[tailLen];
    stream.Position = fileLength - tailLen;
    stream.ReadExactly(tail, 0, tailLen);
    return tail;
  }

  /// <summary>
  /// Locates the embedded Metakit VFS using the runtime's own footer arithmetic.
  /// </summary>
  public static bool TryLocateVfs(Stream stream, out long start, out long size) {
    start = 0; size = 0;
    var tail = ReadTail(stream, out var fileLength);
    var idx = tail.AsSpan().LastIndexOf(CompressorId);
    if (idx < 0)
      return false;

    var off = tail.Length - idx;                       // distance from id start to EOF
    var trailerPos = fileLength - 16 - off;
    if (trailerPos < 0)
      return false;
    Span<byte> hdr = stackalloc byte[16];
    stream.Position = trailerPos;
    stream.ReadExactly(hdr);
    var a = ReadBigEndianU32(hdr[..4]);
    var c = ReadBigEndianU32(hdr.Slice(8, 4));
    var b = ReadBigEndianU32(hdr.Slice(4, 4));
    if (a != 0x80000000u || (c >> 24) != 0x80u)
      return false;

    var vfsStart = fileLength - 16 - b - off;
    if (vfsStart < 0 || b == 0 || b > fileLength)
      return false;
    start = vfsStart;
    size = b;
    return true;
  }

  private static uint ReadBigEndianU32(ReadOnlySpan<byte> s)
    => ((uint)s[0] << 24) | ((uint)s[1] << 16) | ((uint)s[2] << 8) | s[3];

  // ── Directory tree ─────────────────────────────────────────────────────────

  private void ParseDirectories() {
    // Directory-name pool: NUL-separated printable names starting at VFS offset 8.
    var names = new List<string>();
    var p = 8;
    while (p < this._vfs.Length) {
      var ch = this._vfs[p];
      if (ch == 0) { p++; continue; }
      if (ch < 0x20 || ch >= 0x7f)
        break;
      var end = Array.IndexOf(this._vfs, (byte)0, p);
      if (end < 0 || end - p > 200)
        break;
      var name = Encoding.Latin1.GetString(this._vfs, p, end - p);
      names.Add(name);
      p = end + 1;
    }

    var count = names.Count;
    if (count == 0)
      return;
    var parentBase = p + count;                        // pool, then length index, then parents
    var parents = new int[count];
    for (var i = 0; i < count; i++)
      parents[i] = parentBase + i < this._vfs.Length ? this._vfs[parentBase + i] : 0xff;

    for (var i = 0; i < count; i++)
      this._dirPaths.Add(BuildPath(names, parents, i));
  }

  private static string BuildPath(List<string> names, int[] parents, int i) {
    if (names[i] == "<root>")
      return string.Empty;
    var chain = new List<string>();
    var j = i;
    var guard = 0;
    while (j != 0xff && j < names.Count && names[j] != "<root>" && guard < names.Count) {
      chain.Add(names[j]);
      j = parents[j];
      guard++;
    }
    chain.Reverse();
    return string.Join('/', chain);
  }

  // ── File catalogue (names + sizes) ───────────────────────────────────────────

  /// <summary>
  /// Scans the VFS for per-directory file-name blocks. A block is a run of
  /// NUL-terminated printable file names immediately followed by a byte-length
  /// index (each byte == name length + 1) and a little-endian int32 size column.
  /// Returns candidate (name, size) pairs used to name extracted contents.
  /// </summary>
  private List<(string Name, long Size)> ScanNamedFiles() {
    var result = new List<(string, long)>();
    var schema = this._vfs.AsSpan().IndexOf("dirs[name:S,parent:I"u8);
    var limit = schema > 0 ? schema : this._vfs.Length;
    var p = 8;
    while (p < limit - 8) {
      var ch = this._vfs[p];
      if (ch < 0x20 || ch >= 0x7f) { p++; continue; }

      var names = new List<string>();
      var q = p;
      while (q < limit) {
        var c = this._vfs[q];
        if (c == 0) { q++; continue; }
        if (c < 0x20 || c >= 0x7f)
          break;
        var end = Array.IndexOf(this._vfs, (byte)0, q);
        if (end < 0 || end - q > 200)
          break;
        var seg = this._vfs.AsSpan(q, end - q);
        var printable = true;
        foreach (var x in seg)
          if (x < 0x20 || x >= 0x7f) { printable = false; break; }
        if (!printable)
          break;
        names.Add(Encoding.Latin1.GetString(seg));
        q = end + 1;
      }

      if (names.Count == 0) { p++; continue; }

      // Validate the length index that must follow the pool.
      var valid = q + names.Count <= this._vfs.Length;
      for (var k = 0; valid && k < names.Count; k++)
        if (this._vfs[q + k] != names[k].Length + 1)
          valid = false;

      // Only accept blocks that plausibly hold real file names (avoids matching
      // stray printable bytes inside compressed data).
      var plausible = names.Count >= 2 || (names.Count == 1 && LooksLikeFileName(names[0]));
      if (valid && plausible) {
        var sizePos = q + names.Count;
        if (sizePos + 4 * names.Count <= this._vfs.Length) {
          var ok = true;
          var sizes = new long[names.Count];
          for (var k = 0; k < names.Count; k++) {
            var s = (long)(uint)(this._vfs[sizePos + 4 * k]
              | (this._vfs[sizePos + 4 * k + 1] << 8)
              | (this._vfs[sizePos + 4 * k + 2] << 16)
              | (this._vfs[sizePos + 4 * k + 3] << 24));
            if (s > this._vfs.Length * 200L) { ok = false; break; }
            sizes[k] = s;
          }
          if (ok) {
            for (var k = 0; k < names.Count; k++)
              result.Add((names[k], sizes[k]));
            p = sizePos + 4 * names.Count;
            continue;
          }
        }
      }
      p = q;
    }
    return result;
  }

  private static bool LooksLikeFileName(string s)
    => s.Length >= 4 && s.Contains('.') && !s.Contains(' ');

  // ── Content extraction ───────────────────────────────────────────────────────

  private void ExtractContents() {
    var named = this.ScanNamedFiles();
    var bySize = new Dictionary<long, Queue<string>>();
    foreach (var (name, size) in named) {
      if (!bySize.TryGetValue(size, out var q))
        bySize[size] = q = new Queue<string>();
      q.Enqueue(name);
    }

    var used = new HashSet<string>();
    var index = 0;
    var p = 8;
    while (p < this._vfs.Length - 6) {
      var h = this.NextHeader(p);
      if (h < 0)
        break;
      var gzip = this._vfs[h] == 0x1f;
      var data = this.InflateMemo(h, gzip, out var compLength);
      if (data == null || data.Length == 0) {
        p = h + 1;
        continue;
      }
      var name = this.PickName(bySize, used, data, index);
      this._files.Add(new BitRockFile(name, data));
      index++;
      p = h + Math.Max(compLength, 1);
    }
  }

  /// <summary>Index of the next zlib/gzip member header at or after <paramref name="from"/>, else -1.</summary>
  private int NextHeader(int from) {
    for (var i = Math.Max(from, 0); i < this._vfs.Length - 1; i++) {
      var a = this._vfs[i];
      var b = this._vfs[i + 1];
      if ((a == 0x78 && (b == 0x01 || b == 0x9c || b == 0xda)) || (a == 0x1f && b == 0x8b))
        return i;
    }
    return -1;
  }

  /// <summary>
  /// Inflates the member at <paramref name="offset"/> and determines its exact
  /// compressed byte length. Because the framework decompressor drains the whole
  /// base stream without reporting consumption, the length is found by growing the
  /// input bound until the output converges, then binary-searching the minimum
  /// bound that still yields the full output.
  /// </summary>
  private byte[]? InflateMemo(int offset, bool gzip, out int compLength) {
    compLength = 0;
    var remaining = this._vfs.Length - offset;

    // Grow phase: double the input window until the decompressed size converges.
    var window = 64;
    var prevLen = -1;
    byte[]? full = null;
    var upper = remaining;
    while (true) {
      var bounded = Math.Min(window, remaining);
      var o = Inflate(offset, gzip, bounded);
      if (o != null) {
        if (o.Length == prevLen && o.Length > 0) {
          full = o;
          upper = bounded;
          break;
        }
        prevLen = o.Length;
        full = o;
      }
      if (bounded >= remaining) {
        upper = remaining;
        break;
      }
      window = Math.Min(window * 2, remaining);
    }

    if (full == null || full.Length == 0)
      return null;

    // Binary search the smallest input window that still produces the full output.
    var lo = 6;
    var hi = upper;
    while (lo < hi) {
      var mid = lo + (hi - lo) / 2;
      var o = Inflate(offset, gzip, mid);
      if (o != null && o.Length >= full.Length)
        hi = mid;
      else
        lo = mid + 1;
    }
    compLength = lo;
    return full;
  }

  /// <summary>Inflates at most <paramref name="length"/> compressed bytes; returns null on failure.</summary>
  private byte[]? Inflate(int offset, bool gzip, int length) {
    try {
      using var src = new MemoryStream(this._vfs, offset, length, writable: false);
      using Stream dec = gzip
        ? new GZipStream(src, CompressionMode.Decompress, leaveOpen: true)
        : new ZLibStream(src, CompressionMode.Decompress, leaveOpen: true);
      using var outMs = new MemoryStream();
      dec.CopyTo(outMs);
      return outMs.ToArray();
    } catch {
      return null;
    }
  }

  private string PickName(Dictionary<long, Queue<string>> bySize, HashSet<string> used, byte[] data, int index) {
    if (bySize.TryGetValue(data.Length, out var q)) {
      while (q.Count > 0) {
        var candidate = q.Dequeue();
        if (used.Add(candidate))
          return candidate;
      }
    }
    return $"content/item{index:D5}{SniffExtension(data)}";
  }

  private static string SniffExtension(byte[] d) {
    if (d.Length >= 8 && d[0] == 0x89 && d[1] == 0x50 && d[2] == 0x4e && d[3] == 0x47) return ".png";
    if (d.Length >= 4 && d[0] == 0x00 && d[1] == 0x00 && d[2] == 0x01 && d[3] == 0x00) return ".ico";
    if (d.Length >= 2 && d[0] == 0xff && d[1] == 0xd8) return ".jpg";
    if (d.Length >= 4 && d[0] == 0x50 && d[1] == 0x4b) return ".zip";
    if (d.Length >= 1 && d[0] == (byte)'<') return ".xml";
    return ".bin";
  }
}

/// <summary>A file recovered from a BitRock installer's virtual file system.</summary>
public sealed record BitRockFile(string Name, byte[] Content);
