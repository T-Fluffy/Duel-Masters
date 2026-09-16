# Backend scale-out (SignalR + match state)

## Current topology

- REST is stateless: any replica can serve any request.
- SignalR matches are **sticky by construction**: each `MatchRoom` (the
  authoritative `DuelGame`) lives in the memory of the instance that created
  it. A client must keep talking to that same instance for the whole match
  (reconnects re-resolve through it).

## Redis backplane (opt-in)

`Program.cs` attaches the StackExchangeRedis backplane **only** when a
`Redis` connection string is configured
(`ConnectionStrings__Redis` / `ConnectionStrings:Redis`):

- Without it: today's in-memory behavior, byte-for-byte. Local runs, unit and
  API tests, and minimal deploys are unaffected.
- With it (`backends/docker-compose.yml` sets it, so the compose stack and
  CI's E2E already exercise it): hub messages fan out across instances under
  the `DuelMasters` channel prefix.

The backplane moves *messages* between nodes; it does **not** move match
state. That is why stickiness is still required (next section), and why item 2
below matters for crash safety.

## Running more than one server replica

Any orchestrator setup (swarm `replicas`, Kubernetes, etc.) MUST route a
match's traffic to one instance for the match lifetime:

- Path- or connection-affinity (sticky sessions) on the load balancer, keyed
  per client connection. SignalR negotiates once per connection, so cookie or
  IP affinity held for the connection lifetime is sufficient.
- Do NOT round-robin hub traffic per request: game mutations run under the
  room's in-process gate, and a second instance has no copy of the room.

## What is still single-instance (Batch 2 follow-ups)

- Live `MatchRoom` state: a restart voids in-progress matches. Planned fix is
  per-turn snapshots + resume (see ranking/ELO notes: voided ranked games
  currently record nothing, which is safe but unsatisfying).
- The `DuelResultsRecordedForRound` guard likewise lives in memory; snapshot
  persistence must carry it to keep exactly-once across restarts.

## Observability (logs → traces/metrics)

The server emits OpenTelemetry **only** when `Otlp:Endpoint` is configured
(`Otlp__Endpoint` env; compose sets it to the local collector). Otherwise it
runs silent, so unit/API tests and minimal deploys see no change.

- Automatic: ASP.NET Core request spans + runtime metrics (GC, threads, CPU).
- Game instruments (`DuelMasters.Server.Services.DuelMetrics`, stable names):
  `duelmasters.matches_finished` (counter, tag `winner_side`) and
  `duelmasters.elo_delta` (histogram of absolute rating movement per ranked
  side). Dashboards and alerts may rely on these names.
- Local sink: `backends/otel-collector-config.yaml` + the `otel` compose
  service (pinned image, debug exporter). Inspect with
  `docker compose -f backends/docker-compose.yml logs otel`. To forward to a
  real backend later, add an exporter to the collector config - no app change.
