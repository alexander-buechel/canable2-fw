#!/usr/bin/env bash
# Runs the Robot Framework tests of the Renode platform.
#
#   renode/run-tests.sh                       all tests under renode/tests
#   renode/run-tests.sh -f "Device Enum*"     only tests matching a name
#   renode/run-tests.sh --variable ELF:/path/to/other.elf
#
# Needs `make` to have been run (tests load build/canable2*.elf), Renode (RENODE_ROOT or `renode` in PATH) and
# Robot Framework. If robotframework is not installed system wide, point ROBOT_DEPS at a directory that contains
# the unpacked `robot` package (default: ~/.local/lib/renode-test-deps).
set -euo pipefail

here="$(cd "$(dirname "$(readlink -f "$0")")" && pwd)"
renode_root="${RENODE_ROOT:-$(dirname "$(readlink -f "$(command -v renode)")")}"
deps="${ROBOT_DEPS:-$HOME/.local/lib/renode-test-deps}"

if [ -d "$deps" ]; then
    export PYTHONPATH="$deps${PYTHONPATH:+:$PYTHONPATH}"
fi

# Without an explicit .robot file on the command line, run every test file.
args=()
case " $* " in
    *.robot*) ;;
    *) args=("$here"/tests/*_*.robot) ;;
esac

cd "$here"
exec "$renode_root/renode-test" --results-dir "$here/results" "${args[@]}" "$@"
