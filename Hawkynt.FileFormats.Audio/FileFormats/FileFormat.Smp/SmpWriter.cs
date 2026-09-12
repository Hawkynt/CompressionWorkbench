#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileFormat.Smp;

/// <summary>
/// Writes a Turtle Beach SampleVision (.smp) file: the 112-byte header, the uint32
/// sample count, the signed 16-bit little-endian samples and a zeroed loop / marker
/// trailer terminated by the MIDI unity note and the sample rate. Mono only. Used by
/// <see cref="SmpFormatDescriptor"/> to assemble a file from a mono WAV.
/// </summary>
public sealed class SmpWriter {

  /// <summary>
  /// Builds a SampleVision file from interleaved-but-mono signed 16-bit little-endian
  /// <paramref name="samplesLe"/> at <paramref name="sampleRate"/> Hz.
  /// </summary>
  public byte[] Write(byte[] samplesLe, int sampleRate, string name = "", string comment = "",
      int midiUnity = 60) {
    var sampleCount = (uint)(samplesLe.Length / 2);

    using var ms = new MemoryStream();
    WriteFixed(ms, SmpReader.Magic, SmpReader.MagicLength);
    WriteFixed(ms, "2.1 ", 4);
    WriteFixed(ms, comment, 60);
    WriteFixed(ms, name, 30);

    Span<byte> u32 = stackalloc byte[4];
    BinaryPrimitives.WriteUInt32LittleEndian(u32, sampleCount);
    ms.Write(u32);

    ms.Write(samplesLe);

    // Zeroed loop + marker trailer; only the MIDI unity note and rate carry data.
    var trailer = new byte[SmpReader.TrailerSize];
    var rateOffset = SmpReader.LoopCount * SmpReader.LoopRecordSize +
                     SmpReader.MarkerCount * SmpReader.MarkerRecordSize;
    trailer[rateOffset] = (byte)midiUnity;
    BinaryPrimitives.WriteUInt32LittleEndian(trailer.AsSpan(rateOffset + 1), (uint)sampleRate);
    ms.Write(trailer);

    return ms.ToArray();
  }

  private static void WriteFixed(Stream s, string text, int length) {
    var field = new byte[length];
    var bytes = Encoding.ASCII.GetBytes(text);
    Array.Copy(bytes, field, Math.Min(bytes.Length, length));
    s.Write(field);
  }
}
