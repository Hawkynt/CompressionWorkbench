using System.IO.Compression;
using System.Text;

namespace FileFormat.Psf;

/// <summary>
/// Reads a Portable Sound Format (PSF) container: 16-byte header, optional reserved blob,
/// zlib-compressed program section, and an optional <c>[TAG]</c> key/value block.
/// Magic and CRC mismatches surface as <see cref="IsCorrupt"/> rather than throwing
/// (except for outright bad magic, which is unrecoverable).
/// </summary>
public sealed class PsfReader : IDisposable {
  private readonly Stream _stream;
  private readonly bool _leaveOpen;
  private bool _disposed;

  /// <summary>Platform/version byte from offset 3 of the header (e.g. 0x01 = PS1, 0x02 = PS2).</summary>
  public byte VersionByte { get; }

  /// <summary>The reserved-area blob (length determined by the header field). May be empty.</summary>
  public byte[] ReservedData { get; }

  /// <summary>The decompressed program payload. Always non-null; empty if the program section was empty.</summary>
  public byte[] ProgramData { get; }

  /// <summary>The CRC-32 value as stored in the header (computed by the producer over the COMPRESSED program bytes).</summary>
  public uint ProgramCrc32 { get; }

  /// <summary>The CRC-32 actually computed by the reader over the raw compressed program bytes.</summary>
  public uint ActualProgramCrc32 { get; }

  /// <summary>True when <see cref="ProgramCrc32"/> doesn't match <see cref="ActualProgramCrc32"/>. Reader does not throw on mismatch.</summary>
  public bool IsCorrupt { get; }

  /// <summary>Parsed tag block (UTF-8 / Latin-1, one <c>key=value</c> per line). Empty if the file had no <c>[TAG]</c> sentinel.</summary>
  public IReadOnlyDictionary<string, string> Tags { get; }

  /// <summary>The flat synthetic-entry view: header.bin, [reserved.bin], program.bin, [tags.txt].</summary>
  public IReadOnlyList<PsfEntry> Entries { get; }

  /// <summary>The raw 16-byte header bytes (kept so the synthetic header.bin entry can round-trip exactly).</summary>
  public byte[] HeaderBytes { get; }

  /// <summary>Opens a PSF container from the given stream.</summary>
  public PsfReader(Stream stream, bool leaveOpen = false) {
    this._stream = stream ?? throw new ArgumentNullException(nameof(stream));
    this._leaveOpen = leaveOpen;

    if (stream.Length - stream.Position < PsfConstants.HeaderSize)
      throw new InvalidDataException("Stream is too small to be a valid PSF container.");

    var header = new byte[PsfConstants.HeaderSize];
    ReadExact(header);
    this.HeaderBytes = header;

    if (header[0] != PsfConstants.Magic[0] || header[1] != PsfConstants.Magic[1] || header[2] != PsfConstants.Magic[2])
      throw new InvalidDataException("Invalid PSF magic.");

    this.VersionByte = header[3];
    var reservedSize = BitConverter.ToUInt32(header, 4);
    var programSize  = BitConverter.ToUInt32(header, 8);
    this.ProgramCrc32 = BitConverter.ToUInt32(header, 12);

    this.ReservedData = ReadBlob(reservedSize, "reserved");
    var compressedProgram = ReadBlob(programSize, "program");
    this.ActualProgramCrc32 = PsfCrc32.Compute(compressedProgram);
    this.IsCorrupt = this.ActualProgramCrc32 != this.ProgramCrc32;

    this.ProgramData = compressedProgram.Length == 0 ? [] : Inflate(compressedProgram);
    this.Tags = ReadTagBlock();
    this.Entries = BuildEntries();
  }

  private byte[] ReadBlob(uint size, string name) {
    if (size == 0)
      return [];
    var remaining = this._stream.Length - this._stream.Position;
    if (size > remaining)
      throw new InvalidDataException($"PSF {name} section ({size} bytes) extends past end of stream.");
    var data = new byte[size];
    ReadExact(data);
    return data;
  }

  private static byte[] Inflate(byte[] compressed) {
    using var src = new MemoryStream(compressed, writable: false);
    using var z = new ZLibStream(src, CompressionMode.Decompress);
    using var dst = new MemoryStream();
    z.CopyTo(dst);
    return dst.ToArray();
  }

  private Dictionary<string, string> ReadTagBlock() {
    // Pre-2010 PSF tools wrote Latin-1; modern ones use UTF-8. The 5-byte ASCII
    // sentinel is the same in both encodings, so we check it first then decode the
    // rest as UTF-8 (which round-trips ASCII-only tags identically to Latin-1).
    var remaining = this._stream.Length - this._stream.Position;
    if (remaining < PsfConstants.TagPrefix.Length)
      return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    var trailing = new byte[remaining];
    ReadExact(trailing);

    var prefix = Encoding.ASCII.GetBytes(PsfConstants.TagPrefix);
    for (var i = 0; i < prefix.Length; ++i)
      if (trailing[i] != prefix[i])
        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    var text = Encoding.UTF8.GetString(trailing, prefix.Length, trailing.Length - prefix.Length);
    return ParseTags(text);
  }

  private static Dictionary<string, string> ParseTags(string text) {
    var tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    foreach (var rawLine in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)) {
      var eq = rawLine.IndexOf('=');
      if (eq <= 0)
        continue;
      var key = rawLine[..eq].Trim();
      var value = rawLine[(eq + 1)..].Trim();
      if (key.Length == 0)
        continue;
      // PSF spec: duplicate keys are a multi-line concatenation; preserve all values
      // joined by newline (matches reference psflib behavior).
      tags[key] = tags.TryGetValue(key, out var existing) ? existing + "\n" + value : value;
    }
    return tags;
  }

  private List<PsfEntry> BuildEntries() {
    var entries = new List<PsfEntry> {
      new() { Name = PsfConstants.EntryHeader, Data = this.HeaderBytes },
    };
    if (this.ReservedData.Length > 0)
      entries.Add(new PsfEntry { Name = PsfConstants.EntryReserved, Data = this.ReservedData });
    entries.Add(new PsfEntry { Name = PsfConstants.EntryProgram, Data = this.ProgramData });
    if (this.Tags.Count > 0)
      entries.Add(new PsfEntry { Name = PsfConstants.EntryTags, Data = SerializeTagsText(this.Tags) });
    return entries;
  }

  private static byte[] SerializeTagsText(IReadOnlyDictionary<string, string> tags) {
    var sb = new StringBuilder();
    foreach (var kvp in tags) {
      // Multi-line values are serialized as repeated key=value lines so the round-trip parses back identically.
      foreach (var line in kvp.Value.Split('\n'))
        sb.Append(kvp.Key).Append('=').Append(line).Append('\n');
    }
    return Encoding.UTF8.GetBytes(sb.ToString());
  }

  private void ReadExact(Span<byte> buffer) {
    var total = 0;
    while (total < buffer.Length) {
      var read = this._stream.Read(buffer[total..]);
      if (read == 0)
        throw new EndOfStreamException("Unexpected end of PSF stream.");
      total += read;
    }
  }

  /// <inheritdoc />
  public void Dispose() {
    if (this._disposed)
      return;
    this._disposed = true;
    if (!this._leaveOpen)
      this._stream.Dispose();
  }
}
