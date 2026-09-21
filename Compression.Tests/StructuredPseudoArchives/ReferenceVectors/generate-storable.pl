#!/usr/bin/env perl
# Regenerates the Perl Storable reference vectors with the real Storable module.
#
#     perl generate-storable.pl
#
# `nstore` is the network-order (portable) variant, which is the only one our reader accepts:
# the machine-order `store` bakes the writing host's byte order and integer width into the file.
#
# Perl randomises hash iteration order per process, so a multi-key hash would serialise its pairs
# in a different order on every run and could never be pinned byte-for-byte. The vector used for
# writer parity therefore nests single-key hashes; the richer vector is read-only, where order does
# not matter because the assertions address entries by path.

use strict;
use warnings;
use Storable qw(nstore);
use Digest::SHA qw(sha256_hex);
use File::Basename qw(dirname);

my $here = dirname(__FILE__);

sub emit {
  my ($name, $value) = @_;
  my $path = "$here/$name";
  nstore($value, $path);
  open(my $handle, '<:raw', $path) or die "cannot reread $path: $!";
  local $/;
  my $bytes = <$handle>;
  close($handle);
  printf("%-42s %7d bytes  sha256=%s\n", $name, length($bytes), sha256_hex($bytes));
}

printf("perl      %vd\n", $^V);
printf("Storable  %s\n\n", $Storable::VERSION);

emit('storable-nested.storable', { dir => { 'file.bin' => "\x00\x01\x7f\x80\xff\x0a" } });
emit('storable-document.storable', {
  dir => {
    count => 7,
    name  => 'value',
    list  => [ 1, 'two', 3 ],
    deep  => { leaf => 'bottom' },
  },
});
