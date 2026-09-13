#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Registry;

namespace FileFormat.Smk;

/// <summary>
/// Packet-preserving Smacker container remuxer. It rebuilds an existing container around already
/// encoded Smacker video/Huffman data and can replace per-frame audio packets without decoding or
/// re-encoding preserved payloads.
/// </summary>
internal static class SmkWriter {

  private const int HeaderSize = 104;
  private const uint FlagRingFrame = 0x01;
  private const int SmkAudPacked = 0x80;
  private static ReadOnlySpan<byte> PacketBundleMagic => "SMKAPKT1"u8;

  public static void Remux(
      Stream sourceStream,
      Stream output,
      IReadOnlyList<ArchiveInputInfo> replacements,
      FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(sourceStream);
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(replacements);
    ArgumentNullException.ThrowIfNull(options);
    _ = options;

    using var sourceBuffer = new MemoryStream();
    sourceStream.CopyTo(sourceBuffer);
    var original = sourceBuffer.ToArray();

    var packetOverrides = new ArchiveInputInfo?[7];
    foreach (var input in replacements) {
      if (input.IsDirectory)
        continue;

      var track = PacketTrackIndex(LeafName(input.ArchiveName));
      if (track < 0)
        throw new InvalidDataException(
          $"Smacker remux only accepts TRACK0.packets through TRACK6.packets replacements; got '{input.ArchiveName}'."
        );
      if (packetOverrides[track] is not null)
        throw new InvalidDataException($"Smacker remux received TRACK{track}.packets more than once.");
      packetOverrides[track] = input;
    }

    if (packetOverrides.All(static input => input is null)) {
      output.Write(original);
      return;
    }

    var source = SmkSource.ParseFull(original);
    var changed = false;
    for (var track = 0; track < packetOverrides.Length; ++track) {
      var input = packetOverrides[track];
      if (input is null)
        continue;

      var replacement = DecodeAudioPacketBundle(input.ReadContent(), source.FrameCount);
      if (source.TrackPacketsEqual(track, replacement))
        continue;

      source.ReplaceTrackPackets(track, replacement);
      changed = true;
    }

    if (!changed) {
      output.Write(original);
      return;
    }

    source.Write(output);
  }

  internal static byte[] EncodeAudioPacketBundle(IReadOnlyList<(int FrameIndex, byte[] Payload)> packets) {
    ArgumentNullException.ThrowIfNull(packets);

    using var output = new MemoryStream();
    output.Write(PacketBundleMagic);
    WriteUInt32(output, checked((uint)packets.Count));
    foreach (var (frameIndex, payload) in packets.OrderBy(static packet => packet.FrameIndex)) {
      if (frameIndex < 0)
        throw new InvalidDataException("Smacker audio packet frame indices cannot be negative.");
      WriteUInt32(output, checked((uint)frameIndex));
      WriteUInt32(output, checked((uint)payload.Length));
      output.Write(payload);
    }
    return output.ToArray();
  }

  private static Dictionary<int, byte[]> DecodeAudioPacketBundle(ReadOnlySpan<byte> data, int frameCount) {
    if (data.Length < 12 || !data[..8].SequenceEqual(PacketBundleMagic))
      throw new InvalidDataException("Smacker audio packet bundle has an invalid SMKAPKT1 header.");

    var count = BinaryPrimitives.ReadUInt32LittleEndian(data[8..]);
    if (count > (uint)frameCount)
      throw new InvalidDataException("Smacker audio packet bundle contains more packets than the container has frames.");

    var result = new Dictionary<int, byte[]>(checked((int)count));
    var offset = 12;
    for (var i = 0u; i < count; ++i) {
      if (offset > data.Length - 8)
        throw new InvalidDataException("Smacker audio packet bundle is truncated before a packet header.");

      var frameIndex = BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]);
      var payloadLength = BinaryPrimitives.ReadUInt32LittleEndian(data[(offset + 4)..]);
      offset += 8;
      if (frameIndex >= (uint)frameCount)
        throw new InvalidDataException($"Smacker audio packet refers to frame {frameIndex}, but the container has only {frameCount} frames.");
      if (payloadLength > int.MaxValue || payloadLength > (uint)(data.Length - offset))
        throw new InvalidDataException("Smacker audio packet bundle contains a truncated packet payload.");

