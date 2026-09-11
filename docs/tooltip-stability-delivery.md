# Stable hover details — v1.7.1

The larger v1.7.0 tooltip could be placed over the mouse pointer near the screen edge. This caused repeated widget leave/enter events and tooltip dismissal/reopening. The original UI smoke used an explicitly shown tooltip on an active text box; it did not exercise the widget's actual hover path.

The fix retains the light design and multi-account text, and replaces automatic native tooltip placement with a borderless, nonactivating hover window. It opens after 350 ms outside the widget, ignores mouse input, and retains its content and position for the current hover. Incoming observations are used on the next hover. Leaving, pressing a mouse button, moving or hiding the widget dismisses it. Existing widget topmost maintenance continues using `SWP_NOACTIVATE`.

No account switching, credential storage, query cadence, or aggregate observation rules change.

## Validation

- The actual hover regression reproduced the original pointer overlap before the fix.
- The replacement test holds the pointer at all four screen corners for four seconds each while repeatedly maintaining widget topmost state and updating pending tooltip text. It checks native window bounds, one continuous display, screen fit, pointer/widget separation, and foreground/input focus preservation.
- Context-menu opening dismisses the popup, and hiding the widget cancels a pending hover.
- The full build retains account lifecycle, isolated query/recovery, aggregate observation, and existing widget focus coverage.

The hover test uses only synthetic usage. It briefly moves the cursor and restores it when finished. No real account screenshots or credentials belong in this repository.
