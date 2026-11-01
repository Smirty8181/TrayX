# UI automation tests

The WPF Control Center and Core are implemented, but this directory currently contains no automated UI tests and is not included in `TrayX.sln`.
The existing unit and integration tests do not verify interaction with a live desktop session.

Before adding UI automation, choose and document a runner that supports the target Windows version and an interactive desktop session. Keep these checks separate from the default solution test run if they require an installed application, desktop access, or additional tooling.

Recommended coverage:

- launch and clean shutdown of the Control Center and Core;
- navigation and settings persistence;
- theme changes and tray-icon actions;
- behavior across supported DPI scales and display configurations.

Until that runner exists, validate these scenarios manually using [the manual test checklist](../../docs/MANUAL_TESTS.md).
