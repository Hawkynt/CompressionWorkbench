# UDF reference vectors (remapped partitions)

Made by `UdfRemappedPartitionTests.Given_UdfToolsAndTheKernel_When_TheVectorsAreRegenerated_Then_TheyReadTheSame`
with `CWB_UDF_WRITE_VECTORS=1`, in WSL (Ubuntu 24.04): `mkudffs` from udftools 2.3 formats each
volume, and the Linux kernel's `udf` driver in the libguestfs appliance (`guestfish`) writes or
reads it. Stored gzipped; each is 16 MiB or less uncompressed.

| File | Made with | What it covers |
| --- | --- | --- |
| `sparable.img.gz` | `mkudffs --media-type=cdrw --udfrev=2.01`, then the kernel wrote the four files of `Inputs()` (a 300,000-byte file, an empty one, nested directories) | a sparable partition: one Type 2 "*UDF Sparable Partition" map, packet length 32, two sparing-table copies |
| `vat.img.gz` | `mkudffs --media-type=cdr --udfrev=2.01`, cut after the VAT block it reports | CD-R layout: the File Set Descriptor and root directory at virtual addresses, resolved through the VAT at the end of the volume |
| `bdr.img.gz` | `mkudffs --media-type=bdr --udfrev=2.50`, cut after the VAT block | BD-R layout: a metadata partition whose metadata file lies in a virtual (VAT) partition |

The kernel cannot extend a VAT, so both write-once volumes hold only their root; the kernel lists
them (`mount-ro` + `find /`) as empty, and so must we. The sparable volume's files must come back
as the kernel wrote them. A further test moves one packet into the spare area the sparing table
names (as a drive does after a write error) and checks that the file still reads the same.
