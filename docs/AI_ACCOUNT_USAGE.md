# AI Subcriptions

V-Notch can show the remaining allowance and reset times of your Codex and Claude
Code subscriptions in the expanded notch. This feature only monitors limits. It
does not send prompts, create AI sessions, or add another chat interface.

Open **Settings → AI Subcriptions** and enable **Track my subscriptions**.
Monitoring is off by default. The section has three tabs:

- **Overview** shows current account limits and the refresh action.
- **Appearance** provides a sample preview and saved controls for visible providers,
  order, density, remaining/used percentages, weekly limits, resets, logos, account
  details, colors, bar thickness, and motion strength. Detail, color, and motion
  controls are collapsed until needed. Replay previews without making requests.
- **Connections** groups Codex and Claude setup separately, with manual executable
  selection and disconnecting under **Advanced setup**.

Appearance changes do not restart quota polling. **Reset appearance** keeps account
connections and monitoring preferences. Apply saves edits; Cancel restores the
previous appearance through the regular settings preview flow. You can also
swipe through the expanded widgets to reach it. Click the widget to view the limits
and reset times, or focus it and press Enter or Space. With monitoring off, this
opens setup instead. Each provider keeps its weekly allowance directly below its
bar, and a missing Claude sample shows the next connection step.

## Codex

Install the official [Codex CLI](https://developers.openai.com/codex/cli) and sign
in to it with your ChatGPT account. Enable **Use signed-in Codex account** in
V-Notch. The account and plan used by Codex appear in the quota tooltip when
available. If V-Notch cannot find Codex, select its `codex.exe` with **Locate
Codex…**. Native installations and the standard npm CLI shim are supported.
The Windows Codex desktop app's bundled CLI is also detected automatically. Use
**Codex · Detect automatically** to clear a manually selected path.
Only choose an executable you trust. Paths and polling consent are never imported
from another person's settings backup.

V-Notch calls the official app-server `account/read` and
`account/rateLimits/read` methods. It does not read or copy `auth.json`, extract
cookies, perform inference, or change your Codex login. Sign in or switch accounts
in Codex itself. Automatic polling runs once every two minutes. **Refresh limits**
can request fresh data sooner, with a ten-second cooldown between reads.
Local-only mode or disabling online AI access cancels the running
read-only child process and prevents further online polling.

Only Codex subscription limits are shown. They are not a universal ChatGPT limit,
an API-key rate limit, or a count of tokens used in V-Notch. Some accounts or CLI
versions do not provide subscription limits. V-Notch reports these states without
generating requests to estimate an allowance.

See the [official app-server account and rate-limit protocol](https://developers.openai.com/codex/app-server).
Sign in with ChatGPT's open-source inference flow is a different capability; it
is not needed to monitor the account already connected to Codex.

## Claude Code

Claude Code exposes subscription limits in its
[documented status-line input](https://code.claude.com/docs/en/statusline). V-Notch
uses that data rather than reading Claude credentials or calling private account
endpoints.

The active Claude Code session must use your Claude subscription. An API key,
`apiKeyHelper`, or custom API endpoint can override a saved Claude login and does
not supply that subscription's limits. Check `claude auth status` (or `/status`
in the session) to verify which login is active. V-Notch reports this configuration
even before the bridge is connected; connecting the bridge does not change login
or billing settings.

1. Enable **Read Claude Code limits** and click **Connect Claude Code**.
2. Restart Claude Code and use it normally. A compatible release reports
   `rate_limits.five_hour` and `rate_limits.seven_day` after activity. No extra
   prompt is sent by V-Notch. If your release or account omits these fields, the
   widget remains in its waiting state; update Claude Code or view its `/usage`
   screen or the official usage dashboard.
3. The bridge also displays a small remaining-allowance summary in Claude's status
   line. Claude Code may hide some default footer hints when a custom status line
   is configured.

Setup adds a status-line command to your user-level Claude settings only when one
does not exist. Other settings are retained, and the original file is backed up
beside it. `CLAUDE_CONFIG_DIR` is respected. Configuration can be overridden by
Claude's project or managed settings; V-Notch does not modify those files.

If you already have a custom status line, V-Notch keeps it. Click **Connect Claude
Code** to prepare the bridge script, then feed the same JSON your script receives
to `%LOCALAPPDATA%\V-Notch\ai-usage\claude-statusline.ps1`. Suppress the bridge's
stdout if you want to keep only your own status display. For example, in a
PowerShell status-line script, capture stdin **once**, forward it, and continue
using the same input for your own display:

```powershell
$raw = [Console]::In.ReadToEnd()
$bridge = Join-Path $env:LOCALAPPDATA 'V-Notch\ai-usage\claude-statusline.ps1'
$raw | & powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $bridge | Out-Null
$session = $raw | ConvertFrom-Json
# Render your existing status line from $session.
```

**Remove Claude bridge** removes only the V-Notch-owned status-line configuration.
It never deletes a replacement custom status line. If you integrated it into your
own script, remove that call yourself. Changes to Claude configuration happen
when you click the connection buttons, independently of V-Notch's Apply/Cancel
settings controls. Disabling monitoring stops V-Notch reads; removing the bridge
also stops its collection in Claude Code after restart.

The bridge stores only percentages, reset timestamps, and observation time in
`%LOCALAPPDATA%\V-Notch\ai-usage\claude.json`. It does not save credentials,
session IDs, prompts, transcript paths, or token estimates. The widget describes
the latest reporting Claude Code session; the status-line contract does not
identify an account. If you use different accounts simultaneously, check the
official usage page for the account you want to inspect.

## Reading the display

Widgets and cards show the **remaining** percentage of each provider-reported
window. Bars shrink from 100% to 0% as the allowance is used, with green fills
while plenty remains and orange/red fills near the limit. For example, 11% used
appears as 89% remaining. Hover over either
provider in the compact notch widget to see its full usage card. Hover
for reset times, the update time, and additional Codex buckets. Reset times use
your Windows locale and time zone. A missing window is unavailable, not 100%
remaining. Samples older than ten minutes are dimmed and marked as last reported.
After a reset time passes, the display waits for a new provider report rather than
assuming the account has replenished its allowance.

Bars smoothly drain to fresh reported values. Opening the limits view reveals
the account panes in sequence, and refresh controls animate only while checking.
These effects follow the app's Reduce Motion setting and Windows animation
preferences, and stop when the view is hidden. Motion strength can be set to zero
to turn off widget effects. Settings tabs, disclosure headers, and control presses
share the same motion preferences.

The widget, settings controls, dropdown choices, and tooltips use the bundled
SF Pro Display Bold face. Its font weight metadata is 700; the original glyph
outlines are unchanged. Compact density reduces label sizes and spacing, while
the configured bar thickness remains explicit.

Claude values refresh locally every 30 seconds, without network access. They
change when Claude Code reports fresh data and may stay stale while it is idle.
The **Manage usage** links open the providers' own pages, which remain the source
of truth for billing and account restrictions. No usage history is uploaded or
aggregated by V-Notch.
