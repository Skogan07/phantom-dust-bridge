> Workflow 6 adds a stable `WaitingForFrames` state and an explicit, same-job retry endpoint. A frame-paused save attempt is rolled back exactly, remains visible on the phone, and waits for the user to restore the game window and retry. Normal Bridge writes require a fresh focused-game snapshot; when the native frame probe is available it must also report ready. The tray shows a non-modal start notice before each write so the user can keep Phantom Dust focused.

# PC Sync protocol v1

The phone performs one reconnect/refresh check per foreground STARTED session, plus explicit manual refresh or reconnect. There is no closed-phone delivery or PC-sync Android system-notification channel. Once a job is accepted, the Bridge owns autonomous resume and game-readiness checks; the phone reconciles the resulting state on its next foreground or manual contact. Bridge tray notices use durable per-job normalized milestone IDs, so repeated timer ticks and Bridge restarts do not spam the user.

Transport: private-LAN HTTPS, port 17431, UTF-8 JSON, camelCase fields, PascalCase state strings, epoch-millisecond timestamps. Requests are limited to 64 KiB. All routes except the bootstrap exchange require `Authorization: Bearer <device-token>`. The bootstrap exchange authenticates its expiring one-time secret. No arbitrary memory API exists.

The exact versioned types are in `pc-bridge/Bridge.Core/Protocol.cs`. Android retains full job JSON, including typed blockers and quantities. Unknown server errors remain visible and must not be interpreted as success.

Snapshots may include `gameProfileSlot` (zero-based), `gameProfileDisplayName`, and `profileBindingKind: "GameSaveSlot"`. Each arsenal includes a card-only `contentFingerprint` in addition to its full target fingerprint. `profileKey` is an opaque hash of Bridge installation ID, supported package family/version, and logical save slot; clients must not parse it. A unique current-to-record match sets `profileVerified` without implying `inventoryVerified`, `safeStateVerified`, or `writeSupported`.

The optional snapshot capability `arsenalImportSupported` permits copying decoded arsenals without implying inventory or write eligibility. Within an explicitly confirmed profile, a unique identical phone/PC pair can claim a link without writing game data. Ambiguous candidates require selection.

Planning accepts optional `requireEmptyTarget` for first-link previews. Link creation defaults to requiring an empty (all Aura) allocated case, unless `overwriteConfirmed=true` accompanies explicit confirmation of the current target fingerprint. Workflow 4 also accepts `identicalDeck` on a link request with `overwriteConfirmed=false`: exact name and skill quantities (including Aura) must match, case capacity must be compatible, and ownership and the exact target fingerprint must validate. This grants no permission to overwrite different content. An already successfully applied active link can reuse its own case. Same-or-larger case matching and resource validation remain independent blockers; confirmation does not override them.

## Pairing

QR/text payload: `{version:1, endpoint, bridgeId, certificateSha256, bootstrapSecret, expiresAt, shortCode, shortCodeExpiresAt, shortCodeAvailable}`. The fingerprint is uppercase SHA-256 of the leaf certificate DER. Android validates the exact leaf and its validity dates in both the trust manager and connection identity verifier. DHCP hostname/IP changes do not replace the paired certificate identity. Discovery is untrusted until this check succeeds.

`POST /api/v1/pair` accepts either `{bootstrapSecret,deviceName,deviceIdentity}` or `{shortCode,deviceName,deviceIdentity}` and returns `{pairingId,token,bridgeId}`. Both credentials expire after five minutes and are one-use. The short code allows five attempts; the QR/text secret allows ten. Matching device identity rotates the phone token while retaining the pairing ID and existing links. Stored device tokens are one-way hashes; the certificate private key is DPAPI-protected. Android encrypts tokens with Keystore AES-GCM under its no-backup directory.

The mDNS TXT record advertises only Bridge ID, display name, certificate fingerprint, API version, and whether short-code pairing is supported. It never advertises the short code or token.

