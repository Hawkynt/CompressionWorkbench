using System.Buffers.Binary;
using System.Text;

namespace FileFormat.Zpaq;

/// <summary>
/// Reads ZPAQ archives in both layouts the format defines: the journaling layout of
/// <c>zpaq</c> 6.x/7.x (versioned transactions of <c>c</c>/<c>d</c>/<c>h</c>/<c>i</c> blocks with
/// deduplicated, SHA-1 checked fragments) and the plain streaming layout (one file per
/// block or segment run).
/// </summary>
/// <remarks>
/// The archive walk follows <c>Jidac::read_archive</c> of zpaq 7.15 (Matt Mahoney, public
/// domain); block decoding is the libzpaq conversion in this namespace. Journaling block
/// names are <c>jDC&lt;YYYYMMDDHHMMSS&gt;&lt;type&gt;&lt;number:10&gt;</c> and their segment comments
/// end in <c>"jDC\x01"</c>. Encrypted archives (AES-256 with a 32-byte salt) are refused.
/// </remarks>
public sealed class ZpaqReader : IDisposable {
  private static ReadOnlySpan<byte> JournalMarker => "jDC\x01"u8;

  private readonly Stream _stream;
  private readonly bool _leaveOpen;
  private readonly List<Fragment> _ht = [new([], -2)];
  private readonly List<Block> _blocks = [];
  private readonly List<VersionInfo> _versions = [];
  private readonly List<IndexRecord> _log = [];
  private readonly List<ZpaqEntry> _entries = [];
  private int _cachedBlock = -1;
  private byte[] _cachedData = [];
  private bool _disposed;

  private sealed record Fragment(byte[] Sha1, int Usize);

  private sealed class Block(uint start, long offset) {
    public uint Start = start;
    public long Offset = offset;
    public long Usize = -1;
    public long Bsize;
    public uint Frags;
  }

  private sealed record VersionInfo(long Date, long CompressedSize);

  private sealed record IndexRecord(int Version, string Name, long Date, long Attributes, List<uint> Fragments, List<(long, int)> Segments);

  /// <summary>Opens a ZPAQ archive and reads its index.</summary>
  /// <param name="stream">A readable stream positioned at the start of the archive.</param>
  /// <param name="leaveOpen">Whether to leave <paramref name="stream"/> open on dispose.</param>
  /// <exception cref="InvalidDataException">The data is not a ZPAQ archive.</exception>
  /// <exception cref="NotSupportedException">The archive is encrypted.</exception>
  public ZpaqReader(Stream stream, bool leaveOpen = false) {
    ArgumentNullException.ThrowIfNull(stream);
    if (!stream.CanRead)
      throw new ArgumentException("Stream must be readable.", nameof(stream));
    this._leaveOpen = leaveOpen;
    if (stream.CanSeek)
      this._stream = stream;
    else {
      var copy = new MemoryStream();
      stream.CopyTo(copy);
      copy.Position = 0;
      this._stream = copy;
      if (!leaveOpen)
        stream.Dispose();
      this._leaveOpen = false;
    }
    this.ReadArchive();
    this._entries.AddRange(this.Snapshot(int.MaxValue));
  }

  /// <summary>Files and directories present in the latest version, ordered by name.</summary>
  public IReadOnlyList<ZpaqEntry> Entries => this._entries;

  /// <summary>Number of versions (transactions) in the archive.</summary>
  public int VersionCount => this._versions.Count;

  /// <summary>Number of blocks that failed to decode and were skipped while reading the index.</summary>
  public int Errors { get; private set; }

  /// <summary>UTC date of version <paramref name="version"/> (1-based), or <see langword="null"/> for a streaming archive.</summary>
  public DateTime? GetVersionDate(int version) {
    ArgumentOutOfRangeException.ThrowIfLessThan(version, 1);
    ArgumentOutOfRangeException.ThrowIfGreaterThan(version, this._versions.Count);
    return ZpaqDate.ToDateTime(this._versions[version - 1].Date);
  }

  /// <summary>The archive contents as of version <paramref name="version"/> (1-based).</summary>
  public IReadOnlyList<ZpaqEntry> GetEntries(int version) {
    ArgumentOutOfRangeException.ThrowIfLessThan(version, 1);
    ArgumentOutOfRangeException.ThrowIfGreaterThan(version, this._versions.Count);
    return this.Snapshot(version);
  }

