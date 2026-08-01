# Companion Feature Ideas

Planning doc only — nothing here is implemented. The pitch: MOOGLradio's
Dalamud plugin already has now-playing access (`NowPlayingClient`,
`Models/NowPlaying.cs`); the opportunity is to lean into being an
**in-game companion**, not just a portable version of the website player.
A desktop tab can be copied to another screen; something that reacts to
where your character is standing and slots into daily play cannot.

All of these are opt-in (default off or unobtrusive) — this plugin
should never feel like it's competing with the game's own UI for
attention.

Organized by (rough) implementation complexity, with value noted for
each. "API dependency" flags features that need new fields/endpoints
from the moogl-radio control-plane (see `ARCHITECTURE.md` in that repo)
beyond what `GET /api/now-playing` currently returns.

## Low complexity, self-contained (client-side only)

These only need what the plugin already has — no new API surface.

- **Block notifications** — Toast/chat message when `NowPlaying.Block`
  changes (e.g. a new DJ set or programming block starts). `Block` is
  already polled every 15s in `NowPlayingClient`; this is a diff-and-notify
  on top of the existing `Updated` event. Config toggle + maybe a
  "notify only if window is closed" option so it's not double-noise.
  **Value: high.** Directly useful, cheap to build, showcases "ambient
  awareness" without opening the window.

- **Track/DJ change chat notification (lighter version of the above)** —
  Optional Echo/chat-line on track change, for players who keep chat
  visible more than they keep the plugin window open. Same diffing
  mechanism as block notifications, could ship together.
  **Value: medium.** Nice-to-have, very low effort once block
  notifications exist.

- **Independent on/off toggle for chat notifications** — Block
  notifications and track/DJ notifications should each have their own
  config switch (not one shared "notifications on" flag) — a player may
  want block announcements but not per-track spam, or vice versa. This
  is a `Configuration.cs` addition plus a couple of checkboxes in the
  settings UI; cheap, but worth calling out explicitly since every
  notification feature below depends on this toggle existing rather than
  being bolted on ad hoc later.
  **Value: high, low cost.** Prevents the plugin from feeling noisy —
  notification fatigue is the fastest way to get a companion feature
  disabled entirely.

- **Listener count as a passing detail** — `NowPlaying.ListenerCount` is
  already fetched but not surfaced in `MainWindow.cs`. Just render it
  ("312 listening") somewhere in the existing UI.
  **Value: low-medium, near-zero cost.** Good "quick win" to bundle with
  another change.

## Medium complexity (client-side UI work)

- **Mini-player mode** — A smaller alternate layout showing just the
  essentials (play/pause, track title, maybe DJ name) instead of the
  full window with cover art and marquee. Likely a toggle in the gear
  settings or a second window class in `Windows/`, sharing state with
  the main window rather than duplicating polling logic. Should still
  respect the existing lock/click-through behavior.
  **Value: high.** Low-friction "leave it running in the corner" use
  case — the more the plugin can shrink out of the way while staying
  useful, the more it earns a permanent spot on screen, which is the
  whole point of a companion rather than a destination window.

- **Compact "what's next" strip** — A collapsed/mini display mode
  showing the upcoming block or DJ, not just current. Needs a small
  amount of new UI work in `MainWindow.cs` (a secondary line/marquee)
  and depends on the API actually exposing a "next" field — see API
  dependency note below. Could ship a placeholder version using only
  currently-known data (e.g. "Block: <name>" with no forward-looking
  info) but the real value is the *next* part.
  **Value: high.** This is the single most-requested-feeling feature in
  the pitch — turns the plugin from "what's playing" into "what's
  coming," which is what makes people plan around it.
  **Depends on:** control-plane exposing schedule/next-block data (not
  in `NowPlaying` today).

