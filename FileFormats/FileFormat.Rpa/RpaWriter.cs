#pragma warning disable CS1591
using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace FileFormat.Rpa;

/// <summary>
/// Writer for Ren'Py archive files (RPA-3.0).  The output layout is:
/// <list type="number">
///   <item>34-byte ASCII header <c>"RPA-3.0 &lt;16-hex-offset&gt; &lt;8-hex-key&gt;\n"</c>.</item>
///   <item>Raw file payloads, back-to-back, starting immediately after the header.</item>
///   <item>zlib-compressed Python pickle index (offset/length XORed with the key)
///         at the offset declared in the header.</item>
/// </list>
/// </summary>
/// <remarks>
/// <para>
/// The format is documented in the Ren'Py source tree at
/// <c>renpy/loader.py</c> (MIT). We emit a minimal protocol-2 pickle via
/// <see cref="RpaPickleWriter"/> — the same subset
/// <see cref="RpaPickleParser"/> consumes — so the output round-trips through
/// our reader. The byte layout matches official tools at the structural level
/// (header line, payload region, zlib-pickled index) but the exact pickle
/// opcode sequence is ours: any RPA reader that doesn't tolerate alternative
/// opcode orderings may need our pickle to be re-emitted by the upstream
/// <c>renpy</c> CLI before use.
/// </para>
/// </remarks>
public sealed class RpaWriter : IDisposable {

  // 16 hex chars for the offset, 8 hex chars for the key, "RPA-3.0 " prefix, single space,
  // terminating LF: 8 + 16 + 1 + 8 + 1 = 34 bytes.
  private const int HeaderLength = 34;

  private readonly Stream _stream;
  private readonly bool _leaveOpen;
  private readonly uint _xorKey;
  private readonly List<(string Path, byte[] Data, byte[] Prefix)> _entries = [];
  private bool _finished;
  private bool _disposed;

  /// <summary>
  /// Initializes a new <see cref="RpaWriter"/> targeting <paramref name="stream"/>.
  /// </summary>
  /// <param name="stream">Destination stream; must be seekable.</param>
  /// <param name="leaveOpen">Whether to leave the underlying stream open on dispose.</param>
  /// <param name="xorKey">RPA-3.x obfuscation key. Defaults to the canonical
  /// Ren'Py <c>0xDEADBEEF</c> placeholder, which still round-trips through
  /// any conforming reader (the key is published in the header line).</param>
  public RpaWriter(Stream stream, bool leaveOpen = false, uint xorKey = 0xDEADBEEFu) {
    this._stream = stream ?? throw new ArgumentNullException(nameof(stream));
    if (!stream.CanSeek)
      throw new ArgumentException("RPA writer requires a seekable stream.", nameof(stream));
    this._leaveOpen = leaveOpen;
    this._xorKey = xorKey;
  }

  /// <summary>
  /// Adds an entry to the archive.
  /// </summary>
  /// <param name="path">Archive-relative path. Forward slashes only.</param>
  /// <param name="data">Entry bytes.</param>
  /// <param name="prefix">Optional prefix bytes stored inside the pickle entry
  /// rather than in the payload region. Defaults to an empty byte array.</param>
  public void AddEntry(string path, byte[] data, byte[]? prefix = null) {
    if (this._finished)
      throw new InvalidOperationException("Cannot add entries after Finish() has been called.");
    ArgumentNullException.ThrowIfNull(path);
    ArgumentNullException.ThrowIfNull(data);
    this._entries.Add((path.Replace('\\', '/'), data, prefix ?? []));
  }

  /// <summary>
  /// Emits the header placeholder, copies each entry's payload to the stream,
  /// builds the zlib-compressed pickle index and back-patches the header with
  /// the real index offset and XOR key.
  /// </summary>
  public void Finish() {
    if (this._finished) return;
    this._finished = true;

    // 1. Reserve the header — back-patched after the payload region is written.
    var headerStart = this._stream.Position;
    var headerPlaceholder = new byte[HeaderLength];
    for (var i = 0; i < HeaderLength; ++i) headerPlaceholder[i] = (byte)' ';
    headerPlaceholder[HeaderLength - 1] = (byte)'\n';
    this._stream.Write(headerPlaceholder);

    // 2. Stream payloads. Each entry's (offset, length) tracks the raw payload
    //    region; the prefix bytes live inside the pickle entry and are NOT
    //    emitted into the payload region (the reader reconstructs the entry
    //    as `prefix + read_from_payload(offset, length - prefix.Length)`).
    var indexEntries = new List<RpaEntry>(this._entries.Count);
    foreach (var (path, data, prefix) in this._entries) {
      var bodyLength = Math.Max(0, data.Length - prefix.Length);
      // Sanity: ensure the supplied prefix really is the first bytes of the entry.
      // (If not, fall back to no-prefix to keep round-trip semantics.)
      var prefixMatches = prefix.Length > 0 && prefix.Length <= data.Length;
      if (prefixMatches) {
        for (var i = 0; i < prefix.Length; ++i)
          if (data[i] != prefix[i]) { prefixMatches = false; break; }
      }
      var effectivePrefix = prefixMatches ? prefix : [];
      var effectiveBody = prefixMatches ? data.AsSpan(prefix.Length) : data.AsSpan();

      var entryOffset = this._stream.Position;
      this._stream.Write(effectiveBody);

      indexEntries.Add(new RpaEntry {
        Path = path,
        Offset = entryOffset,
        // Length stored in the pickle is the FULL entry length (prefix + body),
        // matching what RpaReader.Extract expects.
        Length = effectivePrefix.Length + effectiveBody.Length,
        Prefix = effectivePrefix,
      });
    }

    // 3. Emit zlib-compressed pickle index at the next free byte.
    var indexOffset = this._stream.Position;
    var pickle = RpaPickleWriter.Emit(indexEntries, this._xorKey);
    using (var zlib = new ZLibStream(this._stream, CompressionLevel.Optimal, leaveOpen: true))
      zlib.Write(pickle);

    // 4. Back-patch the header with the real index offset and key.
    var endPosition = this._stream.Position;
    var header = $"RPA-3.0 {indexOffset:x16} {this._xorKey:x8}\n";
    var headerBytes = Encoding.ASCII.GetBytes(header);
    if (headerBytes.Length != HeaderLength)
      throw new InvalidOperationException(
        $"Internal RPA header length mismatch (expected {HeaderLength}, got {headerBytes.Length}). " +
        "Header layout drifted.");
    this._stream.Position = headerStart;
    this._stream.Write(headerBytes);
    this._stream.Position = endPosition;
  }

  /// <inheritdoc />
  /// <summary>
  /// Releases resources held by this instance.
  /// </summary>
  public void Dispose() {
    if (this._disposed) return;
    this._disposed = true;
    if (!this._finished) Finish();
    if (!this._leaveOpen) this._stream.Dispose();
  }
}
