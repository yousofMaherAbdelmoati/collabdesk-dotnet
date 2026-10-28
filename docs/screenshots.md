# Real browser screenshots

The following Playwright artifacts are checked in from the NET-011 local browser run. The test used three isolated browser contexts and fictional `*.test` accounts; it created a ticket, demonstrated agent-only claim rejection for a customer, exchanged SignalR messages, reconnected to persisted history, cancelled an SSE request, and completed an administrator report.

| Artifact | Scenario |
| --- | --- |
| [Customer live chat](../frontend/test-results/demo-NET-011-real-browser--501c6-SE-cancellation-and-reports/net-011-customer-live-chat.png) | Live SignalR conversation and receipt update. |
| [Customer reconnect](../frontend/test-results/demo-NET-011-real-browser--501c6-SE-cancellation-and-reports/net-011-customer-reconnected-history.png) | Persisted history after reconnect. |
| [Administrator report](../frontend/test-results/demo-NET-011-real-browser--501c6-SE-cancellation-and-reports/net-011-admin-report-completed.png) | Durable report reaches completed state. |

They are evidence of that recorded local run, not a substitute for rerunning `frontend/e2e/demo.spec.ts` against the reviewed revision.
