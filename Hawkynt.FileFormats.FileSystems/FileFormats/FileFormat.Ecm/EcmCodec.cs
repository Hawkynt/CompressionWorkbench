using System.Buffers.Binary;
using FileFormat.Cdi;

namespace FileFormat.Ecm;

/// <summary>
/// Encoder and decoder for the Error Code Modeler (ECM) container: CD-ROM sectors whose
/// sync pattern, header, EDC and ECC can be regenerated are stored without them.
/// </summary>
/// <remarks>
/// Implemented from <c>docs/ECM-ON-DISK.md</c>; the reference ecm/unecm tools are a
/// black-box oracle only. Sector integrity comes from the ECMA-130 code in
/// <see cref="CdiCdSectorIntegrity"/>.
/// </remarks>
public static class EcmCodec {
  /// <summary>The four-byte file magic <c>"ECM\0"</c>.</summary>
  public static ReadOnlySpan<byte> Magic => "ECM\0"u8;

  private const int RawSectorSize = 2352;
  private const int Mode2SectorSize = 2336;
  private const int Mode1StoredSize = 3 + 2048;
  private const int Form1StoredSize = 4 + 2048;
  private const int Form2StoredSize = 4 + 2324;
  private const uint EndMarker = 0xFFFFFFFF;

  // Records are flushed at this payload size so the encoder streams in bounded memory.
  private const int FlushThreshold = 1 << 22;
  private const int WindowSize = 1 << 22;

  private static ReadOnlySpan<byte> Sync => [0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x00];

  /// <summary>Record kinds of the ECM stream.</summary>
  private enum RecordType : byte { Literal = 0, Mode1 = 1, Mode2Form1 = 2, Mode2Form2 = 3 }

  /// <summary>Decodes an ECM stream, verifying the trailing EDC of the reconstructed data.</summary>
  /// <exception cref="InvalidDataException">The stream is not ECM, is truncated or its checksum does not match.</exception>
  public static void Decode(Stream input, Stream output) {
    ArgumentNullException.ThrowIfNull(input);
    ArgumentNullException.ThrowIfNull(output);

    Span<byte> magic = stackalloc byte[4];
    if (input.ReadAtLeast(magic, 4, throwOnEndOfStream: false) != 4 || !magic.SequenceEqual(Magic))
      throw new InvalidDataException("Not an ECM stream: the \"ECM\\0\" magic is missing.");

    var sector = new byte[RawSectorSize];
    var edc = 0u;
    for (;;) {
      var (type, count) = ReadTypeCount(input);
      if (count == EndMarker)
        break;
      var remaining = (long)count + 1;
      if (type == RecordType.Literal) {
        while (remaining > 0) {
          var chunk = (int)Math.Min(remaining, sector.Length);
          ReadExactly(input, sector.AsSpan(0, chunk));
          edc = UpdateEdc(edc, sector.AsSpan(0, chunk));
          output.Write(sector, 0, chunk);
          remaining -= chunk;
        }
        continue;
      }

      for (; remaining > 0; --remaining) {
        var produced = ReconstructSector(input, sector, type);
        edc = UpdateEdc(edc, produced);
        output.Write(produced);
      }
    }

    Span<byte> trailer = stackalloc byte[4];
    ReadExactly(input, trailer);
    var stored = BinaryPrimitives.ReadUInt32LittleEndian(trailer);
    if (stored != edc)
      throw new InvalidDataException($"ECM checksum mismatch: stored 0x{stored:X8}, reconstructed 0x{edc:X8}.");
  }

