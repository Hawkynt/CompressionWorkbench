namespace FileFormat.Grp;

/// <summary>
/// Creates BUILD Engine GRP archives.
/// Call <see cref="AddFile"/> for each file, then <see cref="Finish"/> (or dispose) to flush.
/// </summary>
public sealed class GrpWriter : IDisposable {
  private readonly Stream _stream;
  private readonly bool _leaveOpen;
  private readonly List<(string Name, byte[] Data)> _files = [];
  private bool _finished;

  /// <summary>Initialises the writer targeting <paramref name="stream"/>.</summary>
  public GrpWriter(Stream stream, bool leaveOpen = false) {
    _stream = stream;
    _leaveOpen = leaveOpen;
  }

  /// <summary>
  /// Queues a file for inclusion. The name is truncated to 12 characters if longer.
  /// </summary>
  public void AddFile(string name, byte[] data) {
    // Enforce the 12-character on-disk limit
    if (name.Length > 12)
      name = name[..12];
    _files.Add((name, data));
  }

  /// <summary>Writes the complete GRP archive to the underlying stream.</summary>
  public void Finish() {
    if (_finished) return;
    _finished = true;

    using var bw = new BinaryWriter(_stream, System.Text.Encoding.ASCII, leaveOpen: true);

    // Magic (12 bytes)
    bw.Write(System.Text.Encoding.ASCII.GetBytes(GrpReader.Magic));

    // File count (uint32 LE)
    bw.Write((uint)_files.Count);

    // Directory: 12-byte null-padded name + uint32 LE size per entry
    var nameBuf = new byte[12];
    foreach (var (name, data) in _files) {
      Array.Clear(nameBuf);
      System.Text.Encoding.ASCII.GetBytes(name, 0, name.Length, nameBuf, 0);
      bw.Write(nameBuf);
      bw.Write((uint)data.Length);
    }

    // Concatenated file data
    foreach (var (_, data) in _files)
      bw.Write(data);
  }

  /// <inheritdoc/>
  public void Dispose() {
    Finish();
    if (!_leaveOpen) _stream.Dispose();
  }
}
