#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileFormat.MatlabV4;

/// <summary>
/// Read-only walker for MATLAB MAT v4 (pre-1996) files. There is no global magic — endianness is
/// derived from the first record's MOPT type code, then each fixed 20-byte record header is parsed
/// followed by the variable name and the real (and optional imaginary) data block. Numeric data
/// itself is intentionally not decoded; the reader only surfaces variable shape and type metadata
/// for the FULL.mat + metadata.ini pseudo-archive.
/// </summary>
public sealed class MatlabV4Reader {

  /// <summary>True when the file's host endianness is little-endian (MOPT M-digit = 0).</summary>
  public bool IsLittleEndian { get; }

  /// <summary>Numeric M-digit of the first record's MOPT code (0=LE, 1=BE, 2=VAX-D, 3=VAX-G, 4=Cray).</summary>
  public uint Machine { get; }

  /// <summary>All top-level variable records discovered while walking the file.</summary>
  public IReadOnlyList<MatlabV4VariableInfo> Variables { get; }

  /// <summary>"full" if every record parsed cleanly to EOF; "partial" if a truncated or implausible record was encountered.</summary>
  public string ParseStatus { get; }

  public MatlabV4Reader(Stream stream) {
    ArgumentNullException.ThrowIfNull(stream);
    if (!stream.CanSeek) throw new ArgumentException("Stream must be seekable.", nameof(stream));
    if (stream.Length < MatlabV4Constants.RecordHeaderSize)
      throw new InvalidDataException("MAT v4 file is shorter than a single record header.");

    stream.Seek(0, SeekOrigin.Begin);

    var firstHeader = new byte[MatlabV4Constants.RecordHeaderSize];
    if (!ReadFull(stream, firstHeader)) throw new InvalidDataException("MAT v4 file truncated within first record header.");

    var detectedLE = TryDecodeMopt(firstHeader.AsSpan(0, 4), littleEndian: true, out var leMachine, out var leO, out var leP, out var leT);
    var detectedBE = TryDecodeMopt(firstHeader.AsSpan(0, 4), littleEndian: false, out var beMachine, out var beO, out var beP, out var beT);

    bool littleEndian;
    uint machine;
    if (detectedLE) {
      littleEndian = true;
      machine = leMachine;
      _ = leO;
      _ = leP;
      _ = leT;
    } else if (detectedBE) {
      littleEndian = false;
      machine = beMachine;
      _ = beO;
      _ = beP;
      _ = beT;
    } else {
      throw new InvalidDataException("MAT v4 first record header has no valid MOPT code in either endianness.");
    }

    this.IsLittleEndian = littleEndian;
    this.Machine = machine;

    // Re-walk from the start now that endianness is known.
    stream.Seek(0, SeekOrigin.Begin);
    var variables = new List<MatlabV4VariableInfo>();
    var status = "full";
    while (stream.Position + MatlabV4Constants.RecordHeaderSize <= stream.Length) {
      var recordStart = stream.Position;
      var header = new byte[MatlabV4Constants.RecordHeaderSize];
      if (!ReadFull(stream, header)) {
        status = "partial";
        break;
      }

      if (!TryDecodeMopt(header.AsSpan(0, 4), littleEndian, out var recMachine, out var recO, out var recP, out var recT)) {
        status = "partial";
        break;
      }
      if (recMachine != machine) {
        // Endian mid-stream change is not supported — bail out gracefully.
        status = "partial";
        break;
      }

      var rows = ReadUInt32(header.AsSpan(4, 4), littleEndian);
      var cols = ReadUInt32(header.AsSpan(8, 4), littleEndian);
      var imagFlag = ReadUInt32(header.AsSpan(12, 4), littleEndian);
      var nameLength = ReadUInt32(header.AsSpan(16, 4), littleEndian);

      if (nameLength == 0 || nameLength > MatlabV4Constants.MaxNameLength) {
        status = "partial";
        break;
      }

      var nameBytes = new byte[nameLength];
      if (!ReadFull(stream, nameBytes)) {
        status = "partial";
        break;
      }
      var name = DecodeName(nameBytes);

      var elementSize = MatlabV4Constants.ElementSize(recP);
      if (elementSize <= 0) {
        status = "partial";
        break;
      }

      // For text/sparse the element layout differs in detail (sparse has 3 columns, text uses uint8/uint16
      // depending on P), but the rows*cols*element-size product still bounds the real-data block, which is
      // all we need to walk past it. We treat it as a best-effort skip.
      var dataBytes = checked((long)rows * cols * elementSize);
      if (dataBytes < 0 || recordStart + MatlabV4Constants.RecordHeaderSize + nameLength + dataBytes > stream.Length) {
        // Can't reach the end of this record's real data — stop here but keep what we have.
        variables.Add(new MatlabV4VariableInfo(name, MatlabV4Constants.TypeName(recT, recP), rows, cols, imagFlag != 0));
        status = "partial";
        break;
      }

      // Skip real data.
      var skipped = SkipBytes(stream, dataBytes);
      if (skipped < dataBytes) {
        variables.Add(new MatlabV4VariableInfo(name, MatlabV4Constants.TypeName(recT, recP), rows, cols, imagFlag != 0));
        status = "partial";
        break;
      }

      // Skip imaginary data if present.
      if (imagFlag != 0) {
        var imagSkipped = SkipBytes(stream, dataBytes);
        if (imagSkipped < dataBytes) {
          variables.Add(new MatlabV4VariableInfo(name, MatlabV4Constants.TypeName(recT, recP), rows, cols, imagFlag != 0));
          status = "partial";
          break;
        }
      }

      variables.Add(new MatlabV4VariableInfo(name, MatlabV4Constants.TypeName(recT, recP), rows, cols, imagFlag != 0));

      if (stream.Position <= recordStart) {
        // Defensive — should be unreachable, but prevents an infinite loop.
        status = "partial";
        break;
      }
    }

    this.Variables = variables;
    this.ParseStatus = status;
  }