## Routes

| Method and path | Meaning |
| --- | --- |
| GET `/api/v1/discovery` | Public local-network Bridge identity and pairing capability metadata; no credentials. |
| GET `/api/v1/status` | Bridge identity, demo/live mode, game state, pause state, message and refresh time. |
| GET `/api/v1/profile/snapshot` | Authoritative capability flags, profile key, arsenals, inventory, fingerprint and timestamp. Unverified live fields remain absent/flagged. |
| POST `/api/v1/sync/plan` | Read-only plan: `{profileKey,targetSlot,expectedFingerprint,deck}`. |
| GET / POST `/api/v1/links` | List owned active links, or explicitly confirm a target. |
| POST `/api/v1/profile-imports` | Atomically and idempotently claim every arsenal in the freshly verified profile for a whole-profile import. |
| DELETE `/api/v1/links/{linkId}` | Retire link and cancel its pending jobs. Idempotent for an owned/missing link. |
| POST `/api/v1/sync/queue` | Persist desired composition with linked target context. |
| GET `/api/v1/jobs` | Only this device's jobs, newest first. |
| GET `/api/v1/jobs/{jobId}` | Only an owned job; other owners receive 404. |
| POST `/api/v1/jobs/{jobId}/retry` | Requeue the same owned `WaitingForFrames` job after its link context is revalidated. |
| POST `/api/v1/jobs/{jobId}/cancel` | Cancel an owned pending job. |
| POST `/api/v1/control/pause` | `{paused:true/false}` pauses this device's processing. Windows local pause covers all devices. |
| POST `/api/v1/unpair` | Revoke this device, retire its links, cancel pending jobs. |

Errors use an HTTP status and `{message}`: 400 malformed input, 401 authentication failure, 403 non-private interface, 404 inaccessible/missing resource, 409 stale/conflicting context. Android persists rejected operations as needs-attention states; network failures remain queued for retry.

## Decks, links, and planning

Deck payload: `{deckId,revision,name,skillIds:[30 IDs],caseCapacity,rulesetId}`. The name and skills are written to the selected PC arsenal; case capacity is not changed. Numbered retail IDs are zero-padded strings, including `000` Aura.

Link request: `{linkId,deckId,profileKey,targetSlot,linkGeneration,confirmedFingerprint}`. Targets are numbered 1–16. Android first confirms its active app profile against the loaded game slot. The confirmed fingerprint must match the freshly read PC target. Generations increase when replacement is explicitly reconfirmed. Changing targets uses a new link ID after unlinking the old target. A slot cannot have two active owners. A changed bound display name produces `ProfileBindingChanged` and requires unlink/rebind.

Profile import request: `{importId,profileKey,decks:[{linkId,deckId,targetSlot,confirmedFingerprint,baselineContentFingerprint}]}`. The Bridge requires an exact one-to-one mapping of every currently decoded arsenal and commits all links or none. Reusing the same import ID and payload returns the stored result; reusing it for different content is rejected.

Queue request adds `{profileKey,targetSlot,expectedFingerprint,confirmedFingerprint,linkId,linkGeneration,clientJourneyId,deck}`. `clientJourneyId` is a UUID created by Android and remains stable across retries, reconnects, and link repair. Both fingerprints must match the confirmed link. Processing rereads the profile and replans; the queued fingerprint never authorizes overwriting a later PC edit.

Plan result: `{canBuild,canApply,missingSkills,blockers,snapshotFingerprint,targetFingerprint,newFreeInventory}`. A missing-skill entry is `{skillId,needed,available,missing}`; a blocker is `{code,message}`. `canBuild` may be true while `canApply` is false in read-only mode. Unverified inventory produces `InventoryUnverified`, not invented shortages.

## Queue guarantees

