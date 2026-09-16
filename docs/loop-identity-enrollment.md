# Loop identity and enrollment

End-to-end map of how a Jibo household ("Loop") flows between BEacon (on-robot
management), BEnch LoopManager (seed sync), and BEefy (NLU/STT recognition).

## Ownership

**BEacon owns household people** — add, rename, remove, and profile photos live
in the robot's local Knowledge Base (`/opt/jibo/Knowledge/jibo/loop`).

**BEefy recognizes people** from each speech turn's `runtime.loop.users` via
`SyncPeopleFromLoopUsers`. It does **not** own household CRUD. The portal Loop
panel only attaches calendars to robot-reported people.

**BEefy `ListLoops`** seeds **owner + robot** only so stock LoopManager can boot.
Human members are never driven from cloud into the robot.

## Boot contract

Stock Neo Hub / SSM `LoopManager` requires `Loop_*.List` / `ListLoops` to return
**exactly one loop** whose `members[]` includes:

- the owner (`accountId === loop.owner`)
- the robot (`accountId === loop.robot`, `type: "robot"`)

If the robot member is missing, `_applyLoopChanges` calls
`rootNode.addEdges(undefined, 'robot')` and throws before any human members are
written to `/opt/jibo/Knowledge/jibo/loop/nodes`. BEefy's `MapLoopRecord`
therefore **includes** the robot member. Human-facing APIs (`ListMembers`) may
still list humans mirrored from robot turns; portal calendars use `People`.

The robot member must have **no `firstName`**. Robot-side `UserNode.isJibo` is
`!data.firstName`; a seeded `"Jibo"` first name made introductions list the
robot as an enrollable person.

## Sync path

```text
BEacon People UI
  -> local Nedb /opt/jibo/Knowledge/jibo/loop/nodes
  -> jibo.kb.loop.loadLoop()
  -> ContextProvider runtime.loop.users
  -> Neo Hub CONTEXT / skills / BEefy SyncPeopleFromLoopUsers

BEefy Loop.ListLoops (owner + robot seed only)
  -> @jibo/jibo-server-client
  -> LoopManager._applyLoopChanges (local-wins for humans)
  -> keeps BEacon humans; refreshes owner/robot seed edges
```

`LoopManager` re-syncs every 60 seconds and on `LoopUpdated` notifications. It
reloads the local user layer before applying cloud changes, does **not** prune
local humans absent from the cloud roster, and does **not** overwrite local
human names or profile photos. `rootNode.data.lastFullSyncTimestamp` is still
the signal that seed sync landed (surfaced in BEacon People).

## Enrollment (robot-local biometrics)

| Step | Face | Voice | Name |
|------|------|------|------|
| Capture | `jibo.ics` + LPS (`createIdentity` name = looper `_id`, kind `face`) | `jibo.jetstream.startEnrollmentTurn` / `createSpeakerModel` | `initNameLearning` / `startNameLearningTurn` |
| Flag | `jibo.kb.loop.setEnrollmentFace` → cloud `SetEnrollment` | `setEnrollmentVoice` | `setPhoneticName` → `UpdatePhoneticName` |

Skills: `@be/introductions` (menu + cloud redirect), `@be/who-am-i` (identity quiz; redirects with `entities.loopMemberReferent`).

## Cloud presence and recognition

`ResolveGreetingPresenceProfile` reads `runtime.perception.speaker`,
`peoplePresent`, and `runtime.loop.users`. Greeting turns call
`UpsertGreetingPresence` and also `RecordRecognitionObservation` with
`source: "turn-context"` (voice for speaker, face for present ids). HTTP
`Loop_*.RecordRecognitionObservation` remains available for smoke seeds.

"Who am I" / name recall falls back to loop `firstName` when personal memory is
empty (same multi-person guard as greetings).

## Profile photos

Stored **on the robot** as KB `photo` assets on each `UserNode` (BEacon uploads
a 384×384 centre-cropped JPEG). Do **not** set `data.photoUrl` for local photos
— LoopManager only downloads `http(s)` URLs and never deletes a local asset just
because cloud has no URL.

Cloud blob paths / signed `/media/loop-member-photo/...` URLs are unused for
household management. Protocol `ListLoops` / `LoopUpdated` omit `photoUrl` so
cloud cannot replace BEacon files.

On-face: firmware `ContactButton` reads the local KB `photo` asset in
introductions / who-am-i pickers. BEacon People shows and edits the same asset.

## Owner seed name

`OpenJibo:Owner:FirstName` / `LastName` (fallback: flat `OpenJibo:OwnerFirstName`
/ `OwnerLastName`) seed the account and owner loop member for cloud boot. Once
BEacon edits the owner locally, LoopManager keeps local-wins on human profile
fields.

## Surfaces

| Surface | Role |
|---------|------|
| BEacon People (`:8123`) | **Manage** household: add / rename / remove / photo; enrollment badges; phonetic name; sync diagnostics |
| BEefy portal Loop | Calendars only (people come from robot-reported roster) |
| Main menu | Introductions + Who am I |
| `@be/introductions` / `@be/who-am-i` / `@be/greetings` | Enrollment and named greetings |
| BEefy NLU/STT | Recognize loop members via `runtime.loop.users` |
