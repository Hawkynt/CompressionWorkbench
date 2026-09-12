using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace FileFormat.Sfar;

/// <summary>
/// WORM writer for the BioWare SFAR (Sirius File ARchive) container format used by
/// Mass Effect 3 DLC. Emits the stored variant only — every block is written
/// verbatim, the compression slot is tagged <c>"\0\0\0\0"</c> and the on-disk
/// block-size field for each slot is set to the canonical stored-block sentinel
/// (zero, which encodes "full <see cref="SfarConstants.DefaultMaxBlockSize"/>").
/// LZX-packed output is intentionally out of scope.
/// </summary>
/// <remarks>
/// <para>
/// File layout produced:
/// </para>
/// <list type="number">
///   <item><description>32-byte header (magic, version, offsets, max-block-size, "stored" tag).</description></item>
///   <item><description>Entry table — one 30-byte record per file (16-byte MD5 path hash + 4-byte block index + 5-byte size + 5-byte data offset).</description></item>
///   <item><description>Block table — one little-endian <see cref="ushort"/> per data block.</description></item>
///   <item><description>Per-entry data blocks, written in entry order.</description></item>
/// </list>
/// <para>
/// Writers may pass entry names directly; we hash them to derive the on-disk MD5
/// path hash (lowercase, forward-slash normalized). To make the resulting archive
/// round-trippable through <see cref="SfarReader"/>, the first entry is always a
/// synthetic <c>Filenames.txt</c> manifest listing the remaining entries in
/// archive order.
/// </para>
/// </remarks>
public sealed class SfarWriter {

  private readonly Stream _output;

  /// <summary>Initializes a new <see cref="SfarWriter"/> targeting <paramref name="output"/>.</summary>
  /// <param name="output">The destination stream. Must be writable and seekable.</param>
  /// <exception cref="ArgumentNullException">Thrown when <paramref name="output"/> is null.</exception>
  /// <exception cref="ArgumentException">Thrown when <paramref name="output"/> is not seekable.</exception>
  public SfarWriter(Stream output) {
    ArgumentNullException.ThrowIfNull(output);
    if (!output.CanSeek) throw new ArgumentException("SFAR writing requires a seekable stream.", nameof(output));
    this._output = output;
  }

  /// <summary>
  /// Writes a stored-mode SFAR archive containing <paramref name="entries"/>.
  /// A synthetic <c>Filenames.txt</c> manifest is prepended at index 0 so the
  /// resulting archive round-trips through <see cref="SfarReader"/> with the
  /// original names preserved.
  /// </summary>
  /// <param name="entries">The (path, data) pairs to emit, in archive order.</param>
  /// <exception cref="ArgumentNullException">Thrown when <paramref name="entries"/> is null.</exception>
  public void Write(IReadOnlyList<(string Path, byte[] Data)> entries) {
    ArgumentNullException.ThrowIfNull(entries);

    // Build manifest from the caller's paths in archive order. The reader convention is
    // one path per line for entries 1..N-1 with the manifest itself at index 0.
    var manifestText = new StringBuilder();
    foreach (var (path, _) in entries)
      manifestText.Append(NormalizePath(path)).Append('\n');
    var manifestBytes = Encoding.UTF8.GetBytes(manifestText.ToString());

    // Composite list: entry[0] = manifest (covered by "Filenames.txt" path hash),
    // entry[1..N] = caller-supplied files in order.
    var allEntries = new List<(byte[] Hash, byte[] Data)>(entries.Count + 1) {
      (HashPath("Filenames.txt"), manifestBytes),
    };
    foreach (var (path, data) in entries)
      allEntries.Add((HashPath(path), data));

    WriteStored(this._output, allEntries);
  }

  /// <summary>
  /// Computes the canonical SFAR path hash: MD5 over the UTF-8 bytes of
  /// <paramref name="path"/> after lowercasing and converting backslashes to
  /// forward slashes (the format's path-normalisation rule).
  /// </summary>
  public static byte[] HashPath(string path) {
    ArgumentNullException.ThrowIfNull(path);
    return MD5.HashData(Encoding.UTF8.GetBytes(NormalizePath(path)));
  }

