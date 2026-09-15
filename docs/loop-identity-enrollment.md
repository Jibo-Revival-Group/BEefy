# Loop identity and enrollment

End-to-end map of how a Jibo household ("Loop") flows between BEefy (cloud) and BEam (on-robot skills).

## Boot contract

Stock Neo Hub / SSM `LoopManager` requires `Loop_*.List` / `ListLoops` to return **exactly one loop** whose `members[]` includes:

- the owner (`accountId === loop.owner`)
- the robot (`accountId === loop.robot`, `type: "robot"`)

If the robot member is missing, `_applyLoopChanges` calls `rootNode.addEdges(undefined, 'robot')` and throws before any human members are written to `/opt/jibo/Knowledge/jibo/loop/nodes`. BEefy's `MapLoopRecord` therefore **includes** the robot member. Human-facing APIs (`ListMembers`, `/api/portal/loop-members`) still filter it out.

The robot member must have **no `firstName`**. Robot-side `UserNode.isJibo` is `!data.firstName`; a seeded `"Jibo"` first name made introductions list the robot as an enrollable person.

## Sync path

```text
BEefy Loop.ListLoops (+ LoopUpdated push)
  -> @jibo/jibo-server-client
  -> LoopManager._applyLoopChanges (jibo-ssm)
  -> Nedb /opt/jibo/Knowledge/jibo/loop/nodes
  -> jibo.kb.loop.loadLoop()
  -> ContextProvider runtime.loop.users
  -> Neo Hub CONTEXT / skills
```

`LoopManager` re-syncs every 60 seconds and on `LoopUpdated` notifications. `rootNode.data.lastFullSyncTimestamp` is the signal that sync landed (surfaced in BEacon People).

## Enrollment (robot-local biometrics)

| Step | Face | Voice | Name |
|------|------|-------|------|
| Capture | `jibo.ics` + LPS (`createIdentity` name = looper `_id`, kind `face`) | `jibo.jetstream.startEnrollmentTurn` / `createSpeakerModel` | `initNameLearning` / `startNameLearningTurn` |
| Flag | `jibo.kb.loop.setEnrollmentFace` → cloud `SetEnrollment` | `setEnrollmentVoice` | `setPhoneticName` → `UpdatePhoneticName` |

Skills: `@be/introductions` (menu + cloud redirect), `@be/who-am-i` (identity quiz; redirects with `entities.loopMemberReferent`).

## Cloud presence and recognition

`ResolveGreetingPresenceProfile` reads `runtime.perception.speaker`, `peoplePresent`, and `runtime.loop.users`. Greeting turns call `UpsertGreetingPresence` and also `RecordRecognitionObservation` with `source: "turn-context"` (voice for speaker, face for present ids). HTTP `Loop_*.RecordRecognitionObservation` remains available for smoke seeds.

"Who am I" / name recall falls back to loop `firstName` when personal memory is empty (same multi-person guard as greetings).

## Profile photos

Stored as a **384×384 center-cropped JPEG** (~30 KB) via `IMediaContentStore` at `loop-member-photo/{loopId}/{memberId}`. Metadata on `LoopMemberRecord`: `PhotoContentHash`, `PhotoContentType`, `PhotoUpdatedUtc`.

Signed URL shape (capability token; robot cannot send auth headers):

```text
/media/loop-member-photo/{memberId}/{contentHash}.jpg?expires={unix}&signature={hmac}
```

- Path basename is the robot's `_syncLoopPhotos` cache key (query is ignored by `url.parse().pathname`).
- Signature lifetime is 24h; every `Loop.list()` re-mints it. Unsigned/expired/wrong-hash → **404** (no existence leak).
- Portal sessions may also authorize the same path (Bearer or `portalSessionToken`).
- Upload: `PUT /api/portal/loop-members/{memberId}/photo` (raw body). Clear: `DELETE .../photo`.
- On-face: firmware `ContactButton` reads the local KB `photo` asset in introductions / who-am-i pickers. BEacon People shows the same local asset read-only.

## Owner seed name

`OpenJibo:Owner:FirstName` / `LastName` (fallback: flat `OpenJibo:OwnerFirstName` / `OwnerLastName`) seed the account and owner loop member. Portal edits win via `PortalEditedUtc`.

## Surfaces

| Surface | Role |
|---------|------|
| BEefy portal People | CRUD + photo upload |
| BEacon People (`:8123`) | Local roster, enrollment badges, phonetic name, last sync time, local photos |
| Main menu | Introductions + Who am I |
| `@be/introductions` / `@be/who-am-i` / `@be/greetings` | Enrollment and named greetings |
