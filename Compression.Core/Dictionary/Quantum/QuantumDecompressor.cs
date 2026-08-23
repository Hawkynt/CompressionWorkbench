namespace Compression.Core.Dictionary.Quantum;

/// <summary>
/// Reads Quantum as a cabinet carries it.
/// </summary>
/// <remarks>
/// <para>A match may reach back past the start of the folder, and what it finds there
/// is zeros: the window begins empty and Quantum encoders do use it. A decoder that
/// refuses such a match stops on perfectly ordinary streams.</para>
///
/// <para>A folder of several data blocks is one stream in the models and a fresh one
/// in the coder: the models carry across, the coder is primed again in each block, and
/// a match may reach back into the blocks before it. Restarting the models decodes the
/// second block as noise from its first byte.</para>
/// </remarks>
public static class QuantumDecompressor {

  /// <summary>Reads the data blocks of one folder, which share their models.</summary>
  public sealed class FolderReader {
    private readonly QuantumModels _models;
    private readonly List<byte> _window = [];

    /// <summary>Starts a folder.</summary>
    /// <param name="windowBits">The window the folder names, 10 to 21.</param>
    public FolderReader(int windowBits) {
      ArgumentOutOfRangeException.ThrowIfLessThan(windowBits, QuantumConstants.MinWindowBits, nameof(windowBits));
      ArgumentOutOfRangeException.ThrowIfGreaterThan(windowBits, QuantumConstants.MaxWindowBits, nameof(windowBits));
      this._models = new(windowBits);
    }

    /// <summary>Reads the next block.</summary>
    /// <param name="compressed">The block as the cabinet carries it.</param>
    /// <param name="uncompressedSize">How many bytes it should yield.</param>
    /// <returns>The plain bytes of this block.</returns>
    public byte[] ReadBlock(ReadOnlyMemory<byte> compressed, int uncompressedSize) {
      var block = Decode(compressed, uncompressedSize, this._models, this._window);
      this._window.AddRange(block);
      return block;
    }
  }

  /// <summary>Decompresses one folder's block.</summary>
  /// <param name="compressed">The block as the cabinet carries it.</param>
  /// <param name="uncompressedSize">How many bytes it should yield.</param>
  /// <param name="windowBits">The window the folder names, 10 to 21.</param>
  /// <returns>The plain bytes.</returns>
  public static byte[] Decompress(ReadOnlyMemory<byte> compressed, int uncompressedSize, int windowBits) {
    ArgumentOutOfRangeException.ThrowIfNegative(uncompressedSize, nameof(uncompressedSize));
    ArgumentOutOfRangeException.ThrowIfLessThan(windowBits, QuantumConstants.MinWindowBits, nameof(windowBits));
    ArgumentOutOfRangeException.ThrowIfGreaterThan(windowBits, QuantumConstants.MaxWindowBits, nameof(windowBits));

    return Decode(compressed, uncompressedSize, new QuantumModels(windowBits), []);
  }

  private static byte[] Decode(
      ReadOnlyMemory<byte> compressed, int uncompressedSize, QuantumModels models, List<byte> window) {
    var decoder = new QuantumRangeDecoder(compressed);
    var output = new byte[uncompressedSize];
    var written = 0;

    while (written < uncompressedSize) {
      var selector = decoder.Decode(models.Selector);
      if (selector < 4) {
        output[written++] = (byte)decoder.Decode(models.Literals[selector]);
        continue;
      }

      int length;
      if (selector == 6) {
        var slot = decoder.Decode(models.Lengths);
        var extra = decoder.DecodeRaw(QuantumConstants.LengthExtraBits[slot]);
        length = 5 + QuantumConstants.LengthBases[slot] + extra;
      } else
        length = selector == 4 ? 3 : 4;

      var positionSlot = decoder.Decode(models.Positions[selector - 4]);
      var positionExtra = decoder.DecodeRaw(QuantumConstants.PositionExtraBits[positionSlot]);
      var distance = QuantumConstants.PositionBases[positionSlot] + positionExtra + 1;

      for (var i = 0; i < length && written < uncompressedSize; ++i) {
        var source = written - distance;
        output[written++] = source >= 0 ? output[source]
          : window.Count + source >= 0 ? window[window.Count + source]
          : (byte)0;
      }
    }

    return output;
  }
}
