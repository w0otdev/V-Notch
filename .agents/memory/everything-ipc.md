---
name: Everything IPC protocol
description: Layout facts for the voidtools Everything WM_COPYDATA v1 query protocol used by Spotlight file search
---

Facts that were easy to get wrong (verified against everything_ipc.h):

- Query window class: `EVERYTHING_TASKBAR_NOTIFICATION`; send `WM_COPYDATA` with `dwData=2` (COPYDATAQUERYW).
- `EVERYTHING_IPC_QUERYW` header is 20 bytes: reply_hwnd (DWORD — 32 bits even on x64), reply_copydata_message, search_flags, offset, max_results; then the null-terminated UTF-16 search string.
- `EVERYTHING_IPC_LISTW` header is **28 bytes** (7 DWORDs): totfolders, totfiles, totitems, numfolders, numfiles, **numitems (offset 20)**, offset. Items follow at offset 28, 12 bytes each: flags, filename_offset, path_offset — string offsets are relative to the list start. Folder flag = 0x1, drive = 0x2.
- The reply arrives as WM_COPYDATA on the window named in reply_hwnd with `dwData == reply_copydata_message`; the buffer is only valid during the message, so parse synchronously in the hook.

**Why:** a first implementation read numitems at offset 4 (a 12-byte header assumption) — plausible-looking but wrong; results were silently truncated/dropped. A unit test in `Tests/EverythingSearchProviderTests.cs` locks the layout.

**How to apply:** any change to Everything IPC parsing must keep that test green; extend it rather than trusting memory of the struct layout.
