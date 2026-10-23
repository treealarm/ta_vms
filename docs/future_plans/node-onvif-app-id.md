# A node's vms and VmsFs find their ONVIF service through a global variable

Found 2026-10-23, running a node of `../ta_install/docker-compose.distributed.yml` against a core
on another machine.

`vms` (`onvif_client`, `onvif_metadata_client`) and `VmsFs` (`Program.cs`) call ONVIF at the app id
in `DAPR_ONVIF_APP_ID`, which `.env` sets once for the whole deployment (`vmsonvif`). On a node that
is the core's ONVIF service, which cannot reach the node's cameras: `GetStreamUri` failed and the
camera never connected until the node's compose set `DAPR_ONVIF_APP_ID=vmsonvif-<node>` for those
two services.

The pairing is already declared in `MEDIA_SERVER_PAIRS` (`server:fs:onvif:logger[:roi]`), which
`VmsOnvif` (`ServerPairRegistry`) and VmsCfg read. Deriving the ONVIF app id from the entry whose
server (for `vms`, its `APP_ID`) or FS (for `VmsFs`) is this process would leave one place where a
node's services are paired, as CLAUDE.md asks, instead of a second variable per node that has to
agree with it. Until then, every node's `vms` and `VmsFs` must be given `DAPR_ONVIF_APP_ID` — in
compose (done) and in `windows/config/services.json` when the Windows installer gets a node role.
