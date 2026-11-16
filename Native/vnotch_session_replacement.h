#pragma once
#include <utility>

namespace vnotch {
// Caller serializes access. Failed initialization leaves an empty session so a later
// request can retry, without retaining the failed GPU allocation beside the CPU model.
template <typename Session, typename Factory>
void ReplaceSession(Session& session, Factory&& factory) {
    session = Session{nullptr};
    session = std::forward<Factory>(factory)();
}
}
