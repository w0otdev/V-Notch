#include "../../Native/vnotch_session_replacement.h"
#include <cassert>
#include <memory>
#include <stdexcept>

struct Session {
    static inline int live = 0;
    Session() { ++live; }
    ~Session() { --live; }
};

int main() {
    auto session = std::make_unique<Session>();
    vnotch::ReplaceSession(session, [] {
        assert(Session::live == 0); // CPU allocation must not overlap the previous GPU session.
        return std::make_unique<Session>();
    });
    assert(session && Session::live == 1);
    try {
        vnotch::ReplaceSession(session, []() -> std::unique_ptr<Session> {
            assert(Session::live == 0);
            throw std::runtime_error("CPU allocation failure");
        });
        assert(false);
    } catch (const std::runtime_error&) { }
    assert(!session && Session::live == 0);
    vnotch::ReplaceSession(session, [] { return std::make_unique<Session>(); });
    assert(session && Session::live == 1);
    session.reset();
    assert(Session::live == 0);
}
