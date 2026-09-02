namespace OggVorbisEncoder.Setup.Templates.BookBlocks.Stereo16.Coupled.Chapter1;

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
         5, 6,17, 8,12, 9,10,10,12,13, 5, 2,17, 4, 9, 5,
         7, 8,11,13,16,16,16,16,16,16,16,16,16,16, 6, 4,
        16, 5,10, 5, 7,10,14,16,13, 9,16,11, 8, 7, 8, 9,
        13,16, 7, 4,16, 5, 7, 4, 6, 8,11,13, 8, 6,16, 7,
         8, 5, 5, 7, 9,13, 9, 8,16, 9, 8, 6, 6, 7, 9,13,
        11,11,16,10,10, 7, 7, 7, 9,13,13,13,16,13,13, 9,
         9, 9,10,13,
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
