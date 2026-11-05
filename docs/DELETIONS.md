# Deletion detection and recycling — v0.3.0

Deletion tracking is an overlay on a scan snapshot. It does not rescan or
recalculate file totals in the background. A watcher starts before scanning,
buffers changes while results are assembled, and then resolves only changed
paths against a compact parent-index/leaf-name lookup. Notifications are bounded
and coalesced; UI changes are published at most ten times per second.

Only a confirmed Windows file/path-not-found result marks a path. Access denied,
I/O errors, and unavailable media do not. Removing a folder also marks its
scanned descendants. A renamed or moved item can have the same appearance as a
deleted item because its original scanned path is gone. Highlighting is
historical: a recreated or restored path stays marked until Rescan.

Individual deleted items are filled red. Their containing folder group does not
receive a red border. Collapsed groups and “More files” aggregates fill only the
proportion of their measured scan-time bytes belonging to deleted items. There
is no minimum red stripe size; a tiny deletion remains tiny, including in a very
thin tile. Zoom into a collapsed folder to inspect individual entries. Unknown
allocation and zero-byte items do not create red allocated area, and separately
drawn siblings do not contribute to an aggregate's red portion. The synthetic
“More files” tile cannot be used as a recycling target. Select a concrete table
item instead.

File and folder sizes, allocated-space attribution, file types, and CSV output
remain the original observation. Moving data into the Recycle Bin does not free
its allocation. Use Rescan to observe a later filesystem state.

## Move to Recycle Bin

The user must select and confirm one actual path. Folder confirmation explicitly
includes all current contents. The command is unavailable during a scan, export,
update, or another recycling operation.

The unprivileged app uses Windows `IFileOperation` on an STA thread with recycling
and undo flags. Its progress sink refuses a non-recycling delete. Windows errors
and cancellation are reported, and the command contains no permanent-delete
fallback. The read-only elevated scan worker exposes no deletion request.

The service refuses scan/drive roots, paths outside the scan, storage-management
folders, non-fixed/remote locations, reparse/offline/cloud paths, and unknown or
changed file identities. Ancestor metadata handles prevent parent replacement
while the operation runs; the selected identity is checked before the Shell
operation and again immediately before deletion. A changing filesystem can still
cause an operation to fail; Rescan before trying again.

Detection, not a successful command message, decides when a segment becomes red.
This also catches deletions performed in Explorer or another application. A
notification overflow or watcher failure leaves existing confirmed highlights
intact and displays an incomplete-tracking message.

## Validation

The v0.3.0 Release build passed 45 tests with no failures or skips on Windows
x64. Dark/light WPF rendering checks confirmed red segments retain their exact
geometry and grouped remainder indicators do not include unrelated siblings.

The verification runner includes real watcher deletion, folder deletion,
rename/move, scan-attachment buffering, duplicate and path-boundary cases,
inaccessible metadata, storage loss, queue overflow, cancellation, disposal, and
100,000-entry subtree tests. Tests also verify historical sizes remain unchanged.

Recycle Bin tests operate only on fresh files and folders created under `work/`.
They check actual Windows recycling, stale file and parent identities, junction
replacement, locked-file failure, and cancellation. They do not empty the Recycle
Bin or operate on existing user files. Network provider behavior, cloud providers,
and enterprise Shell policies require separate environment-specific testing.

Windows API references: [operation flags](https://learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/nf-shobjidl_core-ifileoperation-setoperationflags),
[pre-delete cancellation](https://learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/nf-shobjidl_core-ifileoperationprogresssink-predeleteitem),
and [aborted operations](https://learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/nf-shobjidl_core-ifileoperation-getanyoperationsaborted).
