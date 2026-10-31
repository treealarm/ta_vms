# A fresh `dev-up` can collide with a sidecar left by an earlier run

Found 2026-10-31 while restarting the dev stack for the M4 end-to-end check.

`scripts/dev-up.sh` failed with `vmsonvif did not open port 5136 within 90s` after a fresh
start. The vmsonvif sidecar could not resolve `vmscfg`, and `/tmp/web_vms-dapr/vmscfg.log`
showed why:

```
level=error msg="Failed to listen for gRPC server on TCP address :51002 with error:
listen tcp :51002: bind: address already in use"
level=fatal msg="Fatal error from runtime: failed to start API gRPC server: could not listen
on any endpoint"
```

Something still held 51002: a `daprd` from an earlier run (the app had died or been killed,
but its sidecar lived on). `dev_stack.py start()` checks only the service's **app** port
before starting (`port %s is taken but %s is not ours`), so a free 5135 says nothing about
the sidecar ports that `dotnet/dapr.yaml` assigns. And when a service never opens its app
port — the timeout path, or the app dying on startup — `start()` exits without stopping the
sidecar it just spawned, so the next attempt meets the same collision.

A `dev-down` does wait for the sidecars it kills (the reason is written at `_stop()`: daprd
holds its ports until it is really gone), but it can only kill what its pid files name.

What is open:

- check every port `dotnet/dapr.yaml` assigns to a service (dapr gRPC, dapr HTTP, metrics,
  internal gRPC) as well as its app port before starting it, and name the holder;
- stop the sidecar `start()` spawned when the app port never opens, so a failed start does
  not leave the next one poisoned.

Workaround until then: stop the stack, find the process holding the sidecar port
(`ss -ltnp`) and kill it by pid, then start again.