Retry identity is the pairing plus `clientJourneyId`; reusing it with different content is rejected. A received newer revision replaces older queued, blocked, or waiting work. If the older journey has begun updating or saving, it finishes safely while only the newest queued successor is retained. A lower revision cannot regain authority by changing link generation. Cancellation is accepted only before writing starts.

Job responses persist the full request, timestamps, state, last plan, live `phase`, `phaseMessage`, and an optional superseding journey reference. Phases are SendingToPc, ReceivedByPc, WaitingForGame, UpdatingPcArsenal, SavingInGame, VerifyingSave, WaitingForFrames, and Synced; blocked work instead provides a required action. A temporary profile mismatch keeps the job retained so it can resume safely. A normal write waits in `WaitingForGameReady` when the fresh snapshot says Phantom Dust is not foreground, with the message `Focus Phantom Dust on your monitor. Sync will start automatically.` If the snapshot is focused but the supplied native readiness probe is negative, it waits with `Keep Phantom Dust focused; waiting for the game to respond.` A waiting job resumes automatically only after both foreground and a supplied probe are positive; older synthetic snapshots may omit either field for an initial attempt. If the game is minimized or its render loop is paused after writing begins, the writer verifies restoration of the exact original Arsenal and holds the same job in `WaitingForFrames`. A rollback, resume, or post-write verification whose outcome is not certain becomes `RecoveryRequired` and is never retried automatically. The writer changes only bounded name/skill fields and reports `Completed` after the saved arsenal has the same name and skill quantities; card order is presentation-only. On startup an interrupted Applying record becomes RecoveryRequired.

`POST /api/v1/jobs/{id}/resolve-recovery` accepts `{profileKey,targetSlot,reviewedFingerprint}` only for the paired device's `RecoveryRequired` job. The Bridge rereads the verified profile and exact target. A match marks that uncertain job `Superseded`, allowing the normal comparison choice to renew the link; a stale review cannot clear the recovery lock.

SQLite commits state with FULL synchronization under a single service lock. Pairings, links, complete requests, pause flags and results survive restart. Demo and live databases are separate. Android's same-database outbox captures edits, reorders, undo edits, and unlink tombstones atomically with deck changes. Backups never replay this outbox.

Android uses a serialized worker chain with periodic retry/recovery. Its three-way comparison uses phone content, current PC content, and the last agreed card hash; simultaneous divergence is never resolved by timestamps or last-write-wins. Windows serializes queue reconciliation and never treats a valid read-only plan as a successful write.

## Testing and fixtures

The central binary map is `pc-bridge/skill-id-map.json`. `SyncTests` checks full mapping coverage and nonnumeric IDs, pairing, idempotency, revisions, ownership, cancellation, and reconciliation. `PcSyncTest` checks Room migration, portable backups, outbox edits, offline relinking, conflict pause, and cancellation during delivery. `PcBridgeEndToEndTest` exercises the actual shared JSON contract and pinned HTTPS server from Android; it requires an explicitly started demo bridge and fresh pairing payload.

### Controlled disposable test extension

Snapshots may include `controlledTestSession` (default false). Only the explicitly armed USB test host supplies true; this is a temporary session identity, while `profileVerified` remains false. Consumers must label it as a disposable experiment, not a verified stable game profile. `AppliedAwaitingGameSave` means live readback succeeded but durable saving has not been proven. `Completed` means either the Bridge observed Phantom Dust commit the real profile payload and exact readback, or a later process session loaded the exact revision. Cancel replies include the actual `state` and `cancelled` boolean; false means the job was no longer cancellable. Terminal applied/recovery states override optimistic phone cancellation.

## Skill count protection

The updated snapshot endpoint advertises `skillCountChecksSupported`; this is separate from `collection.status`. Plans include `checkedAt` and structured `missingSkills`. Unknown or inconsistent inventory prevents application, and an inventory-blocked job requires a new explicit attempt rather than resuming on a timer. A phone-side guidance toggle never bypasses this bridge check.
