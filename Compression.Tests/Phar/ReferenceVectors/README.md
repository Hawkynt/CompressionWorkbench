# PHAR reference vectors

| File | Origin |
| --- | --- |
| `md5.phar`, `sha1.phar`, `sha256.phar`, `sha512.phar` | php-src `ext/phar/tests/files/` (revision `49b2bc4532ec00859256707ac3771657d2889808`, fetched 2026-10-26): one member, one signature of each digest type, manifest API 1.1.0. |
| `openssl.phar` | same source: an OpenSSL-signed phar (128-byte RSA signature, its length stored before the type). |
| `blog.phar`, `stuboflength1041.phar` | same source: longer stubs (the latter 1041 bytes before `__HALT_COMPILER();`). |
| `gh20442.phar` | same source: lower-case `__halt_compiler();` and a single explicit directory entry. |
| `php-sha256.phar`, `php-md5.phar` | written by PHP 8.4.25 (Windows, `Phar` class, `make.php` below): `setStub("<?php __HALT_COMPILER();")`, alias `cwb-<file name>`, archive metadata `['generator' => 'php']`, `a.txt` (`hello phar\n`, metadata `'note'`), `dir/b.bin` (bytes 0..255 four times, chmod 0644), `addEmptyDir('empty')`, stored. |
| `php-gz-sha1.phar`, `php-sha512-gz.phar` | same inputs, `compressFiles(Phar::GZ)`. |
| `php-bz2-sha256.phar` | same inputs, `compressFiles(Phar::BZ2)`. |
| `php-listing.txt` | PHP 8.4.25's view of every file above (`PharTests.ListingScript`): signature type and digest, alias, serialized metadata, and per member size, compressed size, CRC-32, permissions, compression, SHA-256 of the content and metadata. |

Our writer must reproduce `php-sha256.phar` and `php-md5.phar` byte for byte from the same inputs and
timestamps.

`make.php`:

```php
function build($path, $sig, $gz, $b) {
  $p = new Phar($path, 0, 'cwb-' . basename($path));
  $p->startBuffering();
  $p->setStub("<?php __HALT_COMPILER();");
  $p->addFromString('a.txt', "hello phar\n");
  $p->addFromString('dir/b.bin', $b);
  $p->addEmptyDir('empty');
  $p->setMetadata(['generator' => 'php']);
  $p['a.txt']->setMetadata('note');
  $p['dir/b.bin']->chmod(0644);
  if ($gz) $p->compressFiles($gz);
  $p->setSignatureAlgorithm($sig);
  $p->stopBuffering();
}
```

The php-src files are distributed under the PHP License, version 3.01
(<https://www.php.net/license/3_01.txt>): "This product includes PHP software, freely available from
<http://www.php.net/software/>". They are unmodified.
