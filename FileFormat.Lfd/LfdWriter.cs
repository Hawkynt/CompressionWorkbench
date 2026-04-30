using System.Text;

namespace FileFormat.Lfd;

/// <summary>
/// Creates a LucasArts X-Wing / TIE Fighter LFD bundle.
/// </summary>
/// <remarks>
/// On dispose we emit a leading RMAP entry indexing every user resource, then each user
/// resource in insertion order. The RMAP is emitted because many original LucasArts tools
/// require a valid index entry as the first record even though our reader does not depend on it.
/// </remarks>
public sealed class LfdWriter : IDisposable {
  private readonly Stream _stream;
  private readonly bool _leaveOpen;
  private readonly List<(string Type, string Name, byte[] Data)> _entries = [];
  private bool _finished;
  private bool _disposed;

  /// <summary>
  /// Initializes a new <see cref="LfdWriter"/>.
  /// </summary>
  public LfdWriter(Stream stream, bool leaveOpen = false) {
    this._stream = stream ?? throw new ArgumentNullException(nameof(stream));
    this._leaveOpen = leaveOpen;
  }

  /// <summary>
  /// Adds a resource to the bundle.
  /// </summary>
  /// <param name="type">Resource type, up to 4 ASCII characters.</param>
  /// <param name="name">Resource name, up to 8 ASCII characters.</param>
  /// <param name="data">Resource payload.</param>
  public void AddEntry(string type, string name, byte[] data) {
    if (this._finished)
      throw new InvalidOperationException("Cannot add entries after Finish() has been called.");

    ArgumentNullException.ThrowIfNull(type);
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(data);

    ValidateField(type, nameof(type), LfdConstants.TypeFieldSize);
    ValidateField(name, nameof(name), LfdConstants.NameFieldSize);

    this._entries.Add((type, name, data));
  }

  /// <summary>
  /// Flushes the RMAP index and all resource records to the underlying stream.
  /// </summary>
  public void Finish() {
    if (this._finished)
      return;
    this._finished = true;

    var rmapPayloadSize = (uint)(this._entries.Count * LfdConstants.HeaderSize);
    Span<byte> headerBuf = stackalloc byte[LfdConstants.HeaderSize];

    WriteHeader(headerBuf, LfdConstants.RmapType, "resource", rmapPayloadSize);
    this._stream.Write(headerBuf);

    foreach (var (type, name, data) in this._entries) {
      WriteHeader(headerBuf, type, name, (uint)data.Length);
      this._stream.Write(headerBuf);
    }

    foreach (var (type, name, data) in this._entries) {
      WriteHeader(headerBuf, type, name, (uint)data.Length);
      this._stream.Write(headerBuf);
      if (data.Length > 0)
        this._stream.Write(data);
    }
  }

  private static void WriteHeader(Span<byte> destination, string type, string name, uint size) {
    destination.Clear();
    WriteField(destination[..LfdConstants.TypeFieldSize], type);
    WriteField(destination.Slice(LfdConstants.TypeFieldSize, LfdConstants.NameFieldSize), name);
    BitConverter.TryWriteBytes(destination[(LfdConstants.TypeFieldSize + LfdConstants.NameFieldSize)..], size);
  }

  private static void WriteField(Span<byte> destination, string value) {
    var bytes = Encoding.ASCII.GetBytes(value);
    bytes.AsSpan().CopyTo(destination);
  }

  private static void ValidateField(string value, string paramName, int maxLength) {
    if (value.Length > maxLength)
      throw new ArgumentException($"Value exceeds maximum length of {maxLength} characters: '{value}'.", paramName);

    foreach (var ch in value) {
      if (ch > 0x7F)
        throw new ArgumentException($"Value must be ASCII: '{value}'.", paramName);
    }
  }

  /// <inheritdoc />
  public void Dispose() {
    if (this._disposed)
      return;
    this._disposed = true;
    if (!this._finished)
      Finish();
    if (!this._leaveOpen)
      this._stream.Dispose();
  }
}
