# Logs and Crash Reports

LifeViz writes a session log to `%APPDATA%\lifeviz\logs\lifeviz.log`. It is buffered on its own thread (flushed at least every 500 ms and capped at 8 MiB per session), so logging never waits on the UI thread and messages still reach disk while the window is frozen.

## Previous sessions

Each launch of the desktop app moves the previous log to `lifeviz.1.log`, shifting older ones to `lifeviz.2.log` and `lifeviz.3.log`. Earlier versions truncated `lifeviz.log` on every launch, which destroyed the only evidence of a crash or forced close. Smoke and diagnostic runs log to `lifeviz-test.log` instead, so running tests never displaces a real session's log. Background bake workers keep their own `worker.log` in the job directory.

## Unexpected exits

A running session records itself as `session-<process id>.running` in the logs folder and removes that marker on a clean shutdown. If the next launch finds a marker whose process is gone (a crash, a frozen window closed from Task Manager or the Windows "not responding" dialog, or power loss), it:

- copies that session's log to `unexpected-exit-<start time>.log` (the newest five are kept, independent of rotation);
- shows a **LifeViz closed unexpectedly** message with the version, start time, the last UI-freeze warning, the last MilkDrop preset that was loading, and the saved log path.

Markers from other LifeViz windows that are still running are left alone; a reused process ID with a different start time is treated as a finished session.

## Freezes

A watchdog thread checks every 500 ms that the UI thread is still processing input-priority work. After 4 s without a response it logs `UI thread has not processed input for N s (LifeViz appears frozen).`, and `UI thread is responding again after a N s stall.` when it recovers. These lines identify freezes that Windows itself may never record (for example when the process is ended from Task Manager).

Unhandled exceptions on any thread are logged, and the log is flushed before a terminating failure ends the process. A WPF render-thread failure additionally keeps `render-failure-last.log` and offers a restart.

## What to send when reporting a problem

The `unexpected-exit-*.log` named in the message, or `lifeviz.1.log` right after a restart, plus the time it happened. For MilkDrop problems, note which presets were in the playlist; preset loads are logged as `projectM loading preset '...'`.

## Validation

`dotnet bin/Release/net9.0-windows/lifeviz.dll --smoke-test session-health` runs in an isolated temp folder. It fakes a crashed session (a marker for an exited process plus a log with a preset load and a freeze), checks the report and preserved log, verifies live and reused-PID markers, clean shutdown, three-deep log rotation, and freezes a real WPF dispatcher for 5.5 s to confirm the watchdog reports exactly one stall and one recovery.
