#pragma once
#include <windows.h>
#include <string>
#include <utility>

namespace vnotch::platform {
// Pollable invalidation, with reconciliation for dropped notifications and
// filesystems that do not support them. Owns a notification handle, not a file.
class DirectoryChangeStamp final {
    HANDLE handle_ = INVALID_HANDLE_VALUE;
    ULONGLONG reconciled_ = 0;
public:
    DirectoryChangeStamp() = default;
    DirectoryChangeStamp(const DirectoryChangeStamp&) = delete;
    DirectoryChangeStamp& operator=(const DirectoryChangeStamp&) = delete;
    DirectoryChangeStamp(DirectoryChangeStamp&& other) noexcept
        : handle_(std::exchange(other.handle_, INVALID_HANDLE_VALUE)), reconciled_(other.reconciled_) {}
    DirectoryChangeStamp& operator=(DirectoryChangeStamp&& other) noexcept {
        if (this != &other) {
            if (handle_ != INVALID_HANDLE_VALUE) FindCloseChangeNotification(handle_);
            handle_ = std::exchange(other.handle_, INVALID_HANDLE_VALUE);
            reconciled_ = other.reconciled_;
        }
        return *this;
    }
    ~DirectoryChangeStamp() { if (handle_ != INVALID_HANDLE_VALUE) FindCloseChangeNotification(handle_); }

    bool Changed(const std::wstring& folder, DWORD filter, bool recursive,
                 ULONGLONG reconciliationMs = 5000) noexcept {
        const auto now = GetTickCount64();
        bool changed = !reconciled_ || now - reconciled_ >=
            (handle_ == INVALID_HANDLE_VALUE ? 1000 : reconciliationMs);
        if (handle_ == INVALID_HANDLE_VALUE) {
            if (!changed) return false;
            handle_ = FindFirstChangeNotificationW(folder.c_str(), recursive, filter);
            changed = true;
        } else {
            const DWORD result = WaitForSingleObject(handle_, 0);
            if (result == WAIT_OBJECT_0) {
                changed = true;
                if (!FindNextChangeNotification(handle_)) {
                    FindCloseChangeNotification(handle_);
                    handle_ = INVALID_HANDLE_VALUE;
                }
            } else if (result == WAIT_FAILED) {
                FindCloseChangeNotification(handle_);
                handle_ = INVALID_HANDLE_VALUE;
                changed = true;
            }
        }
        if (changed) reconciled_ = now;
        return changed;
    }
};
}
