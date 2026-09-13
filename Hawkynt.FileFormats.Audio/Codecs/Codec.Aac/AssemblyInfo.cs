#pragma warning disable CS1591
using System.Runtime.CompilerServices;

// The unit-test assembly hand-crafts AAC bitstreams and therefore needs the
// internal Huffman codebook tables to look up codewords when building frames.
[assembly: InternalsVisibleTo("Compression.Tests")]
