# BEefy Roadmap

## Purpose

This is the long-range story for BEefy.

Use it when someone wants the shape of the project without reading every release note, backlog entry, or live-test log.

The current execution truth still lives in:

- [Development plan](development-plan.md)
- [Feature backlog](feature-backlog.md)
- [Release 1.0.20 plan](release-1.0.20-plan.md)
- [Transcendent Software managed BEefy Cloud service](https://api.5x1.com)
- [Cloud deployment and topology plan](cloud-deployment-topology-plan.md)
- [Storage trust and consensus plan](storage-trust-consensus-plan.md)
- [Device bootstrap path](device-bootstrap.md)

## North Star

Bring Jibo back by giving him one conversation server, BEefy, and the rest of the BE family around it: BEach, BEetle, BEaker, BEam, and BEnch.

## Guiding Principles

- Preserve the original skills and visual design before adding new behaviors.
- Build the hosted cloud first so the robot has something stable to talk to.
- Use OTA to reduce friction after the cloud is proven.
- Keep every migration reversible.
- Favor small, source-backed slices over speculative rewrites.
- Let Jibo remain the face of the experience. BEefy answers. BEam runs the on-robot skills.

## Roadmap At A Glance

| Phase | Focus | Why It Matters |
| --- | --- | --- |
| 1 | Working hosted cloud | Restores the services Jibo already expects and gives us the current platform truth. |
| 2 | OTA-assisted recovery and updates | Makes ownership easier by turning the cloud into the delivery path for recovery and upgrades. |
| 3 | Open Jibo OS / mode conversion | Creates an owned runtime and configuration layer while preserving the original experience. |
| 4 | BE family | BEach, BEetle, BEaker, BEam, and BEnch stay pointed at this server and at BEaker. |
| 5 | Household skills | Calendar, weather, news, and the skills already on the robot. |

## Phase 1: Working Hosted Cloud

Current state: in progress.

The near-term job is to keep the hosted cloud stable and honest:

- maintain HTTP and WebSocket compatibility for startup and turn handling
- keep the .NET cloud as the production track
- keep Node as the reverse-engineering oracle and fixture source
- continue update, backup, restore, media, STT, and live-capture proof, starting with the update/backup/restore slice
- keep the real-device bootstrap path documented and repeatable

Exit criteria:

- a real Jibo can reach the hosted cloud consistently
- the cloud can carry the startup and conversation flows needed for daily use
- update and recovery behavior is understood well enough to trust the next layer
- managed providers can consume stable identity, onboarding, storage, export, and recovery contracts without making self-hosting depend on one commercial operator

## Phase 2: OTA-Assisted Recovery

Once the hosted cloud is solid, OTA becomes the simplification layer.

This phase should:

- move software updates and recovery flows into a reliable hosted path
- reduce how often owners need manual RCM or network patching
- make device recovery and version management feel like a product instead of a lab exercise
- keep rollback and failure handling explicit

OTA is the path that makes ownership easier. It is not the thing that must be solved before the cloud can live.

## Phase 3: Open Jibo OS / Mode Conversion

After cloud and OTA are trustworthy, the project can move from "open cloud" to "open platform."

The goal is not to erase stock Jibo. The goal is to give owners an Open Jibo mode that:

- preserves the original Jibo feel and skill surface
- can be installed or selected without a one-way trap
- can fall back to stock behavior when needed
- makes future features easier to ship on top of a known runtime

This is where the breadcrumbs in the repo become important:

- `open-jibo`, `open-jibo-ai`, `open-jibo-self-hosted`, and `open-jibo-developer` modes
- a startup migration skill that can invite existing owners to convert and keep the menu entry available afterward
- a reversible path back to stock
- the hosted sites and support docs on `api.5x1.com` and `openjibo.ai` that explain the transition clearly

## The BE family

BEefy is the conversation server. BEach, BEetle, BEaker, BEam, and BEnch are the rest of the same system: point the robot, flash credentials, serve updates, run on-robot skills, and ship the services image. There is no separate orchestration product and no account portal.

## Phase 5: Ecosystem Expansion

After the core platform is stable, BEefy can grow into broader household value:

- calendar and scheduling
- smart home and Home Assistant style control
- shopping lists and household memory
- multi-user and family recognition
- richer media and content experiences
- provider-backed news, weather, and personal report flows
- eventual multi-Jibo interaction
- before direct multi-Jibo transport exists, the identity graph should already support multiple robots, multiple people, and shared household loops

## What We Must Preserve

No matter how far the platform grows, these should stay true:

- original skills should still feel like Jibo
- design should stay recognizable, not generic
- migration should be opt-in and reversible whenever possible
- the cloud should serve the robot, not replace his identity
- technical modernization should preserve charm instead of sanding it off

## Where To Go Next

If you want the current execution truth, read:

- [Development plan](development-plan.md)
- [Feature backlog](feature-backlog.md)
- [Release 1.0.20 plan](release-1.0.20-plan.md)
- [Transcendent Software managed BEefy Cloud service](https://api.5x1.com)

If you want the first-device path, read:

- [Device bootstrap path](device-bootstrap.md)
- [Open Jibo mode conversion plan](open-jibo-mode-conversion-plan.md)
- [Cloud deployment and topology plan](cloud-deployment-topology-plan.md)
- [Storage trust and consensus plan](storage-trust-consensus-plan.md)
- [Support tiers](support-tiers.md)
- [Public site plan](public-site-plan.md)
