namespace OggVorbisEncoder.Setup.Templates.BookBlocks.Stereo44.Uncoupled.Chapter2;

/// <summary>
/// Represents a chapter 2 long.
/// </summary>
public class Chapter2Long : IStaticCodeBook
{
        /// <summary>
    /// Gets the dimensions.
    /// </summary>
public int Dimensions { get; } = 2;

        /// <summary>
    /// Gets the length list.
    /// </summary>
public byte[] LengthList { get; } = {
         5, 9,14,12,15,13,10,13, 7, 4, 5, 6, 8, 7, 8,12,
        13, 4, 3, 5, 5, 6, 9,15,12, 6, 5, 6, 6, 6, 7,14,
        14, 7, 4, 6, 4, 6, 8,15,12, 6, 6, 5, 5, 5, 6,14,
         9, 7, 8, 6, 7, 5, 4,10,10,13,14,14,15,10, 6, 8,
    };

        /// <summary>
    /// Gets the map type.
    /// </summary>
public CodeBookMapType MapType { get; } = (CodeBookMapType)0;
        /// <summary>
    /// Gets the quant min.
    /// </summary>
public int QuantMin { get; } = 0;
        /// <summary>
    /// Gets the quant delta.
    /// </summary>
public int QuantDelta { get; } = 0;
        /// <summary>
    /// Gets the quant.
    /// </summary>
public int Quant { get; } = 0;
        /// <summary>
    /// Gets the quant sequence p.
    /// </summary>
public int QuantSequenceP { get; } = 0;

        /// <summary>
    /// Gets the quant list.
    /// </summary>
public int[] QuantList { get; } = null;
}