  /// <summary>
  /// Decodes a 4-byte MOPT word in the requested endianness and validates each digit.
  /// Returns true only when M ≤ 4, O = 0, P ≤ 5, T ≤ 2 — the per-spec plausibility envelope.
  /// </summary>
  private static bool TryDecodeMopt(ReadOnlySpan<byte> bytes, bool littleEndian, out uint machine, out uint o, out uint precision, out uint matrixType) {
    var mopt = ReadUInt32(bytes, littleEndian);
    machine = mopt / 1000;
    var rem1 = mopt % 1000;
    o = rem1 / 100;
    var rem2 = rem1 % 100;
    precision = rem2 / 10;
    matrixType = rem2 % 10;

    if (mopt > 4999) return false;
    if (machine > MatlabV4Constants.MaxMachine) return false;
    if (o != 0) return false;
    if (precision > MatlabV4Constants.MaxPrecision) return false;
    if (matrixType > MatlabV4Constants.MaxType) return false;
    return true;
  }

  private static string DecodeName(byte[] nameBytes) {
    // NameLength includes the null terminator; strip trailing NULs.
    var end = nameBytes.Length;
    while (end > 0 && nameBytes[end - 1] == 0) end--;
    return Encoding.ASCII.GetString(nameBytes, 0, end);
  }

  private static uint ReadUInt32(ReadOnlySpan<byte> bytes, bool littleEndian)
    => littleEndian ? BinaryPrimitives.ReadUInt32LittleEndian(bytes) : BinaryPrimitives.ReadUInt32BigEndian(bytes);

  private static bool ReadFull(Stream stream, byte[] buffer) {
    var read = 0;
    while (read < buffer.Length) {
      var n = stream.Read(buffer, read, buffer.Length - read);
      if (n <= 0) return false;
      read += n;
    }
    return true;
  }

  private static long SkipBytes(Stream stream, long count) {
    if (count <= 0) return 0;
    if (stream.CanSeek) {
      var remaining = stream.Length - stream.Position;
      var toSkip = Math.Min(count, remaining);
      stream.Seek(toSkip, SeekOrigin.Current);
      return toSkip;
    }
    var skipBuf = new byte[Math.Min(count, 8192)];
    long total = 0;
    while (total < count) {
      var want = (int)Math.Min(skipBuf.Length, count - total);
      var n = stream.Read(skipBuf, 0, want);
      if (n <= 0) break;
      total += n;
    }
    return total;
  }
}
