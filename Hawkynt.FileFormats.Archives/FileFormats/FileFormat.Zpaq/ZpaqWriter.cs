using System.Text;

namespace FileFormat.Zpaq;

/// <summary>
/// Writes a ZPAQ journaling archive holding one version (transaction): the <c>c</c> header,
/// the deduplicated and compressed <c>d</c> data blocks, their <c>h</c> fragment tables and the
/// <c>i</c> index, exactly as <c>zpaq add</c> lays them out.
/// </summary>
/// <remarks>
/// <para>
/// Follows <c>Jidac::add</c> of zpaq 7.15 (Matt Mahoney, public domain): files are split into
/// content-defined fragments with the order-1 rolling hash, identical fragments are stored
/// once, fragments are packed into blocks (16 MiB at method 1, 64 MiB at 2..5) that end with
/// the fragment-size list, and each block's method is chosen from the analysed redundancy
/// and text/x86 hints. The whole archive is produced when the writer is disposed.
/// </para>
/// <para>
/// <see cref="Method"/> accepts zpaq's <c>-method</c> values: <c>0</c>..<c>5</c> with an
/// optional block-size digit (<c>"14"</c>, <c>"36"</c>), or an explicit <c>x</c> method string.
/// </para>
/// </remarks>
public sealed class ZpaqWriter : IDisposable {
  private const int OrderOneTables = 4;
  private const int Fragment = 6;

  // 32768/((i+1)*204), zpaq 7.15 Jidac::add(); entries past the 160 listed are zero.
  private static readonly byte[] AnalysisDivisor = BuildAnalysisDivisor();

  private readonly Stream _stream;
  private readonly bool _leaveOpen;
  private readonly Dictionary<string, PendingEntry> _entries = new(StringComparer.Ordinal);
  private bool _disposed;

  private sealed class PendingEntry(string name, long date, long attr, byte[]? data) {
    public readonly string Name = name;
    public readonly long Date = date;
    public readonly long Attr = attr;
    public readonly byte[]? Data = data;
    public readonly List<uint> Ptr = [];
    public ulong SortKey;
  }

  /// <summary>Creates a writer that emits the archive to <paramref name="stream"/> on dispose.</summary>
  /// <param name="stream">Writable destination.</param>
  /// <param name="leaveOpen">Whether to leave <paramref name="stream"/> open on dispose.</param>
  /// <param name="method">zpaq compression method; <c>"1"</c> is zpaq's default.</param>
  public ZpaqWriter(Stream stream, bool leaveOpen = false, string method = "1") {
    ArgumentNullException.ThrowIfNull(stream);
    ArgumentException.ThrowIfNullOrEmpty(method);
    if (!stream.CanWrite)
      throw new ArgumentException("Stream must be writable.", nameof(stream));
    if (!"0123456789x".Contains(method[0]))
      throw new ArgumentException("Method must begin with 0..5 or x.", nameof(method));
    this._stream = stream;
    this._leaveOpen = leaveOpen;
    this.Method = method;
  }

  /// <summary>The zpaq compression method used for the data blocks.</summary>
  public string Method { get; }

  /// <summary>Date of the transaction; defaults to the time of disposal.</summary>
  public DateTime? TransactionDate { get; set; }

  /// <summary>Adds (or replaces) a file.</summary>
  public void AddFile(string fileName, byte[] data, DateTime? lastModified = null) {
    ObjectDisposedException.ThrowIf(this._disposed, this);
    ArgumentNullException.ThrowIfNull(fileName);
    ArgumentNullException.ThrowIfNull(data);
    var name = fileName.Replace('\\', '/').TrimStart('/');
    ArgumentException.ThrowIfNullOrEmpty(name, nameof(fileName));
    if (name.EndsWith('/'))
      throw new ArgumentException("A file name cannot end with '/'.", nameof(fileName));
    this._entries[name] = new(name, this.DateOf(lastModified), 'w' | 0x20L << 8, data);
  }

