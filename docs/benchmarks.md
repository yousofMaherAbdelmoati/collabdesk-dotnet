# Measured benchmark evidence

These are recorded measurements from NET-012, not current-run claims. Each run used deterministic fictional tickets (seed `12012`), a fixed 90-day UTC window starting 2026-01-01, 3 warmups, and 15 measurements. Environment recorded in the JSON: .NET 10.0.13, Ubuntu 24.04.5, PostgreSQL 16.15, Redis 7.4.11.

| Ticket count | Strategy | Median ms | Maximum ms | Database commands |
| ---: | --- | ---: | ---: | ---: |
| 10,000 | on-demand EF | 10.7012 | 15.1315 | 3 |
| 10,000 | precomputed | 0.9774 | 1.0342 | 1 |
| 10,000 | Redis-cached | 0.5190 | 0.6088 | 0 |
| 50,000 | on-demand EF | 38.2008 | 40.7160 | 3 |
| 50,000 | precomputed | 0.7736 | 2.0687 | 1 |
| 50,000 | Redis-cached | 0.4953 | 0.7019 | 0 |
| 100,000 | on-demand EF | 78.3536 | 82.5782 | 3 |
| 100,000 | precomputed | 0.6326 | 2.2811 | 1 |
| 100,000 | Redis-cached | 0.5794 | 0.6667 | 0 |

Raw measurements: [`10k`](benchmarks/net-012-10000.json), [`50k`](benchmarks/net-012-50000.json), [`100k`](benchmarks/net-012-100000.json). `on-demand-ef` runs three grouped EF queries; `precomputed` reads durable aggregates; `redis-cached` reads verified serialized aggregate rows after cache population. All strategies were required to return the same rows.

The tool rejects a database name not beginning `collabdesk_benchmark_` and non-loopback hosts. Results are local point-in-time measurements—not load tests, capacity commitments, or production SLOs.
