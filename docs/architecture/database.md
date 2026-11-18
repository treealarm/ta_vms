# Creating a database: name it in `.env`, nothing else

> Moved out of `CLAUDE.md`. This is the full statement, including why
> `docker-entrypoint-initdb.d` is not used; the rule itself lives there.

## Creating a database: name it in `.env`, nothing else

Databases are created by exactly one thing per platform, and both read the same names: in Docker
`scripts/prepare-databases.sh`, run as the `db-init` compose service; on Windows the supervisor
`TaVmsHost` (`../ta_install/windows`), at every start. Each creates **every** database named by a
`POSTGRES_*_DB` variable in the environment. (On Windows there is nothing to cross-check against
the Dapr components either: `TaVmsHost` writes them from the same environment.) So a new service's database is added by putting `POSTGRES_<NAME>_DB` into `.env` (and
`../ta_install/.env`), and by nothing else.

Do **not** go back to `docker-entrypoint-initdb.d`: that hook only fires when the data directory is
initialised from empty, so anything added to it is silently skipped on every deployment that
already has data — which is all of them after the first. And do not hardcode a list of database
names anywhere: names are stamped per tenant (see `multitenant_admin/mt_admin/DbAdmin`), so a fixed
list is wrong by construction.

`DbMigrator` migrates the schema *inside* an existing database; it does not create one. A missing
database now fails fast with a message naming it, instead of retrying "Postgres not ready" forever.