- **"Time until" block notifications (1 hour / 15 minutes before)** —
  Extends the chat-notification system with scheduled lead-time pings
  ("Block X starts in 1 hour" / "in 15 minutes"), gated by the same
  chat-notification toggle above. Needs a local scheduler that compares
  "now" against the block's known start time and fires once per
  threshold (careful not to double-fire across the 15s poll interval,
  and to reset correctly if a block's start time shifts). Should
  probably be its own sub-toggle under notifications, since not
  everyone wants advance pings vs. just "it started" pings.
  **Value: high.** This is what actually lets someone plan around a
  set instead of just reacting to it — closer to a calendar reminder
  than a status readout, which is a step up in "companion" feel.
  **Depends on:** control-plane exposing block *start time*, not just
  current block name — same schedule data dependency as the "what's
  next" strip above, so these two should likely be designed together.

- **Theming system (general)** — Before zone-aware anything, build the
  underlying concept of a "theme": a named bundle of colors/accents
  (background tint, text color, accent color) that `Windows/Theme.cs`
  can apply, plus a way to switch between them. Start with a small set
  of hand-picked, user-selectable themes in the settings UI (e.g.
  Default, Dark, a MOOGLradio-branded accent color) — this is the
  foundational plumbing (a `Theme` model/enum, a "current theme"
  config value, `MainWindow.cs` reading from the active theme instead
  of hardcoded colors) that everything else in this section builds on.
  **Value: high, and it's a prerequisite.** Cheap on its own, and
  avoids having to retrofit theme-switching logic later once
  zone-awareness (or any other automatic theme trigger) is layered on
  top of it.

  - **Zone-aware visual themes (built on the above)** — Use Dalamud's
    client state / zone change events (`IClientState.TerritoryChanged`
    or similar) to automatically switch the *active* theme (from the
    set established above) based on current zone or zone type (city
    vs. dungeon vs. overworld), rather than introducing a separate
    theming mechanism. Needs to stay tasteful — no jarring recoloring
    mid-combat, and should probably disable/dim during duties so it
    doesn't distract. Should be an opt-in mode layered on top of manual
    theme selection (e.g. "Auto (zone-based)" as one more entry in the
    theme picker), not a separate on/off switch that fights with it.
    **Value: medium-high, novelty/delight factor.** This is the feature
    that's hardest to copy to a desktop site — it's the clearest
    "ritual" hook since it makes the plugin feel present in the game
    world. Also the easiest to overdo, so needs restraint in design (a
    small color accent, not a full re-skin).

## Higher complexity / cross-system dependency

- **One-click Discord event reminder** — Button in the plugin that,
  for an upcoming scheduled DJ set/event, either (a) opens the Discord
  event/invite link in the user's browser, or (b) sets a local
  in-game/OS reminder (toast at T-minus N minutes). Needs: the
  control-plane to expose event data (time, Discord link) via the API,
  a way to open URLs from Dalamud (`Dalamud.Utility.Util.OpenLink` or
  similar — should prompt/confirm before opening per this plugin's own
  security posture, since it's launching an external browser), and
  local scheduling/notification logic (a timer + toast, since Dalamud
  plugins don't get OS-level notifications) for the "remind me" case.
  **Value: high but effort-heavy.** Turns the plugin into a bridge
  between the in-game session and the Discord community, which is a
  real differentiator, but it touches the most systems (new API
  contract, external link handling, local reminder scheduling) and has
  the most user-facing failure modes (stale events, timezone bugs,
  missed reminders while the client is closed).
  **Depends on:** control-plane exposing event schedule data with
  Discord links; needs new API contract design first.

## Suggested sequencing

1. Notification toggle plumbing + block notifications + track/DJ
   notifications + listener count surfaced — all self-contained, ship
   together as a quick "companion v1". The toggle should land first
   since everything notification-related hangs off it.
2. Mini-player mode (self-contained UI work, independent of the rest).
3. Basic theming system + a small set of selectable themes
   (self-contained, and a deliberate prerequisite for zone-aware
   theming rather than a nice-to-have).
4. Zone-aware theming as an "Auto" option on top of #3 (needs design
   care — build in isolation, get it feeling right before shipping).
5. Coordinate with the moogl-radio control-plane on exposing block
   *schedule* data (next block + start times) — this unlocks the
   "what's next" strip, the "time until" lead-time notifications, and
   is a prerequisite for the Discord reminder feature. Worth designing
   these three together since they share the same API dependency.
6. "What's next" strip and "time until" notifications once the API
   supports it.
7. Discord event reminder last — biggest scope, depends on #5's API
   work plus new local-notification and external-link-handling code.
