# Account manager design exploration · 2026-09-22

The user asked to improve the current interface together, following the latest
design-partner guidance, and selected **A — ordered list** on 2026-09-22.
Its bright canvas, unboxed three-part summary and aligned account rows are now
implemented in native WinForms for 1.8.0. The approved raster is the
visual reference; the earlier HTML sketches are not the implementation source.

## Compared directions

- [A — ordered list](direction-a.png): one light canvas, a clear total and reset
  summary, aligned account/value/reset/action columns, restrained row boundaries.
  Recommended for quick comparisons, longer names and more accounts. Its visual
  character is quieter, and the large reset date could be reduced further in the
  native pass if it competes with the balance.
- [B — three cards](direction-b.png): each account becomes a distinct paper card.
  The three-account composition is stronger and more tactile. It uses more space,
  and additional accounts or long aliases need a layout rule before implementation.

Both use synthetic values: 63 + 81 + 24 = 168 percent remaining out of 300 percent,
with distinct future reset times. The latest reset is explicitly a reset time,
not an expiry deadline for all remaining capacity. No real identities or usage
were accessed for these concepts.

## Critique and comparison basis

The current [installed-design capture](../../images/soft-paper/opening-complete.png)
has a large bordered summary, repeated row borders and status phrases, and a
decorative three-shape motif detached from the task. The alternatives emphasize
typographic hierarchy and alignment instead of adding more decoration.

