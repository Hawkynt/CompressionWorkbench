# Web archive reference vectors

| File | Origin |
| --- | --- |
| `dwlnetnl-example.webarchive` | `testdata/example.webarchive` of [dwlnetnl/webarchive](https://github.com/dwlnetnl/webarchive) (commit 64a213c, BSD-2-Clause, © 2015 Anner van Hardenbroek): the project's Safari-saved copy of `http://example.org/`, main resource only. Unmodified. |
| `ticky-crouton.webarchive` | `fixtures/crouton.webarchive` of [ticky/webarchive](https://github.com/ticky/webarchive) (branch `develop`, commit 82ec0da, MIT / Apache-2.0, © 2022 Jessica Stokes): the project's Safari-saved copy of `https://crouton.net/` with one image subresource carrying an archived `WebResourceResponse`. Unmodified. |
| `plistlib-frames.webarchive` | Written for this suite by CPython 3.13 `plistlib.dumps(fmt=FMT_BINARY, sort_keys=False)`: a page, a stylesheet, an image on another origin with a percent-encoded name and a query, an opaque response blob, a subframe archive with its own subresource, and an `XTestValues` array holding a UTF-16 string, 1/4/8/16-byte and negative integers, a real, booleans, a date and a nested array. |
| `plistlib-listing.txt` | CPython `plistlib`'s view of the three files: URL, size and SHA-256 of every resource, page first, subresources, then each subframe the same way. |

Our binary plist writer must reproduce `plistlib-frames.webarchive` byte for byte from the value tree it decodes.

The pywebarchive oracle test (`ArchiveExternal`) runs where `import webarchive` works (`pip install pywebarchive==0.5.2`; a directory installed with `pip install --target` can be named in `CWB_PYTHONPATH`).
