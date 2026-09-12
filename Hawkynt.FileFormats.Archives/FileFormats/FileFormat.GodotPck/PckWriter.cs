#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace FileFormat.GodotPck;

/// <summary>
/// Writes Godot Engine PCK (resource pack) files in pack_version 1 format (Godot 3.x compatible).
/// The directory is written first, followed by file data, so all offsets can be calculated
/// up-front without seeking back.
/// </summary>
public sealed class PckWriter : IDisposable {
  private readonly Stream _output;
  private readonly bool _leaveOpen;
  private readonly List<(string Path, byte[] Data)> _files = [];
  private bool _finished;

  /// <summary>
  /// Creates a new PCK writer that will write to <paramref name="output"/>.
  /// </summary>
  /// <param name="output">A writable stream. Must support <see cref="Stream.Position"/>.</param>
  /// <param name="leaveOpen">When <c>true</c>, <paramref name="output"/> is not closed on dispose.</param>
  public PckWriter(Stream output, bool leaveOpen = false) {
    _output    = output ?? throw new ArgumentNullException(nameof(output));
    _leaveOpen = leaveOpen;
  }

  /// <summary>Adds a file to the pack.</summary>
  /// <param name="path">The virtual path (e.g. "res://scenes/main.tscn").</param>
  /// <param name="data">The raw file content.</param>
  public void AddFile(string path, byte[] data) {
    ArgumentNullException.ThrowIfNull(path);
    ArgumentNullException.ThrowIfNull(data);
    _files.Add((path, data));
  }

  /// <summary>
  /// Finalises the PCK and flushes all data to the underlying stream.
  /// Must be called exactly once; subsequent calls are no-ops.
  /// </summary>
  public void Finish() {
    if (_finished) return;
    _finished = true;
    WriteAll();
  }

  /// <inheritdoc/>
  public void Dispose() {
    Finish();
    if (!_leaveOpen) _output.Dispose();
  }

  // ── private implementation ─────────────────────────────────────────────────

  // PCK v1 header layout (40 bytes):
  //   [0 ] "GDPC"          4 bytes
  //   [4 ] pack_version=1  uint32 LE
  //   [8 ] ver_major=1     uint32 LE
  //   [12] ver_minor=0     uint32 LE
  //   [16] ver_patch=0     uint32 LE
  //   [20] reserved        16 bytes (zeros)
  //   [36] file_count      uint32 LE
  // Total header: 40 bytes
  //
  // Per-entry directory record:
  //   uint32 LE path_length  (byte count of UTF-8 path, null-terminated, padded to 4)
  //   path_length bytes      (null-padded to 4-byte boundary)
  //   uint64 LE offset       (absolute byte offset of file data in the stream)
  //   uint64 LE size         (byte count of file data)
  //   16 bytes               MD5 hash

  private const int HeaderSize = 40; // "GDPC" + 5×uint32 + 16 reserved + file_count

  private void WriteAll() {
    // Pre-compute padded path bytes for each entry
    var pathBufs = new byte[_files.Count][];
    for (var i = 0; i < _files.Count; i++) {
      var raw = Encoding.UTF8.GetBytes(_files[i].Path + '\0');
      // Pad to 4-byte boundary
      var padded = ((raw.Length + 3) / 4) * 4;
      var buf = new byte[padded];
      raw.CopyTo(buf, 0);
      pathBufs[i] = buf;
    }

    // Directory record size per entry: 4 (path_len) + padded_path + 8 (offset) + 8 (size) + 16 (md5)
    var dirSize = 0L;
    for (var i = 0; i < _files.Count; i++)
      dirSize += 4 + pathBufs[i].Length + 8 + 8 + 16;

    // Data starts immediately after header + directory
    var dataBase = HeaderSize + dirSize;

    // Compute per-file data offsets and MD5 hashes
    var offsets = new long[_files.Count];
    var hashes  = new byte[_files.Count][];
    var pos     = dataBase;
    for (var i = 0; i < _files.Count; i++) {
      offsets[i] = pos;
      hashes[i]  = MD5.HashData(_files[i].Data);
      pos       += _files[i].Data.Length;
    }

    // ── Write header ──────────────────────────────────────────────────────
    _output.Write("GDPC"u8);
    WriteUInt32LE(1);          // pack_version
    WriteUInt32LE(1);          // ver_major
    WriteUInt32LE(0);          // ver_minor
    WriteUInt32LE(0);          // ver_patch
    _output.Write(new byte[16]); // reserved
    WriteUInt32LE((uint)_files.Count);

    // ── Write directory ───────────────────────────────────────────────────
    for (var i = 0; i < _files.Count; i++) {
      WriteUInt32LE((uint)pathBufs[i].Length);
      _output.Write(pathBufs[i]);
      WriteUInt64LE((ulong)offsets[i]);
      WriteUInt64LE((ulong)_files[i].Data.Length);
      _output.Write(hashes[i]);
    }

    // ── Write data ────────────────────────────────────────────────────────
    foreach (var (_, data) in _files)
      _output.Write(data);
  }

  private void WriteUInt32LE(uint v) {
    Span<byte> b = stackalloc byte[4];
    BinaryPrimitives.WriteUInt32LittleEndian(b, v);
    _output.Write(b);
  }

  private void WriteUInt64LE(ulong v) {
    Span<byte> b = stackalloc byte[8];
    BinaryPrimitives.WriteUInt64LittleEndian(b, v);
    _output.Write(b);
  }
}
