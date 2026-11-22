# Account manager · 2026-11-21

The user asked for a more refined account manager without changing what it does
("계정 관리 패널을 완전히 새롭게 디자인하고 싶어. 기능까지 싹 다 바꾸고 싶은 건
아닌데, 지금의 시각화 방식을 좀 더 세련되게 바꾸고 싶고"), together with three
changes: queries only when the user asks, an Antigravity tab, and a widget size
set as a number. Two designers worked from the same brief without seeing each
other's work, and the two proposals were compared screen by screen (A–G) on a
comparison board. The agent recommended the Antigravity proposal in its second revision; the user
chose it and asked for it to be installed. It is the decided design, and it
builds on the 2026-09-22 ordered list rather than replacing it.

## What changed

- **One top bar.** The title, the Codex · Claude · Antigravity tabs with their
  account counts, the page's last check time and its full query share one
  38px line, so the account list starts higher.
- **A two-part summary.** The weekly total and the next reset, with the latest
  reset as a line under it. The figures are smaller (26px and 21px), so the
  summary no longer presses on the list. Every value the rows show adds up
  until its week resets, an old one included, and the next reset is the
  soonest of them; an account without a value, or whose last read failed, is
  left out and the note says how many count ("5개 중 3개 확인"). 1.10.0 added
  up only values read since the widget started, so after a restart the
  window opened on a dash and "확인 필요", the large dash the design set out
  to remove.
- **Rows to choose from.** Each row's remaining weekly share is 21px bold over
  an 8px bar, beside the time until it refills and its date. A row without a
  value shows a dash, not an empty bar.
- **Coloured labels only for failures.** "다시 로그인 필요" and "조회 실패" keep a
  red ground; stale values, waiting reads and progress are plain text.
- **A ↻ on every row and a ⋯ menu card.** The menu is a white card with a 1px
  border in the control-border colour; on Windows 11 it takes the system's
  small rounded corners and shadow, as other menus do.
- **The main Claude account** (the one that shares its sign-in folder with the
  app and terminal) gets a one-line amber notice with **따로 로그인**.
- **The Antigravity page.** The account Antigravity is signed in to stands in a
  pale teal band with four values (Gemini and Claude·GPT, weekly and five
  hours); accounts read before are listed under it.
- **The widget size window.** − and + either side of the number, a slider with
  its 100% mark and the widget's size in pixels.

Removed: the weekly pace marker. Both proposals drew where usage would stand if
spread evenly over the week; the user asked "\"고르게 썼다면 지금쯤\" 이건 도대체
뭐야?", and it was taken out with its legend and tooltip. The user moves to the
next account once one runs out, so what remains and when it refills already say
what is needed.

## Queries only when asked

Opening the window or changing tabs reads nothing. Each row shows its last value
with the time it was read, a value kept from an earlier run like any other of
its age (old after six hours or past its reset); a row's ↻ reads that account, and the top bar's full
query reads the page: on the Claude page every signed-in account, three at a
time; on the Codex page one batch; on the Antigravity page the account signed
in now.

## Where the app departs from the design

- The size window runs from 50% to 200%, the widget's own range (the design
  drew 65–150%). It opens without the number selected; the first click into the
  box, or the first digit typed, takes the whole number so a new one replaces it.
- While the Claude page is queried, up to three accounts read together, the
  reads' limit; screen B shows one of them waiting.
- **종료하면 다시 열기** shows only when there are sessions to fill.
- The menu's corners are the system's small radius on Windows 11 (the design
  drew 10px) and square on Windows 10, where the border alone makes the card.
- Sign in again shows an arrow into a bracket and sign out an arrow out of it,
  the usual way round (the design had them swapped).
- At the narrowest width the top bar drops the check time when it does not fit;
  in English the list heading reads "Accounts".
- The title bar is the system's. Where Windows shows an accent colour or dark
  mode on title bars it is dark, as on the maintainer's PC, while the design
  drew a light one; the window follows the setting the user chose for every
  window.
- When no account has a value to count, the next reset reads "확인 필요" with
  the soonest earlier schedule, if one is known; the design has no such screen.

## Tokens

`AccountUiTheme` holds the design's tokens: canvas `#FAF9F6`, surface
`#FFFFFF`, subtle `#F2F0EB`, rules `#E6E2DA` and `#F0EEE8`, control border
`#DBD7CD`, ink `#191816` `#4A4741` `#757068` `#9E9990`, action `#232220`.
Providers: Codex blue `#4C6FE0`, Claude terracotta `#C8603E`, Antigravity teal
`#1D8A78`, each with a darker ink and two pale grounds. Type is the bundled
Pretendard in Regular, SemiBold and Bold.

## Validation

`--account-style-preview` opens the window with made-up accounts on a hidden
desktop at 200%, in Korean and with `--english`, and saves each screen when
`GFS_ACCOUNT_UI_CAPTURE` names a file in the folder for the pictures. It reports
controls cut by their parent, shortened or squeezed text and text outside
Pretendard. The captures were set beside the design's screens and checked by
eye. A picture the window draws of itself cannot show the system's corners and
shadow; the preview checks that Windows took them.

Each screen is saved twice: as the window draws itself (`DrawToBitmap`, which
paints every child over its parent's pixels) and as Windows composed it from
each control's own paint (`-screen`, `PrintWindow` with
`PW_RENDERFULLCONTENT`), the picture a person sees. 1.10.0, installed on the
maintainer's PC and never published, had tabs and buttons that left their
see-through parts unpainted: WinForms treats a
Button as opaque and paints nothing under it, so on the screen those parts
showed whatever the shared paint buffer held last (pieces of the check time,
**Check all** and **Windows · WSL connections**), while the self-drawn
pictures looked right. Every screen now also paints each double-buffered
control alone twice, over two colours, and fails if any pixel differs. The
preview includes the Codex page as the maintainer's window showed it when the
defect was reported (`C2-codex-old`): values kept from before the widget
restarted, hours old, which the total adds up.

`--account-window-tests` (part of the full suite) checks what the pictures
cannot: opening reads nothing, a row's ↻ reads that account alone, a full query
reads three accounts at most and canceling stops them, an Antigravity read with
no account signed in says so and keeps earlier values, and closing the window
during a query waits for the reads to stop.
