using System.Diagnostics;
using System.Text;
using Compression.Core.Checksums;
using Compression.Core.Dictionary.Lzh;

namespace FileFormat.Lzh;

/// <summary>
/// Creates LHA/LZH archives.
/// </summary>
public sealed class LhaWriter {
  private readonly List<(string name, byte[] data)> _files = [];
  private readonly string _method;
  private readonly int _positionBits;
  private readonly LhaArchiverGeneration _generation;
  private readonly LhaHeaderLevel _headerLevel;

  /// <summary>
  /// Initializes a new <see cref="LhaWriter"/>.
  /// </summary>
  /// <param name="method">Compression method (default "-lh5-").</param>
  /// <param name="generation">Archiver family/generation that constrains allowed method IDs.</param>
  /// <param name="headerLevel">Physical LHA header layout. Independent of <paramref name="generation"/>.</param>
  public LhaWriter(
      string method = LhaConstants.MethodLh5,
      LhaArchiverGeneration generation = LhaArchiverGeneration.Extended,
      LhaHeaderLevel headerLevel = LhaHeaderLevel.Level1) {
    ArgumentNullException.ThrowIfNull(method);
    if (!LhaCompatibility.IsImplemented(method))
      throw new NotSupportedException($"Unsupported LHA compression method for writing: {method}");

    LhaCompatibility.EnsureSupported(generation, method);

    this._method = method;
    this._generation = generation;
    this._headerLevel = headerLevel;
    this._positionBits = method switch {
      LhaConstants.MethodLh0 or LhaConstants.MethodLz4 or LhaConstants.MethodPm0 => 0,
      LhaConstants.MethodLzs => -1,
      LhaConstants.MethodLz5 => -2,
      LhaConstants.MethodLh1 => -3,
      LhaConstants.MethodPm1 => -5,
      LhaConstants.MethodPm2 => -6,
      LhaConstants.MethodLh2 or LhaConstants.MethodLh3 => LzhConstants.Lh5PositionBits,
      LhaConstants.MethodLh4 => LzhConstants.Lh4PositionBits,
      LhaConstants.MethodLh5 => LzhConstants.Lh5PositionBits,
      LhaConstants.MethodLh6 => LzhConstants.Lh6PositionBits,
      LhaConstants.MethodLh7 => LzhConstants.Lh7PositionBits,
      _ => throw new UnreachableException(),
    };
  }

  /// <summary>Adds a file to the archive.</summary>
  public void AddFile(string name, byte[] data) {
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(data);
    this._files.Add((name, data));
  }

  /// <summary>Writes the archive to a stream.</summary>
  public void WriteTo(Stream output) {
    ArgumentNullException.ThrowIfNull(output);
    foreach (var (name, data) in this._files)
      this.WriteEntry(output, name, data);
  }

  /// <summary>Creates an LHA archive as a byte array.</summary>
  public byte[] ToArray() {
    using var ms = new MemoryStream();
    this.WriteTo(ms);
    return ms.ToArray();
  }

  /// <summary>Creates an LHA archive split into multiple volumes.</summary>
  public static byte[][] CreateSplit(
      long maxVolumeSize,
      IEnumerable<(string Name, byte[] Data)> entries,
      string method = LhaConstants.MethodLh5,
      LhaArchiverGeneration generation = LhaArchiverGeneration.Extended,
      LhaHeaderLevel headerLevel = LhaHeaderLevel.Level1) {
    var writer = new LhaWriter(method, generation, headerLevel);
    foreach (var (name, data) in entries)
      writer.AddFile(name, data);
    return Compression.Core.Streams.VolumeHelper.SplitIntoVolumes(writer.ToArray(), maxVolumeSize);
  }

  private void WriteEntry(Stream output, string name, byte[] data) {
    var storedMethod = this.GetStoredMethod();
    byte[] compressed;
    var method = this._method;

    if (data.Length == 0 || this._positionBits == 0) {
      compressed = data;
    } else if (this._positionBits == -5) {
      compressed = PmaEncoder.Encode(data, 2);
    } else if (this._positionBits == -6) {
      compressed = PmaEncoder.Encode(data, 3);
    } else if (this._positionBits == -1) {
      compressed = LzsEncoder.Encode(data);
    } else if (this._positionBits == -2) {
      compressed = Lz5Encoder.Encode(data);
    } else if (this._positionBits == -3) {
      compressed = Lh1Encoder.Encode(data);
    } else {
      var encoder = new LzhEncoder(this._positionBits);
      compressed = encoder.Encode(data);
    }

    if (this._positionBits != 0 && compressed.Length >= data.Length) {
      compressed = data;
      method = storedMethod;
    }

    LhaCompatibility.EnsureSupported(this._generation, method);

    var nameBytes = Encoding.ASCII.GetBytes(name);
    var crc = Crc16.Compute(data);
    var timestamp = DateTime.Now;

    switch (this._headerLevel) {
      case LhaHeaderLevel.Level0:
        WriteLevel0Header(output, method, compressed.Length, data.Length, timestamp, nameBytes, crc);
        break;
      case LhaHeaderLevel.Level1:
        WriteLevel1Header(output, method, compressed.Length, data.Length, timestamp, nameBytes, crc);
        break;
      case LhaHeaderLevel.Level2:
        WriteLevel2Header(output, method, compressed.Length, data.Length, timestamp, nameBytes, crc);
        break;
      default:
        throw new ArgumentOutOfRangeException(nameof(this._headerLevel), this._headerLevel, "Unsupported LHA header level.");
    }

    output.Write(compressed);
  }

