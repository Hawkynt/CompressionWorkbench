#pragma warning disable CS1591

using System.Buffers.Binary;
using Compression.Registry;

namespace FileFormat.Ape;

/// <summary>
/// Parser/writer for the modern (3.98+) Monkey's Audio container around already-coded APE frames.
/// Seek-table entries are relative to the <c>MAC </c> signature, not the physical file start;
/// leading ID3/JUNK bytes therefore remain outside the address space just as in the reference SDK.
/// </summary>
internal static class ApeContainer {

  internal const int DescriptorSize = 52;
  internal const int HeaderSize = 24;
  internal const int MinModernVersion = 3980;
  internal const int MaxKnownVersion = 3990;

  internal sealed record Frame(uint SeekOffset, long DurationSamples, byte[] Data);

  internal sealed class Parsed {
    public required byte[] Original { get; init; }
    public required int MacOffset { get; init; }
    public required ushort Version { get; init; }
    public required ushort CompressionLevel { get; init; }
    public required ushort FormatFlags { get; init; }
    public required uint BlocksPerFrame { get; init; }
    public required uint FinalFrameBlocks { get; init; }
    public required uint TotalFrames { get; init; }
    public required ushort BitsPerSample { get; init; }
    public required ushort Channels { get; init; }
    public required uint SampleRate { get; init; }
    public required int DescriptorLength { get; init; }
    public required int HeaderLength { get; init; }
    public required int SeekTableLength { get; init; }
    public required int HeaderDataLength { get; init; }
    public required ulong AudioDataLength { get; init; }
    public required int TerminatingDataLength { get; init; }
    public required int FrameDataStart { get; init; }
    public required int FrameDataEnd { get; init; }
    public required int TailEnd { get; init; }
    public required byte[] LeadingData { get; init; }
    public required byte[] SeekTableData { get; init; }
    public required byte[] HeaderData { get; init; }
    public required byte[] AudioData { get; init; }
    public required byte[] TerminatingData { get; init; }
    public required byte[] TrailingData { get; init; }
    public required IReadOnlyList<Frame> Frames { get; init; }

    public long TotalSamples => this.TotalFrames == 0
      ? 0
      : checked((long)(this.TotalFrames - 1) * this.BlocksPerFrame + this.FinalFrameBlocks);

    public byte[] CodecPayload => this.Original.AsSpan(this.MacOffset, this.FrameDataEnd - this.MacOffset).ToArray();
  }

  internal static int FindMacOffset(ReadOnlySpan<byte> file) {
    for (var offset = 0; offset + 6 <= file.Length; ++offset) {
      if (!file.Slice(offset, 4).SequenceEqual("MAC "u8))
        continue;
      var version = BinaryPrimitives.ReadUInt16LittleEndian(file[(offset + 4)..]);
      if (version is >= MinModernVersion and <= MaxKnownVersion)
        return offset;
    }
    return -1;
  }

