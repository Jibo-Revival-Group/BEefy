# Free Agent Mode — operator setup and limits

Free Agent requests use a dedicated anonymous HTTPS API route, not the legacy protocol dispatcher. No robot authentication or enrollment is required. Any caller can claim any robot ID, exhaust another robot's quota, or rotate IDs; quotas are abuse mitigation, not identity or authorization. Enable only if this exposure is acceptable. Requests relay through the authenticated BEefyER worker, which calls loopback Ollama with fixed model `gemma3:4b`.

## Enablement and request

Set `OpenJibo__Agent__Free__Enabled=true` on a single BEefy instance. Example POST `/api/agent/free/chat` JSON:

```json
{"requestId":"0123456789abcdef0123456789abcdef","robotId":"robot-1","messages":[{"role":"user","content":"Hello"}]}
```

`robotId` is required, 1–128 UTF-16 code units, nonblank and without control characters; it is self-reported and spoofable. `requestId` is exactly 32 lowercase hexadecimal characters. Messages must alternate user/assistant and end with user; unknown and duplicate fields are rejected. HTTPS is required. There is no model or URL override.

On BEefyER, install the optional worker dependency and enable the extension:

```sh
/opt/BEefyER/.venv/bin/python -m pip install -r /opt/BEefyER/requirements-chat.txt
```

Set `BEEFYER_CHAT_ENABLED=true` in the worker service environment and restart it. Provision Ollama and `gemma3:4b` separately on the **BEefyER host**. The worker advertises configured capability, not a live model health check. Older workers, absent workers, busy capacity, unloaded models, and upstream errors fail closed; there is no direct robot-to-Ollama fallback.

## Free-only quota

Only `/api/agent/free/chat` invokes the limiter. Each self-reported robot ID has 50 admissions per rolling 30 minutes, and each trusted client IP has 200 admissions per rolling hour. Either denial returns 429 with Retry-After; the two counters are charged atomically. An attacker can spoof or rotate robot IDs, and users sharing an IP share its quota. Other services and providers are not limited by this Free quota.

The limiter and request-replay tombstones are process-local, bounded in-memory stores: default maximum 10,000 keys per quota dimension and 10,000 active replay IDs; replay IDs are keyed by canonical client IP and expire after two minutes. They reset on process restart and are not coordinated across replicas. **Run one BEefy API replica if the quota must be globally strict.** A multi-replica deployment requires shared atomic quota/replay state and routing to the replica owning the worker connection before enabling Free; a local in-memory limiter cannot promise cluster-wide limits.

Client IP comes from `HttpContext.Connection.RemoteIpAddress` after ASP.NET forwarded-header middleware. Configure only known/trusted ingress proxies; the endpoint does not trust a request-supplied `X-Forwarded-For`. Direct access from clients that can spoof the network source is outside the proxy's trust model.

## Security and failure behavior

- HTTPS only, strict bounded body, and per-IP request-ID replay rejection; there is no robot authentication.
- Worker chat uses a separate capacity-one slot, maximum 120-second backend lease, bounded protocol messages, and sanitized error codes. No prompts/replies/credentials are written to application request logs by this route.
- Client cancellation or lease expiry sends worker cancellation and keeps the chat slot quarantined until a matching terminal event or connection teardown. The worker acknowledges cancellation only after HTTP cleanup and slot release. A missing acknowledgement leaves chat unavailable but does not disconnect shared ASR.
- This provides source-level behavior only. Before production rollout, verify trusted-proxy configuration, replica count, API log/telemetry filters, and provider retention. Install/configure the model locally and perform a consented loopback and robot smoke test. This checkout has not been deployed or tested with a real Ollama model or robot.
