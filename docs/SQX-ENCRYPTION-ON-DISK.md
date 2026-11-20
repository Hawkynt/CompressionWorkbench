# SQX encryption

How SQX archives protect file data and headers with a password, as `FileFormat.Sqx` reads it.
The SQX 1.1 format notes (SQX SDK 2.06, `sqx_note.txt`, section 6) name the algorithms; the rest
was measured on archives Squeez CommandLine 5.2 (Linux) writes for chosen inputs and passwords,
in both formats 1.1 and 2.0, with stored, compressed and solid files.

## Key

The key is the 16-byte MD5 digest of the password's bytes. Squeez CommandLine on Linux takes the
bytes of its argument as given (UTF-8 for a non-ASCII password).

## File data

A file header with flag bit 1 (`0x0002`) marks its data encrypted. The data, as the method
produced it (stored bytes, or the compressed stream), is padded to a whole number of 16-byte
blocks and encrypted with AES-128 in ECB mode; the padding bytes are left over from the encoder's
buffer and carry no meaning. The compressed size in the header counts the padded length; the
original size and the CRC-32 describe the plain file, so a wrong password shows as a CRC failure.
Measured: the ciphertext of a 16-byte stored file is the single AES-128-ECB block of its bytes
under MD5("secret"), and a 1-byte file takes one whole block.

## Headers

Main header flag bit 1 (`0x0002`) marks the headers after it encrypted (Squeez's `-ph` switch).
Each following header is stored as a 16-bit little-endian length L, a multiple of 8, and L bytes:
the header (with its usual CRC-16 and size fields; its size field says where it ends), padded to L and encrypted with Blowfish
in ECB mode, each 32-bit half of a block taken little-endian, under the 16-byte MD5 key. File data
follows its encrypted header unencrypted by this layer (it carries its own AES encryption when
flag bit 1 of the decrypted file header says so).