  /// <summary>Returns the entry's content as a read-only stream.</summary>
  public Stream Extract(ZpaqEntry entry) => new MemoryStream(this.ExtractBytes(entry), writable: false);

  /// <summary>Decodes the entry's content, verifying every fragment's (or segment's) SHA-1.</summary>
  /// <exception cref="InvalidDataException">The data is corrupt or a checksum does not match.</exception>
  public byte[] ExtractBytes(ZpaqEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    ObjectDisposedException.ThrowIf(this._disposed, this);
    if (entry.IsDirectory)
      return [];
    return entry.Segments.Count > 0 ? this.ExtractStreaming(entry) : this.ExtractJournaling(entry);
  }

  /// <inheritdoc/>
  public void Dispose() {
    if (this._disposed)
      return;
    this._disposed = true;
    if (!this._leaveOpen)
      this._stream.Dispose();
  }

  // ── index ──────────────────────────────────────────────────────────────────

  private void ReadArchive() {
    var s = this._stream;
    s.Position = 0;
    Span<byte> head = stackalloc byte[4];
    var nr = s.ReadAtLeast(head, 4, throwOnEndOfStream: false);
    if (nr > 0 && !head[..nr].SequenceEqual("7kSt"u8[..nr]) && (nr < 4 || !head[..3].SequenceEqual("zPQ"u8) || head[3] < 1)) {
      if (nr == 4 && head[..3].SequenceEqual("zPQ"u8))
        throw new InvalidDataException("ZPAQ: unsupported block level.");
      throw new InvalidDataException("Not a ZPAQ archive (or an encrypted one, which needs the password-derived key).");
    }
    s.Position = 0;

    var lastFile = "";
    long blockOffset = 0;
    long dataOffset = 0;
    var foundData = false;
    var first = true;
    var done = false;
    var dt = new Dictionary<string, IndexRecord>(StringComparer.Ordinal);

    while (!done) {
      var d = new ZpaqDecompresser(new ZpaqStreamReader(s));
      var jumped = false;
      try {
        while (!jumped && d.FindBlock()) {
          foundData = true;
          var segs = 0;
          var skip = false;
          string? filename;
          while ((filename = d.FindFilename()) != null) {
            if (filename.Length > 0)
              lastFile = filename.Replace('\\', '/');
            var comment = d.ReadComment();

            if (comment.AsSpan().EndsWith(JournalMarker)) {
              if (Encoding.UTF8.GetByteCount(filename) != 28 || !filename.StartsWith("jDC", StringComparison.Ordinal))
                ZpaqError.Throw("bad journaling block name");
              if (skip)
                ZpaqError.Throw("mixed journaling and streaming block");

              long usize = 0;
              for (var i = 0; i < comment.Length && char.IsAsciiDigit((char)comment[i]); ++i) {
                usize = usize * 10 + comment[i] - '0';
                if (usize > 0xffffffff)
                  ZpaqError.Throw("journaling block too big");
              }
              long fdate = 0, num = 0;
              int k;
              for (k = 3; k < 17 && char.IsAsciiDigit(filename[k]); ++k)
                fdate = fdate * 10 + filename[k] - '0';
              if (k != 17 || fdate < 19000000000000L || fdate >= 30000000000000L)
                ZpaqError.Throw("bad date");
              for (k = 18; k < 28 && char.IsAsciiDigit(filename[k]); ++k)
                num = num * 10 + filename[k] - '0';
              if (k != 28 || num > 0xffffffff)
                ZpaqError.Throw("bad fragment");

              var type = filename[17];
              var os = new ZpaqBuffer((int)Math.Min(usize, 1 << 20)) { Limit = usize };
              if (type is 'c' or 'h' or 'i') {
                var sha1 = new ZpaqSha1();
                d.SetOutput(os);
                d.SetSha1(sha1);
                d.Decompress();
                var stored = d.ReadSegmentEnd();
                d.SetOutput(null);
                d.SetSha1(null);
                if (os.Size != usize)
                  ZpaqError.Throw("bad block size");
                if (usize != sha1.Size)
                  ZpaqError.Throw("bad checksum size");
                if (stored != null && !stored.AsSpan().SequenceEqual(sha1.Result()))
                  ZpaqError.Throw("bad checksum");
              } else
                d.ReadSegmentEnd();

              switch (type) {
                case 'c': {
                    if (os.Size < 8)
                      ZpaqError.Throw("c block too small");
                    dataOffset = s.Position + 1 - d.Buffered;
                    var jmp = BinaryPrimitives.ReadInt64LittleEndian(os.Span);
                    if (jmp < 0) {
                      done = true;  // incomplete transaction: roll back to here
                      jumped = true;
                      break;
                    }
                    this._versions.Add(new(fdate, jmp));
                    if (jmp > 0) {
                      s.Position = dataOffset + jmp;
                      jumped = true;
                    }
                    break;
                  }
                case 'h': {
                    if (this._versions.Count == 0)
                      ZpaqError.Throw("fragment table before transaction header");
                    if (os.Size % 24 != 4)
                      ZpaqError.Throw("bad h block size");
                    var n = (uint)((os.Size - 4) / 24);
                    if (num < 1 || num + n > 0xffffffff)
                      ZpaqError.Throw("bad h fragment");
                    var span = os.Span;
                    var bsize = BinaryPrimitives.ReadUInt32LittleEndian(span);
                    var p = 4;
                    for (var i = 0u; i < n; ++i) {
                      if (i == 0)
                        this._blocks.Add(new((uint)num, dataOffset) { Usize = 8, Bsize = bsize, Frags = n });
                      while (this._ht.Count <= num + i)
                        this._ht.Add(new([], -2));
                      var hash = span.Slice(p, 20).ToArray();
                      p += 20;
                      var f = BinaryPrimitives.ReadUInt32LittleEndian(span[p..]);
                      p += 4;
                      if (f > 0x7fffffff)
                        ZpaqError.Throw("fragment too big");
                      this._ht[(int)(num + i)] = new(hash, (int)f);
                      this._blocks[^1].Usize += f + 4u;
                    }
                    dataOffset += bsize;
                    break;
                  }
                case 'i': {
                    if (this._versions.Count == 0)
                      ZpaqError.Throw("index before transaction header");
                    this.ParseIndex(os.Span, dt);
                    break;
                  }
                case 'd':
                  break;
                default:
                  ZpaqError.Throw("unexpected journaling block");
                  break;
              }
              if (jumped)
                break;
            } else {
              // Streaming format: each segment is (a part of) a file.
              if (this._versions.Count == 0)
                this._versions.Add(new(0, -1));
              d.ReadSegmentEnd();
              skip = true;
              var fn = lastFile;
              if (filename.Length > 0 || first || !dt.TryGetValue(fn, out var rec)) {
                rec = new(this._versions.Count, fn, ParseStreamingDate(comment), ParseStreamingAttributes(comment), [], []);
                dt[fn] = rec;
                this._log.Add(rec);
              }
              rec.Segments.Add((blockOffset, segs));
              if (segs == 0 || this._blocks.Count == 0)
                this._blocks.Add(new((uint)this._ht.Count, blockOffset));
              this._ht.Add(new([], -1));
            }
            ++segs;
            first = false;
          }
          if (!jumped)
            blockOffset = s.Position - d.Buffered;
        }
        if (!jumped)
          done = true;
      } catch (InvalidDataException) when (foundData) {
        // Like zpaq: report, then resume scanning for the next block after the bad one.
        ++this.Errors;
        s.Position -= d.Buffered;
      }
    }
    if (s.Length > 0 && !foundData)
      throw new InvalidDataException("ZPAQ: archive contains no data.");
  }