  internal static bool TryParseModern(byte[] file, out Parsed? parsed) {
    ArgumentNullException.ThrowIfNull(file);
    parsed = null;

    var macOffset = FindMacOffset(file);
    if (macOffset < 0 || file.Length - macOffset < DescriptorSize)
      return false;

    var descriptor = file.AsSpan(macOffset);
    var version = BinaryPrimitives.ReadUInt16LittleEndian(descriptor[4..]);
    var descriptorLength64 = BinaryPrimitives.ReadUInt32LittleEndian(descriptor[8..]);
    var headerLength64 = BinaryPrimitives.ReadUInt32LittleEndian(descriptor[12..]);
    var seekTableLength64 = BinaryPrimitives.ReadUInt32LittleEndian(descriptor[16..]);
    var headerDataLength64 = BinaryPrimitives.ReadUInt32LittleEndian(descriptor[20..]);
    var audioDataLow = BinaryPrimitives.ReadUInt32LittleEndian(descriptor[24..]);
    var audioDataHigh = BinaryPrimitives.ReadUInt32LittleEndian(descriptor[28..]);
    var terminatingDataLength64 = BinaryPrimitives.ReadUInt32LittleEndian(descriptor[32..]);
    var audioDataLength = audioDataLow | ((ulong)audioDataHigh << 32);

    if (descriptorLength64 is < DescriptorSize or > int.MaxValue ||
        headerLength64 is < HeaderSize or > int.MaxValue ||
        seekTableLength64 > int.MaxValue || headerDataLength64 > int.MaxValue ||
        terminatingDataLength64 > int.MaxValue)
      return false;

    var descriptorLength = (int)descriptorLength64;
    var headerLength = (int)headerLength64;
    var seekTableLength = (int)seekTableLength64;
    var headerDataLength = (int)headerDataLength64;
    var terminatingDataLength = (int)terminatingDataLength64;

    var headerStart = (long)macOffset + descriptorLength;
    var headerEnd = headerStart + headerLength;
    var seekStart = headerEnd;
    var seekEnd = seekStart + seekTableLength;
    var frameDataStart64 = seekEnd + headerDataLength;
    if (headerStart < 0 || headerEnd > file.Length || seekEnd > file.Length || frameDataStart64 > file.Length)
      return false;

    var header = file.AsSpan((int)headerStart, headerLength);
    var compressionLevel = BinaryPrimitives.ReadUInt16LittleEndian(header);
    var formatFlags = BinaryPrimitives.ReadUInt16LittleEndian(header[2..]);
    var blocksPerFrame = BinaryPrimitives.ReadUInt32LittleEndian(header[4..]);
    var finalFrameBlocks = BinaryPrimitives.ReadUInt32LittleEndian(header[8..]);
    var totalFrames = BinaryPrimitives.ReadUInt32LittleEndian(header[12..]);
    var bitsPerSample = BinaryPrimitives.ReadUInt16LittleEndian(header[16..]);
    var channels = BinaryPrimitives.ReadUInt16LittleEndian(header[18..]);
    var sampleRate = BinaryPrimitives.ReadUInt32LittleEndian(header[20..]);

    if ((ulong)seekTableLength < (ulong)totalFrames * sizeof(uint))
      return false;

    var frameDataStart = (int)frameDataStart64;
    var frameDataEnd64 = (ulong)frameDataStart + audioDataLength;
    if (frameDataEnd64 > (ulong)file.Length || frameDataEnd64 > int.MaxValue)
      return false;
    var frameDataEnd = (int)frameDataEnd64;

    var tailEnd64 = (ulong)frameDataEnd + (uint)terminatingDataLength;
    if (tailEnd64 > (ulong)file.Length || tailEnd64 > int.MaxValue)
      return false;
    var tailEnd = (int)tailEnd64;

    var frames = new List<Frame>(checked((int)totalFrames));
    for (var index = 0; index < totalFrames; ++index) {
      var seekOffset = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan((int)seekStart + checked((int)index * 4), 4));
      var start64 = (ulong)macOffset + seekOffset;
      ulong end64;
      if (index + 1 < totalFrames) {
        var next = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan((int)seekStart + checked((int)(index + 1) * 4), 4));
        end64 = (ulong)macOffset + next;
      } else
        end64 = (ulong)frameDataEnd;

      if (start64 < (ulong)frameDataStart || start64 > (ulong)frameDataEnd || end64 < start64 || end64 > (ulong)frameDataEnd)
        return false;