  /// <summary>Encodes <paramref name="input"/> as an ECM stream.</summary>
  public static void Encode(Stream input, Stream output) {
    ArgumentNullException.ThrowIfNull(input);
    ArgumentNullException.ThrowIfNull(output);

    output.Write(Magic);
    var window = new byte[WindowSize];
    int start = 0, end = 0;
    var eof = false;
    var edc = 0u;
    var pending = new RunBuffer(output);
    var skipLiterals = 0;

    for (;;) {
      if (end - start < RawSectorSize && !eof) {
        Buffer.BlockCopy(window, start, window, 0, end - start);
        end -= start;
        start = 0;
        while (end < window.Length) {
          var read = input.Read(window, end, window.Length - end);
          if (read == 0) { eof = true; break; }
          edc = UpdateEdc(edc, window.AsSpan(end, read));
          end += read;
        }
      }

      var available = end - start;
      if (available == 0)
        break;

      var span = window.AsSpan(start, available);
      if (skipLiterals > 0) {
        var n = Math.Min(skipLiterals, available);
        pending.Add(RecordType.Literal, span[..n], n);
        skipLiterals -= n;
        start += n;
        continue;
      }

      // The sync and header in front of a Mode-2 sector are kept as literals; the 2 336
      // bytes after them are what the Mode-2 record types model.
      if (available >= 16 && span[..12].SequenceEqual(Sync) && span[15] == 2) {
        skipLiterals = 16;
        continue;
      }

      switch (Detect(span)) {
        case RecordType.Mode1:
          pending.AddMode1(span[..RawSectorSize]);
          start += RawSectorSize;
          break;
        case RecordType.Mode2Form1:
          pending.Add(RecordType.Mode2Form1, span.Slice(4, Form1StoredSize), 1);
          start += Mode2SectorSize;
          break;
        case RecordType.Mode2Form2:
          pending.Add(RecordType.Mode2Form2, span.Slice(4, Form2StoredSize), 1);
          start += Mode2SectorSize;
          break;
        default:
          pending.Add(RecordType.Literal, span[..1], 1);
          ++start;
          break;
      }
    }

    pending.Flush();
    WriteTypeCount(output, RecordType.Literal, EndMarker);
    Span<byte> trailer = stackalloc byte[4];
    BinaryPrimitives.WriteUInt32LittleEndian(trailer, edc);
    output.Write(trailer);
  }

  /// <summary>Classifies the data at the start of <paramref name="data"/>.</summary>
  private static RecordType Detect(ReadOnlySpan<byte> data) {
    if (data.Length >= RawSectorSize && data[..12].SequenceEqual(Sync) && data[15] == 1) {
      if (data.Slice(0x814, 8).ContainsAnyExcept((byte)0))
        return RecordType.Literal;
      if (CdiCdSectorIntegrity.ComputeEdc(data[..0x810]) != BinaryPrimitives.ReadUInt32LittleEndian(data[0x810..]))
        return RecordType.Literal;
      Span<byte> copy = stackalloc byte[RawSectorSize];
      data[..RawSectorSize].CopyTo(copy);
      CdiCdSectorIntegrity.WriteEcc(copy, zeroAddressForParity: false);
      return copy[0x81C..].SequenceEqual(data[0x81C..RawSectorSize]) ? RecordType.Mode1 : RecordType.Literal;
    }

    if (data.Length < Mode2SectorSize || !data[..4].SequenceEqual(data.Slice(4, 4)))
      return RecordType.Literal;

    if (CdiCdSectorIntegrity.ComputeEdc(data[..0x808]) == BinaryPrimitives.ReadUInt32LittleEndian(data[0x808..])) {
      Span<byte> raw = stackalloc byte[RawSectorSize];
      raw.Clear();
      data[..Mode2SectorSize].CopyTo(raw[16..]);
      CdiCdSectorIntegrity.WriteEcc(raw, zeroAddressForParity: true);
      if (raw[(16 + 0x80C)..].SequenceEqual(data[0x80C..Mode2SectorSize]))
        return RecordType.Mode2Form1;
    }

    return CdiCdSectorIntegrity.ComputeEdc(data[..0x91C]) == BinaryPrimitives.ReadUInt32LittleEndian(data[0x91C..])
      ? RecordType.Mode2Form2
      : RecordType.Literal;
  }