  private static string NormalizePath(string path) => path.ToLowerInvariant().Replace('\\', '/');

  private static void WriteStored(Stream output, List<(byte[] Hash, byte[] Data)> entries) {
    const int maxBlockSize = SfarConstants.DefaultMaxBlockSize;

    var slotsPerEntry = new int[entries.Count];
    var totalBlocks = 0;
    for (var i = 0; i < entries.Count; ++i) {
      slotsPerEntry[i] = entries[i].Data.Length == 0
        ? 0
        : (entries[i].Data.Length + maxBlockSize - 1) / maxBlockSize;
      totalBlocks += slotsPerEntry[i];
    }

    var entryTableSize = entries.Count * SfarConstants.EntrySize;
    var blockTableSize = totalBlocks * 2;

    var entriesOffset = SfarConstants.HeaderSize;
    var blockTableOffset = entriesOffset + entryTableSize;
    var dataOffset = blockTableOffset + blockTableSize;

    // ── Header ────────────────────────────────────────────────────────────
    Span<byte> header = stackalloc byte[SfarConstants.HeaderSize];
    header.Clear();
    BinaryPrimitives.WriteUInt32LittleEndian(header[..4], SfarConstants.Magic);
    BinaryPrimitives.WriteUInt32LittleEndian(header[4..8], 0x00010000u); // version
    BinaryPrimitives.WriteUInt32LittleEndian(header[8..12], (uint)dataOffset);
    BinaryPrimitives.WriteUInt32LittleEndian(header[12..16], (uint)entriesOffset);
    BinaryPrimitives.WriteUInt32LittleEndian(header[16..20], (uint)entries.Count);
    BinaryPrimitives.WriteUInt32LittleEndian(header[20..24], (uint)blockTableOffset);
    BinaryPrimitives.WriteUInt32LittleEndian(header[24..28], (uint)maxBlockSize);
    SfarConstants.CompressionStored.CopyTo(header[28..32]);
    output.Write(header);

    // ── Pre-compute per-entry positions ───────────────────────────────────
    var perEntryDataOffsets = new long[entries.Count];
    var perEntryBlockIndices = new int[entries.Count];
    var cursorOffset = (long)dataOffset;
    var cursorBlock = 0;
    for (var i = 0; i < entries.Count; ++i) {
      perEntryDataOffsets[i] = cursorOffset;
      perEntryBlockIndices[i] = cursorBlock;
      cursorOffset += entries[i].Data.Length;
      cursorBlock += slotsPerEntry[i];
    }

    // ── Entry table ───────────────────────────────────────────────────────
    Span<byte> entryBuf = stackalloc byte[SfarConstants.EntrySize];
    for (var i = 0; i < entries.Count; ++i) {
      entryBuf.Clear();
      entries[i].Hash.AsSpan().CopyTo(entryBuf[..16]);
      BinaryPrimitives.WriteInt32LittleEndian(entryBuf[16..20], perEntryBlockIndices[i]);
      WriteFiveByteLE(entryBuf[20..25], entries[i].Data.Length);
      WriteFiveByteLE(entryBuf[25..30], perEntryDataOffsets[i]);
      output.Write(entryBuf);
    }

    // ── Block table (all sentinel 0 — "stored, full max-block-size") ──────
    if (totalBlocks > 0) {
      var zeros = new byte[totalBlocks * 2];
      output.Write(zeros);
    }

    // ── Data ──────────────────────────────────────────────────────────────
    foreach (var (_, data) in entries)
      if (data.Length > 0)
        output.Write(data);
  }

  private static void WriteFiveByteLE(Span<byte> dest, long value) {
    dest[0] = (byte)(value & 0xFF);
    dest[1] = (byte)((value >> 8) & 0xFF);
    dest[2] = (byte)((value >> 16) & 0xFF);
    dest[3] = (byte)((value >> 24) & 0xFF);
    dest[4] = (byte)((value >> 32) & 0xFF);
  }
}
