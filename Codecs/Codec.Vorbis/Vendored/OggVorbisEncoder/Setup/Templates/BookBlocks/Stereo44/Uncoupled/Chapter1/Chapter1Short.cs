namespace OggVorbisEncoder.Setup.Templates.BookBlocks.Stereo44.Uncoupled.Chapter1;

/// <summary>
/// Represents a chapter 1 short.
/// </summary>
public class Chapter1Short : IStaticCodeBook
{
        /// <summary>
    /// Gets the dimensions.
    /// </summary>
public int Dimensions { get; } = 2;

        /// <summary>
    /// Gets the length list.
    /// </summary>
public byte[] LengthList { get; } = {
        12,13,14,13,17,12,15,17, 5, 5, 6,10,10,11,15,16,
         4, 3, 3, 7, 5, 7,10,16, 7, 7, 7,10, 9,11,12,16,
         6, 5, 5, 9, 5, 6,10,16, 8, 7, 7, 9, 6, 7, 9,16,
        11, 7, 3, 6, 4, 5, 8,16,12, 9, 4, 8, 5, 7, 9,16,
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
