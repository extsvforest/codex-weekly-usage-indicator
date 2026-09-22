# Soft paper account interface

The current release candidate is **1.8.0**, implementing the user's
[selected A ordered-list design](design/2026-09-22/README.md). The description
below records the earlier preview.2 treatment and its validation history; its
two-column overview, paper borders and decorative motif have been superseded.

The existing 920 × 740 logical-pixel manager, two-column overview, account rows,
actions, dialogs and manual query rules retain their structure. This is the local
1.8.0-preview.2 styling candidate, based on the user's selected “부드러운 종이”
style: firm Korean typography against warm, thin paper surfaces.

- Pretendard 1.3.9 Regular, SemiBold and Bold are embedded unchanged from the
  upstream static TrueType distribution. Fonts are registered privately for this
  process; installation and network access are not required at runtime.
- Ivory #f6f3ed, charcoal #292d32 and plum #402e38 anchor the interface. Apricot,
  pale blue and butter yellow form the small header motif. The active row uses a
  pale blue sheet and side marker; actions retain text and native button semantics.
- Paper is interpreted as clean color planes, thin borders, shallow shadows and a
  turned corner. Small native UI surfaces omit bitmap grain and sculptural imagery;
  neither text nor data is baked into an image.
- Buttons have consistent hover, pressed, disabled and keyboard-focus treatments.
  The name field keeps a native TextBox inside a padded frame. Menus and hover
  details use the same paper palette and typography.
- Tooltip placement, mouse transparency, nonactivation and hover lifecycle are
  unchanged. The widget's dimensions, provider colors and alias-free display stay
  unchanged. No account authentication or usage query implementation changed.

Font source: https://github.com/orioncactus/pretendard/tree/v1.3.9/packages/pretendard/dist/public/static/alternative
See `THIRD_PARTY_NOTICES.txt` for the full SIL Open Font License and copyright.

## Visual review

Native captures use synthetic values: [manager](images/soft-paper/manager.png),
[before](images/soft-paper/before.png), [name editor](images/soft-paper/rename.png),
[partial results](images/soft-paper/partial.png),
[long name](images/soft-paper/long-name.png), [tooltip](images/soft-paper/tooltip.png).

`AccountStylePreview` opens production WinForms with synthetic accounts. Set
`GFS_ACCOUNT_TEST_ROOT` to a task workspace and `GFS_ACCOUNT_UI_CAPTURE` to a PNG
path, then run the test project with `--account-style-preview`. It captures the
manager, a long mixed Korean/English name, and the name editor without real queries.
The ordinary regression suite covers registration, rename, cancellation, aggregate
coverage, keyboard focus, three-account scrolling and actual tooltip hover.

The target is Windows at 175% DPI. Native captures, partial-result and progress
states are reviewed alongside the selected style specimen. Aesthetic acceptance
belongs to the user's next feedback; successful tests are not a taste verdict.

## Local validation

The initial preview.1 `scripts/build.ps1` run passed all 29 regression groups. Actual hover
survived all four corners with no post-show hiding and with keyboard focus
preserved. This short cursor-driven check now runs first, before the slower
fixtures, to avoid surprising someone using the mouse later in a local run.
The final executable passed the private-path scan and was installed through the
normal per-user scheduled-task installer. The installed file matched SHA-256
`d39113101c88a4dcad3e9caa324690011678c8cabf685bebd3f8b3e3be8c6757`;
one supervisor and one widget were running after startup. No public release was
created for this preview.

On 2026-09-15, preview.2 corrected opening-time layout churn exposed by the paper
restyle. The full build passed all 30 regression groups, including the new native
first-paint/partial-response geometry test, disabled account-action checks, real
tooltip hover and keyboard focus. See [the cause and fix](account-opening-stability.md).
The final executable passed the private-path scan. The normal per-user installer
upgraded preview.1 to preview.2 and verified the installed file against SHA-256
`b4eaf2410672a4d6fbaa48784954ece1b014d43c0ee95b2489693c1acd2f2f35`
(8,452,278 bytes). One supervisor and one widget remained running after startup.
This is a local preview installation; no public release was created. User review
of the installed opening behavior remains separate from the automated result.