  /// <summary>Adds a directory entry.</summary>
  public void AddDirectory(string dirName, DateTime? lastModified = null) {
    ObjectDisposedException.ThrowIf(this._disposed, this);
    ArgumentNullException.ThrowIfNull(dirName);
    var name = dirName.Replace('\\', '/').TrimStart('/');
    ArgumentException.ThrowIfNullOrEmpty(name, nameof(dirName));
    if (!name.EndsWith('/'))
      name += '/';
    this._entries[name] = new(name, this.DateOf(lastModified), 'w' | 0x10L << 8, null);
  }

  /// <inheritdoc/>
  public void Dispose() {
    if (this._disposed)
      return;
    this._disposed = true;
    try {
      if (this._entries.Count > 0)
        this.WriteTransaction();
      this._stream.Flush();
    } finally {
      if (!this._leaveOpen)
        this._stream.Dispose();
    }
  }

  private long DateOf(DateTime? time) => ZpaqDate.FromDateTime(time ?? this.TransactionDate ?? DateTime.UtcNow);

  private void WriteTransaction() {
    var date = ZpaqDate.FromDateTime(this.TransactionDate ?? DateTime.UtcNow);
    if (date is < 19000000000000L or >= 30000000000000L)
      throw new InvalidOperationException("ZPAQ dates must lie in the years 1900..2999.");

    var method = this.Method;
    if (method.Length == 1)
      method += method[0] is >= '2' and <= '9' ? "6" : "4";
    var logBlockSize = 20 + AtoiPrefix(method, 1);
    if (logBlockSize is < 20 or > 31)
      throw new InvalidOperationException("ZPAQ block size must be 0..11.");
    var blocksize = (uint)((1L << logBlockSize) - 4096);
    var maxFragment = (8128u << Fragment) > blocksize - 12 ? blocksize - 12 : 8128u << Fragment;
    var minFragment = (64u << Fragment) > maxFragment ? maxFragment : 64u << Fragment;

    // Files to compress, sorted like zpaq: by extension (first 5 bytes), then descending size.
    var vf = new List<PendingEntry>();
    foreach (var e in this._entries.Values) {
      if (e.Data == null)
        continue;
      ulong key = 0;
      var sp = 0;
      foreach (var b in Encoding.UTF8.GetBytes(e.Name)) {
        ulong c = b;
        if (c is >= 'A' and <= 'Z')
          c += 'a' - 'A';
        if (c == '/') {
          sp = 0;
          key = 0;
        } else if (c == '.') {
          sp = 8;
          key = 0;
        } else if (sp > 3)
          key += c << (--sp * 8);
      }
      var s = (long)e.Data.Length >> 14;
      if (s >= 1 << 24)
        s = (1 << 24) - 1;
      key += (ulong)((1 << 24) - s - 1);
      e.SortKey = key;
      vf.Add(e);
    }
    vf.Sort((a, b) => a.SortKey != b.SortKey ? a.SortKey.CompareTo(b.SortKey) : CompareUtf8(a.Name, b.Name));

    // Fragment table: index 0 is unused, IDs start at 1.
    var ht = new List<(byte[] Sha1, int Usize)> { ([], -2) };
    var htIndex = new Dictionary<string, uint>(StringComparer.Ordinal);
    const uint htsize = 1;

    var dataBlocks = new MemoryStream();
    var csize = new List<int>();
    var blocklist = new List<int>();
    var sb = new ZpaqBuffer();
    uint frags = 0, redundancy = 0, text = 0, exe = 0;
    var o1prev = new byte[OrderOneTables * 256];
    var fragbuf = new byte[maxFragment];
    var dataWriter = new ZpaqStreamWriter(dataBlocks);

    for (var fi = 0; fi <= vf.Count; ++fi) {
      var data = fi < vf.Count ? vf[fi].Data! : null;
      var pos = 0;
      for (var fj = 0; ; ++fj) {
        long sz = 0;
        uint hits = 0;
        var c = -1;
        uint htptr = 0;
        byte[] sha1Result = [];
        var o1 = new byte[256];
        if (data != null) {
          var c1 = 0;
          var h = 0u;
          while (true) {
            c = pos < data.Length ? data[pos++] : -1;
            if (c != -1) {
              if (c == o1[c1]) {
                h = (h + (uint)c + 1) * 314159265u;
                ++hits;
              } else
                h = (h + (uint)c + 1) * 271828182u;
              o1[c1] = (byte)c;
              c1 = c;
              fragbuf[sz++] = (byte)c;
            }
            if (c == -1 || sz >= maxFragment || (h < 1u << (22 - Fragment) && sz >= minFragment))
              break;
          }
          sha1Result = Compression.Core.Checksums.Sha1.Compute(fragbuf.AsSpan(0, (int)sz));
          htptr = htIndex.GetValueOrDefault(Convert.ToHexString(sha1Result));
        }

        if (htptr == 0) {
          var text1 = 0;
          var exe1 = 0;
          var h1 = sz;
          var o1ct = new byte[256];
          for (var i = 0; i < 256; ++i) {
            if (o1ct[o1[i]] < 255)
              h1 -= (sz * AnalysisDivisor[o1ct[o1[i]]++]) >> 15;
            if (o1[i] == ' ' && (char.IsAsciiLetterOrDigit((char)i) || i == '.' || i == ','))
              ++text1;
            if (o1[i] != 0 && (i < 9 || i == 11 || i == 12 || (i >= 14 && i <= 31) || i >= 240))
              --text1;
            if (i is >= 192 and < 240 && o1[i] != 0 && (o1[i] < 128 || o1[i] >= 192))
              --text1;
            if (o1[i] == 139)
              ++exe1;
          }
          text1 = text1 >= 3 ? 1 : 0;
          exe1 = exe1 >= 5 ? 1 : 0;
          if (sz > 0)
            h1 = h1 * h1 / sz;
          var h2 = (uint)h1;
          if (h2 > hits)
            hits = h2;
          h2 = (uint)(o1ct[0] * sz / 256);
          if (h2 > hits)
            hits = h2;
          h2 = 0;
          for (var i = 0; i < 256 * OrderOneTables; ++i)
            if (o1prev[i] == o1[i & 255])
              ++h2;
          h2 = (uint)(h2 * sz / (256 * OrderOneTables));
          if (h2 > hits)
            hits = h2;
          if (hits > sz)
            hits = (uint)sz;

          var newblock = false;
          if (frags > 0 && fj == 0 && fi < vf.Count) {
            long esize = vf[fi].Data!.Length;
            var newsize = sb.Size + esize + (esize >> 14) + 4096 + frags * 4L;
            if (newsize > blocksize / 4 && redundancy < sb.Size / 128)
              newblock = true;
            if (newblock) {
              var ct = 0u;
              for (var i = 0; i < 256 * OrderOneTables; ++i)
                if (o1prev[i] != 0 && o1prev[i] == o1[i & 255])
                  ++ct;
              if (ct > OrderOneTables * 2)
                newblock = false;
            }
            if (newsize >= blocksize)
              newblock = true;
          }
          if (sb.Size + sz + 80 + frags * 4L >= blocksize)
            newblock = true;
          if (fi == vf.Count)
            newblock = true;
          if (frags < 1)
            newblock = false;

          if (newblock) {
            for (var i = ht.Count - (int)frags; i < ht.Count; ++i)
              PutLe(sb, (ulong)ht[i].Usize, 4);
            PutLe(sb, 0, 4);
            PutLe(sb, frags, 4);
            var m = method;
            if (char.IsAsciiDigit(method[0]))
              m += "," + ZpaqMethods.Itos(redundancy / (sb.Size / 256 + 1)) + "," + ZpaqMethods.Itos((exe > frags ? 2 : 0) + (text > frags ? 1 : 0));
            var fn = "jDC" + ZpaqMethods.Itos(date, 14) + "d" + ZpaqMethods.Itos(ht.Count - frags, 10);
            var before = dataBlocks.Length;
            ZpaqMethods.CompressBlock(sb, dataWriter, m, fn, [.. JournalComment]);
            csize.Add((int)(dataBlocks.Length - before));
            sb = new();
            blocklist.Add(ht.Count - (int)frags);
            frags = redundancy = text = exe = 0;
            Array.Clear(o1prev);
          }

          sb.Write(fragbuf.AsSpan(0, (int)sz));
          ++frags;
          redundancy += hits;
          exe += (uint)exe1 * 4;
          text += (uint)text1 * 2;
          if (sz >= minFragment) {
            Array.Copy(o1prev, 256, o1prev, 0, 256 * (OrderOneTables - 1));
            Array.Copy(o1, 0, o1prev, 256 * (OrderOneTables - 1), 256);
          }
        }

        if (data != null) {
          if (htptr == 0) {
            htptr = (uint)ht.Count;
            ht.Add((sha1Result, (int)sz));
            htIndex.TryAdd(Convert.ToHexString(sha1Result), htptr);
          }
          vf[fi].Ptr.Add(htptr);
        }
        if (c == -1)
          break;
      }
    }

    var output = new ZpaqStreamWriter(this._stream);

    // Transaction header: the size of the data blocks that follow it.
    var header = new ZpaqBuffer();
    PutLe(header, (ulong)dataBlocks.Length, 8);
    ZpaqMethods.CompressBlock(header, output, "0", "jDC" + ZpaqMethods.Itos(date, 14) + "c" + ZpaqMethods.Itos(htsize, 10), [.. JournalComment]);

    dataBlocks.Position = 0;
    dataBlocks.CopyTo(this._stream);

    // Fragment tables, one per data block.
    blocklist.Add(ht.Count);
    for (var i = 0; i < csize.Count; ++i) {
      if (blocklist[i] >= blocklist[i + 1])
        continue;
      var table = new ZpaqBuffer();
      PutLe(table, (ulong)csize[i], 4);
      for (var j = blocklist[i]; j < blocklist[i + 1]; ++j) {
        table.Write(ht[j].Sha1);
        PutLe(table, (ulong)ht[j].Usize, 4);
      }
      ZpaqMethods.CompressBlock(table, output, "0", "jDC" + ZpaqMethods.Itos(date, 14) + "h" + ZpaqMethods.Itos(blocklist[i], 10), [.. JournalComment]);
    }

    // Index, in byte order of the names like zpaq's std::map.
    var index = new ZpaqBuffer();
    var dtcount = 0;
    var ordered = this._entries.Values.OrderBy(e => e.Name, Comparer<string>.Create(CompareUtf8)).ToList();
    for (var k = 0; ; ++k) {
      if (k < ordered.Count) {
        var e = ordered[k];
        PutLe(index, (ulong)e.Date, 8);
        index.Write(Encoding.UTF8.GetBytes(e.Name));
        index.Put(0);
        PutLe(index, 5, 4);
        PutLe(index, (ulong)e.Attr, 5);
        PutLe(index, (ulong)e.Ptr.Count, 4);
        foreach (var p in e.Ptr)
          PutLe(index, p, 4);
      }
      if (index.Size > 16000 || (index.Size > 0 && k >= ordered.Count)) {
        ZpaqMethods.CompressBlock(index, output, "1", "jDC" + ZpaqMethods.Itos(date) + "i" + ZpaqMethods.Itos(++dtcount, 10), [.. JournalComment]);
        index = new();
      }
      if (k >= ordered.Count)
        break;
    }
  }

