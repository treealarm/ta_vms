# What a multi-machine deployment still leaves open on the network

Found 2026-10-23 while building the core/node split: Consul resolves app ids, Dapr Sentry issues
the sidecars' certificates against a token per app id (its `jwks` validator), and every sidecar's
API, metrics and app stay on the loopback (`../ta_install/docker-compose.distributed.yml`,
`../ta_install/scripts/distributed.sh`). These are what is left.

- **Sentry's and placement's healthz and metrics listen on every address.** Sentry 1.18.7 ignored
  both `--healthz-listen-address 127.0.0.1` and `--metrics-listen-address 127.0.0.1` (it logged
  `[::]:<port>`), and placement on the Windows test box listens on `[::]:6051` although
  `windows/config/services.json` gives it `--healthz-listen-address 127.0.0.1`. Read-only, but they
  should not be on the LAN: find out why, or close them in the core's firewall.
- **Keycloak is reached over plain HTTP from every machine's web** (it listens on the core's
  address since each machine runs a web): users' passwords at login, and the administrator's
  password for user management, cross the LAN in clear text. HTTPS on Keycloak, with a certificate
  of the core, and `KEYCLOAK_URL` on `https://`.
- **Every MediaMTX's API (9997) and WHEP (8889) listen on its machine's address**, by design now:
  VmsUtils on the core asks each one who is watching, and every machine's web relays WHEP to the
  camera's MediaMTX. Both answer only with `vms_read`'s password, which every machine holds (below).
- **PostgreSQL and RabbitMQ are reached from the nodes in clear text**, behind their passwords
  (`distributed.sh` refuses the defaults). TLS for both: `hostssl` and `ssl=on` with a certificate
  of the core; AMQPS for RabbitMQ. A role for the nodes with access to the state store and `ta_fs`
  only, if the migrations of `ta_fs` do not need more. That role is what bounds a node today: with
  the core's PostgreSQL password it reads every secret VmsCfg keeps, MediaMTX's among them. Dropping
  `VMS_MEDIAMTX_*` from the package would close nothing: a node's `vms` gets the MediaMTX row, read
  password included, from VmsCfg, and its own MediaMTX runs on those accounts.
- **Consul has no ACLs yet**, only gossip encryption: anything that reaches an agent's HTTP API
  (the loopback of a machine of the deployment) can register or deregister services. ACLs with
  default deny, a token per agent and one for service registration.
- **Tokens never expire, and removing a node does not revoke them.** A node's tokens are its
  credentials; revoking one means a new signing key in Sentry's JWKS and new tokens for every
  machine. A per-node key (several keys in the JWKS, VmsCfg signing with the node's own) would let
  Administration → Deployment revoke the node it removes. The signing key is now read by VmsCfg,
  which makes the packages: on Windows the service account can read `core\jwt.key`, as it already
  reads Sentry's issuer key and every password in its environment.
- **A node's package travels from the core's web over plain HTTP** (`POST /api/deployment/join`),
  with every password of the core in it. HTTPS on web_srv, with a certificate the node's installer
  can check (its fingerprint shown in the web and asked for in the installer), would close both the
  eavesdropper and a machine posing as the core.
- **Joining has no rate limit.** With enrollment closed the password is checked at Keycloak, which
  has its own brute-force detection (off in the realm we ship); with it open there is nothing to
  guess. A limit per remote address on `/api/deployment/join` is cheap once there is a reason.
- **A node on premises joining a core in the cloud** (a tenant's core in Kubernetes): every port of
  the list above would have to cross the internet. Needs a design of its own — a tunnel per node,
  or the node's sidecars reaching the core through one gateway — before it is offered.
- **The firewall per role** on Windows (Install-TaVms.ps1) and its description for Linux, where
  Docker's published ports bypass ufw — with `network_mode: host` they do not, which helps.
- **The machines of a deployment must share a subnet**: the Windows firewall rules are open to
  `LocalSubnet`, and the core's PostgreSQL lets in `samenet`. A node behind a router needs the
  deployment's address ranges named instead, in the installer and in `pg_hba`.
