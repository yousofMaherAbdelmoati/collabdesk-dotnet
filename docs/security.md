# Security and dependency review

Scope: fictional local test data only. The project intentionally does not contain production credentials, customer data, deployment secrets, or a hosted endpoint.

Run these checks from a real Git checkout before sharing or publishing:

```bash
dotnet list CollabDesk.sln package --vulnerable --include-transitive
(cd frontend && npm audit --omit=dev --audit-level=high)
scripts/verify-no-secrets.sh
```

`verify-no-secrets.sh` scans repository source while excluding generated/dependency folders; when `.git` is available it also scans reachable Git history. The GitHub Actions workflow uses `fetch-depth: 0` so that historical scan is possible in CI.

The scanner is a defense-in-depth guard, not a guarantee. Review alerts manually; never whitelist a real credential. Local fixture passwords and Compose database credentials are deliberately public development values and are documented as non-production.

The checked-in Compose file is bound to `127.0.0.1` because it intentionally runs Development fixtures over HTTP. Any deployment beyond that local demonstration needs secret injection, HTTPS at the application or trusted proxy, approved CORS origins, logging/alerting, dependency update policy, and an operational review. Login requests use a per-IP fixed-window limit; deployers should tune it for their proxy topology. No security certification or penetration test is represented here.
