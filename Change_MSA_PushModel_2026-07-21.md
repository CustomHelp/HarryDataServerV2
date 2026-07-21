# MSA result delivery: poll → push (2026-07-21)

## Requested change
The PLC should send the MSA `Request;<BaseID>` **once**, get `Wait`, and then receive the finished
result **without polling again** — the server pushes `OK`/`NG` on the same connection when done.

## Before / after
- **Before (poll):** `Request` → `Wait`; the PLC had to keep re-sending `Request` until it got
  `OK`/`NG`. "No rows in DB yet" cleared the state and relied on the next poll.
- **After (push):** `Request` → immediate `Wait`; when `EvaluateAsync` finishes, the server sends
  `OK;<BaseID>` / `NG;<BaseID>` (or `Error;<BaseID>;<desc>`) **unsolicited** on the same open
  connection. Because the PLC no longer re-polls, `EvaluateAsync` now **retries internally** (up to
  `MaxGatherAttempts` = 60, one flush interval apart) while the run's rows are still being committed
  to `msa_measurements`, instead of waiting for another request.

## Implementation
- `ISpsServer.PushMsaResultAsync(moduleKey, baseId, status)` + `TcpSpsServer`:
  - Tracks the current PLC connection per MSA channel (`_msaConnections`, newest wins, removed on
    disconnect).
  - A per-connection write lock serialises receive-loop responses and unsolicited pushes.
  - `SpsChannelExtensions.MsaChannelForModule` reverse-maps "M50" → the channel to push on.
- `MsaService.EvaluateAsync`: internal retry-until-data loop; on completion sets the cached
  `OK`/`NG` **and** calls `PushMsaResultAsync`; pushes `Error;<desc>` on a genuine fault and on
  "no measurements after N attempts". The cached word is kept as a **poll fallback** (a late
  re-`Request` still gets an answer).

## PLC requirement (coordinate with the PLC programmer)
The PLC **must keep the request TCP connection open** to receive the pushed result. If it closes
the connection, the push is logged as skipped ("no open PLC connection"); the result is still
cached, so a re-`Request` on a new connection returns it.

## Files
`Models/SpsChannel.cs`, `Services/ISpsServer.cs`, `Communication/TcpSpsServer.cs`,
`Services/MsaService.cs`, `HarryDataServer.Tests/Program.cs`, `CLAUDE.md` (§5).

## Tests / build
`HarryDataServer.Tests` adds Case I (channel ↔ module mapping). **ALL TESTS PASSED.**
Debug + full Release solution build: **0 errors** (1 pre-existing unrelated warning in HarryAnalysis).
