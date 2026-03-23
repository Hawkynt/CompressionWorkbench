#!/usr/bin/perl
use strict;
use warnings;
use File::Find;

my $directory = $ARGV[0] || '.';
die "Directory '$directory' does not exist\n" unless -d $directory;

my @projectFiles = _FindProjectFiles($directory);
die "No .csproj files found in '$directory'\n" unless @projectFiles;

my $commitCount = _QueryGitCommitCount();
print "Git commit count: $commitCount\n";

_UpdateVersions(\@projectFiles, $commitCount);

sub _FindProjectFiles {
  my ($dir) = @_;
  my @files;
  find(sub {
    push @files, $File::Find::name if /\.csproj$/i;
  }, $dir);
  return @files;
}

sub _QueryVersionFromFile {
  my ($file) = @_;
  open my $fh, '<', $file or die "Cannot read '$file': $!\n";
  while (<$fh>) {
    if (/<Version>([\d.]+)<\/Version>/) {
      close $fh;
      my $version = $1;
      # Normalize to 3 segments
      my @parts = split /\./, $version;
      while (@parts < 3) { push @parts, '0'; }
      return join('.', @parts[0..2]);
    }
  }
  close $fh;
  return undef;
}

sub _QueryGitCommitCount {
  my $count = `git rev-list HEAD --all --branches --full-history --count`;
  chomp $count;
  return $count || '0';
}

sub _UpdateVersions {
  my ($files, $commitCount) = @_;
  for my $file (@$files) {
    my $version = _QueryVersionFromFile($file);
    unless ($version) {
      print "SKIP: No <Version> tag in $file\n";
      next;
    }
    my $newVersion = "$version.$commitCount";
    print "Updating $file: $version -> $newVersion\n";

    my $tmpFile = "$file.\$\$\$";
    open my $in, '<', $file or die "Cannot read '$file': $!\n";
    open my $out, '>', $tmpFile or die "Cannot write '$tmpFile': $!\n";
    while (<$in>) {
      s/<Version>[\d.]+<\/Version>/<Version>$newVersion<\/Version>/;
      print $out $_;
    }
    close $in;
    close $out;
    unlink $file or die "Cannot remove '$file': $!\n";
    rename $tmpFile, $file or die "Cannot rename '$tmpFile' to '$file': $!\n";
  }
}