  private static ReadOnlySpan<byte> JournalComment => "jDC\x01"u8;

  private static void PutLe(ZpaqBuffer sb, ulong x, int n) {
    for (; n > 0; --n) {
      sb.Put((int)(x & 255));
      x >>= 8;
    }
  }

  private static int AtoiPrefix(string s, int i) {
    var r = 0;
    for (; i < s.Length && char.IsAsciiDigit(s[i]); ++i)
      r = r * 10 + s[i] - '0';
    return r;
  }

  private static int CompareUtf8(string? a, string? b) =>
    Encoding.UTF8.GetBytes(a ?? "").AsSpan().SequenceCompareTo(Encoding.UTF8.GetBytes(b ?? ""));

  private static byte[] BuildAnalysisDivisor() {
    byte[] listed = [
      160, 80, 53, 40, 32, 26, 22, 20, 17, 16, 14, 13, 12, 11, 10, 10,
      9, 8, 8, 8, 7, 7, 6, 6, 6, 6, 5, 5, 5, 5, 5, 5,
      4, 4, 4, 4, 4, 4, 4, 4, 3, 3, 3, 3, 3, 3, 3, 3,
      3, 3, 3, 3, 3, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2,
      2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2,
      1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1,
      1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1,
      1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1,
      1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1,
      1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1,
    ];
    var t = new byte[256];
    listed.CopyTo(t, 0);
    return t;
  }
}
