using System.Buffers.Binary;
using System.Text;
using Compression.Core.Dictionary.Sqx;

namespace FileFormat.Sqx;

/// <summary>
/// Writes SQX archives the way Squeez-c (MIT) does: a main block, one file block per file with its
/// LZ77 + Huffman stream (methods 1-4, each file on its own, not solid) or the stored bytes when
/// compression does not pay, and the end block.
/// </summary>
public sealed class SqxWriter {
  private readonly List<(string Name, byte[] Data, DateTime? Modified)> _files = [];
  private readonly byte _format;
  private readonly byte _method;
  private readonly int _dictionarySize;

  /// <summary>Creates a writer.</summary>
  /// <param name="method">1 (fast) to 4 (best): longer match searches, lazy matching from 2.</param>
  /// <param name="dictionarySize">Dictionary size: a power of two from 32 KiB to 4 MiB.</param>
  /// <param name="format"><see cref="SqxTables.Format20"/> (default) or <see cref="SqxTables.Format11"/>.</param>
  public SqxWriter(byte method = 2, int dictionarySize = 256 * 1024, byte format = SqxTables.Format20) {
    ArgumentOutOfRangeException.ThrowIfLessThan(method, 1);
    ArgumentOutOfRangeException.ThrowIfGreaterThan(method, 4);
    if (dictionarySize is < 32 * 1024 or > 4 << 20 || (dictionarySize & (dictionarySize - 1)) != 0)
      throw new ArgumentOutOfRangeException(nameof(dictionarySize));
    if (format is not (SqxTables.Format11 or SqxTables.Format20))
      throw new ArgumentOutOfRangeException(nameof(format));
    this._method = method;
    this._dictionarySize = dictionarySize;
    this._format = format;
  }

  /// <summary>Adds a file; <paramref name="name"/> may contain <c>/</c> separators.</summary>
  public void AddFile(string name, byte[] data, DateTime? modified = null) {
    ArgumentException.ThrowIfNullOrEmpty(name);
    ArgumentNullException.ThrowIfNull(data);
    this._files.Add((name, data, modified));
  }

  /// <summary>Writes the archive.</summary>
  public void WriteTo(Stream output) => output.Write(this.ToArray());

  /// <summary>Builds the archive.</summary>
  public byte[] ToArray() {
    using var ms = new MemoryStream();
    var main = new byte[SqxContainer.MainBlockSize];
    main[2] = SqxContainer.BlockMain;
    BinaryPrimitives.WriteUInt16LittleEndian(main.AsSpan(3), 0x0004);
    BinaryPrimitives.WriteUInt16LittleEndian(main.AsSpan(5), SqxContainer.MainBlockSize);
    SqxContainer.Signature.CopyTo(main.AsSpan(7));
    main[12] = SqxTables.Format11;
    Seal(main);
    ms.Write(main);

    foreach (var (name, data, modified) in this._files) {
      var stream = SqxLzEncoder.Encode(data, this._format, this._dictionarySize, this._method);
      var method = this._method;
      if (stream.Length >= data.Length) {
        stream = data;
        method = 0;
      }

      var rawName = Encoding.Latin1.GetBytes(name.Replace('/', '\\'));
      if (rawName.Length > ushort.MaxValue - SqxContainer.HeaderSize - 28)
        throw new ArgumentException($"SQX file name too long: {name}");
      var header = new byte[SqxContainer.HeaderSize + 28 + rawName.Length];
      header[2] = SqxContainer.BlockFile;
      BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(3), SqxTables.DictionaryFlags(this._dictionarySize, this._format));
      BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(5), (ushort)header.Length);
      var p = header.AsSpan(SqxContainer.HeaderSize);
      p[3] = 0x0C;                 // host file system, as sqc writes it
      p[4] = this._format;
      p[5] = method;
      BinaryPrimitives.WriteUInt32LittleEndian(p[6..], SqxContainer.RawCrc32(data));
      BinaryPrimitives.WriteUInt32LittleEndian(p[10..], 0x20);
      BinaryPrimitives.WriteUInt32LittleEndian(p[14..], ToDos(modified));
      BinaryPrimitives.WriteUInt32LittleEndian(p[18..], (uint)stream.Length);
      BinaryPrimitives.WriteUInt32LittleEndian(p[22..], (uint)data.Length);
      BinaryPrimitives.WriteUInt16LittleEndian(p[26..], (ushort)rawName.Length);
      rawName.CopyTo(p[28..]);
      Seal(header);
      ms.Write(header);
      ms.Write(stream);
    }

    var end = new byte[SqxContainer.HeaderSize];
    end[2] = SqxContainer.BlockEnd;
    BinaryPrimitives.WriteUInt16LittleEndian(end.AsSpan(5), SqxContainer.HeaderSize);
    Seal(end);
    ms.Write(end);
    return ms.ToArray();
  }

  private static void Seal(byte[] block) => BinaryPrimitives.WriteUInt16LittleEndian(block, SqxContainer.BlockCrc(block.AsSpan(2)));

  private static uint ToDos(DateTime? value) {
    if (value is not { } t || t.Year < 1980 || t.Year > 2107)
      return 0;
    var date = (uint)(t.Day | t.Month << 5 | (t.Year - 1980) << 9);
    var time = (uint)(t.Second / 2 | t.Minute << 5 | t.Hour << 11);
    return date << 16 | time;
  }
}