      var index = checked((int)frameIndex);
      if (!result.TryAdd(index, data.Slice(offset, checked((int)payloadLength)).ToArray()))
        throw new InvalidDataException($"Smacker audio packet bundle contains frame {index} more than once.");
      offset += checked((int)payloadLength);
    }

    if (offset != data.Length)
      throw new InvalidDataException("Smacker audio packet bundle has trailing bytes.");
    return result;
  }

  private static int PacketTrackIndex(string name)
    => name is ['T' or 't', 'R' or 'r', 'A' or 'a', 'C' or 'c', 'K' or 'k', >= '0' and <= '6', '.', 'p' or 'P', 'a' or 'A', 'c' or 'C', 'k' or 'K', 'e' or 'E', 't' or 'T', 's' or 'S']
      ? name[5] - '0'
      : -1;

  private static string LeafName(string name) {
    var slash = Math.Max(name.LastIndexOf('/'), name.LastIndexOf('\\'));
    return slash >= 0 ? name[(slash + 1)..] : name;
  }

  private static void WriteUInt32(Stream output, uint value) {
    Span<byte> bytes = stackalloc byte[4];
    BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
    output.Write(bytes);
  }

  private sealed class SmkSource {
    private readonly byte[] _header;
    private readonly byte[] _trees;
    private readonly List<Frame> _frames;
    private readonly byte[] _trailing;

    private SmkSource(byte[] header, byte[] trees, List<Frame> frames, byte[] trailing) {
      this._header = header;
      this._trees = trees;
      this._frames = frames;
      this._trailing = trailing;
    }

    public int FrameCount => this._frames.Count;

    public static SmkSource ParseFull(byte[] data) {
      ArgumentNullException.ThrowIfNull(data);
      if (data.Length < HeaderSize)
        throw new InvalidDataException("Smacker container is shorter than its 104-byte header.");

      var header = data[..HeaderSize];
      var (frameCount, treeSize) = ReadHeader(header);
      var frameSizeBytes = checked(frameCount * 4);
      var tablesEnd = checked(HeaderSize + frameSizeBytes + frameCount);
      var dataStart = checked(tablesEnd + treeSize);
      if (dataStart > data.Length)
        throw new InvalidDataException("Smacker frame tables or Huffman trees extend past the end of the container.");

      return ParseParts(
        header,
        data.AsSpan(HeaderSize, frameSizeBytes).ToArray(),
        data.AsSpan(HeaderSize + frameSizeBytes, frameCount).ToArray(),
        data.AsSpan(tablesEnd, treeSize).ToArray(),
        data[dataStart..]
      );
    }

    private static SmkSource ParseParts(
        byte[] header,
        byte[] frameSizes,
        byte[] frameTypes,
        byte[] trees,
        byte[] frameData) {
      if (header.Length != HeaderSize)
        throw new InvalidDataException($"Smacker header must be exactly {HeaderSize} bytes.");

      var (frameCount, treeSize) = ReadHeader(header);
      if (frameSizes.Length != checked(frameCount * 4))
        throw new InvalidDataException("Smacker frame-size table length does not match the physical frame count.");
      if (frameTypes.Length != frameCount)
        throw new InvalidDataException("Smacker frame-type table length does not match the physical frame count.");
      if (trees.Length != treeSize)
        throw new InvalidDataException("Smacker Huffman block length does not match TreesSize in the header.");

      var frames = new List<Frame>(frameCount);
      var offset = 0;
      for (var index = 0; index < frameCount; ++index) {
        var sizeEntry = BinaryPrimitives.ReadUInt32LittleEndian(frameSizes.AsSpan(index * 4));
        var frameLength = sizeEntry & ~3u;
        if (frameLength > int.MaxValue || frameLength > (uint)(frameData.Length - offset))
          throw new InvalidDataException($"Smacker frame {index} extends past the available frame data.");

        frames.Add(ParseFrame(
          frameData.AsSpan(offset, checked((int)frameLength)),
          frameTypes[index],
          (byte)(sizeEntry & 3)
        ));
        offset += checked((int)frameLength);
      }

      return new SmkSource(header.ToArray(), trees.ToArray(), frames, frameData[offset..]);
    }

    public bool TrackPacketsEqual(int track, IReadOnlyDictionary<int, byte[]> replacement) {
      var present = 0;
      for (var frameIndex = 0; frameIndex < this._frames.Count; ++frameIndex) {
        var hasExisting = this._frames[frameIndex].Audio.TryGetValue(track, out var existing);
        var hasReplacement = replacement.TryGetValue(frameIndex, out var candidate);
        if (hasExisting != hasReplacement)
          return false;
        if (!hasExisting)
          continue;
        ++present;
        if (!existing!.AsSpan().SequenceEqual(candidate))
          return false;
      }
      return present == replacement.Count;
    }

    public void ReplaceTrackPackets(int track, IReadOnlyDictionary<int, byte[]> replacement) {
      if ((uint)track >= 7)
        throw new ArgumentOutOfRangeException(nameof(track));

      var descriptorOffset = 72 + track * 4;
      var sampleRate = this._header[descriptorOffset]
        | this._header[descriptorOffset + 1] << 8
        | this._header[descriptorOffset + 2] << 16;
      if (replacement.Count != 0 && sampleRate == 0)
        throw new InvalidDataException($"Smacker track {track} is not declared in the source header; its codec/rate cannot be inferred safely.");

      foreach (var frame in this._frames)
        frame.Audio.Remove(track);
      foreach (var (frameIndex, payload) in replacement)
        this._frames[frameIndex].Audio.Add(track, payload);

      var maxUnpacked = 0u;
      var audioFlags = this._header[descriptorOffset + 3];
      foreach (var payload in replacement.Values) {
        uint unpacked;
        if ((audioFlags & SmkAudPacked) != 0) {
          if (payload.Length < 4)
            throw new InvalidDataException($"Compressed Smacker audio packet for track {track} lacks its unpacked-length prefix.");
          unpacked = BinaryPrimitives.ReadUInt32LittleEndian(payload);
        } else {
          unpacked = checked((uint)payload.Length);
        }
        maxUnpacked = Math.Max(maxUnpacked, unpacked);
      }
      BinaryPrimitives.WriteUInt32LittleEndian(this._header.AsSpan(24 + track * 4), maxUnpacked);
    }

    public void Write(Stream output) {
      var encodedFrames = new byte[this._frames.Count][];
      var frameSizes = new uint[this._frames.Count];
      var frameTypes = new byte[this._frames.Count];

      for (var index = 0; index < this._frames.Count; ++index) {
        var frame = this._frames[index];
        using var body = new MemoryStream();
        body.Write(frame.Palette);

        var frameType = frame.Palette.Length == 0 ? (byte)0 : (byte)1;
        for (var track = 0; track < 7; ++track) {
          if (!frame.Audio.TryGetValue(track, out var payload))
            continue;
          frameType |= (byte)(1 << (track + 1));
          WriteUInt32(body, checked((uint)payload.Length + 4));
          body.Write(payload);
        }
        body.Write(frame.Video);

        while ((body.Length & 3) != 0)
          body.WriteByte(0);
        if (body.Length > uint.MaxValue - 3)
          throw new InvalidDataException($"Smacker frame {index} is too large to encode.");

        encodedFrames[index] = body.ToArray();
        frameSizes[index] = checked((uint)encodedFrames[index].Length) | frame.SizeFlags;
        frameTypes[index] = frameType;
      }

      output.Write(this._header);
      foreach (var size in frameSizes)
        WriteUInt32(output, size);
      output.Write(frameTypes);
      output.Write(this._trees);
      foreach (var frame in encodedFrames)
        output.Write(frame);
      output.Write(this._trailing);
    }

    private static Frame ParseFrame(ReadOnlySpan<byte> data, byte frameType, byte sizeFlags) {
      var offset = 0;
      byte[] palette = [];
      if ((frameType & 1) != 0) {
        if (data.IsEmpty)
          throw new InvalidDataException("Smacker palette flag is set on an empty frame.");
        var paletteLength = data[0] * 4;
        if (paletteLength == 0 || paletteLength > data.Length)
          throw new InvalidDataException("Smacker palette block extends past its frame.");
        palette = data[..paletteLength].ToArray();
        offset = paletteLength;
      }

      var audio = new Dictionary<int, byte[]>();
      for (var track = 0; track < 7; ++track) {
        if ((frameType & (1 << (track + 1))) == 0)
          continue;
        if (offset > data.Length - 4)
          throw new InvalidDataException($"Smacker frame audio header for track {track} is truncated.");

        var packetLength = BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]);
        if (packetLength < 4 || packetLength > int.MaxValue || packetLength > (uint)(data.Length - offset))
          throw new InvalidDataException($"Smacker frame audio packet for track {track} extends past its frame.");
        audio.Add(track, data.Slice(offset + 4, checked((int)packetLength - 4)).ToArray());
        offset += checked((int)packetLength);
      }

      return new Frame(palette, audio, data[offset..].ToArray(), sizeFlags);
    }

    private static (int FrameCount, int TreeSize) ReadHeader(ReadOnlySpan<byte> header) {
      if (header.Length != HeaderSize ||
          !(header[..4].SequenceEqual("SMK2"u8) || header[..4].SequenceEqual("SMK4"u8)))
        throw new InvalidDataException("Smacker header has an invalid signature or size.");

      var logicalFrames = BinaryPrimitives.ReadUInt32LittleEndian(header[12..]);
      var flags = BinaryPrimitives.ReadUInt32LittleEndian(header[20..]);
      var physicalFrames = (ulong)logicalFrames + ((flags & FlagRingFrame) != 0 ? 1UL : 0UL);
      if (physicalFrames > 0xFFFFFF || physicalFrames > int.MaxValue)
        throw new InvalidDataException("Smacker frame count is outside the supported range.");

      var treeSize = BinaryPrimitives.ReadUInt32LittleEndian(header[52..]);
      if (treeSize > int.MaxValue)
        throw new InvalidDataException("Smacker Huffman tree block is too large.");
      return (checked((int)physicalFrames), checked((int)treeSize));
    }
  }

  private sealed record Frame(
    byte[] Palette,
    Dictionary<int, byte[]> Audio,
    byte[] Video,
    byte SizeFlags
  );
}