The Library's Soft Paper specimen supplied the warm ivory/plum palette and the
relationship of firm type to gentle paper surfaces. The Library's preserved
Microsoft WinUI settings illustration and Carbon table captures were inspected
for shared baselines, quiet default states, restrained borders and action emphasis.
Their upstream references are
[Microsoft settings layout](https://learn.microsoft.com/en-us/windows/apps/design/app-settings/guidelines-for-app-settings#layout)
and [Carbon data table](https://carbondesignsystem.com/components/data-table/usage/).
This round used the preserved reference images, not a fresh live product audit.

The v1 and refined images are retained. Refinement removed invented account-role
subtitles, reduced a competing headline, removed an unquantified pie icon, and
restored the active account management menu. These are generated raster concepts:
minor icon/text details are not new feature commitments. In particular B's small
sort chevron does not establish a selectable sorting feature.

## Artifacts and limits

- `direction-a.png`, `direction-b.png`: current comparison proposals, generated
  with built-in imagegen. [Prompt set and focused edits](prompts.md).
- `direction-a-v1.png`, `direction-b-v1.png`: first versions, kept so intermediate
  visuals remain recoverable rather than disappearing with temporary work.
- `index.html`, `styles.css`, `app.js`: independent exploratory HTML with synthetic
  normal/loading/partial states, name editor and simulated actions. It predates
  the generated refinements and is **not a pixel-equivalent implementation** of
  either final image. JavaScript syntax was checked; browser rendering and
  interactions were not verified. A local server launch was blocked by automatic
  approval review; local file navigation was blocked by browser URL policy.
  No alternate browser or security workaround was attempted.

## Native implementation

- The overview separates total remaining capacity, nearest reset and latest
  confirmed reset. A failed or incomplete batch still shows an unknown total
  and a clearly labelled confirmed subtotal. Reset times remain per-account.
- Shared account/value/reset/action columns replace individual bordered cards.
  Muted identity tiles and meter colors stay attached to accounts during sorting;
  the active account has one quiet background and a readable status badge.
- Names occupy one line with ellipsis; hover exposes the full alias. Compact
  query states expose their complete reason and last observation through hover
  and accessibility descriptions. Rename, individual refresh, details, delete
  and switching remain available through the existing controls and menus.
- Native text and vector painting reproduce the direction without rasterized
  text, new dependencies or any change to authentication/query rules. The
  original first-paint batching, in-place partial responses and native disabled
  action semantics remain in place.
- Default size remains 920 × 740 logical pixels so all three accounts also fit
  while the progress or partial-result message is present. Deliberately reducing
  the window height permits scrolling rather than shrinking controls.

The user approved the A direction, then confirmed satisfaction with its installed
preview.3 execution and requested a public release. The release also includes
Korean/English switching at the user's explicit request.

## Actual output and checks

Native production forms with synthetic accounts:
[175% manager](native/manager-175.png), [100% manager](native/manager-100.png),
[long alias](native/long-name.png), [name editor](native/rename.png),
[loading](native/progress.png), [partial failure](native/partial.png),
[minimum window](native/minimum.png). The 100% fixture uses a DPI-unaware process
to exercise the native 96-DPI layout without changing Windows display settings.
The 175% captures use the actual per-monitor DPI mode. These are WinForms
DrawToBitmap captures; nonclient title-bar rendering may differ from DWM.

The full 30-group regression suite passed during this implementation, including
four-corner tooltip stability, focus, authentication isolation, account operations
and first-paint geometry. The final row sizing refinements were then checked with
the combined-manager, opening and style fixtures at 96 and 168 DPI. Parent-bound
assertions now catch clipped child layouts as well as clipped text and buttons.
The small-window fixture resizes the outer window to its scaled native minimum;
an impossible ClientSize request had left WinForms reporting rejected bounds.

Two final full-suite attempts stopped at the initial pointer-driven hover check:
the trace recorded actual pointer departure (`cursorHeld=False`, enter/leave),
not an observed stationary-hover regression. The earlier four-corner checks
passed twice. No hover assertions were weakened, and no further cursor retries
were made. Final packaging used the same publish options after the focused UI
checks; the complete suite was not reported as a fresh pass on that final run.

Final opening inspection recorded 72 events with zero violated expectations.
The normal-user installer upgraded preview.2 to **1.8.0-preview.3** on 2026-09-22.
The installed executable matched the packaged SHA-256
`476cdfea0b10216ad92b31578f91866893e14586adc2b50263b472bbf336fe45`
(8,455,350 bytes). One supervisor and one widget remained running; the one-time
helper completed with exit code 0. The executable passed the private-path scan
and contains no PDB. This is a local preview installation, not a public release.

## Korean and English release candidate

The native interface, menus, dialogs, tooltip and known application errors use
explicit translation templates. Existing account aliases and stored authentication
remain unchanged. **Language / 언어** is in the widget and tray context menu.
An idle open manager is recreated from the same dependencies and observation set
without querying. Physical bounds are restored during Load after DPI scaling.
Busy operations, owned dialogs and a disabled modal owner block language changes.

English output with synthetic accounts is preserved at [175%](native/en-manager-175.png)
and [100%](native/en-manager-100.png), with the [name editor](native/en-rename.png),
[switch preparation](native/en-switch.png) and [tooltip renderer](native/en-tooltip.png).
Korean account names in these images demonstrate that aliases are not translated.

`LanguageUiSmoke` passed at actual 175% scaling: KO→EN→KO retained physical bounds,
the completed batch, aliases and active auth bytes, with zero usage calls. It also
tested in-flight and modal blockers. `LocalizationTests` passed resource loading,
format parity and rendering, known-message round trips, direct literal coverage,
tooltip labels and isolated settings migration/preservation. Language resources
explicitly disable culture-based satellite splitting so the single-file binary
contains both catalogs. Existing settings retain Korean; new settings use the
Windows language, including the first position/visibility save.

A fresh local full build stopped at its first real-pointer hover fixture because
the pointer left the widget (`cursorHeld=False`). The full suite was not claimed
as passed and was not retried locally. The required full-suite result and final
artifact verification are recorded in the v1.8.0 PR, Windows CI and release.

Claude implementation advice identified the open-window DPI restoration risk and
catalog validation gap; both received specific fixes and native/runtime checks.
The separate frozen-candidate reviewer recommended ready with no remaining code
blockers. Its clean-CI test-root finding was fixed and the localization test passed
with that environment variable absent. Passing full Windows CI and downloaded
artifact checks remain required before publishing. The font's OFL notice is
included in release packaging. The README now starts with native widget and
manager images, a simple usage path, a concrete 63 + 81 + 24 example and hover
details; implementation and recovery details remain available in expandable sections.
