# CollabDesk.NET

A fictional help-desk portfolio proof of concept built with ASP.NET Core 10, EF Core/PostgreSQL, Redis, SignalR, SSE, and React. It is source code for review and local demonstration—not a hosted product or a claim of production readiness.

## Run locally

Prerequisites: .NET SDK 10, Node 22+, and Docker Compose.

```bash
dotnet restore
dotnet build CollabDesk.sln --no-restore --warnaserror
dotnet test CollabDesk.sln --no-build --no-restore
docker compose up --build --wait
```

Open `http://localhost:5000`; stop the disposable local stack with `docker compose down --volumes`. The UI is served by the API. For frontend-only work, run `npm ci && npm run dev` in `frontend/`.

## Inspect the local data services

The API connects to PostgreSQL and Redis over Docker's private `backend` network,
using the service names `db` and `redis`.  When using the local admin tools,
do **not** enter `localhost`: from inside either tool's container, `localhost`
means that tool's own container.

| Tool | Address | Connection to add |
| --- | --- | --- |
| pgAdmin | `http://localhost:5050` | Host `db`, port `5432`, database `collabdesk`, user `collabdesk`, password `collabdesk` |
| Redis Insight | `http://localhost:5540` | Host `redis`, port `6379`, database `0`, no username, password, or TLS |

The values in `.env.example` are intended for a process running inside this
Compose network. It is an example file only; ASP.NET Core does not load `.env`
files automatically. A locally host-run API therefore needs separately supplied
configuration and host-published database/cache ports.

All seeded accounts are fictional and local-only. Their shared development password is `local-fixture-password`:

| Role | Email |
| --- | --- |
| Customer | `demo-customer@collabdesk.test` |
| Agent | `demo-agent@collabdesk.test` |
| Administrator | `demo-admin@collabdesk.test` |

Never reuse these values, the Compose credentials, or Development's HTTP cookie setting outside local development.

## Reviewer map

- [Architecture and trade-offs](docs/architecture.md) — component diagram plus EF, SignalR, async worker/outbox, cache, and concurrency boundaries.
- [API guide](docs/api.md) — OpenAPI location, authentication/CSRF rules, endpoint map, and live-connection contract.
- [Test evidence](docs/testing.md) — test-to-feature mapping and isolated PostgreSQL/Redis commands.
- [Benchmarks](docs/benchmarks.md) — recorded, measured results and methodology; no inferred numbers.
- [Screenshots](docs/screenshots.md) — checked-in Playwright artifacts from real browser contexts.
- [Security and dependency checks](docs/security.md) — scope, commands, and limitations.
- [CV-safe portfolio bullets](docs/cv-bullets.md) — verified hands-on POC wording only.

The typed OpenAPI document is exposed by a running application at `/openapi/v1.json`; `/health` checks EF Core database connectivity. The CI workflow in [`.github/workflows/ci.yml`](.github/workflows/ci.yml) restores, builds, tests, builds the frontend, runs dependency and secret scans, and exercises disposable PostgreSQL/Redis services. It is configuration only until run by a CI provider.

## Important limits

This is not security-certified and has no production deployment configuration. SignalR has no Redis backplane, so chat requires one API replica. Redis is a best-effort cache, not a correctness store. Consult the architecture notes before describing any behavior as production-ready.
