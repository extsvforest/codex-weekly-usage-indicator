# Account manager opening stability

The 1.8.0-preview.2 candidate fixes the visible layout churn reported after the
Soft Paper restyle. It keeps the existing manager structure, fonts, palette,
account actions and manual query boundaries.

## Reproduction and cause

The production form was tested at 175% Windows scaling with three synthetic
accounts, both with saved observations and without them. Geometry was observed
from the first native `Paint`, not just a settled `DrawToBitmap` capture.

Before the fix, the first paint contained no account rows. `Shown` then populated
rows at a temporary width of 262 pixels before expanding them to 1,526 pixels,
and opened the query notice below the overview. With no cached reset times,
individual responses also changed the row order while the batch was running.

The new transparent paper surfaces exposed these intermediate states. A native
paint stack confirmed that disabling the whole account table recursively called
`Control.OnEnabledChanged`, which forces `UpdateWindow`. Suspending layout alone
did not stop that synchronous paint; moving preparation to `Load` alone was also
insufficient. The relevant framework behavior is in the official
[WinForms Control source](https://github.com/dotnet/winforms/blob/v8.0.0/src/System.Windows.Forms/src/System/Windows/Forms/Control.cs).

## Fix

- Prepare the initial account list, DPI-adjusted size and query state in `Load`
  before presenting the window. The query continues asynchronously; display and
  cancellation do not wait for a network response.
- Batch layout changes through the nested tables. Compose the manager's child
  windows together so transparent controls do not expose separate paper layers.
- Disable the account action buttons individually instead of disabling all
  labels and containers. Custom buttons keep native enabled/accessibility
  semantics but enqueue repainting instead of forcing an immediate update.
- Preserve row positions throughout a batch. When it finishes, apply the final
  nearest-reset order once. Value changes do not rebuild an unchanged row grid.

The existing query notice still closes on success, so the list moves up once
when that notice disappears. Errors, cancellation and recovery retain their
existing visible messages. No authentication or account query implementation
was changed, and the widget/tooltip retain their separate nonactivation behavior.

## Verification

`AccountOpeningSmoke` exercises actual production forms with held synthetic
responses. It checks all three rows and final widths from the first native paint,
stable geometry in painted loading frames and between individual responses,
final reset ordering, scrolling, quiet local refresh, hide/show and unchanged
active credentials. Run it with `--account-opening-smoke`; `--trace-only` is a
diagnostic mode that records failures without substituting for the required test.

The in-flight query test verifies every row's switch/menu button and the
add/rename/delete actions are disabled, and tries the disabled row buttons. It
does not equate a disabled container with protected account actions.

Preserved synthetic captures: [loading](images/soft-paper/opening-loading.png),
[completed](images/soft-paper/opening-complete.png). These document appearance;
the native paint/geometry regression is the evidence for opening stability.

Full build and local installation verification are recorded in
[the Soft Paper UI record](soft-paper-ui.md).
