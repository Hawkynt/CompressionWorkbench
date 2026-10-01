using System.Globalization;
using System.Text.RegularExpressions;

namespace FileFormat.Dar;

/// <summary>
/// The slices of one DAR archive glued into the archive-level byte stream: every slice's bytes
/// between its slice header and its one-byte trailer, in slice order (<c>docs/DAR-ON-DISK.md</c>,
/// section 3). A slice handed in as a <see cref="FileStream"/> named <c>basename.N.dar</c> brings
/// its siblings from the same directory; any other stream is taken as the whole archive.
/// </summary>
internal sealed partial class DarSliceSet : IDisposable {
  private const int HeaderReadLimit = 64 * 1024;
  private const int MaxSlices = 100_000;

  private readonly List<Stream> _owned;

  private DarSliceSet(IReadOnlyList<DarSliceHeader> headers, Stream archive, List<Stream> owned) {
    this.Headers = headers;
    this.Archive = archive;
    this._owned = owned;
  }

  /// <summary>The slice headers, first slice first.</summary>
  public IReadOnlyList<DarSliceHeader> Headers { get; }

  /// <summary>The concatenated archive-level stream (read-only, seekable).</summary>
  public Stream Archive { get; }

  [GeneratedRegex(@"^(?<base>.+)\.(?<n>[0-9]+)\.dar$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
  private static partial Regex SliceName();

  /// <summary>Opens the slice set <paramref name="stream"/> belongs to.</summary>
  /// <exception cref="InvalidDataException">A slice header is unreadable, or slices are missing or
  /// belong to another archive.</exception>
  public static DarSliceSet Open(Stream stream) {
    var owned = new List<Stream>();
    try {
      var slices = new List<Stream>();
      var match = stream is FileStream { Name: { Length: > 0 } name } ? SliceName().Match(Path.GetFileName(name)) : Match.Empty;
      if (match.Success && int.TryParse(match.Groups["n"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var own) && own >= 1) {
        var directory = Path.GetDirectoryName(Path.GetFullPath(((FileStream)stream).Name))!;
        var baseName = match.Groups["base"].Value;
        var extension = Path.GetFileName(((FileStream)stream).Name)[^4..];
        for (var n = 1; n <= MaxSlices; ++n) {
          Stream slice;
          if (n == own)
            slice = stream;
          else {
            var path = Path.Combine(directory, $"{baseName}.{n.ToString(CultureInfo.InvariantCulture)}{extension}");
            if (!File.Exists(path)) {
              if (n > own && slices.Count > 0 && LastOf(slices[^1]))
                break;
              throw new InvalidDataException($"DAR slice {n} ({Path.GetFileName(path)}) is missing.");
            }
            slice = File.OpenRead(path);
            owned.Add(slice);
          }
          slices.Add(slice);
          if (n >= own && LastOf(slice))
            break;
        }
      } else
        slices.Add(stream);

      var headers = new List<DarSliceHeader>(slices.Count);
      var segments = new List<(Stream Source, long Offset, long Length)>(slices.Count);
      for (var i = 0; i < slices.Count; ++i) {
        var header = ReadHeader(slices[i]);
        if (!header.IsValid)
          throw new InvalidDataException($"DAR slice {i + 1}: {header.Problem}.");
        if (header.Extension != 'T')
          throw new NotSupportedException($"DAR slice {i + 1} uses the pre-format-8 header extension '{header.Extension}'.");
        if (i > 0 && !header.InternalName.AsSpan().SequenceEqual(headers[0].InternalName))
          throw new InvalidDataException($"DAR slice {i + 1} belongs to another archive (internal name differs).");
        var last = header.IsLastSlice;
        if (last == true && i != slices.Count - 1)
          throw new InvalidDataException($"DAR slice {i + 1} says it is the last, yet slice {i + 2} follows.");
        if (i == slices.Count - 1 && last != true)
          throw new InvalidDataException(slices.Count == 1
            ? "This DAR slice is not the last of its archive and its sibling slices are not available."
            : $"DAR slice {i + 1} is not the last slice and slice {i + 2} is missing.");
        var payload = slices[i].Length - header.HeaderLength!.Value - 1;
        if (payload < 0)
          throw new InvalidDataException($"DAR slice {i + 1} is shorter than its own header.");
        headers.Add(header);
        segments.Add((slices[i], header.HeaderLength.Value, payload));
      }
      var archive = new ConcatenatedStream(segments);
      return new DarSliceSet(headers, archive, owned);
    } catch {
      foreach (var s in owned)
        s.Dispose();
      throw;
    }
  }

  private static bool LastOf(Stream slice) => ReadHeader(slice).IsLastSlice == true;

  /// <summary>Reads the slice header from the start of <paramref name="slice"/> and its trailer
  /// from the last byte.</summary>
  public static DarSliceHeader ReadHeader(Stream slice) {
    var length = slice.Length;
    var head = new byte[(int)Math.Min(length, HeaderReadLimit)];
    slice.Position = 0;
    slice.ReadExactly(head);
    var lastByte = -1;
    if (length > 0) {
      slice.Position = length - 1;
      lastByte = slice.ReadByte();
    }
    return DarSliceHeader.Parse(head, lastByte);
  }

  /// <inheritdoc/>
  public void Dispose() {
    foreach (var s in this._owned)
      s.Dispose();
  }

  /// <summary>A read-only seekable view over byte ranges of several streams, back to back.</summary>
  private sealed class ConcatenatedStream : Stream {
    private readonly (Stream Source, long Offset, long Length)[] _segments;
    private readonly long[] _starts;
    private long _position;

    public ConcatenatedStream(List<(Stream Source, long Offset, long Length)> segments) {
      this._segments = [.. segments];
      this._starts = new long[segments.Count + 1];
      for (var i = 0; i < segments.Count; ++i)
        this._starts[i + 1] = this._starts[i] + segments[i].Length;
    }

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => this._starts[^1];

    public override long Position {
      get => this._position;
      set => this._position = value >= 0 ? value : throw new ArgumentOutOfRangeException(nameof(value));
    }

    public override int Read(byte[] buffer, int offset, int count) => this.Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer) {
      var total = 0;
      while (buffer.Length > 0 && this._position < this.Length) {
        var index = Array.BinarySearch(this._starts, this._position);
        if (index < 0)
          index = ~index - 1;
        while (index < this._segments.Length - 1 && this._starts[index + 1] <= this._position)
          ++index;
        var (source, offset, length) = this._segments[index];
        var within = this._position - this._starts[index];
        var chunk = (int)Math.Min(buffer.Length, length - within);
        source.Position = offset + within;
        var read = source.Read(buffer[..chunk]);
        if (read <= 0)
          throw new EndOfStreamException("A DAR slice ended earlier than its recorded length.");
        buffer = buffer[read..];
        this._position += read;
        total += read;
      }
      return total;
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
