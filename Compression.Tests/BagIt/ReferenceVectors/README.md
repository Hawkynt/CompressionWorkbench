# BagIt reference vectors

The oracle is bagit-python 1.8.1, the Library of Congress reference implementation (public domain),
from PyPI.

| File | Made by | Notes |
| --- | --- | --- |
| `loc-sha256-sha512.zip` | `bagit.make_bag(..., checksums=["sha256", "sha512"])` with Contact-Name and External-Description tags | zipped with Python's `zipfile`, one top-level directory |
| `loc-md5.zip` | `bagit.make_bag(..., checksums=["md5"])` | same payload |
| `ours.zip` | `BagItFormatDescriptor.Create` (see `WriteProbe.BagIt`) | SHA-512 manifests |
| `ours-edited.zip` | our `Add` + `Remove` applied to `loc-sha256-sha512.zip` | keeps its tags and both algorithms |

Each bag has three payload files, one of them empty and one with spaces and UTF-8 in its name.
bagit-python's `Bag.validate()` accepts both of ours (`bagit-listing.txt`), and `BagItTests`
validates both of its own.

One disagreement: RFC 8493 (BagIt 1.0) requires `%` in manifest paths to be written as `%25`, but
bagit-python 1.8.1 only encodes CR and LF. The reader decodes `%25` for 1.0 bags only, and the
writer follows the RFC. A name containing `%` therefore cannot be checked against bagit-python, and
none of the oracle fixtures has one.
