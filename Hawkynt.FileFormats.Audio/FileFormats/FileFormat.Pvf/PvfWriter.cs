#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileFormat.Pvf;

/// <summary>
/// Writes a binary mgetty Portable Voice Format (.pvf) file: the <c>"PVF1\n"</c>
/// magic line, a <c>"&lt;channels&gt; &lt;rate&gt; &lt;bits&gt;\n"</c> header line and
/// one big-endian signed 32-bit integer per sample, sign-extended from the 16-bit
/// source. Used by <see cref="PvfFormatDescriptor"/> to assemble a file from
/// per-channel mono WAVs.
/// </summary>
public sealed class PvfWriter {

  /// <summary>
  /// Builds a PVF1 file (bits = 16) from 16-bit interleaved <paramref name="samples"/>
  /// at <paramref name="sampleRate"/> Hz with <paramref name="numChannels"/> channels.
  /// </summary>
  public byte[] Write(short[] samples, int numChannels, int sampleRate) {
    using var ms = new MemoryStream();
    var header = Encoding.ASCII.GetBytes($"PVF1\n{numChannels} {sampleRate} 16\n");
    ms.Write(header);

    Span<byte> word = stackalloc byte[4];
    foreach (var s in samples) {
      BinaryPrimitives.WriteInt32BigEndian(word, s);
      ms.Write(word);
    }
    return ms.ToArray();
  }
}
