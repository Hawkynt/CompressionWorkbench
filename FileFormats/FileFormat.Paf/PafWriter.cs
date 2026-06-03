#pragma warning disable CS1591
using System.Buffers.Binary;

namespace FileFormat.Paf;

/// <summary>
/// Writes an Ensoniq PARIS Audio File (.paf): the 24-byte little-endian header marked
/// <c>"fap "</c>, zero-padded to the 2048-byte data offset, followed by interleaved
/// signed 16-bit little-endian PCM. Used by <see cref="PafFormatDescriptor"/> to
/// assemble a file from per-channel mono WAVs.
/// </summary>
public sealed class PafWriter {

  /// <summary>
  /// Builds a 16-bit little-endian PAF from <paramref name="interleavedLe"/> at
  /// <paramref name="sampleRate"/> Hz with <paramref name="numChannels"/> channels.
  /// </summary>
  public byte[] Write(byte[] interleavedLe, int numChannels, int sampleRate) {
    if (numChannels < 1)
      throw new ArgumentException("PAF needs at least one channel.", nameof(numChannels));

    var file = new byte[PafReader.DataOffset + interleavedLe.Length];
    var head = file.AsSpan();
    "fap "u8.CopyTo(head);                                                       // little-endian magic
    BinaryPrimitives.WriteUInt32LittleEndian(head[4..], 0);                      // version
    BinaryPrimitives.WriteUInt32LittleEndian(head[8..], 1);                      // endianness = little
    BinaryPrimitives.WriteUInt32LittleEndian(head[12..], (uint)sampleRate);
    BinaryPrimitives.WriteUInt32LittleEndian(head[16..], (uint)PafReader.FormatPcm16);
    BinaryPrimitives.WriteUInt32LittleEndian(head[20..], (uint)numChannels);

    interleavedLe.CopyTo(file.AsSpan(PafReader.DataOffset));
    return file;
  }
}