      var start = (int)start64;
      var end = (int)end64;
      var duration = index + 1 == totalFrames ? finalFrameBlocks : blocksPerFrame;
      frames.Add(new Frame(seekOffset, duration, file.AsSpan(start, end - start).ToArray()));
    }

    parsed = new Parsed {
      Original = file,
      MacOffset = macOffset,
      Version = version,
      CompressionLevel = compressionLevel,
      FormatFlags = formatFlags,
      BlocksPerFrame = blocksPerFrame,
      FinalFrameBlocks = finalFrameBlocks,
      TotalFrames = totalFrames,
      BitsPerSample = bitsPerSample,
      Channels = channels,
      SampleRate = sampleRate,
      DescriptorLength = descriptorLength,
      HeaderLength = headerLength,
      SeekTableLength = seekTableLength,
      HeaderDataLength = headerDataLength,
      AudioDataLength = audioDataLength,
      TerminatingDataLength = terminatingDataLength,
      FrameDataStart = frameDataStart,
      FrameDataEnd = frameDataEnd,
      TailEnd = tailEnd,
      LeadingData = file.AsSpan(0, macOffset).ToArray(),
      SeekTableData = file.AsSpan((int)seekStart, seekTableLength).ToArray(),
      HeaderData = file.AsSpan((int)seekEnd, headerDataLength).ToArray(),
      AudioData = file.AsSpan(frameDataStart, frameDataEnd - frameDataStart).ToArray(),
      TerminatingData = file.AsSpan(frameDataEnd, terminatingDataLength).ToArray(),
      TrailingData = file.AsSpan(tailEnd).ToArray(),
      Frames = frames,
    };
    return true;
  }

  internal static void WriteModern(
      Stream output,
      int version,
      int compressionLevel,
      ushort formatFlags,
      uint blocksPerFrame,
      int bitsPerSample,
      int channels,
      int sampleRate,
      IReadOnlyList<AudioPacket> packets,
      ReadOnlySpan<byte> leadingData,
      ReadOnlySpan<byte> headerData,
      ReadOnlySpan<byte> terminatingData,
      ReadOnlySpan<byte> trailingData) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(packets);
    if (version is < MinModernVersion or > ushort.MaxValue)
      throw new NotSupportedException($"Monkey's Audio muxing requires a modern file version, got {version}.");
    if (compressionLevel is < 0 or > ushort.MaxValue)
      throw new ArgumentOutOfRangeException(nameof(compressionLevel));
    if (bitsPerSample is < 0 or > ushort.MaxValue || channels is < 1 or > ushort.MaxValue || sampleRate < 1)
      throw new ArgumentOutOfRangeException(nameof(bitsPerSample), "Invalid Monkey's Audio stream geometry.");

    var frames = packets.Where(static packet => !packet.IsHeader).ToArray();
    if (frames.Length == 0)
      throw new ArgumentException("Monkey's Audio muxing requires at least one coded frame.", nameof(packets));
    if (packets.Any(static packet => packet.IsHeader))
      throw new NotSupportedException("Monkey's Audio has no separate header packets; codec-private data belongs in the container envelope.");

    blocksPerFrame = blocksPerFrame != 0
      ? blocksPerFrame
      : checked((uint)frames[0].DurationSamples);
    if (blocksPerFrame == 0)
      throw new InvalidDataException("Monkey's Audio blocks-per-frame must be positive.");

    for (var index = 0; index < frames.Length; ++index) {
      var duration = frames[index].DurationSamples;
      if (duration <= 0 || duration > uint.MaxValue)
        throw new InvalidDataException($"APE frame {index} has invalid duration {duration}.");
      if (index + 1 < frames.Length && duration != blocksPerFrame)
        throw new InvalidDataException($"APE frame {index} has {duration} blocks; non-final frames require {blocksPerFrame}.");
    }

    var finalFrameBlocks = checked((uint)frames[^1].DurationSamples);
    var seekTableLength64 = checked((ulong)frames.Length * 4);
    if (seekTableLength64 > uint.MaxValue)
      throw new NotSupportedException("Monkey's Audio seek table exceeds its 32-bit length field.");
    if ((ulong)headerData.Length > uint.MaxValue || (ulong)terminatingData.Length > uint.MaxValue)
      throw new NotSupportedException("Monkey's Audio WAV header/tail data exceeds its 32-bit length field.");

    ulong audioDataLength = 0;
    foreach (var frame in frames)
      audioDataLength = checked(audioDataLength + (ulong)frame.Data.LongLength);

    var seekTableLength = (uint)seekTableLength64;
    var frameDataStart = checked((ulong)(DescriptorSize + HeaderSize) + seekTableLength + (ulong)headerData.Length);
    var running = frameDataStart;
    foreach (var frame in frames) {
      if (running > uint.MaxValue)
        throw new NotSupportedException("Monkey's Audio frame seek offsets are 32-bit and this stream exceeds that address space.");
      running = checked(running + (ulong)frame.Data.LongLength);
    }

    output.Write(leadingData);

    Span<byte> descriptor = stackalloc byte[DescriptorSize];
    "MAC "u8.CopyTo(descriptor);
    BinaryPrimitives.WriteUInt16LittleEndian(descriptor[4..], checked((ushort)version));
    BinaryPrimitives.WriteUInt16LittleEndian(descriptor[6..], 0);
    BinaryPrimitives.WriteUInt32LittleEndian(descriptor[8..], DescriptorSize);
    BinaryPrimitives.WriteUInt32LittleEndian(descriptor[12..], HeaderSize);
    BinaryPrimitives.WriteUInt32LittleEndian(descriptor[16..], seekTableLength);
    BinaryPrimitives.WriteUInt32LittleEndian(descriptor[20..], checked((uint)headerData.Length));
    BinaryPrimitives.WriteUInt32LittleEndian(descriptor[24..], (uint)audioDataLength);
    BinaryPrimitives.WriteUInt32LittleEndian(descriptor[28..], (uint)(audioDataLength >> 32));
    BinaryPrimitives.WriteUInt32LittleEndian(descriptor[32..], checked((uint)terminatingData.Length));
    // MD5 is deliberately zero after packet editing: retaining the source digest would be a lie.
    output.Write(descriptor);

    Span<byte> header = stackalloc byte[HeaderSize];
    BinaryPrimitives.WriteUInt16LittleEndian(header, checked((ushort)compressionLevel));
    BinaryPrimitives.WriteUInt16LittleEndian(header[2..], formatFlags);
    BinaryPrimitives.WriteUInt32LittleEndian(header[4..], blocksPerFrame);
    BinaryPrimitives.WriteUInt32LittleEndian(header[8..], finalFrameBlocks);
    BinaryPrimitives.WriteUInt32LittleEndian(header[12..], checked((uint)frames.Length));
    BinaryPrimitives.WriteUInt16LittleEndian(header[16..], checked((ushort)bitsPerSample));
    BinaryPrimitives.WriteUInt16LittleEndian(header[18..], checked((ushort)channels));
    BinaryPrimitives.WriteUInt32LittleEndian(header[20..], checked((uint)sampleRate));
    output.Write(header);

    Span<byte> seek = stackalloc byte[4];
    running = frameDataStart;
    foreach (var frame in frames) {
      BinaryPrimitives.WriteUInt32LittleEndian(seek, checked((uint)running));
      output.Write(seek);
      running += (ulong)frame.Data.LongLength;
    }

    output.Write(headerData);
    foreach (var frame in frames)
      output.Write(frame.Data);
    output.Write(terminatingData);
    output.Write(trailingData);
  }
}