  private void ParseIndex(ReadOnlySpan<byte> s, Dictionary<string, IndexRecord> dt) {
    var p = 0;
    var version = this._versions.Count;
    while (p + 9 <= s.Length) {
      var date = BinaryPrimitives.ReadInt64LittleEndian(s[p..]);
      p += 8;
      var len = s[p..].IndexOf((byte)0);
      if (len < 0)
        ZpaqError.Throw("filename too long");
      if (len > 65535)
        ZpaqError.Throw("filename too long");
      var name = Encoding.UTF8.GetString(s.Slice(p, len));
      p += len + 1;
      long attr = 0;
      var ptr = new List<uint>();
      if (date != 0) {
        if (p + 4 > s.Length)
          ZpaqError.Throw("missing attr");
        var na = BinaryPrimitives.ReadUInt32LittleEndian(s[p..]);
        p += 4;
        if (p + na > s.Length || na > 65535)
          ZpaqError.Throw("attr too long");
        for (var i = 0; i < na; ++i, ++p)
          if (i < 8)
            attr += (long)s[p] << (i * 8);
        if (p + 4 > s.Length)
          ZpaqError.Throw("missing ptr");
        var ni = BinaryPrimitives.ReadUInt32LittleEndian(s[p..]);
        p += 4;
        if (ni > (s.Length - p) / 4u)
          ZpaqError.Throw("ptr list too long");
        for (var i = 0u; i < ni; ++i, p += 4)
          ptr.Add(BinaryPrimitives.ReadUInt32LittleEndian(s[p..]));
      }
      var rec = new IndexRecord(version, name, date, attr, ptr, []);
      dt[name] = rec;
      this._log.Add(rec);
    }
  }

