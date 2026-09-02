namespace OggVorbisEncoder.Setup.Templates.BookBlocks.Stereo16.Uncoupled.Chapter2;

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
         5, 8,10,10,10,11,11,12,14,18, 7, 5, 5, 6, 8, 9,
        10,12,14,17, 9, 5, 4, 5, 6, 8,10,11,13,19, 9, 5,
         4, 4, 5, 6, 9,10,12,17, 8, 6, 5, 4, 4, 5, 7,10,
        11,15, 8, 7, 7, 6, 5, 5, 6, 9,11,14, 8, 9, 8, 7,
         6, 5, 6, 7,11,14, 9,11,11, 9, 7, 6, 6, 6, 9,14,
        11,14,15,13, 9, 8, 7, 7, 9,14,13,15,19,17,12,11,
        10, 9,10,14,
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
