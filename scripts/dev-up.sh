#!/usr/bin/env bash
# Starts the dev stack (or the named services) outside VS Code. See scripts/dev_stack.py.
exec python3 "$(dirname "$0")/dev_stack.py" up "$@"