  private static long ParseStreamingDate(byte[] comment) {
    // Streaming comments are "<date> <attr>" with a 14-digit decimal date.
    long date = 0;
    var i = 0;
    for (; i < comment.Length && char.IsAsciiDigit((char)comment[i]); ++i)
      date = date * 10 + comment[i] - '0';
    return i == 14 ? date : 0;
  }

  private static long ParseStreamingAttributes(byte[] comment) {
    var sp = Array.IndexOf(comment, (byte)' ');
    if (sp < 0 || sp + 1 >= comment.Length)
      return 0;
    long attr = comment[sp + 1];
    long value = 0;
    for (var i = sp + 2; i < comment.Length && char.IsAsciiDigit((char)comment[i]); ++i)
      value = value * 10 + comment[i] - '0';
    return attr | value << 8;
  }

  private List<ZpaqEntry> Snapshot(int version) {
    var state = new SortedDictionary<string, IndexRecord>(StringComparer.Ordinal);
    foreach (var rec in this._log) {
      if (rec.Version > version)
        break;
      state[rec.Name] = rec;
    }
    var result = new List<ZpaqEntry>();
    foreach (var rec in state.Values) {
      var streaming = rec.Segments.Count > 0;
      if (!streaming && rec.Date == 0)
        continue;  // deleted in this version
      long size = 0;
      long csize = 0;
      if (streaming)
        size = -1;
      else
        foreach (var j in rec.Fragments) {
          if (j == 0 || j >= this._ht.Count)
            continue;
          var frag = this._ht[(int)j];
          if (frag.Usize < 0) {
            size = -1;
            break;
          }
          size += frag.Usize;
          var b = this.FindBlock(j);
          if (b >= 0 && this._blocks[b].Usize > 0)
            csize += this._blocks[b].Bsize * frag.Usize / this._blocks[b].Usize;
        }
      result.Add(new(rec.Name, size, csize, ZpaqDate.ToDateTime(rec.Date), rec.Version, rec.Attributes, rec.Fragments, rec.Segments));
    }
    return result;
  }

  // ── extraction ─────────────────────────────────────────────────────────────

  private int FindBlock(uint fragment) {
    int lo = 0, hi = this._blocks.Count - 1;
    while (lo <= hi) {
      var mid = (lo + hi) >>> 1;
      var b = this._blocks[mid];
      if (fragment < b.Start)
        hi = mid - 1;
      else if (fragment >= b.Start + b.Frags)
        lo = mid + 1;
      else
        return mid;
    }
    return -1;
  }

  private byte[] ExtractJournaling(ZpaqEntry entry) {
    if (entry.Size < 0)
      ZpaqError.Throw("file references fragments of unknown size");
    var output = new byte[entry.Size];
    var pos = 0;
    foreach (var j in entry.Fragments) {
      if (j == 0 || j >= this._ht.Count)
        ZpaqError.Throw("bad fragment pointer");
      var frag = this._ht[(int)j];
      if (frag.Usize == 0)
        continue;
      var bi = this.FindBlock(j);
      if (bi < 0)
        ZpaqError.Throw($"fragment {j} is in no data block");
      var data = this.DecodeBlock(bi);
      var b = this._blocks[bi];
      long offset = 0;
      for (var f = b.Start; f < j; ++f)
        offset += this._ht[(int)f].Usize;
      data.AsSpan((int)offset, frag.Usize).CopyTo(output.AsSpan(pos));
      pos += frag.Usize;
    }
    return output;
  }

