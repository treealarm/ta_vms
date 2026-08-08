#!/usr/bin/env bash
# Stops the dev stack (or the named services). See scripts/dev_stack.py.
exec python3 "$(dirname "$0")/dev_stack.py" down "$@"
