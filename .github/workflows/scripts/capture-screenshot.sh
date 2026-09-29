#!/usr/bin/env bash
# Captures one documentation screenshot of the NativeForms shell.
#
# The WPF shell rendered its own visual tree to a PNG. NativeForms has no equivalent — nothing in
# it draws a window to a bitmap — so the app only builds the fixture and shows the window, and the
# picture is taken from outside, here. Everything that made the old captures reproducible still
# lives in the app: fixed payloads, fixed timestamps, a fixed scramble seed, and an analysis window
# that leaves its elapsed time out of the status line.
#
# Usage: capture-screenshot.sh <target> <output.png>
#   target: archive-browser | analysis | maintenance
set -euo pipefail

target="$1"
output="$2"
mkdir -p "$(dirname "$output")"

# The workflow captures several targets back to back. A fixed display number failed on the second:
# the previous server's lock file outlived it, the new Xvfb refused to start, and the shell found no
# display at all. So each capture takes the first display nobody holds.
display=""
for n in $(seq 99 199); do
  if [ ! -e "/tmp/.X$n-lock" ] && [ ! -e "/tmp/.X11-unix/X$n" ]; then
    display=":$n"
    break
  fi
done
[ -n "$display" ] || { echo "::error::No free X display between :99 and :199."; exit 1; }

Xvfb "$display" -screen 0 1200x760x24 -nolisten tcp &
xvfb_pid=$!

# Waiting for the server to exit, not just signalling it, is what lets it remove its lock file
# before the next capture looks for a free display.
cleanup() {
  kill "$xvfb_pid" 2>/dev/null || true
  wait "$xvfb_pid" 2>/dev/null || true
}
trap cleanup EXIT

# Give the server a moment to accept connections before the app tries to map a window, and say so
# plainly if it never does, rather than letting the shell fail with a less direct message.
ready=""
for _ in $(seq 1 50); do
  if DISPLAY="$display" xdpyinfo >/dev/null 2>&1; then ready=1; break; fi
  sleep 0.2
done
[ -n "$ready" ] || { echo "::error::Xvfb did not come up on $display."; exit 1; }

DISPLAY="$display" dotnet "$APP_BINARY" "--screenshot=$target" &
app_pid=$!

# The analysis fixture runs a full scan before its window has anything to show, so the settle time
# is generous rather than tight. A shorter wait produced half-drawn grids.
sleep 25

if ! kill -0 "$app_pid" 2>/dev/null; then
  echo "::error::The shell exited before the $target screenshot could be taken."
  exit 1
fi

DISPLAY="$display" import -window root -quality 95 "$output"
kill "$app_pid" 2>/dev/null || true
wait "$app_pid" 2>/dev/null || true

if [ ! -s "$output" ]; then
  echo "::error::No screenshot was written to $output."
  exit 1
fi

echo "Captured $target -> $output ($(stat -c%s "$output") bytes)"
