using Compression.Core.Streams;

namespace FileFormat.LiveMaker;

/// <summary>
/// A LiveMaker archive together with the files a split archive keeps beside it: the directory in
/// <c>name.ext</c> (or at the head of <c>name.dat</c>), the data in <c>name.dat</c> followed by
/// <c>name.001</c>, <c>name.002</c>, ... joined in order.
/// </summary>
/// <remarks>
/// The layout follows GARbro's <c>ArcFormats/LiveMaker/ArcVF.cs</c> (MIT, see
/// THIRD-PARTY-NOTICE.GARbro.txt), which opens a set through its <c>.dat</c> file and joins the
/// numbered parts; pylivemaker writes such sets with the parts cut at 1 GiB, which joining reads the
/// same. Sets are found next to an archive opened from a file; a stream without a file name is read
/// on its own.
/// </remarks>
internal sealed class LiveMakerArchiveSet : IDisposable {
  private readonly List<Stream> _owned;

  private LiveMakerArchiveSet(LiveMakerReader reader, List<Stream> owned) {
    this.Reader = reader;
    this._owned = owned;
  }

  /// <summary>The reader over the whole set.</summary>
  public LiveMakerReader Reader { get; }

  /// <summary>
  /// Opens the archive in <paramref name="stream"/> with the rest of its set when it is a file:
  /// a <c>.ext</c> directory reads its <c>.dat</c> and parts, a <c>.dat</c> without a directory
  /// reads its <c>.ext</c>, and a <c>.dat</c> with one reads its parts.
  /// </summary>
  /// <exception cref="InvalidDataException">No well-formed archive, or a directory whose data file is missing.</exception>
  public static LiveMakerArchiveSet Open(Stream stream) {
    ArgumentNullException.ThrowIfNull(stream);
    if (stream is not FileStream { Name: { Length: > 0 } path })
      return new(new LiveMakerReader(stream), []);

    var extension = Path.GetExtension(path);
    var owned = new List<Stream>();
    try {
      if (extension.Equals(".ext", StringComparison.OrdinalIgnoreCase) && StartsWithDirectory(stream)) {
        var dat = Sibling(path, ".dat") ?? throw new InvalidDataException($"LiveMaker directory '{Path.GetFileName(path)}' has no .dat data file beside it.");
        var data = OpenAll([dat, .. Parts(path)], owned);
        return new(new LiveMakerReader(stream, Join(data, owned), separateIndex: true), owned);
      }

      if (extension.Equals(".dat", StringComparison.OrdinalIgnoreCase)) {
        var parts = Parts(path);
        if (!StartsWithDirectory(stream) && Sibling(path, ".ext") is { } ext) {
          var index = File.OpenRead(ext);
          owned.Add(index);
          var data = OpenAll(parts, owned);
          return new(new LiveMakerReader(index, Join([stream, .. data], owned), separateIndex: true), owned);
        }

        if (parts.Count > 0 && StartsWithDirectory(stream)) {
          var data = OpenAll(parts, owned);
          return new(new LiveMakerReader(stream, Join([stream, .. data], owned), separateIndex: false), owned);
        }
      }

      return new(new LiveMakerReader(stream), owned);
    } catch {
      foreach (var s in owned) s.Dispose();
      throw;
    }
  }

  /// <inheritdoc />
  public void Dispose() {
    foreach (var s in this._owned) s.Dispose();
  }

  private static bool StartsWithDirectory(Stream stream) {
    if (stream.Length < 10) return false;
    Span<byte> head = stackalloc byte[2];
    stream.Position = 0;
    return stream.Read(head) == 2 && head[0] == (byte)'v' && head[1] == (byte)'f';
  }

  /// <summary><c>name.001</c>, <c>name.002</c>, ... up to the first missing one.</summary>
  private static List<string> Parts(string path) {
    var parts = new List<string>();
    for (var i = 1; i < 1000; ++i) {
      if (Sibling(path, $".{i:D3}") is not { } part) break;
      parts.Add(part);
    }
    return parts;
  }

  private static string? Sibling(string path, string extension) {
    foreach (var candidate in new[] { extension, extension.ToUpperInvariant() }) {
      var p = Path.ChangeExtension(path, candidate);
      if (File.Exists(p) && !string.Equals(Path.GetFullPath(p), Path.GetFullPath(path), StringComparison.Ordinal)) return p;
    }
    return null;
  }

  private static List<Stream> OpenAll(IEnumerable<string> paths, List<Stream> owned) {
    var streams = new List<Stream>();
    foreach (var p in paths) {
      var s = File.OpenRead(p);
      owned.Add(s);
      streams.Add(s);
    }
    return streams;
  }

  private static Stream Join(List<Stream> data, List<Stream> owned) {
    if (data.Count == 1) return data[0];
    var joined = new ConcatenatedStream([.. data], leaveOpen: true);
    owned.Add(joined);
    return joined;
  }
}
