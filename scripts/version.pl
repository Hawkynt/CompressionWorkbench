#!/usr/bin/perl
use strict;
use warnings;
use FindBin;
use File::Find;

# Compute project version as MAJOR.MINOR.PATCH.BUILD where the first three
# segments come from the project's MSBuild config and BUILD is the git commit
# count. Used by CI to produce a unique version per push without requiring
# any repo-specific config file -- reads from the standard MSBuild props.
#
# Usage:
#   perl scripts/version.pl            # MAJOR.MINOR.PATCH.BUILD
#   perl scripts/version.pl --base     # MAJOR.MINOR.PATCH
#   perl scripts/version.pl --build    # BUILD

my $mode = $ARGV[0] // '';
die "Usage: $0 [--base|--build]\n"
  unless $mode eq '' || $mode eq '--base' || $mode eq '--build';

my $repoRoot = "$FindBin::Bin/..";

if ($mode eq '--build') {
  print _QueryBuildNumber($repoRoot), "\n";
  exit 0;
}

my $base = _QueryBaseVersion($repoRoot);
if ($mode eq '--base') {
  print $base, "\n";
  exit 0;
}

my $build = _QueryBuildNumber($repoRoot);
print "$base.$build\n";
exit 0;


# ---------------------------------------------------------------------------

sub _QueryBaseVersion {
  my ($root) = @_;

  # 1) csproj at root and one level deep -- mirrors UpdateVersions.pl scan.
  my @csprojs = _FindCsprojFiles($root);
  for my $file (@csprojs) {
    my $v = _ReadVersionTag($file);
    return $v if defined $v;
  }

  # 2) Fall back to Directory.Build.props at repo root.
  my $props = "$root/Directory.Build.props";
  if (-f $props) {
    my $v = _ReadVersionTag($props);
    return $v if defined $v;
  }

  die "version.pl: could not find <Version>X.Y.Z</Version> in any csproj or Directory.Build.props under $root\n";
}

sub _FindCsprojFiles {
  my ($root) = @_;
  my @files;
  # Root + one level deep -- avoids recursing into bin/obj of every project.
  for my $dir ($root, glob("$root/*")) {
    next unless -d $dir;
    push @files, glob("$dir/*.csproj");
  }
  return @files;
}

sub _ReadVersionTag {
  my ($file) = @_;
  open my $fh, '<', $file or return undef;
  while (my $line = <$fh>) {
    # Strict: 3 numeric dot-separated segments only. Avoids matching
    # PackageVersion, AssemblyVersion, FileVersion, or commented-out tags.
    if ($line =~ m{<Version>\s*(\d+\.\d+\.\d+)\s*</Version>}) {
      close $fh;
      return $1;
    }
  }
  close $fh;
  return undef;
}

sub _QueryBuildNumber {
  my ($root) = @_;
  my $count = `git -C "$root" rev-list --count HEAD 2>&1`;
  return '0' if $? != 0;
  chomp $count;
  return $count =~ /^\d+$/ ? $count : '0';
}
