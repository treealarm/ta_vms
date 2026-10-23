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
- **PostgreSQL and RabbitMQ are reached from the nodes in clear text**, behind their passwords
  (`distributed.sh` refuses the defaults). TLS for both: `hostssl` and `ssl=on` with a certificate
  of the core; AMQPS for RabbitMQ. A role for the nodes with access to the state store and `ta_fs`
  only, if the migrations of `ta_fs` do not need more.
- **Consul has no ACLs yet**, only gossip encryption: anything that reaches an agent's HTTP API
  (the loopback of a machine of the deployment) can register or deregister services. ACLs with
  default deny, a token per agent and one for service registration.
- **RabbitMQ's Erlang distribution and epmd listen on every address** on Windows (25672, 4369),
  behind the cookie. Forcing them onto the loopback broke `rabbitmqctl`, which looks the node up as
  `rabbit@<machine name>` at the machine's LAN address. Would need the node renamed to
  `rabbit@localhost`, which moves RabbitMQ's data directory: a migration.
- **Tokens never expire.** A node's tokens are its credentials; revoking one means a new signing
  key in Sentry's JWKS and new tokens for every machine. A per-node key (several keys in the JWKS)
  would let one node be revoked alone.
- **The firewall per role** on Windows (Install-TaVms.ps1) and its description for Linux, where
  Docker's published ports bypass ufw — with `network_mode: host` they do not, which helps.
