# 05 — Multi-session lifecycle

**What to build:** the end-to-end behaviour this ticket makes work, from the user's perspective — not a layer-by-layer implementation list.

You can run multiple named sessions concurrently, each backed by its own `pi --mode rpc` child, and attach a browser tab to whichever session you want. Sessions are created only when you initialize them — never implicitly on connect. Three explicit lifecycle actions exist: **init** (new/resume), **recycle** (kill the child but preserve the history file so work isn't lost), and **delete** (remove the session permanently). Running a long agent in one session doesn't block another.

**Blocked by:** 01 — Tracer bullet: live session in browser

**Status:** done

- [ ] Initialize creates a named session with its own pi child
- [ ] Multiple named sessions run concurrently; a tab attaches to one session's stream
- [ ] Recycle stops the child but leaves the session resumable/history intact
- [ ] Delete removes the session permanently
- [ ] A slow session doesn't block interaction with another
