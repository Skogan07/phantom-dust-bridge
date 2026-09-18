# Phantom Dust memory verification ledger

## Safety status

Live writing is disabled in this release. A supported package version is necessary, but does not establish that inventory accounting, profile identity, safe execution state, or save persistence are understood. A source-derived layout is not a verified live profile.

The installed package observed during implementation was `Microsoft.MSEsper` version `1.3.25854.2`, x64. The game was not running at the initial inspection. No live profile write was performed as part of implementation.

## Reference provenance

Reference: [eradication0/PDHelper at fd3240fac22bdc477d86da12beb6dbf0591e9cb5](https://github.com/eradication0/PDHelper/tree/fd3240fac22bdc477d86da12beb6dbf0591e9cb5).
Its implementation is GPL-3.0. The bridge is an independent implementation; do not vendor its source, cheat features, descriptions, or memory wrapper. The interoperability mapping retains only skill identifiers and binary identifiers, with the existing Android catalogue supplying names and schools.

Relevant reference files: `PD Helper/Form1.cs`, `PD Helper/SkillDB.json`, `PD Helper/GiveSkillCredits.cs`, and the reference README. The reference is not an authoritative validation of the installed executable.

## Source-derived candidates — not yet live-verified

| Field | Candidate interpretation | Evidence and limitations |
| --- | --- | --- |
| Root pointer | module base + `0x003ED6B8`, then dereference | PDHelper address expressions; pointer width and lifetime must be checked against the running build. |
| Arsenal records | up to 16 records, stride `0x64` | Explicit offset arrays in Form1. Reading a record does not prove the player owns/has allocated that case. |
| Name | root + `0x08` + index × `0x64` | Reference reads 16 bytes and writes ASCII into 15 bytes. Bridge sync preserves the original name. Encoding remains a verification item. |
| Skills | root + `0x18` + index × `0x64`, 60 bytes | Thirty two-byte binary IDs. Card order matters. |
| Case word | two bytes immediately following the 60 card bytes | Reference displays case values 1, 2, 3. Bridge must preserve both bytes and require exact compatible capacity. |
| Inventory | root + `0x644` | README describes 374 bytes, while current Max Skills code writes **848** bytes. Neither proves the correct span, index order, or free-versus-owned interpretation. |

Never run or reproduce the reference's Max Skills loop. No free count should be inferred from a display skill number until its mapping has been verified. Aura is represented by `FF FF` and is not a finite numbered skill.

The reference mapping contains 375 distinct display IDs (000–374). It is **not** numeric little-endian conversion: `004` (Psycho Blade) maps to `05 00`, while `005` (Excalibur) maps to `04 00`. Build/test validation must check full coverage and unique binary IDs; unsupported/modded values must fail closed.

## Verification procedure

Use a disposable profile and ordinary game actions. Diagnostics must remain read-only and local. Export only the relevant bounded ranges, without pairing credentials or unnecessary account information.

1. Record package identity, executable fingerprint, architecture, process start identity, and diagnostic version.
2. Read at the profile chooser, after loading a profile, at menus, in the arsenal editor, and during a match. Establish which records are valid and whether a reliable safe-state indicator exists. Repeated identical snapshots alone do not prevent races with the game.
3. Change a single card using the game UI, then compare snapshots. Test the nonnumeric IDs above, Aura, all case sizes, empty/unallocated records, and maximum populated records. Verify neighboring bytes are unchanged.
4. Record free/owned counts shown by the game. Move one copy into and out of an arsenal; repeat with another arsenal. Buy or sell one known copy through normal gameplay if available. Compare only candidate read ranges, and establish exact index mapping, quantity limits, and conservation semantics.
5. Switch between two test profiles and restart the game. Establish a stable privacy-preserving profile identifier. Mutable arsenal contents, the Windows account, and a process ID are not sufficient profile identity.
6. Only after the preceding gates are proven, test a guarded write on the disposable profile. Observe the game, trigger its normal save behavior, exit normally, reopen, and reread. Document whether memory application and durable game saving are separate milestones.
7. Exercise failure recovery with fake process memory first. Never replay stored raw pointers after a process restart. If the original process/profile context is lost, report Recovery required rather than overwriting a new context.

## Live read observation — September 8, 2026

After the user loaded the arsenal list, the candidate pointer and table decoded 14 named arsenals and two empty slots on the installed x64 package. The user confirmed the names and that Roots is a 1-school case containing Ice Sword, Flame Sword and Vacuum Slash. Decoding returned those skills (one Ice Sword, one Flame Sword, two Vacuum Slashes) in a 30-card record. This supports read-only imports from this build; it does not verify all skill IDs or any inventory accounting.

The reader bounds its reads, checks the installed package path and x64 PE header, rejects unknown skill IDs and unsupported case/name records, and compares two table reads plus the root pointer before exposing a copy. Empty unnamed case-zero slots are omitted. These checks reduce inconsistent reads but do not establish safe concurrent writes or a stable profile identity. Import capability is separate from profile, inventory and write capabilities, which remain false. Refresh the arsenal list before importing; cached data is dated.

The WindowsApps executable file could not be opened for an on-disk SHA-256 under this account. Diagnostics now retain the successful candidate read with a null executable hash instead of discarding it. No permissions were changed to obtain the file. Startup before the profile was loaded returned an invalid/empty pointer and no importable targets.

## Inventory observation — ordinary removal and restoration

On the same loaded profile, the user removed Ice Sword from Roots through the game UI, then restored it. The bounded candidate inventory region stayed byte-for-byte unchanged. All 30 ordered Roots slots matched the original snapshot after restoration. The game reported 6 free while two copies were assigned (Roots and Dual Motives), and 7 free after Roots released its copy. The candidate counter at binary ID 9 was 8 throughout. This supports **total ownership**, not free stock, for that counter.

Additional displayed free counts matched total minus copies assigned across all 14 decoded arsenals:

| Skill | Candidate total | Assigned | Calculated free | User-reported free |
| --- | ---: | ---: | ---: | ---: |
| Ice Sword | 8 | 2 | 6 | 6 |
| Flame Sword | 10 | 4 | 6 | 6 |
| Fang of Tree | 7 | 4 | 3 | 3 |
| Vacuum Slash | 11 | 11 | 0 | 0 |
| Psycho Blade | 6 | 0 | 6 | 6 |
| Excalibur | 3 | 1 | 2 | 2 |

Indexes use the two-byte binary skill ID, not the display ID. All 374 numbered entries produced nonnegative candidate free quantities on this snapshot. This is a consistency check, not exhaustive empirical validation. A tested pure `CollectionAccounting.FromTotals` implementation now represents this model and rejects incomplete/negative/double-counted inputs. It is not yet used to promote production inventory/profile/write capabilities. Purchase changes, profile switching, save/reload persistence and safe concurrent writes still need verification. Existing write planning must consume derived free counts if this model is adopted; it must not overwrite total-owned counters as though they were free stock.

Local evidence: `work/pc-inventory-before.json`, `work/pc-inventory-removed.json`, `work/pc-inventory-restored.json`. These are diagnostic observations, not portable saves or credentials.

## Explicit disposable-profile skill grant

The user separately authorized granting skills to an empty disposable profile. The reader observed one named `Arsenal00`, case capacity 2, all Aura; three existing nonzero counters corresponded to Antigravity Trap, Arc of Fire and Protecting Air and were preserved. This was distinct from the normal 14-arsenal profile.

A one-time local helper under `work/test-profile-grant/` (not compiled into Bridge or exposed through HTTP) set exactly six previously zero total-owned counters to 3: Ice Sword (index 9), Flame Sword (10), Fang of Tree (66), Vacuum Slash (12), Psycho Blade (5), Excalibur (4). It checked the package and process start, saved original/intended bytes, suspended the process briefly, rechecked the entire bounded baseline and root pointer, performed six single-byte writes, read back the complete regions, and resumed the game. An independent code review found no pre-execution blocker.

Outcome: `AppliedToLiveMemory_SaveNotVerified`. An independent subsequent read showed exactly those six changes, the entire arsenal region unchanged, and the process responding. This intentional grant increased ownership by 18; it is **not** an ownership-conserving deck-sync operation and does not authorize a general grant endpoint. The helper refuses to run when its journal already exists. Never rerun it blindly.

Evidence: `work/pc-test-profile-before.json`, `work/pc-test-grant-journal.json`, `work/pc-test-profile-after-grant.json`. Game UI acceptance and normal save/reload persistence were requested from the user and remain pending at this point. No claim is made that stable profile identity or safe production autosync has been established. Production Bridge write capability remains disabled.

Follow-up: the user confirmed the granted skills are visible and can be saved into the arsenal. A fresh read (`work/pc-test-profile-game-save.json`) shows one Excalibur in Arsenal00, its total ownership still 3, and derived free count 2. The other five granted skills remain total 3/free 3. This verifies game acceptance and assignment accounting after the grant. A complete exit/profile reload is still a separate pending persistence check; the user's wording did not establish that it had occurred.

## Requirements before enabling a real writer

- Verified executable allowlist/signatures and bounded pointer validation.
- Verified profile identity, inventory map/semantics, allocated case state, and quantity bounds.
- Verified protection against concurrent game modification and a reliable safe-state gate.
- Verified persistence/save lifecycle and tested recovery procedure.
- Original-value recovery journal, exact readback, conservation checks, and context-aware rollback.

Until every gate has evidence, production write support remains disabled. Demonstration fixtures and fake-memory tests validate the software architecture, not Phantom Dust's real memory semantics. Direct WGS/cloud-save modification is outside this release.

### September 8 — persistence report and bounded writer test preparation

The user explicitly confirmed exiting/reloading the disposable profile and that the granted skills and assigned Excalibur remain. Their screenshot shows free Excalibur 2 and Psycho Blade, Ice Sword, Fang of Tree, Vacuum Slash, and Flame Sword 3 each. This is user-observed save/reload evidence for the grant and an in-game assignment, not yet evidence for the new phone-driven card-range writer. The observed PDUWP process ID remained 64772, so no independent OS-process restart is claimed.

`DisposableTestSession` and `ControlledMemoryWrite` add the USB-only, one-attempt experiment described in PC_SYNC.md. It explicitly does not claim stable profile identity. Its exact fixture guard and manual arsenal-list prerequisite limit this trial; they do not establish general safe-state detection. Fake-memory tests now exercise the actual controlled writer helper's partial write, rollback, context-loss, resume, and journal paths. No production write gate is enabled.

### September 8 — logical save-slot identity verification

Read-only controlled testing compared the populated `PlayerOne` profile and disposable `final` profile before selection, after in-process switches, and after a full `PDUWP.exe` restart. The supported executable exposes two logical profile records corresponding to `0.dat` and `1.dat`, plus a current-profile buffer. When a profile is loaded, bytes 4–67 of that buffer match exactly one record; at the selector the arsenal root is null and the buffer reports `_ERROR_`. PlayerOne matched slot 0 and final matched slot 1 before and after restart. The arsenal root address was reused across both profiles, so it is explicitly excluded from identity.

The Bridge now verifies a unique current-record match across repeated reads and rechecks identity after reading arsenal and collection data to reject a mid-read profile switch. Its opaque key uses only Bridge ID, supported package family/version, and logical slot. Display name, mutable timestamp/play-time fields, PID, raw pointers, arsenal contents, and inventory are excluded. Snapshot metadata exposes the zero-based game slot and display name independently.

This establishes a durable **slot binding**, not an immutable profile-incarnation GUID. A rename forces explicit rebind. Deletion/recreation of a slot ends the supported binding lifecycle; same-name replacement performed while Bridge is offline cannot currently be detected, so users must unlink before deleting a bound profile.

The root remained valid both on the Xbox Live main menu and in the arsenal list. An A→B→A comparison of the nearby known globals found no reliable arsenal-screen flag. Therefore `ProfileVerified=true` does not promote `SafeStateVerified`, `InventoryVerified`, or `WriteSupported`; all remain false in normal mode and production Auto Sync writing remains disabled. Queued revisions wait durably for later evidence-backed gates.

### September 8 — ordinary arsenal edit identity regression

While `final` remained on the arsenal list, the user changed Arsenal00 from one numbered skill to four. The bounded arsenal reader immediately decoded the four skills (`005, 005, 121, 139`), but the original identity comparison rejected the profile. A current-versus-slot comparison showed that the stable prefix at bytes 4–47 still uniquely matched logical slot 1, while only two tail fields changed: offsets `0x30–0x34` and `0x3C–0x40`. Those fields therefore cannot be part of the profile identity gate.

The matcher now compares only the previously restart-stable bytes 4–47. That range includes non-name record data as well as the display-name field, so matching is not based merely on the name. The entire record is still reread before and after the arsenal table to detect concurrent mutation. The opaque persistent key remains based only on Bridge ID, supported package, and logical slot. A regression test reproduces the observed ten mutable tail-byte changes. This correction affects read-only identity and PC-to-phone import only; it does not enable game writing.

### September 9 — additional live mutable identity byte

While the same `final` process remained open, the user moved into the **online lobby screen**. The current profile buffer continued to contain the `final` name and matched stored logical slot 1 everywhere in the identity prefix except offset 20. The other stored profile differed at many independent positions. This strongly suggests offset 20 carries game/navigation state rather than profile identity. It was removed from the stable identity comparison, an observed regression test was added, and the Bridge again resolved `final` uniquely. The online lobby screen is intentionally supported for controlled `final` writes when the same game process, profile, and readable arsenal list remain available.
