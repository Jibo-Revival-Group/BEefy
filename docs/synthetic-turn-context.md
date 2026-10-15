# Context ordering for menu launches

The weather button in BEam sends the same words as a spoken request, but the
physical robot's native JetStream service sends synthetic global turns in this
order:

```text
LISTEN (hotphrase: true, mode: CLIENT_ASR or CLIENT_NLU)
CLIENT_ASR / CLIENT_NLU
CONTEXT (runtime location, speaker, skill and other context)
```

BEefy previously finalized the client message immediately. The later CONTEXT
could not affect that response. For weather, neither an explicit place nor runtime
coordinates were available at lookup time, so OpenWeatherReportProvider used its
configured default, Boston,US. Voice recognition normally gives CONTEXT time to
arrive before the final transcript is processed.

This is different from the JavaScript JetStream simulator, which sends CONTEXT
before synthetic input. Testing only the simulator hides the physical-robot bug.

## Evidence

The checked-out native binary at
`BEnch/usr/local/bin/jibo-jetstream-service` has SHA-256
`c3c138ff1d3599d7fe50bf9afdc036dfd7165897cf4481fadd54cbc236046f30`.
Its symbol table and Thumb disassembly show:

- `ListenLoop::phw_jm_hub_client_connect_result` passes the mimicked ASR/NLU to
  `hold_asr_nlu_properties_for_sending`, then calls `LhubClient::send_listen`.
- In `LhubClient::send_listen`, the LISTEN is sent at `0xae0e2`,
  `sending_client_asr_nlu` is called at `0xae154`, then runtime context is fetched
  at `0xae17c` and the CONTEXT is sent at `0xae1a6`.
- `get_client_asr_or_nlu_mode` declares CLIENT_ASR or CLIENT_NLU when synthetic
  input is present.

No live robot connection was used. Regression tests reproduce these packet
orders through the cloud WebSocket service.

## Fix and coverage

For a global LISTEN declaring CLIENT_ASR or CLIENT_NLU, BEefy now retains early
client input in that transaction until CONTEXT arrives. It resumes only after
location, identity and other context have been applied. Transaction reset and
existing no-input timeout clear pending input. Local skill NLU pass-through,
standalone client messages, and legacy clients without a declared mode keep their
existing behavior.

`SyntheticTurnContextTests` checks weather provider coordinates and town for both
message orders and both input modes. It also checks context delivery for every
BEam cloud menu button: full report, weather, news, calendar, commute, joke, dance,
and surprise. These assertions cover the shared context loss; they do not assert
that each feature had a separately observed user-visible failure.

The earlier BEam weather-only city suffix workaround was removed. The fix needs
to be deployed with BEefy; BEam menu requests retain their original text.
