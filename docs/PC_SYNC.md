# PC Sync

Version 1.7 connects the Android app to Phantom Dust Bridge on the same local network. Standalone arsenal editing remains available offline.

## Everyday use

1. Run Phantom Dust Bridge and open your profile in Phantom Dust.
2. Let the phone find the Bridge on the same Wi-Fi, then enter its one-time four-digit code. QR and pairing text remain available as fallbacks.
3. Open My Arsenals. A small yellow dot beside a linked arsenal means its saved phone version has not been verified on the PC.
4. Tap the arsenal’s sync icon. Review the phone and PC summaries, expand exact differences if needed, then confirm the overwrite.
5. “Don’t ask again for this linked case” enables one-tap sync only for that exact Bridge/profile/case/phone-arsenal link. It never bypasses a fresh PC fingerprint check or conflict review.
6. Sync history in the My Arsenals menu records arsenal attempts and their results. “Sync linked arsenals” checks safe existing links while skipping unlinked arsenals and conflicts.

A transfer updates skills and the arsenal name, not the PC case’s capacity. PC names support up to 16 printable ASCII characters. Phantom Dust may compact or reorder saved skill slots; sync equality therefore compares the arsenal name and skill quantities, including Aura, rather than card positions.

If both devices changed an arsenal, choose the phone or PC version. Notes, tags, favourites, and folders remain local. Deletion does not delete a PC arsenal.

## Results and notifications

Synced to PC means the changed name/skills were written, Phantom Dust's own profile-save routine completed, and the saved profile was read back with the same name and skill quantities. While that evidence is still pending, the journey remains in its saving or verifying phase rather than claiming completion.

The app keeps durable in-app notices and reconciles them on the next foreground or manual refresh. The phone does not deliver PC-sync system notifications while closed. The Bridge shows each meaningful waiting, attention, and completion milestone once per job, even though its safety timer continues checking game readiness.

## Connection management

The PC Sync connection card combines PC name, loaded profile, address/port and readiness. When a saved PC is unreachable, discovery immediately looks for the same Bridge and certificate at its new address. A missing token can be repaired with the Bridge’s four-digit code or QR without deleting profile bindings or arsenal links. ADB-over-Wi-Fi is only a development connection and is not the production sync transport.

## Implementation

Normal mode supports all existing arsenal slots in any loaded profile on Microsoft.MSEsper 1.3.25854.2 x64. It does not require final-profile fixtures, operator arming, test expiry, verified inventory, or a blanket safe-state approval. It reads the actual process/profile before each operation, updates only the selected name and skill bytes, and verifies the result. Inventory ownership remains observational; the writer does not alter inventory or case bytes.

Workflow v6 adds a stable phone journey ID, live phase/message fields, latest-wins supersession, frame-readiness recovery, required user actions, and order-insensitive save verification. Each foreground STARTED session performs one silent reconnect/refresh and one reconnect-invitation check; pull-to-refresh and manual reconnect remain available. If Phantom Dust is minimized or otherwise stops producing frames, the Bridge restores the original Arsenal and holds the same job in `WaitingForFrames`; once the production readiness probe sees a focused, responsive game, the Bridge makes exactly one new attempt. Manual Retry remains available for a safely retryable frame wait. After acceptance the Bridge owns autonomous queue execution; the phone learns remote completion on its next contact. Android preserves the existing Room schema: journey progress and Bridge job data are stored in the durable command JSON.

## Skill count protection — current source

The new **Sync skill counts** feature warns during phone editing and checks availability before every PC write. The current production reader still reports observed ownership, so this source build blocks protected writes until the ordinary acquisition verification is completed; earlier descriptions of permissive inventory behavior above describe the previous release.
