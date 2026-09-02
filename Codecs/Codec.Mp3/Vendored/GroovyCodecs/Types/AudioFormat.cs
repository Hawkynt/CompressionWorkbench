using System.Collections.Generic;

namespace GroovyCodecs.Types
{
    /// <summary>
    /// Represents an audio format.
    /// </summary>
public class AudioFormat
    {
        /// <summary>for buffer estimation</summary>
        public int AverageBytesPerSecond { get; set; }

        /// <summary>
        /// Gets a value indicating whether big endian.
        /// </summary>
public bool BigEndian { get; set; }

        /// <summary>number of bits per sample of mono data</summary>
        public short BitsPerSample { get; set; }

        /// <summary>block size of data</summary>
        public short BlockAlign { get; set; }

        /// <summary>number of channels</summary>
        public short Channels { get; set; }

        /// <summary>
        /// Gets or sets the properties.
        /// </summary>
public Dictionary<string, object> Properties { get; set; }

        /// <summary>sample rate</summary>
        public int SampleRate { get; set; }

        /// <summary>
        /// Gets a value indicating whether is floating point.
        /// </summary>
public bool IsFloatingPoint { get; set; }
    }
}