# Plan: Fix Arena Clickability + Rebuild DM Board Layout

## Status context
Plan mode is blocking normal file edits. This file is the approved-to-execute plan.
Pending: exit plan mode to implement.

## Goal
Resolve the two user-reported runtime problems in the Arena, verified by real input pressure:
1. "Nothing in the arena is clickable."
2. Arena visual disposition does not follow the standard Duel Masters board architecture.
3. (Secondary, coupled) "All my handcards are highlighted" — treat as layout/selection symptom; assert the
   exactly-one-selected invariant during verification.

## Findings (already analyzed, from code reading — no edits made)
- All previous harness verification drove clicks via `EmitSignal(gui_input)` on CardViews, which bypasses the
  real input pipeline (hit-testing, z-order, MouseFilter, clipping, CanAct gating). First real playtest exposed
  the gap.
- Most likely clickability causes, in order of likelihood:
  a. The arena layout overflows the 1920x1080 window. BuildLayout stacks 11 rows
     (opp hand / opp mana / opp battle column + HUD + bottom shields / battle / mana + your hand + footer)
     inside a ScrollContainer; battle/mana/hand rows can sit below the fold, so real clicks there land on the
     scroll/clip area rather than the cards.
  b. An input-swallowing control on top of the zones (verify via `Viewport.GuiPick(pos)` — returns the exact
     control real input would hit). Proven-clean so far: `_selectRoot` hides on Start, `_handPopup`/`_inspectOverlay`
     default hidden, `SceneOptionsMenu` only grabs its 44px corner.
  c. `CanAct`/`_aiDriving`/phase gating silently early-returning handlers (needs a runtime trace).
- Guardrail carried over: 0-warning build, 40 domain tests green, temp harness deleted before handoff.

## Phase 0 — Diagnostics harness (temp `tools/runtime_check/`, deleted at the end)
Rebuild SmokeCheck.cs + SmokeCheck.tscn with REAL-input primitives:
- `GetViewport().GuiPick(pos)` probe: prints the picked control + ancestor chain at any screen point.
- Real click: `GetViewport().PushInput(InputEventMouseButton{Left, Pressed, Position=global center})`,
  then release, then assert resulting state.
- Subscribe to a target view's `GuiInput` to observe whether real events are actually delivered to it.
- Geometry diagnostics: window size; each zone view's global rect; per-row "inside viewport?" check;
  ScrollContainer v-scroll MaxValue vs. visible size (proves/falsifies overflow-below-the-fold).
- State trace at click time: `CanAct`, `_mode`, `_aiDriving`, `_selectRoot.Visible`, active player, phase.
- Reflection helpers (Field / Call / ZoneViews / FindButton) from the previous harness, reuse as-is.
- Run headless first; if PushInput does not route events headless, rerun the same scene windowed
  (approved: brief window flash, auto-quit after ~10-15s) so the true GUI pipeline is exercised.
- Assert the interaction battery through real clicks: start duel, mana tap/untap, hand popup open,
  Look-charg/cast/summon affordability, popup, inspector open+Esc close, Esc popup close, summon adds NEW
  creature, cast to grave, 5-cost gate disabled, exactly-one-selected invariant on the hand each step.

Deliverable: a concrete root-cause verdict (which of (a)/(b)/(c), or another) based on the probe output.

## Phase 1 — Fix clickability root cause
Depends on Phase 0 evidence:
- Overflow (a): fold Zone building so everything fits 1080p without scroll AND make card sizes derive from a
  vertical budget (see Phase 2 sizing rule) rather than overflowing into a scroll view.
- Blocker (b): remove/adjust the offending control and its MouseFilter/parent since it must never eat zone input.
- Gate (c): correct the AI-driving / phase bookkeeping that leaves handlers no-oping from turn one.
Re-run Phase 0 harness until 0 FAIL before touching layout (isolate cause).

## Phase 2 — Rebuild layout to DM architecture (mirrored, symmetric — approved)
New vertical stack, top → bottom:
```
Header             (< Main Menu  ARENA  <spacer>  New Duel)
OPP HAND            compact face-down stack row (outer edge)
OPP MANA            compact row, outer edge (mirrored 180deg decor)
OPP SHIELDS │ OPP DECK │ OPP GRAVE    middle band
OPP BATTLE          inner, adjacent to center (largest)
HUD                 turn / prompt
BATTLE ZONE         inner, adjacent to center (largest)
SHIELDS │ DECK │ GRAVE                middle band
MANA ZONE           outer edge, closest to player
YOUR HAND           art cards at the very bottom edge
Footer             (<End Turn>  <spacer>  hint)
```
User spec mapping (their words -> our rows):
- Battle at the top of your side, closest to opponent  -> BATTLE ZONE directly under HUD (inner).
- Shields horizontally in a row beneath the battle zone -> SHIELDS │ DECK │ GRAVE middle band.
- Deck at the far side corner; Graveyard next to/behind it -> DECK + GRAVE mini-piles beside the shields row.
- Mana at the very bottom, closest to you, number readable -> MANA ZONE row above YOUR HAND.
- Mirrored top side (approved): opponent battle faces ours across the HUD.

Design rules:
- Battle zones get `SizeFlagsVertical = ExpandFill` (absorb slack; shrink rather than overflow).
- New `RecomputeCardSize()` budget: reserve fixed chrome (header, HUD, footer, hand title, margins/seps) then
  divide remaining height so Full cards, Mana and Stack sizes all fit a 1080p viewport with NO vertical scroll.
- Replace plain DECK/GRAVE count labels with compact mini-piles: face-down back + `DECK (n)` caption for the
  deck; face-up top card + `GRAVE (n)` for the graveyard (empty grave shows a dim placeholder). Fields
  `_bottomDeckLabel/_bottomGraveLabel/_topDeckLabel/_topGraveLabel` keep point.

## Phase 3 — Verify (real input, new layout)
- Phase 0 harness updated for new geometry: per-zone GuiPick must return the zone's CardView; every zone
  clickable through real clicks; no scroll required at 1080p (v-scroll MaxValue == 0).
- Windowed fallback pass (~10-15s, approved) for the true GUI pick on the real display.
- `dotnet build` 0 warnings; `dotnet test` 40/40.

## Cleanup & handoff
- Delete `tools/runtime_check/`.
- Final build + test green.
- Handoff note: rebuild C# in editor (F5) required; playtest script (start duel -> click hand card -> popup ->
  look/summon/cast; click own mana to manually tap/untap; ready creature click prompts attack; Esc closes
  popup/inspector).

## Open decisions (resolved)
- Proceed with the plan & execute after plan-mode lift. (Approved)
- Mirror the opponent side symmetrically. (Approved)
- Windowed verification fallback allowed. (Approved)