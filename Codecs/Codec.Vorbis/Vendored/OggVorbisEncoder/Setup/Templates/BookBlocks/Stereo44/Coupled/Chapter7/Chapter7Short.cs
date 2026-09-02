namespace OggVorbisEncoder.Setup.Templates.BookBlocks.Stereo44.Coupled.Chapter7;

/// <summary>
/// Represents a chapter 7 short.
/// </summary>
public class Chapter7Short : IStaticCodeBook
{
        /// <summary>
    /// Gets the dimensions.
    /// </summary>
public int Dimensions { get; } = 2;

        /// <summary>
    /// Gets the length list.
    /// </summary>
public byte[] LengthList { get; } = {
         4,11,12,14,15,15,17,17,18,18, 5, 6, 6, 8, 9,10,
        13,17,18,19, 7, 5, 4, 6, 8, 9,11,15,19,19, 8, 6,
         5, 5, 6, 7,11,14,16,17, 9, 7, 7, 6, 7, 7,10,13,
        15,19,10, 8, 7, 6, 7, 6, 7, 9,14,16,12,10, 9, 7,
         7, 6, 4, 5,10,15,14,13,11, 7, 6, 6, 4, 2, 7,13,
        16,16,15, 9, 8, 8, 8, 6, 9,13,19,19,17,12,11,10,
        10, 9,11,14,
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
