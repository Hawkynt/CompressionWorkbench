# Sourced by the test and fixture scripts (and by tests that run shell commands): removes the
# scratch files and directories a script made itself, without rm -rf.
#
#   cwb_rmtree [--sudo] <path>...    delete each path: its files, then its emptied directories
#   cwb_fresh_dir <dir>              make <dir> and require it to be empty
#
# cwb_rmtree only touches scratch-shaped paths: something below /tmp, or a directory inside a wine
# prefix's drive_c. Anything else, and any path holding "..", is refused with a message and left
# alone. Symbolic links are removed as links, never followed.

cwb_rmtree() {
  local s=
  if [ "$1" = "--sudo" ]; then s=sudo; shift; fi
  local p
  for p in "$@"; do
    case "$p" in
      *..*) echo "cwb_rmtree: refusing '$p'" >&2; continue ;;
      /tmp/?*|*/drive_c/?*) ;;
      *) echo "cwb_rmtree: refusing '$p': not a scratch path" >&2; continue ;;
    esac
    if [ -d "$p" ] && [ ! -L "$p" ]; then
      $s find "$p" -depth ! -type d -exec rm -f -- {} +
      $s find "$p" -depth -type d -exec rmdir -- {} +
    elif [ -e "$p" ] || [ -L "$p" ]; then
      $s rm -f -- "$p"
    fi
  done
}

cwb_fresh_dir() {
  mkdir -p "$1" || return 1
  if [ -n "$(ls -A "$1")" ]; then
    echo "cwb_fresh_dir: '$1' is not empty; empty it or pass another directory" >&2
    return 1
  fi
}
