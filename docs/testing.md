# Test-to-feature evidence

Run the dependency-free suite:

```bash
dotnet restore
dotnet build CollabDesk.sln --no-restore --warnaserror
dotnet test CollabDesk.sln --no-build --no-restore
```

| Feature | Evidence |
| --- | --- |
| Authentication, CSRF, authorization, OpenAPI, pagination | `AuthenticationSpecification` |
| Idempotent ticket creation, atomic claims, message de-duplication | `ConcurrencySpecification` (PostgreSQL guarded) |
| SignalR authorization, reconnect history, receipts, revocation | `ChatHubSpecification` |
| Bounded/cancellable SSE | `AssistantStreamSpecification` |
| Report access, expiry, downloads, aggregation | `ReportSpecification` |
| Leases, retries, outbox at-least-once behavior | `DurableWorkSpecification` |
| Cache invalidation, resilience, cold-miss coalescing | `TicketCacheSpecification` (Redis portion guarded) |
| Checked-in migrations and concurrency metadata | `MigrationSpecification` |
| Fake-data and loopback guards for measurements | `BenchmarkFixtureSpecification` |
| Documentation/evidence index presence | `PortfolioEvidenceSpecification` |
| Full real-service API/SignalR path | `ComposeApiSpecification` (PostgreSQL + Redis guarded) |
| Real browser collaboration/report flow | `frontend/e2e/demo.spec.ts` (Playwright) |

To run real-service tests safely, start only disposable loopback services and provide their explicitly named endpoints:

```bash
docker compose -p collabdesk-tests -f docker-compose.yml -f docker-compose.test.yml up -d --wait db redis
COLLABDESK_TEST_POSTGRES='Host=127.0.0.1;Port=55433;Database=collabdesk_test_net013;Username=collabdesk;Password=collabdesk' \
COLLABDESK_TEST_REDIS='127.0.0.1:56380' \
  dotnet test CollabDesk.sln --no-build --no-restore
docker compose -p collabdesk-tests -f docker-compose.yml -f docker-compose.test.yml down --volumes --remove-orphans
```

The fixture rejects database names outside `collabdesk_test_` and non-loopback hosts. Browser tests need `docker compose up --build --wait`, then `cd frontend && npm ci && npx playwright install --with-deps chromium && npm run test:e2e`. These commands are not interchangeable with a verified run; see the task report for what was actually executed.
