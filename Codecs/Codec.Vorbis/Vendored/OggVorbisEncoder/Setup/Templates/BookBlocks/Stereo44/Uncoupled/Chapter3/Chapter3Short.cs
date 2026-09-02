namespace OggVorbisEncoder.Setup.Templates.BookBlocks.Stereo44.Uncoupled.Chapter3;

/// <summary>
/// Represents a chapter 3 short.
/// </summary>
public class Chapter3Short : IStaticCodeBook
{
        /// <summary>
    /// Gets the dimensions.
    /// </summary>
public int Dimensions { get; } = 2;

        /// <summary>
    /// Gets the length list.
    /// </summary>
public byte[] LengthList { get; } = {
        14,14,14,15,13,15,12,16,10, 8, 7, 9, 9, 8,12,16,
        10, 5, 4, 6, 5, 6, 9,16,14, 8, 6, 8, 7, 8,10,16,
        14, 7, 4, 6, 3, 5, 8,16,15, 9, 5, 7, 4, 4, 7,16,
        13,10, 6, 7, 4, 3, 4,13,13,12, 7, 9, 5, 5, 6,12,
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
