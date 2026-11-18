# Login: Keycloak everywhere, the standalone installs bring their own

> Moved out of `CLAUDE.md`. This is the full statement; the rule itself lives there.

## Login: Keycloak everywhere, the standalone installs bring their own

web_srv logs users in with Keycloak's password grant, server-side; the browser never talks to
Keycloak, so `KEYCLOAK_URL` only has to be reachable from web_srv. `KEYCLOAK_URL` empty still means
"no authentication at all", but no product deployment runs that way any more: both standalone
installs in `../ta_install` ship a Keycloak of their own and import its realm from
`../ta_install/init_files/keycloak/realm.json` on the first start. Dev runs against
multitenant_admin's, on `localhost:8080` from the services debugged on the host (F5).

- Realm roles are the whole model: `admin` sees and administers everything; anyone else sees the
  tree nodes granted to their `preferred_username` or one of their realm roles. Client roles are
  ignored.
- Rights live on the tree in VmsMap (`rights(object_id, subject, action)`, inherited downwards);
  VmsCfg no longer has permission tables of its own. web_srv's `RightsService` asks
  `GetEffectiveRights` for the signed-in user, caches the answer and drops it when VmsMap bumps
  `rights-version`; `[RequiresRight(...)]` is the attribute every action carries. The open/enforced
  switch is one value for the whole deployment, also on the tree.
- Nobody is made to change a password, the initial `admin`/`admin` included (user, 2026-10-22:
  whoever wants to changes it): the view controls lead to `POST /api/auth/change-password`. If a
  forced change is ever wanted back, it cannot be Keycloak's "temporary password" or required
  actions: Keycloak refuses the password grant for those ("Account is not fully set up"), so the
  user could never log in to change anything.
- The realm has no password policy (Keycloak would refuse to import `admin`/`admin` under one) and
  no required profile fields (a user without an email could not log in); web_srv checks a new
  password's length itself.
- `[AllowAnonymous]` belongs on actions, never on a controller: on the class it overrides every
  `[Authorize]` inside, which is how `/api/auth/users` was once open to anyone.
