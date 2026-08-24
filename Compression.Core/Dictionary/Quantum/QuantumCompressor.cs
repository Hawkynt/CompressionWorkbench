namespace Compression.Core.Dictionary.Quantum;

/// <summary>
/// Writes Quantum as a cabinet carries it.
/// </summary>
/// <remarks>
/// <para>A folder is closed before any of its models would take a fourth rescale.
/// That rescale sorts the model, and what the sort does to symbols of equal frequency
/// has not been measured; stopping short of it means the writer never has to guess,
/// and a cabinet of several folders is an entirely ordinary cabinet. The cost is
/// ratio, since each folder starts its models afresh — never correctness.</para>
///
/// <para>Everything this emits has been checked by writing it into a cabinet and
/// asking libmspack, through <c>cabextract</c>, to read it back.</para>
/// </remarks>
public static class QuantumCompressor {

  /// <summary>One folder's worth of compressed data.</summary>
  /// <param name="Compressed">The block a cabinet should carry.</param>
  /// <param name="Consumed">How many plain bytes it covers.</param>
  public readonly record struct Folder(byte[] Compressed, int Consumed);


  /// <summary>
  /// Compresses as much of <paramref name="data"/> from <paramref name="offset"/> as
  /// one folder may hold.
  /// </summary>
  /// <param name="data">The data to compress.</param>
  /// <param name="offset">Where in it to start.</param>
  /// <param name="windowBits">The window this folder will name, 10 to 21.</param>
  /// <returns>The block and how far it got.</returns>
  public static Folder CompressFolder(ReadOnlySpan<byte> data, int offset, int windowBits) {
    return CompressBlock(data, offset, offset, windowBits, null);
  }

  /// <summary>
  /// Compresses everything as one folder, as the run of data blocks a cabinet
  /// carries it in.
  /// </summary>
  /// <param name="data">The data to compress.</param>
  /// <param name="windowBits">The window this folder will name, 10 to 21.</param>
  /// <returns>The blocks, in order.</returns>
  /// <remarks>
  /// The models carry across the blocks; only the coder starts afresh in each. A
  /// block that restarts the models decodes as noise from its first byte.
  /// </remarks>
  public static IReadOnlyList<Folder> CompressBlocks(ReadOnlySpan<byte> data, int windowBits) {
    var blocks = new List<Folder>();
    if (data.Length == 0)
      return blocks;

    var models = new QuantumModels(windowBits);
    var offset = 0;
    while (offset < data.Length) {
      var block = CompressBlock(data, 0, offset, windowBits, models);
      if (block.Consumed <= 0)
        throw new InvalidOperationException("The Quantum compressor made no progress.");

      blocks.Add(block);
      offset += block.Consumed;
    }

    return blocks;
  }

  /// <summary>Compresses one block, optionally against models a previous block left.</summary>
  private static Folder CompressBlock(ReadOnlySpan<byte> data, int folderStart, int offset, int windowBits, QuantumModels? carried) {
    ArgumentOutOfRangeException.ThrowIfLessThan(windowBits, QuantumConstants.MinWindowBits, nameof(windowBits));
    ArgumentOutOfRangeException.ThrowIfGreaterThan(windowBits, QuantumConstants.MaxWindowBits, nameof(windowBits));

    var models = carried ?? new QuantumModels(windowBits);
    var encoder = new QuantumRangeEncoder();
    var window = (1 << windowBits) - 1;
    var position = offset;

    while (position < data.Length) {
      if (position - offset >= QuantumConstants.MaxBlockSize)
        break;

      var (length, distance) = FindMatch(data, folderStart, offset, position, window, models);
      if (length >= QuantumConstants.MinMatch) {
        WriteMatch(encoder, models, length, distance);
        position += length;
      } else {
        WriteLiteral(encoder, models, data[position]);
        ++position;
      }
    }

    return new(encoder.Finish(), position - offset);
  }

  /// <summary>
  /// Compresses everything, as the sequence of folders a cabinet should hold.
  /// </summary>
  /// <param name="data">The data to compress.</param>
  /// <param name="windowBits">The window each folder will name.</param>
  /// <returns>The folders, in order.</returns>
  public static IReadOnlyList<Folder> Compress(ReadOnlySpan<byte> data, int windowBits) {
    var folders = new List<Folder>();
    var offset = 0;
    while (offset < data.Length) {
      var folder = CompressFolder(data, offset, windowBits);
      if (folder.Consumed <= 0)
        throw new InvalidOperationException("The Quantum compressor made no progress.");

      folders.Add(folder);
      offset += folder.Consumed;
    }

    return folders;
  }

  private static void WriteLiteral(QuantumRangeEncoder encoder, QuantumModels models, byte value) {
    var selector = value >> 6;
    encoder.Encode(models.Selector, models.Selector.IndexOf(selector));
    var literals = models.Literals[selector];
    encoder.Encode(literals, literals.IndexOf(value));
  }

  private static void WriteMatch(QuantumRangeEncoder encoder, QuantumModels models, int length, int distance) {
    var selector = QuantumConstants.SelectorForLength(length);
    encoder.Encode(models.Selector, models.Selector.IndexOf(selector));

    if (selector == 6) {
      var (lengthSlot, lengthExtra) = QuantumConstants.LengthSlot(length);
      encoder.Encode(models.Lengths, models.Lengths.IndexOf(lengthSlot));
      encoder.EncodeRaw(lengthExtra, QuantumConstants.LengthExtraBits[lengthSlot]);
    }

    var (slot, extra) = QuantumConstants.PositionSlot(distance);
    var positions = models.Positions[selector - 4];
    encoder.Encode(positions, positions.IndexOf(slot));
    encoder.EncodeRaw(extra, QuantumConstants.PositionExtraBits[slot]);
  }

  private static (int Length, int Distance) FindMatch(
      ReadOnlySpan<byte> data, int folderStart, int blockStart, int position, int window, QuantumModels models) {
    var bestLength = 0;
    var bestDistance = 0;

    // a match may reach back over the whole folder, window permitting, but may not
    // run past the block it is in: a data block says how many bytes it decodes to
    var earliest = Math.Max(folderStart, position - window);
    var room = Math.Min(QuantumConstants.MaxMatch, QuantumConstants.MaxBlockSize - (position - blockStart));
    for (var distance = 1; distance <= position - earliest; ++distance) {
      if (data[position - distance] != data[position])
        continue;

      var length = 0;
      while (position + length < data.Length
             && length < room
             && data[position + length] == data[position - distance + length])
        ++length;

      // a shorter match may reach where a longer one's selector cannot
      while (length >= QuantumConstants.MinMatch && !models.CanCode(length, distance))
        --length;

      if (length >= QuantumConstants.MinMatch && length > bestLength) {
        bestLength = length;
        bestDistance = distance;
      }
    }

    return (bestLength, bestDistance);
  }
}