  /// <summary>Reads one stored sector and rebuilds it; returns the bytes it stands for.</summary>
  private static ReadOnlySpan<byte> ReconstructSector(Stream input, byte[] sector, RecordType type) {
    Array.Clear(sector);
    Sync.CopyTo(sector);
    switch (type) {
      case RecordType.Mode1:
        ReadExactly(input, sector.AsSpan(0x0C, 3));
        ReadExactly(input, sector.AsSpan(0x10, 0x800));
        sector[0x0F] = 1;
        BinaryPrimitives.WriteUInt32LittleEndian(sector.AsSpan(0x810), CdiCdSectorIntegrity.ComputeEdc(sector.AsSpan(0, 0x810)));
        CdiCdSectorIntegrity.WriteEcc(sector, zeroAddressForParity: false);
        return sector;
      case RecordType.Mode2Form1:
        ReadExactly(input, sector.AsSpan(0x14, Form1StoredSize));
        sector[0x0F] = 2;
        sector.AsSpan(0x14, 4).CopyTo(sector.AsSpan(0x10));
        BinaryPrimitives.WriteUInt32LittleEndian(sector.AsSpan(0x818), CdiCdSectorIntegrity.ComputeEdc(sector.AsSpan(0x10, 0x808)));
        CdiCdSectorIntegrity.WriteEcc(sector, zeroAddressForParity: true);
        return sector.AsSpan(0x10, Mode2SectorSize);
      case RecordType.Mode2Form2:
        ReadExactly(input, sector.AsSpan(0x14, Form2StoredSize));
        sector[0x0F] = 2;
        sector.AsSpan(0x14, 4).CopyTo(sector.AsSpan(0x10));
        BinaryPrimitives.WriteUInt32LittleEndian(sector.AsSpan(0x92C), CdiCdSectorIntegrity.ComputeEdc(sector.AsSpan(0x10, 0x91C)));
        return sector.AsSpan(0x10, Mode2SectorSize);
      default:
        throw new InvalidDataException($"ECM record type {type} carries no sector.");
    }
  }

  private static (RecordType Type, uint Count) ReadTypeCount(Stream input) {
    var c = input.ReadByte();
    if (c < 0)
      throw new InvalidDataException("ECM stream truncated before its end marker.");
    var type = (RecordType)(c & 3);
    var count = (uint)(c >> 2) & 0x1F;
    var bits = 5;
    while ((c & 0x80) != 0) {
      c = input.ReadByte();
      if (c < 0)
        throw new InvalidDataException("ECM stream truncated inside a record count.");
      if (bits > 31 || (uint)(c & 0x7F) >= 0x80000000u >> (bits - 1))
        throw new InvalidDataException("ECM record count exceeds 32 bits.");
      count |= (uint)(c & 0x7F) << bits;
      bits += 7;
    }
    return (type, count);
  }

  private static void WriteTypeCount(Stream output, RecordType type, uint countMinusOne) {
    var value = countMinusOne;
    output.WriteByte((byte)((value >= 32 ? 0x80u : 0u) | ((value & 31) << 2) | (uint)type));
    value >>= 5;
    while (value != 0) {
      output.WriteByte((byte)((value >= 128 ? 0x80u : 0u) | (value & 127)));
      value >>= 7;
    }
  }

  private static uint UpdateEdc(uint edc, ReadOnlySpan<byte> data) => CdiCdSectorIntegrity.ComputeEdc(edc, data);

  private static void ReadExactly(Stream input, Span<byte> buffer) {
    if (input.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false) != buffer.Length)
      throw new InvalidDataException("ECM stream truncated inside a record.");
  }

  /// <summary>Accumulates one run of same-typed records and writes it as a single type/count record.</summary>
  private sealed class RunBuffer(Stream output) {
    private readonly MemoryStream _payload = new();
    private RecordType _type;
    private uint _count;

    public void AddMode1(ReadOnlySpan<byte> sector) {
      this.Prepare(RecordType.Mode1);
      this._payload.Write(sector.Slice(0x0C, 3));
      this._payload.Write(sector.Slice(0x10, 0x800));
      ++this._count;
      this.FlushIfLarge();
    }

    public void Add(RecordType type, ReadOnlySpan<byte> stored, int units) {
      this.Prepare(type);
      this._payload.Write(stored);
      this._count += (uint)units;
      this.FlushIfLarge();
    }

    public void Flush() {
      if (this._count == 0)
        return;
      WriteTypeCount(output, this._type, this._count - 1);
      this._payload.Position = 0;
      this._payload.CopyTo(output);
      this._payload.SetLength(0);
      this._count = 0;
    }

    private void Prepare(RecordType type) {
      if (this._count > 0 && this._type != type)
        this.Flush();
      this._type = type;
    }

    private void FlushIfLarge() {
      if (this._payload.Length >= FlushThreshold)
        this.Flush();
    }
  }
}
