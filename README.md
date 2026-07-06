# STS2 Dojo

A **Slay the Spire 2** mod that lets you replay a specific fight from your run history as a standalone practice combat — a training dojo for the fights you keep losing.

Pick any combat from any past run (including runs you played before installing the mod), and the Dojo drops you straight into that single fight with game state (deck, relics, HP, gold, and potions) reconstructed from your run history. Win or lose, nothing is saved and the run doesn't progress: you just get a **Try Again** button. Practice a boss until you've got the line down, without replaying three acts to reach it.

## What it does

- **Adds a "Practice Dojo" button to the main menu.** Opens a browser of your real (non-modded) single-player run history — the mod reads your existing `.run` files directly.
- **Browse and filter your runs.** Sidebar filters by character, ascension, victory/defeat, and max act reached, plus free-text search (character, fight, relic, or card name) and sort by newest/oldest/floor/ascension. Each run row shows the character, result, floor reached, end HP, date, duration, deck/relic counts, seed, and the death quote.
- **Expand a run to its floor map.** Click through any run's per-act map and pick a **combat** floor to replay. Fights the current game version can't reconstruct (e.g. content from a mod you no longer have installed) are greyed out.
- **Replay Setup screen.** Before launching, preview the reconstructed fight state (HP / gold / potions / relics / deck) and optionally tweak the relic and card counter state the log can't recover (e.g. Pen Nib strikes, charge counters), with Zero / Random / Primed presets.
- **Play the fight.** You drop into that single combat. On win or loss a **Completion screen** appears with **Try Again**, **Return to Dojo**, and **Return to Main Menu** — no rewards, no map, no save writes, no run progression.
- **View Deck.** Open the game's own deck viewer for any run's end-of-run deck (or a saved fight's captured deck) right from the menu.
- **Share fights.** Export a fight setup as a short clipboard code (`STS2DOJO1.…`) or save it to a library, and import codes others share with you. A shared fight is **seed-pinned**, so everyone who loads it gets a bit-for-bit identical fight — same opening hand, shuffles, and enemy intents. Saved and imported fights live on a **Saved Fights** tab.

## What it doesn't do (by design)

- **Single-player only.** Multiplayer runs are excluded.
- **Combats only.** No shops, events, or rewards.
- **No exact-original RNG for history replays.** A historical `.run` file doesn't store per-combat RNG, so replaying a past fight uses **fresh combat RNG each attempt** (arguably better for practice anyway). Bit-for-bit determinism only applies to *shared* fights, which capture live RNG at export time.
- **No persistence.** The Dojo never writes to your save. It's a throwaway sandbox.

Fight reconstruction is highly accurate but not perfect — a few fields the run log doesn't store (like relic counter state mid-run) default to fresh/zero and can be adjusted in the Replay Setup screen.

## Requirements

- **Slay the Spire 2** (game version 0.107.0 or newer).

Slay the Spire 2 has built-in C# mod support, so no separate mod loader is needed. Install STS2 Dojo like any other StS2 mod — via the in-game / Steam Workshop mod flow, or by dropping the built mod into your game's `mods/` folder — then enable it in the mod menu.

> **macOS note:** on some macOS versions the modded game can hang at the main menu on startup due to a native Sentry telemetry deadlock (an engine-level issue, not specific to this mod). STS2 Dojo ships a built-in workaround for it.

---

## Development

STS2 Dojo is a C# mod built on the standard StS2 modding toolchain — .NET, Godot/Megadot, [Harmony](https://harmony.pardeike.net/), and the [`Alchyr/ModTemplate-StS2`](https://github.com/Alchyr/ModTemplate-StS2) template. It uses the game's native mod loading and doesn't depend on BaseLib. Everything is done via Harmony patches and code-built UI; there is no `.pck` yet.

### Layout

- `STS2DojoCode/` — the mod source (initializer, main-menu entry point, the Dojo browser screen, reconstruction pipeline, replay launch, completion screen, and the fight-sharing codec).
- `STS2DojoCode/Reconstruction/` — the pure "`.run` file + floor → reconstructed loadout" logic, kept dependency-light so it can be unit-tested outside the game.
- `STS2Dojo.Tests/` — a lightweight, no-NuGet console test harness. It deliberately does **not** load `sts2.dll`; instead it compiles the production reconstruction/decision-helper source against minimal test doubles.
- `CLAUDE.md` — the detailed design reference and build log: the data-model reality, the field-by-field reconstruction contract, and the rationale behind the major decisions. Start here for architecture.

### Build & test

```sh
# Build and auto-copy the dll/pdb/json into the game's mods/STS2Dojo/ folder
dotnet build STS2Dojo.csproj

# Build to a throwaway mods dir instead of the game folder
dotnet build STS2Dojo.csproj -p:ModsPath="<some temp dir>"

# Run the unit tests
dotnet run --project STS2Dojo.Tests/STS2Dojo.Tests.csproj
```

A `Makefile` wraps these as `make deploy` / `make build` / `make test`. The build resolves the game/`sts2.dll` paths automatically (see `Sts2PathDiscovery.props` / `Directory.Build.props`); per-machine overrides go in a git-ignored `local.props`.

> Some reconstruction tests read local `.run` history fixtures that are **not** committed to this repo (they're personal game data). Those fixtures need to be present locally for the full test suite to pass.

To try changes in-game, launch Slay the Spire 2 through Steam with the mod enabled (a direct launch of the `.app` can crash in Sentry). Rebuild and relaunch to pick up a new build.