  private byte[] DecodeBlock(int bi) {
    if (bi == this._cachedBlock)
      return this._cachedData;
    var b = this._blocks[bi];
    long outputSize = 0;
    for (var f = b.Start; f < b.Start + b.Frags; ++f)
      outputSize += this._ht[(int)f].Usize;

    this._stream.Position = b.Offset;
    var d = new ZpaqDecompresser(new ZpaqStreamReader(this._stream));
    var output = new ZpaqBuffer((int)Math.Min(b.Usize, 1 << 24)) { Limit = b.Usize };
    d.SetOutput(output);
    if (!d.FindBlock())
      ZpaqError.Throw("archive block not found");
    while (d.FindFilename() != null) {
      d.ReadComment();
      d.Decompress();
      if (output.Size >= outputSize)
        break;
      d.ReadSegmentEnd();
    }
    if (output.Size < outputSize)
      ZpaqError.Throw("unexpected end of compressed data");

    var data = output.Data;
    long q = 0;
    for (var f = b.Start; f < b.Start + b.Frags; ++f) {
      var frag = this._ht[(int)f];
      var hash = Compression.Core.Checksums.Sha1.Compute(data.AsSpan((int)q, frag.Usize));
      if (!hash.AsSpan().SequenceEqual(frag.Sha1))
        ZpaqError.Throw($"fragment {f} checksum failed");
      q += frag.Usize;
    }
    this._cachedBlock = bi;
    this._cachedData = data;
    return data;
  }

  private byte[] ExtractStreaming(ZpaqEntry entry) {
    var output = new ZpaqBuffer();
    ZpaqDecompresser? d = null;
    long openBlock = -1;
    var nextSegment = 0;
    foreach (var (blockOffset, segment) in entry.Segments) {
      if (d == null || openBlock != blockOffset || segment < nextSegment) {
        this._stream.Position = blockOffset;
        d = new(new ZpaqStreamReader(this._stream));
        if (!d.FindBlock())
          ZpaqError.Throw("archive block not found");
        openBlock = blockOffset;
        nextSegment = 0;
      }
      while (nextSegment <= segment) {
        if (d.FindFilename() == null)
          ZpaqError.Throw("missing segment");
        d.ReadComment();
        var target = nextSegment == segment;
        var sha1 = target ? new ZpaqSha1() : null;
        d.SetOutput(target ? output : null);
        d.SetSha1(sha1);
        d.Decompress();
        var stored = d.ReadSegmentEnd();
        d.SetOutput(null);
        d.SetSha1(null);
        if (sha1 != null && stored != null && !stored.AsSpan().SequenceEqual(sha1.Result()))
          ZpaqError.Throw("segment checksum failed");
        ++nextSegment;
      }
    }
    return output.ToArray();
  }
}

/// <summary>Conversion between ZPAQ's decimal dates (<c>YYYYMMDDHHMMSS</c>, UTC) and <see cref="DateTime"/>.</summary>
internal static class ZpaqDate {
  /// <summary>Encodes a UTC time as a decimal date.</summary>
  public static long FromDateTime(DateTime time) {
    var t = time.Kind == DateTimeKind.Local ? time.ToUniversalTime() : time;
    return t.Year * 10000000000L + t.Month * 100000000L + t.Day * 1000000L + t.Hour * 10000L + t.Minute * 100L + t.Second;
  }

  /// <summary>Decodes a decimal date, or <see langword="null"/> when it is not a valid date.</summary>
  public static DateTime? ToDateTime(long date) {
    if (date <= 0)
      return null;
    var year = (int)(date / 10000000000L % 10000);
    var month = (int)(date / 100000000 % 100);
    var day = (int)(date / 1000000 % 100);
    var hour = (int)(date / 10000 % 100);
    var minute = (int)(date / 100 % 100);
    var second = (int)(date % 100);
    try {
      return new DateTime(year, month, day, hour, minute, second, DateTimeKind.Utc);
    } catch (ArgumentOutOfRangeException) {
      return null;
    }
  }
}
