namespace FileSystem.Iso;

/// <summary>
/// The ISO 9660 file system of one optical-disc track (CDI, MDF, NRG, …) read through
/// <see cref="IsoReader"/>: the same Rock Ridge, Joliet, multi-extent, interleaved and
/// zisofs handling a plain ISO gets, whatever the container's sector framing.
/// </summary>
/// <remarks>
/// The container supplies the 2 048-byte user data of each logical sector (null for one it
/// does not have — past the track, or in a gap between sessions). Such a sector reads as
/// zeros so the tree can still be listed, and extracting a file that lies on one fails
/// instead of returning zeros.
/// </remarks>
internal sealed class IsoTrackView : IDisposable {
  private const int SectorSize = 2048;
  private readonly SectorStream _sectors;

  /// <summary>The reader over the track's user data.</summary>
  public IsoReader Reader { get; }

  private IsoTrackView(SectorStream sectors, IsoReader reader) {
    this._sectors = sectors;
    this.Reader = reader;
  }

  /// <summary>
  /// Opens the track's file system; null when the user data does not hold one that
  /// <see cref="IsoReader"/> accepts, so the caller can fall back to its own walk.
  /// </summary>
  public static IsoTrackView? TryOpen(Func<long, byte[]?> readUserData, long sectorCount) {
    ArgumentNullException.ThrowIfNull(readUserData);
    if (sectorCount <= 16) return null;
    var sectors = new SectorStream(readUserData, sectorCount);
    try {
      return new IsoTrackView(sectors, new IsoReader(sectors, leaveOpen: true));
    } catch (Exception e) when (e is InvalidDataException or IOException or ArgumentException or OverflowException) {
      return null;
    }
  }

  /// <summary>The last segment of an entry's path.</summary>
  public static string LeafName(IsoEntry entry) {
    var slash = entry.Name.LastIndexOf('/');
    return slash < 0 ? entry.Name : entry.Name[(slash + 1)..];
  }

  /// <summary>The first logical sector of an entry's data.</summary>
  public static int StartSector(IsoEntry entry) => (int)Math.Min(int.MaxValue, entry.FirstSector);

  /// <summary>
  /// The entry's bytes; <see cref="InvalidDataException"/> when any of its sectors is one the
  /// container does not have.
  /// </summary>
  public byte[] Extract(IsoEntry entry, string format) {
    ArgumentNullException.ThrowIfNull(entry);
    var bytes = this.Reader.Extract(entry);
    foreach (var segment in IsoReader.Segments(entry)) {
      if (segment.Length <= 0) continue;
      var first = segment.PhysicalOffset / SectorSize;
      var last = (segment.PhysicalOffset + segment.Length - 1) / SectorSize;
      for (var lba = first; lba <= last; ++lba)
        if (this._sectors.IsMissing(lba))
          throw new InvalidDataException($"{format}: '{entry.Name}' lies on sector {lba}, which is outside the disc's data tracks.");
    }
    return bytes;
  }

  public void Dispose() => this.Reader.Dispose();

  /// <summary>A read-only stream of the logical sectors' user data.</summary>
  private sealed class SectorStream(Func<long, byte[]?> readUserData, long sectorCount) : Stream {
    private readonly HashSet<long> _missing = [];
    private long _cachedLba = -1;
    private byte[] _cached = new byte[SectorSize];
    private long _position;

    public bool IsMissing(long lba) {
      if (lba < 0 || lba >= sectorCount) return true;
      this.Load(lba);
      return this._missing.Contains(lba);
    }

    private void Load(long lba) {
      if (lba == this._cachedLba) return;
      var data = readUserData(lba);
      if (data is not { Length: >= SectorSize }) {
        this._missing.Add(lba);
        data = new byte[SectorSize];
      }
      this._cached = data;
      this._cachedLba = lba;
    }

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => sectorCount * SectorSize;

    public override long Position {
      get => this._position;
      set => this._position = value >= 0 ? value : throw new ArgumentOutOfRangeException(nameof(value));
    }

    public override int Read(byte[] buffer, int offset, int count) => this.Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer) {
      var total = (int)Math.Max(0, Math.Min(buffer.Length, this.Length - this._position));
      var done = 0;
      while (done < total) {
        var lba = this._position / SectorSize;
        var within = (int)(this._position % SectorSize);
        var chunk = Math.Min(SectorSize - within, total - done);
        this.Load(lba);
        this._cached.AsSpan(within, chunk).CopyTo(buffer[done..]);
        done += chunk;
        this._position += chunk;
      }
      return done;
    }

    public override long Seek(long offset, SeekOrigin origin) => this.Position = origin switch {
      SeekOrigin.Begin => offset,
      SeekOrigin.Current => this._position + offset,
      _ => this.Length + offset,
    };

    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
  }
}
