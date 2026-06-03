#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Codec.Pcm;

namespace FileFormat.Its;

/// <summary>
/// Decodes an Impulse Tracker 80-byte <c>IMPS</c> sample header (the same structure used
/// both for standalone <c>.its</c> files and embedded inside <c>.it</c> / <c>.iti</c>) into
/// a playable mono WAV. Shared by <see cref="ItsFormatDescriptor"/> and the ITI instrument
/// descriptor (which scans for <c>IMPS</c> blocks).
/// </summary>
public static class ItsSampleDecoder {

  public const int HeaderSize = 80;
  public const int FallbackSampleRate = 8363;

  /// <summary>Parsed header fields plus the resolved on-disk PCM data range.</summary>
  public readonly record struct ParsedSample(
    string Name, string DosName, int SampleRate, int Bits, bool Signed, bool Compressed,
    int DataOffset, int ByteLength);

  /// <summary>
  /// Reads the IMPS header at <paramref name="headerOff"/>. The sample pointer is treated as
  /// a file-absolute offset (the .it / .its convention); a zero / out-of-range pointer leaves
  /// <see cref="ParsedSample.ByteLength"/> at 0. Returns false when there is no valid header.
  /// </summary>
  public static bool TryParse(byte[] blob, int headerOff, out ParsedSample result) {
    result = default;
    if (headerOff < 0 || headerOff + HeaderSize > blob.Length) return false;
    if (!(blob[headerOff] == 'I' && blob[headerOff + 1] == 'M' && blob[headerOff + 2] == 'P' && blob[headerOff + 3] == 'S'))
      return false;

    var dosName = ReadAsciiTrim(blob, headerOff + 4, 12);
    var flags = blob[headerOff + 18];
    var name = ReadAsciiTrim(blob, headerOff + 20, 26);
    var cvt = blob[headerOff + 46];
    var lengthSamples = (int)Math.Min(int.MaxValue, BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(headerOff + 48, 4)));
    var c5speed = (int)Math.Min(int.MaxValue, BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(headerOff + 60, 4)));
    var samplePointer = (int)Math.Min(int.MaxValue, BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(headerOff + 72, 4)));

    var is16 = (flags & 0x02) != 0;
    var compressed = (flags & 0x08) != 0;
    var signed = (cvt & 0x01) != 0;
    var rate = c5speed > 0 ? c5speed : FallbackSampleRate;
    var bits = is16 ? 16 : 8;

    var byteLen = 0;
    var dataOff = samplePointer;
    if (lengthSamples > 0 && samplePointer > 0 && samplePointer < blob.Length) {
      var wanted = (long)lengthSamples * (is16 ? 2 : 1);
      byteLen = (int)Math.Min(wanted, blob.Length - samplePointer);
      if (byteLen < 0) byteLen = 0;
    } else {
      dataOff = 0;
    }

    result = new ParsedSample(name, dosName, rate, bits, signed, compressed, dataOff, byteLen);
    return true;
  }

  /// <summary>
  /// Builds a playable mono WAV from a parsed IMPS sample. 8-bit samples are rebiased to
  /// WAV's unsigned 8-bit (signed-8 → +128, already-unsigned → pass through); 16-bit signed
  /// is passed through, 16-bit unsigned is rebiased to signed. Returns null when the sample is
  /// compressed (IT215 packing is not decoded) or has no usable data.
  /// </summary>
  public static byte[]? BuildWav(byte[] blob, in ParsedSample s) {
    if (s.Compressed || s.ByteLength <= 0 || s.DataOffset <= 0) return null;
    var raw = blob.AsSpan(s.DataOffset, s.ByteLength);
    if (s.Bits == 16) {
      if (s.Signed) return PcmCodec.ToWavBlob(raw.ToArray(), 1, s.SampleRate, 16);
      var conv = new byte[raw.Length];
      for (var i = 0; i + 1 < raw.Length; i += 2) {
        var v = (ushort)(raw[i] | (raw[i + 1] << 8));
        var sv = unchecked((short)(v - 32768));
        conv[i] = (byte)(sv & 0xFF);
        conv[i + 1] = (byte)((sv >> 8) & 0xFF);
      }
      return PcmCodec.ToWavBlob(conv, 1, s.SampleRate, 16);
    }
    if (s.Signed) {
      var u = new byte[raw.Length];
      for (var i = 0; i < raw.Length; ++i) u[i] = unchecked((byte)(raw[i] + 128));
      return PcmCodec.ToWavBlob(u, 1, s.SampleRate, 8);
    }
    return PcmCodec.ToWavBlob(raw.ToArray(), 1, s.SampleRate, 8);
  }

  public static string ReadAsciiTrim(byte[] blob, int offset, int length) {
    var end = Math.Min(offset + length, blob.Length);
    var sb = new StringBuilder();
    for (var i = offset; i < end; ++i) {
      var b = blob[i];
      if (b == 0) break;
      if (b >= 0x20 && b < 0x7F) sb.Append((char)b);
    }
    return sb.ToString().Trim();
  }

  public static string SanitizeFileName(string name) {
    var sb = new StringBuilder(name.Length);
    foreach (var c in name)
      sb.Append(char.IsLetterOrDigit(c) || c is '_' or '-' or '.' ? c : '_');
    var s = sb.ToString().Trim('.');
    return s.Length == 0 ? "sample" : s;
  }
}