  private string GetStoredMethod() => this._method switch {
    LhaConstants.MethodLz4 or LhaConstants.MethodLzs or LhaConstants.MethodLz5 => LhaConstants.MethodLz4,
    LhaConstants.MethodPm0 or LhaConstants.MethodPm1 or LhaConstants.MethodPm2 => LhaConstants.MethodPm0,
    _ => LhaConstants.MethodLh0,
  };

  private static void WriteLevel0Header(
      Stream output,
      string method,
      int compressedSize,
      int originalSize,
      DateTime timestamp,
      byte[] nameBytes,
      ushort crc) {
    using var payloadStream = new MemoryStream();
    using (var writer = new BinaryWriter(payloadStream, Encoding.ASCII, leaveOpen: true)) {
      writer.Write(Encoding.ASCII.GetBytes(method));
      writer.Write((uint)compressedSize);
      writer.Write((uint)originalSize);
      writer.Write(MsdosTimestamp(timestamp));
      writer.Write((byte)0x20);
      writer.Write((byte)LhaHeaderLevel.Level0);
      writer.Write(CheckedNameLength(nameBytes, 233, LhaHeaderLevel.Level0));
      writer.Write(nameBytes);
      writer.Write(crc);
    }

    var payload = payloadStream.ToArray();
    if (payload.Length > byte.MaxValue)
      throw new InvalidDataException("LHA level-0 header exceeds its one-byte size limit.");

    using var outputWriter = new BinaryWriter(output, Encoding.ASCII, leaveOpen: true);
    outputWriter.Write((byte)payload.Length);
    outputWriter.Write(CalculateChecksum(payload));
    outputWriter.Write(payload);
  }

  private static void WriteLevel1Header(
      Stream output,
      string method,
      int compressedSize,
      int originalSize,
      DateTime timestamp,
      byte[] nameBytes,
      ushort crc) {
    using var payloadStream = new MemoryStream();
    using (var writer = new BinaryWriter(payloadStream, Encoding.ASCII, leaveOpen: true)) {
      writer.Write(Encoding.ASCII.GetBytes(method));
      writer.Write((uint)compressedSize);
      writer.Write((uint)originalSize);
      writer.Write(MsdosTimestamp(timestamp));
      writer.Write((byte)0x20);
      writer.Write((byte)LhaHeaderLevel.Level1);
      writer.Write(CheckedNameLength(nameBytes, 230, LhaHeaderLevel.Level1));
      writer.Write(nameBytes);
      writer.Write(crc);
      writer.Write(LhaConstants.OsIdentifierUnix);
      writer.Write((ushort)0);
    }

    var payload = payloadStream.ToArray();
    if (payload.Length > byte.MaxValue)
      throw new InvalidDataException("LHA level-1 header exceeds its one-byte size limit.");

    using var outputWriter = new BinaryWriter(output, Encoding.ASCII, leaveOpen: true);
    outputWriter.Write((byte)payload.Length);
    outputWriter.Write(CalculateChecksum(payload));
    outputWriter.Write(payload);
  }

  private static void WriteLevel2Header(
      Stream output,
      string method,
      int compressedSize,
      int originalSize,
      DateTime timestamp,
      byte[] nameBytes,
      ushort crc) {
    var filenameExtensionSize = checked(nameBytes.Length + 3);
    if (filenameExtensionSize > ushort.MaxValue)
      throw new InvalidDataException("LHA level-2 filename extension exceeds its 16-bit size limit.");

    var unpaddedHeaderSize = checked(26 + filenameExtensionSize);
    var paddingSize = (unpaddedHeaderSize & byte.MaxValue) == 0 ? 1 : 0;
    var totalHeaderSize = checked(unpaddedHeaderSize + paddingSize);
    if (totalHeaderSize > ushort.MaxValue)
      throw new InvalidDataException("LHA level-2 header exceeds its 16-bit total-size limit.");

    var unixSeconds = new DateTimeOffset(timestamp).ToUnixTimeSeconds();
    if (unixSeconds < 0 || unixSeconds > uint.MaxValue)
      throw new ArgumentOutOfRangeException(nameof(timestamp), "LHA level-2 timestamp is outside the 32-bit UNIX-time range.");

    using var writer = new BinaryWriter(output, Encoding.ASCII, leaveOpen: true);
    writer.Write((ushort)totalHeaderSize);
    writer.Write(Encoding.ASCII.GetBytes(method));
    writer.Write((uint)compressedSize);
    writer.Write((uint)originalSize);
    writer.Write((uint)unixSeconds);
    writer.Write((byte)0);
    writer.Write((byte)LhaHeaderLevel.Level2);
    writer.Write(crc);
    writer.Write(LhaConstants.OsIdentifierUnix);
    writer.Write((ushort)filenameExtensionSize);
    writer.Write((byte)0x01);
    writer.Write(nameBytes);
    writer.Write((ushort)0);
    if (paddingSize != 0)
      writer.Write((byte)0);
  }

  private static byte CheckedNameLength(byte[] nameBytes, int maximum, LhaHeaderLevel level) {
    if (nameBytes.Length > maximum)
      throw new InvalidDataException($"LHA {level} filename exceeds the {maximum}-byte base-header limit.");
    return (byte)nameBytes.Length;
  }

  private static byte CalculateChecksum(ReadOnlySpan<byte> bytes) {
    byte checksum = 0;
    foreach (var value in bytes)
      checksum += value;
    return checksum;
  }

  private static uint MsdosTimestamp(DateTime dt) {
    var time = (dt.Hour << 11) | (dt.Minute << 5) | (dt.Second / 2);
    var date = ((dt.Year - 1980) << 9) | (dt.Month << 5) | dt.Day;
    return (uint)((date << 16) | time);
  }
}
