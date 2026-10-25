# MultiJanela 2.2.2 — login and arrangement

Local build, 2026-10-23. Not uploaded to GitHub in this task.

## Login

- Keep the visible login/challenge page intact while checking account identity in a separate WebView on the same account profile. Failed/expired/slow identity checks no longer restart the visible flow.
- Preserve popup/opener semantics with an owned, resizable child WebView on the same environment. The main credential page remains restricted to HTTPS Roblox domains. Verification children additionally allow the two providers documented by Roblox: arkoselabs.com and funcaptcha.com. No CAPTCHA automation, fingerprint spoofing, cookie export or credential capture was added.
- Reuse an unfinished Add account draft while the manager remains open, avoiding a new browser-profile directory on every cancelled retry.
- Add manual reload without clearing cookies, a regular-browser comparison action, and a bounded local diagnostic containing only runtime version, known provider domains and error/status codes. No request paths, query parameters, headers, bodies, cookies or tokens are logged.
- A regular-browser session remains separate; the comparison action does not import it.

## Arrangement

- Balance clients across rows and fill every row, including incomplete final rows.
- Score landscape viewport proportions and allow spacing to shrink when measured client minimums require it.
- Measure DWM visible bounds and compensate invisible resize handles, including negative monitor coordinates.
- Restore, place, settle and verify in a serialized background operation, with repeated UI requests coalesced.
- Report unavoidable overlap when a client will not fit at the requested size. A minimum imposed by the client cannot be overridden by the layout arithmetic.

## Validation

- 36 functional/native cases passed, including five live fixture windows checked against target visible geometry and eight minimum-size fixtures checked for screen containment.
- 10 offline WebView2 integration checks passed: missing identity rejection, page/JS-state preservation, real popup opener, popup close and valid identity completion. HTTP responses were intercepted in a fresh fixture profile. No real Roblox account was signed in or challenged.
- 74 English and 119 Portuguese UI checks passed, including compact login/help actions at 780x580 and the existing account/queue/settings regressions.
- Portable self-contained Windows x64 executable generated and versioned 2.2.2.

## Limits and follow-up

The screenshot's unknown-error string does not establish an HTTP status or the cause of account restrictions. These changes correct browser flow/layout defects; they do not prove Roblox will accept a restricted account. Use the new local diagnostics and a manual comparison in a current regular browser to distinguish application-specific failures from account/network/service failures. Do not repeatedly retry failed verification.

Roblox guidance: https://en.help.roblox.com/hc/en-us/articles/203312820-Issues-Logging-In-or-Out-in-a-Browser
Microsoft popup contract: https://learn.microsoft.com/en-us/microsoft-edge/webview2/reference/win32/icorewebview2newwindowrequestedeventargs
Microsoft visible frame contract: https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getwindowrect
