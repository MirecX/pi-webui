import { el, Signal } from "./reactive.js";

/**
 * RPC event shapes (subset used by the live transcript). Values mirror the pi RPC
 * protocol JSONL events relayed verbatim from the server via WebSocket.
 */
interface RpcEvent {
  type: string;
  [key: string]: unknown;
}

interface MessageUpdateEvent extends RpcEvent {
  assistantMessageEvent?: { type: string; delta?: string };
}

/** Server-side lifecycle frame pushed by the WS bridge (tickets #05/#06). */
interface SessionInfo {
  name: string;
  status: "running" | "recycled" | "stored" | "deleted";
  title?: string | null;
}

/** A user message available for forking (rpc.md get_fork_messages). */
interface ForkMessage {
  entryId: string;
  text: string;
}

/** A model as reported by get_available_models / set_model (subset of the pi Model). */
interface ModelInfo {
  id: string;
  name?: string;
  provider?: string;
  [key: string]: unknown;
}

/**
 * Server-side <c>result</c> frame (ticket #04): carries the RPC response data for a
 * model/thinking request, keyed by <c>target</c> (models / thinking_levels / set_model / set_thinking_level).
 */
interface ResultFrame extends RpcEvent {
  target?: string;
  data?: unknown;
}

/**
 * A pi HITL request (rpc.md extension_ui_request) relayed to the browser (ticket #07).
 * Dialog methods (select/confirm/input/editor) surface as modals; notify is transient.
 */
interface HitlRequest {
  id: string;
  method: "select" | "confirm" | "input" | "editor" | "notify";
  title?: string;
  message?: string;
  placeholder?: string;
  prefill?: string;
  options?: string[];
  notifyType?: string;
}

/** A single assistant message being streamed (text + optional thinking). */
interface ActiveAssistant {
  text: HTMLElement;
  thinking: HTMLElement | null;
}

/** Extract visible text from a pi content block (or string). Never yields "[object Object]". */
function blockText(block: unknown): string {
  if (typeof block === "string") return block;
  if (!block || typeof block !== "object") return "";
  const b = block as Record<string, any>;
  switch (b.type) {
    case "text":
      return String(b.text ?? "");
    case "image":
      return "[image]";
    case "tool_result": {
      // tool_result content may be a string or an array of blocks
      const c = b.content;
      if (typeof c === "string") return c;
      if (Array.isArray(c)) return c.map(blockText).filter(Boolean).join("\n");
      return "";
    }
    default:
      return "";
  }
}

/** Render a message `content` (string or block array) to plain visible text. */
function contentText(content: unknown): string {
  if (typeof content === "string") return content;
  if (Array.isArray(content)) return content.map(blockText).filter(Boolean).join("\n");
  if (content && typeof content === "object") return blockText(content);
  return "";
}

/** JSON.stringify that never throws (returns a readable string fallback). */
function safeJson(v: unknown): string {
  try {
    return JSON.stringify(v, null, 2);
  } catch {
    return String(v ?? "");
  }
}

/** A readable role label for transcript rows. */
function roleLabel(role: string): string {
  return role === "user" ? "You" : role === "assistant" ? "Assistant" : String(role);
}

/** Live, updating transcript renderer driven by RPC events. */
class Transcript {
  private readonly root: HTMLElement;
  private status: HTMLElement | null = null;
  private active: ActiveAssistant | null = null;
  /** Rows created by tool_execution_start, keyed by toolCallId. */
  private readonly toolRows = new Map<string, HTMLElement>();

  constructor(root: HTMLElement) {
    this.root = root;
  }

  /** Reset for a fresh session attachment (live-stream: no replay). */
  clear(): void {
    this.root.textContent = "";
    this.status = null;
    this.active = null;
    this.toolRows.clear();
  }

  /** True when no transcript rows have been rendered yet (used to decide if a history replay is still needed). */
  isEmpty(): boolean {
    return this.root.childElementCount === 0;
  }

  handle(ev: RpcEvent): void {
    switch (ev.type) {
      case "agent_start":
      case "turn_start":
        this.setStatus(`● ${ev.type}`, "status-active");
        break;
      case "agent_end":
      case "agent_settled":
      case "turn_end":
        this.setStatus(`✓ ${ev.type}`);
        this.active = null;
        break;

      case "message_start": {
        const msg = (ev as { message?: { role?: string; content?: unknown } | undefined }).message;
        if ((msg?.role ?? "message") === "assistant") this.startAssistant();
        this.renderBlocks(msg?.content, msg?.role ?? "message");
        break;
      }
      case "message_update":
        this.update(ev as MessageUpdateEvent);
        break;
      case "message_end":
        if (this.active) {
          this.appendBody(this.active.text);
          this.active = null;
        }
        break;

      case "tool_execution_start": {
        const t = ev as { toolName?: string; toolCallId?: string };
        // Reuse a row already created by a tool_use content block (same toolCallId) so the
        // tool isn't rendered twice; otherwise create the tool row now.
        const existing = t.toolCallId ? this.toolRows.get(t.toolCallId) : undefined;
        const row = existing ?? this.appendToolRow(t.toolName ?? "tool");
        if (!existing && t.toolCallId) this.toolRows.set(t.toolCallId, row);
        break;
      }
      case "tool_execution_update": {
        const u = ev as { toolCallId?: string; partialResult?: { content?: Array<{ text?: string }> } };
        const row = (u.toolCallId && this.toolRows.get(u.toolCallId)) ?? this.lastToolRow() ?? this.lastRow();
        const text = u.partialResult?.content?.map((c) => c.text ?? "").join("");
        if (row && text) this.appendCode(row, text);
        break;
      }
      case "tool_execution_end":
        break;

      case "bash_execution_update": {
        const b = ev as { id?: string; delta?: string };
        const row = (b.id ? this.toolRows.get(b.id) : undefined) ?? this.lastToolRow() ?? this.ensureBashRow();
        this.appendCode(row, String(b.delta ?? ""));
        break;
      }

      case "extension_error": {
        const e = ev as { error?: string };
        this.appendRow("error", String(e.error ?? "extension error"), "error-row");
        break;
      }
      default:
        this.setStatus(ev.type); // unknown / future events still surface
    }
  }

  private startAssistant(): void {
    const row = el("div", undefined, "row assistant");
    row.append(el("span", "Assistant", "role"));
    const body = el("div", undefined, "body-mono");
    row.append(body);
    this.appendElement(row);
    this.active = { text: body, thinking: null };
  }

  /** Render a message's content blocks (text / tool_use / tool_result / image) correctly. */
  private renderBlocks(content: unknown, role: string): void {
    const blocks = Array.isArray(content) ? content : content == null ? [] : [content];
    for (const blk of blocks) {
      if (typeof blk === "string") {
        if (this.active && role === "assistant") this.active.text.textContent += blk;
        else this.appendRow(role === "user" ? "You" : role, blk);
        continue;
      }
      if (!blk || typeof blk !== "object") continue;
      const b = blk as Record<string, any>;
      if (b.type === "text") {
        const t = String(b.text ?? "");
        if (role === "assistant" && this.active) this.active.text.textContent += t;
        else if (t) this.appendRow(role === "user" ? "You" : role, t);
      } else if (b.type === "tool_use") {
        const row = this.appendToolRow(`>>> ${b.name ?? "tool"}`);
        if (b.id) this.toolRows.set(String(b.id), row);
        // prettier the payload (e.g. bash: show the command JSON); fall back to raw input otherwise
        let code: string;
        try {
          code = JSON.stringify(b.input, null, 2);
        } catch {
          code = String(b.input ?? "");
        }
        this.appendCode(row, code);
      } else if (b.type === "tool_result") {
        const text = contentText(b.content);
        const row = (b.tool_use_id && this.toolRows.get(String(b.tool_use_id))) ?? this.lastToolRow();
        if (row) this.appendCode(row, text);
        else if (text) this.appendRow("tool result", text);
      } else if (b.type === "image") {
        if (!this.active) this.startAssistant();
        this.appendRow("image", "[image]");
      } else {
        // unknown/future block: fall back to text if it has any, never "[object Object]"
        const t = contentText(b) || (b.thinking ? String(b.thinking) : "");
        if (t && role !== "assistant") this.appendRow(role === "user" ? "You" : role, t);
      }
    }
  }

  /**
   * Replay a session's past history on (re)attach from pi's get_messages result. Renders
   * complete messages (no live streaming); tool_use/tool_result blocks are grouped into the
   * same tool row via toolCallId so the transcript reads the same as before a refresh.
   */
  renderHistorical(role: string, content: unknown): void {
    const blocks = Array.isArray(content) ? content : content == null ? [] : [content];
    for (const blk of blocks) {
      if (typeof blk === "string") {
        this.appendRow(roleLabel(role), blk);
        continue;
      }
      const b = blk as Record<string, any>;
      if (!b || typeof b !== "object") {
        const t = String(blk ?? "");
        if (t) this.appendRow(roleLabel(role), t);
        continue;
      }
      if (b.type === "text") {
        this.appendRow(roleLabel(role), String(b.text ?? ""));
      } else if (b.type === "tool_use") {
        const row = this.appendToolRow(`>>> ${b.name ?? "tool"}`);
        if (b.id) this.toolRows.set(String(b.id), row);
        this.appendCode(row, safeJson(b.input));
      } else if (b.type === "tool_result") {
        const text = contentText(b.content);
        const row = (b.tool_use_id && this.toolRows.get(String(b.tool_use_id))) ?? this.lastToolRow();
        if (row) this.appendCode(row, text);
        else if (text) this.appendRow("tool result", text);
      } else if (b.type === "image") {
        this.appendRow("image", "[image]");
      } else {
        const t = contentText(b) || (b.thinking ? String(b.thinking) : "");
        if (t && role !== "assistant") this.appendRow(roleLabel(role), t);
      }
    }
  }

  private update(ev: MessageUpdateEvent): void {
    const delta = ev.assistantMessageEvent;
    if (!delta) return;
    let target: HTMLElement | null = null;
    if (delta.type.startsWith("thinking")) {
      if (!this.active) this.startAssistant();
      if (this.active) {
        if (!this.active.thinking) {
          this.active.thinking = el("details", undefined, "thinking");
          this.active.thinking.append(el("summary", "thinking"));
          this.active.thinking.append(el("pre"));
          this.active.text.parentElement?.prepend(this.active.thinking);
        }
        target = this.active.thinking.querySelector("pre");
      }
    } else {
      // text_delta with no active assistant row: open one rather than dropping it.
      if (!this.active) this.startAssistant();
      if (this.active) target = this.active.text;
    }
    if (target && delta.delta) target.textContent += delta.delta;
  }

  // --- row helpers ----------------------------------------------------------

  private lastRow(): HTMLElement | null {
    const children = this.root.children;
    return children.length ? children[children.length - 1] as HTMLElement : null;
  }

  private appendRow(label: string, text: string, cls = ""): void {
    const row = el("div", undefined, `row ${cls}`);
    row.append(el("span", label, "role"));
    const body = el("div", text, "body");
    row.append(body);
    this.appendElement(row);
  }

  private appendToolRow(label: string): HTMLElement {
    const row = el("div", undefined, "row tool-row");
    row.append(el("span", label, "role"));
    row.append(el("div", undefined, "body-mono"));
    this.appendElement(row);
    return row;
  }

  private lastToolRow(): HTMLElement | null {
    const children = this.root.children;
    for (let i = children.length - 1; i >= 0; i--) {
      const child = children[i] as HTMLElement;
      if (child.classList.contains("tool-row")) return child;
    }
    return null;
  }

  private appendCode(row: HTMLElement, text: string): void {
    const pre = row.querySelector("pre");
    if (pre) pre.textContent += text;
    else row.append(el("pre", text));
  }

  private appendBody(body: HTMLElement): void {
    if (body.parentElement) this.appendElement(body.parentElement);
  }

  private ensureBashRow(): HTMLElement {
    let last = this.lastRow();
    if (!last || !last.classList.contains("bash-row")) {
      last = el("div", undefined, "row bash-row");
      last.append(el("span", "bash", "role"));
      last.append(el("div", undefined, "body-mono"));
      this.appendElement(last);
    }
    return last;
  }

  private setStatus(text: string, cls = "status"): void {
    if (!this.status) {
      this.status = el("div", text, cls);
      this.appendElement(this.status);
    } else {
      this.status.textContent = text;
      this.status.className = cls;
    }
  }

  private appendElement(node: HTMLElement): void {
    this.root.append(node);
    node.scrollIntoView({ block: "end", behavior: "smooth" });
  }
}

function readToken(): string {
  const q = new URLSearchParams(location.search).get("token");
  if (q) return q;
  const m = document.cookie.match(/(?:^|; )token=([^;]+)/);
  return m ? decodeURIComponent(m[1]) : "";
}

function setup(): void {
  const app = document.querySelector<HTMLElement>("#app")!;
  const transcript = new Transcript(document.querySelector<HTMLElement>("#transcript")!);

  const status = new Signal<string>("connecting…");
  const statusEl = el("div", undefined, "ws-status");
  status.subscribe((v) => (statusEl.textContent = v));
  app.prepend(statusEl);

  const sessionListEl = document.querySelector<HTMLUListElement>("#session-list")!;
  const activeEl = document.querySelector<HTMLElement>("#active-session")!;
  const newBtn = document.querySelector<HTMLButtonElement>("#new")!;
  const recycleBtn = document.querySelector<HTMLButtonElement>("#recycle")!;
  const deleteBtn = document.querySelector<HTMLButtonElement>("#delete")!;
  const forkBtn = document.querySelector<HTMLButtonElement>("#fork")!;
  const cloneBtn = document.querySelector<HTMLButtonElement>("#clone")!;

  const token = readToken();

  // Refresh the session panel periodically while a session is active, so stats/state/
  // structure stay live during a run (they aren't otherwise pushed by the agent).
  setInterval(() => { if (active) refreshPanel(); }, 5000);

  // --- controller ----------------------------------------------------------
  const sessions = new Map<string, SessionInfo>(); // name -> info
  let active = "";
  let forkMessages: ForkMessage[] = [];
  let pendingFork = false;
  let historyAttempts = 0;

  // --- HITL dialogs (ticket #07) ---------------------------------------------
  // Pending dialog requests are kept PER SESSION (name -> request), so a modal on one
  // session never blocks another: switching the active session shows that session's own
  // pending modal (or none), and answering goes out over the attached session's WS.
  const pendingModals = new Map<string, HitlRequest>();

  // HITL answers buffered while the WS is not OPEN (mid-reconnect), keyed by session name,
  // so an answer is never silently dropped mid-reconnect and the agent left blocked. Flushed
  // on the next (re)connect to the same session. Bounded per session: at most one dialog can
  // be pending per session, so at most one buffered answer per session.
  const pendingAnswers = new Map<string, { id: string; payload: Record<string, unknown> }>();


  // --- model + thinking pickers (ticket #04) --------------------------------
  const modelSelect = document.querySelector<HTMLSelectElement>("#model-select")!;
  const thinkingSelect = document.querySelector<HTMLSelectElement>("#thinking-select")!;
  let availableModels: ModelInfo[] = [];
  let availableThinkingLevels: string[] = [];
  let currentModel: ModelInfo | null = null;
  let currentThinkingLevel: string | null = null;

  // --- session panel: compaction / retry / state / stats / structure / export (ticket #08) --
  const compactBtn = document.querySelector<HTMLButtonElement>("#compact")!;
  const autoCompactionChk = document.querySelector<HTMLInputElement>("#auto-compaction")!;
  const autoRetryChk = document.querySelector<HTMLInputElement>("#auto-retry")!;
  const exportBtn = document.querySelector<HTMLButtonElement>("#export")!;
  const refreshPanelBtn = document.querySelector<HTMLButtonElement>("#refresh-panel")!;
  const sessionPanel = document.querySelector<HTMLElement>("#session-panel")!;
  const sessionStateEl = document.querySelector<HTMLElement>("#session-state")!;
  const sessionStatsEl = document.querySelector<HTMLElement>("#session-stats")!;
  const sessionEntriesEl = document.querySelector<HTMLUListElement>("#session-entries")!;
  const sessionExportEl = document.querySelector<HTMLElement>("#session-export")!;
  const sessionStructure = document.querySelector<HTMLDetailsElement>("#session-structure")!;
  let lastExportPath: string | null = null;

  // Auto-retry is an optimistic, PER-SESSION value: rpc.md get_state does NOT expose
  // autoRetryEnabled, so pi can't confirm it back. We persist what the user toggles for
  // each session in this map and restore it on switch/reconnect so the checkbox reflects
  // the user's choice for that session's lifetime instead of silently resetting (and
  // misreporting). With no known value yet, default to off (pi's default setting).
  const sessionAutoRetry = new Map<string, boolean>();

  const kv = (k: string, v: unknown, suffix = ""): string =>
    `${k}: ${String(v ?? "—")}${suffix}`;

  function renderState(d?: unknown): void {
    const s = (d ?? {}) as {
      model?: { id?: string; name?: string } | null;
      thinkingLevel?: string | null;
      isStreaming?: boolean;
      isCompacting?: boolean;
      autoCompactionEnabled?: boolean;
      messageCount?: number;
      sessionName?: string | null;
    };
    const lines = [
      kv("model", s.model?.name || s.model?.id || "—"),
      kv("thinking", s.thinkingLevel),
      kv("streaming", s.isStreaming),
      kv("compacting", s.isCompacting),
      kv("messages", s.messageCount),
    ];
    sessionStateEl.textContent = lines.join(" · ");
    // reflect the authoritative auto-compaction flag from pi's state
    if (typeof s.autoCompactionEnabled === "boolean") autoCompactionChk.checked = s.autoCompactionEnabled;
  }

  function renderStats(d?: unknown): void {
    const s = (d ?? {}) as {
      totalMessages?: number;
      tokens?: { total?: number; input?: number; output?: number };
      cost?: number;
      contextUsage?: { percent?: number | null; tokens?: number | null; contextWindow?: number };
    };
    const ctx = s.contextUsage;
    const lines = [
      kv("messages", s.totalMessages),
      kv("total tokens", s.tokens?.total),
      kv("cost", s.cost, "$"),
      kv("context", ctx?.percent != null ? `${ctx.percent}%` : "—"),
    ];
    sessionStatsEl.textContent = lines.join(" · ");
  }

  /** A node returned by rpc.md get_tree: { entry, children, label?, labelTimestamp? }. */
  interface TreeNode {
    entry?: { type?: string; id?: string; message?: { role?: string } | null } | null;
    children?: TreeNode[] | null;
    label?: string | null;
  }

  function nodeLabel(e: TreeNode["entry"]): string {
    if (!e) return "?";
    const role = e.message?.role ?? "";
    return `${e.type ?? "?"}${role ? ` [${role}]` : ""} ${e.id ?? ""}`.trim();
  }

  /** Recursively render the session as an indented tree (nested <ul>), not a flat list. */
  function appendTreeNodes(parent: HTMLUListElement, nodes: TreeNode[]): void {
    for (const n of nodes ?? []) {
      const li = el("li", undefined);
      li.append(el("span", n.label ?? nodeLabel(n.entry)));
      parent.append(li);
      const kids = n.children ?? [];
      if (kids.length) {
        const ul = el("ul", undefined, "tree-children");
        appendTreeNodes(ul, kids);
        li.append(ul);
      }
    }
  }

  function renderTree(d?: unknown): void {
    sessionEntriesEl.textContent = "";
    const tree = ((d ?? {}) as { tree?: TreeNode[] }).tree ?? [];
    if (!tree.length) {
      sessionEntriesEl.append(el("li", "(no entries yet)"));
      return;
    }
    appendTreeNodes(sessionEntriesEl, tree);
  }

  function renderExport(): void {
    sessionExportEl.textContent = "";
    if (!lastExportPath) {
      sessionExportEl.append(el("span", "No export yet."));
      return;
    }
    sessionExportEl.append(el("span", `Saved: ${lastExportPath}`));
    const link = el("a", "Download transcript") as HTMLAnchorElement;
    link.href = `/api/sessions/${encodeURIComponent(active)}/export`;
    link.title = "Download the exported HTML transcript";
    sessionExportEl.append(" ", link);
  }

  // --- agent state (running/queued/idle), driven by relayed RPC events ---------
  const agentStateEl = document.querySelector<HTMLElement>("#agent-state")!;
  let agentRunning = false;
  let aborting = false;
  let stopped = false; // distinct "stopped" state set on abort, cleared on agent settle
  let queuedSteer = 0;
  let queuedFollow = 0;

  function renderAgentState(): void {
    const parts: string[] = [];
    if (stopped) parts.push("stopped");
    else if (aborting) parts.push("stopping");
    else if (agentRunning) parts.push("running");
    else parts.push("idle");
    if (queuedSteer > 0) parts.push(`${queuedSteer} steer`);
    if (queuedFollow > 0) parts.push(`${queuedFollow} follow-up`);
    agentStateEl.textContent = parts.join(" · ");
    agentStateEl.className = `agent-state ${stopped ? "stopped" : aborting ? "stopping" : agentRunning ? "running" : "idle"}`;
  }

  // --- WebSocket ----------------------------------------------------------
  const proto = location.protocol === "https:" ? "wss:" : "ws:";
  let ws: WebSocket | null = null;

  function wsUrl(): string {
    const base = `${proto}//${location.host}/ws?session=${encodeURIComponent(active)}`;
    return token ? `${base}&token=${encodeURIComponent(token)}` : base;
  }

  function wsSend(obj: unknown): void {
    if (ws?.readyState === WebSocket.OPEN) ws.send(JSON.stringify(obj));
  }

  // --- API (REST) ----------------------------------------------------------
  async function api(path: string, init?: RequestInit): Promise<Response> {
    const res = await fetch(path, init);
    if (!res.ok) throw new Error(`API ${res.status} ${res.statusText}`);
    return res;
  }

  async function createSession(name: string): Promise<SessionInfo> {
    const res = await api("/api/sessions", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ name }),
    });
    const info = (await res.json()) as SessionInfo;
    sessions.set(name, info);
    return info;
  }

  /** Resume a stored/recycled session: REST init resumes an existing history file. */
  async function resumeSession(name: string): Promise<void> {
    try {
      await createSession(name);
    } catch {
      status.set(`failed to resume ${name}`);
      return;
    }
    selectSession(name);
  }

  async function refreshSessions(): Promise<void> {
    const res = await api("/api/sessions");
    const list = (await res.json()) as SessionInfo[];
    sessions.clear();
    for (const s of list) sessions.set(s.name, s);
  }

  // --- rendering -----------------------------------------------------------
  function statusClass(s: string): string {
    return s === "running" ? "running" : s === "stored" ? "stored" : "recycled";
  }

  function renderSessions(): void {
    sessionListEl.textContent = "";
    // Iterate the map in insertion order, which refreshSessions seeds from the server's
    // newest-first order (don't re-sort by name — that would undo it). WS lifecycle events
    // append newer sessions at the end, which is consistent.
    for (const info of sessions.values()) {
      const name = info.name;
      const st = info?.status ?? "";
      const item = el("li", undefined, "session-item");
      if (name === active) item.classList.add("active");
      item.append(el("span", undefined, `dot ${statusClass(st)}`));
      // show the auto-title when present (ticket #06); the stable name in the tooltip
      const labelText = info?.title && info.title !== name ? `${info.title} — ${name}` : name;
      const label = el("span", labelText, "name");
      label.title = name;
      const del = el("button", "×", "del");
      del.title = "Delete session";
      del.addEventListener("click", (e) => {
        e.stopPropagation();
        wsSend({ type: "delete", name });
      });
      item.append(label, del);
      // reselect resumes a stored/recycled session (REST init resumes its history file)
      item.addEventListener("click", () => {
        if (st && st !== "running") void resumeSession(name);
        else selectSession(name);
      });
      sessionListEl.append(item);
    }
    activeEl.textContent = active ? `session: ${active}` : "";
    recycleBtn.disabled = !active;
    deleteBtn.disabled = !active;
    forkBtn.disabled = !active;
    cloneBtn.disabled = !active;
    compactBtn.disabled = !active;
    exportBtn.disabled = !active;
    autoCompactionChk.disabled = !active;
    autoRetryChk.disabled = !active;
    refreshPanelBtn.disabled = !active;
    sessionPanel.classList.toggle("hidden", !active);
    if (!active) {
      sessionStateEl.textContent = "";
      sessionStatsEl.textContent = "";
      sessionEntriesEl.textContent = "";
      sessionExportEl.textContent = "";
    }
  }

  function selectSession(name: string): void {
    if (name === active) return;
    active = name;
    if (!sessions.has(name)) sessions.set(name, { name, status: "running" });
    lastExportPath = null; // the export link is per-session; never leak across tabs
    sessionExportEl.textContent = "";
    renderSessions();
    connect();
    renderHitlModal(); // show this session's own pending modal (or none)
  }

  function fallbackAfterActiveDeleted(): void {
    active = "";
    const names = [...sessions.keys()].sort();
    if (names.length) selectSession(names[0]);
    else showEmptyState();
  }

  /**
   * Empty state: sessions are created ONLY on explicit user init — never implicitly
   * on connect/boot. So when nothing exists we just prompt the user to create one.
   */
  function showEmptyState(): void {
    active = "";
    status.set("no sessions — create one to begin");
    sessionListEl.textContent = "";
    renderSessions(); // resets the active label + disables lifecycle controls
    renderPickers();   // disables the pickers (no active session)
    sessionListEl.append(el("li", "No sessions yet. Click “+ New” to create one.", "empty-hint"));
  }

  /** Stable key for a model option (provider + id joined with a NUL; safe if either contains "/"). */
  function modelKey(m: ModelInfo): string {
    return `${m.provider ?? ""}\u0000${m.id}`;
  }

  /** Rebuild the model + thinking dropdowns and reflect the current selection. */
  function renderPickers(): void {
    modelSelect.textContent = "";
    for (const m of availableModels) {
      const opt = el("option", m.name ? `${m.name} (${m.provider}/${m.id})` : `${m.provider}/${m.id}`) as HTMLOptionElement;
      opt.value = modelKey(m);
      modelSelect.append(opt);
    }
    if (currentModel) modelSelect.value = modelKey(currentModel);
    else modelSelect.selectedIndex = -1;

    thinkingSelect.textContent = "";
    for (const lvl of availableThinkingLevels) {
      const opt = el("option", lvl) as HTMLOptionElement;
      opt.value = lvl;
      thinkingSelect.append(opt);
    }
    if (currentThinkingLevel) thinkingSelect.value = currentThinkingLevel;
    else thinkingSelect.selectedIndex = -1;

    const disabled = !active;
    modelSelect.disabled = disabled;
    thinkingSelect.disabled = disabled;
  }

  /** Apply a model/thinking <c>result</c> frame, populating lists and reflecting selection. */
  function handleResult(r: ResultFrame): void {
    switch (r.target) {
      case "models": {
        availableModels = (r.data as { models?: ModelInfo[] } | undefined)?.models ?? [];
        renderPickers();
        break;
      }
      case "thinking_levels": {
        availableThinkingLevels = (r.data as { levels?: string[] } | undefined)?.levels ?? [];
        renderPickers();
        break;
      }
      case "set_model": {
        if (r.data) {
          currentModel = r.data as ModelInfo;
          if (!availableModels.some((m) => modelKey(m) === modelKey(currentModel!))) {
            availableModels = [...availableModels, currentModel];
          }
          renderPickers();
          // Thinking levels depend on the selected model (rpc.md): re-fetch the list
          // for the newly-applied model so the picker reflects its supported levels.
          wsSend({ type: "thinking_levels" });
        }
        break;
      }
      case "state": {
        // Single unified path (formerly get_state/state were duplicated): rpc.md get_state
        // exposes model + thinkingLevel, so this restores the attached session's ACTUAL
        // current selection for the pickers AND renders the session panel from one round trip.
        const d = r.data as { model?: ModelInfo | null; thinkingLevel?: string } | undefined;
        if (d?.model) {
          currentModel = d.model;
          if (d.model && !availableModels.some((m) => modelKey(m) === modelKey(d.model!))) {
            availableModels = [...availableModels, d.model];
          }
        }
        currentThinkingLevel = d?.thinkingLevel ?? null;
        renderPickers();
        renderState(r.data);
        break;
      }
      case "set_thinking_level":
        // plain set returns no data; the level we selected is already current.
        renderPickers();
        break;
      case "get_fork_messages": {
        forkMessages = (r.data as { messages?: ForkMessage[] } | undefined)?.messages ?? [];
        break;
      }
      case "fork":
      case "clone": {
        // a fork/clone may create a new branch file; refresh the stored list (ticket #06)
        forkMessages = [];
        pendingFork = false;
        void refreshSessions().then(() => renderSessions());
        break;
      }
      case "state": {
        renderState(r.data);
        break;
      }
      case "stats": {
        renderStats(r.data);
        break;
      }
      case "structure": {
        renderTree(r.data);
        break;
      }
      case "history": {
        // Replay the session's past messages on (re)attach (rpc.md get_messages). If it
        // came back empty (freshly-resumed child not ready, or a lost request during a
        // reload), retry so history isn't silently missing.
        const msgs = (r.data as { messages?: Array<{ role?: string; content?: unknown }> } | undefined)?.messages ?? [];
        for (const m of msgs) transcript.renderHistorical(m.role ?? "message", m.content);
        if (msgs.length === 0) {
          if (historyAttempts < 5) { historyAttempts++; setTimeout(() => requestHistoryIfNeeded(), 900); }
        } else {
          historyAttempts = 0;
        }
        break;
      }
      case "export_html": {
        const d = r.data as { path?: string } | undefined;
        if (d?.path) {
          lastExportPath = d.path;
          renderExport();
        }
        break;
      }
      case "compact":
      case "set_auto_compaction":
      case "set_auto_retry": {
        // confirmation acks; refresh state/stats to reflect the applied change
        refreshPanel();
        break;
      }
    }
  }

  /** Re-fetch state/stats/structure for the active session's panel. */
  function refreshPanel(): void {
    if (!active) return;
    wsSend({ type: "state" });
    wsSend({ type: "stats" });
    wsSend({ type: "structure" });
  }

  /**
   * Re-request the session's history if the transcript is still empty. History is requested
   * once on connect but can otherwise be lost if the WS was interrupted (page reload) or the
   * freshly-resumed child wasn't ready (server replies "not running" instead of a result);
   * re-requesting whenever the session reports running and the transcript is empty recovers it.
   */
  function requestHistoryIfNeeded(): void {
    if (!transcript.isEmpty()) return; // already has content (history or live) — don't clobber
    if (ws?.readyState === WebSocket.OPEN) wsSend({ type: "history" });
  }

  function handleFrame(obj: RpcEvent): void {
    if (obj.type === "session_event") {
      const s = obj.session as SessionInfo;
      let modalDirty = false;
      if (s.status === "deleted") {
        sessions.delete(s.name);
        // drop any stale per-session pending dialog so a deleted session's request never
        // lingers or resurfaces after the tab switches back to a (recreated) session
        modalDirty = pendingModals.delete(s.name);
      } else {
        const prev = sessions.get(s.name);
        // merge so a title-only event (auto-title, ticket #06) keeps the known status/name
        sessions.set(s.name, { name: s.name, status: s.status, title: s.title ?? prev?.title });
        if (s.status === "recycled") {
          // a recycled session's child is gone, so its pending dialog (and any buffered
          // answer) can no longer be answered/resolved from the UI — clear them, not stale
          modalDirty = pendingModals.delete(s.name);
          pendingAnswers.delete(s.name);
        }
      }

      if (s.name === active && s.status === "deleted") fallbackAfterActiveDeleted();
      else {
        renderSessions();
        // reflect a just-cleared stale modal for the attached session (close the overlay)
        if (modalDirty && s.name === active) renderHitlModal();
      }
      return;
    }
    if (obj.type === "error") {
      status.set(`⚠ ${String((obj as { message?: string }).message ?? "error")}`);
      return;
    }
    if (obj.type === "result") {
      handleResult(obj as ResultFrame);
      if (obj.target === "get_fork_messages" && pendingFork) {
        pendingFork = false;
        showForkPicker();
      }
      return; // model/thinking/fork picker data only, not a transcript event
    }
    if (obj.type === "agent_start") {
      agentRunning = true;
      aborting = false;
      stopped = false;
      renderAgentState();
      requestHistoryIfNeeded(); // session is now running — recover history if the transcript is still empty
    } else if (obj.type === "agent_settled") {
      agentRunning = false;
      aborting = false;
      stopped = false;
      renderAgentState();
      refreshPanel(); // stats/state/structure are stale after a run ends — refetch now
    } else if (obj.type === "queue_update") {
      const q = obj as { steering?: unknown[]; followUp?: unknown[] };
      queuedSteer = Array.isArray(q.steering) ? q.steering.length : 0;
      queuedFollow = Array.isArray(q.followUp) ? q.followUp.length : 0;
      renderAgentState();
      return; // queue status only, not a transcript event
    }
    if (obj.type === "extension_ui_request") {
      // HITL (rpc.md): dialogs surface as modals, notify as a transient notice. Neither is
      // a transcript event; the modal is tracked per-session so one session never blocks
      // another (the current one shows, the rest stay pending by name).
      const req = obj as unknown as HitlRequest;
      if (req.method === "notify") {
        showNotice(req); // non-modal, non-blocking
      } else if (active && req.id) {
        pendingModals.set(active, req);
        renderHitlModal();
      }
      return;
    }
    transcript.handle(obj); // live RPC event
  }

  function connect(): void {
    transcript.clear();
    status.set("connecting…");
    agentRunning = false;
    aborting = false;
    stopped = false;
    queuedSteer = 0;
    queuedFollow = 0;
    renderAgentState();
    if (ws) {
      const old = ws;
      old.onclose = null; // the new connection owns the close path now
      old.close();
    }
    const url = wsUrl();
    const socket = new WebSocket(url);
    ws = socket;
    socket.onopen = () => {
      status.set("● connected");
      // Per-session pickers: refetch the available lists for THIS session on every
      // (re)connect / tab switch, so one session's selection never bleeds into another.
      availableModels = [];
      availableThinkingLevels = [];
      currentModel = null;
      currentThinkingLevel = null;
      renderPickers();
      wsSend({ type: "models" });
      wsSend({ type: "thinking_levels" });
      // The session panel's `state` frame (via refreshPanel) is the single unified get_state
      // path — it restores the actual current model + thinking level AND renders the panel.
      // No separate `get_state` send here (deduped).
      refreshPanel();
      // Replay the session's past history into the (just-cleared) transcript, so a page
      // refresh / tab switch shows prior work instead of an empty, live-only feed.
      historyAttempts = 0;
      requestHistoryIfNeeded();
      // Retry history after a beat in case the freshly-resumed child wasn't ready yet.
      setTimeout(() => requestHistoryIfNeeded(), 1200);
      // Restore this session's optimistic auto-retry choice (per-session; unknown -> off).
      autoRetryChk.checked = sessionAutoRetry.get(active) ?? false;
      // Flush a HITL answer buffered while the socket was down, so a mid-reconnect answer
      // reaches the freshly-reconnected session's child instead of being silently dropped.
      const buffered = pendingAnswers.get(active);
      if (buffered) {
        pendingAnswers.delete(active);
        wsSend({ type: "hitl_response", id: buffered.id, ...buffered.payload });
      }
    };
    socket.onmessage = (evt) => {
      let obj: RpcEvent;
      try {
        obj = JSON.parse(String(evt.data)) as RpcEvent;
      } catch {
        return; // ignore malformed frames
      }
      handleFrame(obj);
    };
    socket.onclose = () => {
      if (ws !== socket) return; // superseded by a newer connection
      status.set("disconnected — retrying…");
      setTimeout(() => connect(), 1000);
    };
    socket.onerror = () => socket.close();
  }

  // --- controls ------------------------------------------------------------
  const input = document.querySelector<HTMLTextAreaElement>("#prompt")!;
  const send = document.querySelector<HTMLButtonElement>("#send")!;
  const sendBox = document.querySelector<HTMLFormElement>("#composer")!;

  input.addEventListener("input", () => { /* keep native behaviour */ });
  const submit = (): void => {
    const text = input.value.trim();
    if (!text) return;
    // rpc.md: a prompt while the agent is streaming REQUIRES a streamingBehavior or it is
    // rejected. When we believe the agent is running, queue it as a steer so it is
    // delivered before the agent's next LLM call; idle prompts need no streamingBehavior.
    const payload: Record<string, unknown> = { type: "prompt", message: text };
    if (agentRunning) payload.streamingBehavior = "steer";
    wsSend(payload);
    input.value = "";
  };
  sendBox.addEventListener("submit", (e) => {
    e.preventDefault();
    submit();
  });
  send.addEventListener("click", submit);
  input.addEventListener("keydown", (e) => {
    if (e.key === "Enter" && !e.shiftKey) {
      e.preventDefault();
      submit();
    }
  });

  // --- model / thinking pickers (ticket #04) --------------------------------
  modelSelect.addEventListener("change", () => {
    if (!active) return;
    const m = availableModels.find((x) => modelKey(x) === modelSelect.value);
    if (!m) return;
    currentModel = m;
    renderPickers();
    wsSend({ type: "set_model", provider: m.provider, modelId: m.id });
  });

  thinkingSelect.addEventListener("change", () => {
    if (!active) return;
    const level = thinkingSelect.value;
    if (!level) return;
    currentThinkingLevel = level;
    renderPickers();
    wsSend({ type: "set_thinking_level", level });
  });

  // --- abort / steer / follow-up (ticket #03) ------------------------------
  const abortBtn = document.querySelector<HTMLButtonElement>("#abort")!;
  const steerInput = document.querySelector<HTMLInputElement>("#steer-input")!;
  const steerSend = document.querySelector<HTMLButtonElement>("#steer-send")!;
  const followupInput = document.querySelector<HTMLInputElement>("#followup-input")!;
  const followupSend = document.querySelector<HTMLButtonElement>("#followup-send")!;

  abortBtn.addEventListener("click", () => {
    if (!active) return;
    aborting = true;
    stopped = true; // reflect "stopped" after an abort is issued (distinct from idle)
    renderAgentState();
    wsSend({ type: "abort" });
  });

  const submitSteer = (): void => {
    const text = steerInput.value.trim();
    if (!text) return;
    wsSend({ type: "steer", message: text });
    steerInput.value = "";
    if (agentRunning) {
      queuedSteer++; // optimistic feedback; authoritative queue_update corrects it
      renderAgentState();
    }
  };
  steerSend.addEventListener("click", submitSteer);
  steerInput.addEventListener("keydown", (e) => {
    if (e.key === "Enter") {
      e.preventDefault();
      submitSteer();
    }
  });

  const submitFollowup = (): void => {
    const text = followupInput.value.trim();
    if (!text) return;
    wsSend({ type: "follow_up", message: text });
    followupInput.value = "";
    if (!agentRunning) {
      queuedFollow++; // optimistic feedback; authoritative queue_update corrects it
      renderAgentState();
    }
  };
  followupSend.addEventListener("click", submitFollowup);
  followupInput.addEventListener("keydown", (e) => {
    if (e.key === "Enter") {
      e.preventDefault();
      submitFollowup();
    }
  });

  newBtn.addEventListener("click", async () => {
    const name = prompt("New session name:", `session-${Date.now().toString(36)}`);
    if (!name || !name.trim()) return;
    const trimmed = name.trim();
    try {
      await createSession(trimmed);
      renderSessions();
      selectSession(trimmed);
    } catch {
      status.set("failed to create session");
    }
  });

  recycleBtn.addEventListener("click", () => {
    if (active) wsSend({ type: "recycle", name: active });
  });

  deleteBtn.addEventListener("click", () => {
    if (active) wsSend({ type: "delete", name: active });
  });

  // --- fork / clone (ticket #06) ------------------------------------------
  forkBtn.addEventListener("click", () => {
    if (!active) return;
    pendingFork = true;
    forkMessages = [];
    wsSend({ type: "get_fork_messages" });
  });

  cloneBtn.addEventListener("click", () => {
    if (!active) return;
    wsSend({ type: "clone" });
  });

  // --- compaction / auto-retry / export / panel refresh (ticket #08) -------
  compactBtn.addEventListener("click", () => {
    if (!active) return;
    wsSend({ type: "compact" });
  });

  autoCompactionChk.addEventListener("change", () => {
    if (!active) return;
    wsSend({ type: "set_auto_compaction", enabled: autoCompactionChk.checked });
  });

  autoRetryChk.addEventListener("change", () => {
    if (!active) return;
    // Optimistic, per-session: pi's get_state does NOT expose autoRetryEnabled, so we keep
    // the user's choice in the session map and restore it on switch/reconnect rather than
    // letting the checkbox reset (and misreport) after a toggle/refresh/tab-switch.
    sessionAutoRetry.set(active, autoRetryChk.checked);
    wsSend({ type: "set_auto_retry", enabled: autoRetryChk.checked });
  });

  exportBtn.addEventListener("click", () => {
    if (!active) return;
    wsSend({ type: "export_html" });
  });

  refreshPanelBtn.addEventListener("click", refreshPanel);

  // --- HITL dialogs (ticket #07) ------------------------------------------

  /**
   * Send the answer for the given request over the ATTACHED session's WS, then clear the modal.
   * If the socket is not OPEN (mid-reconnect) the answer is buffered per-session (bounded) and
   * flushed on reconnect, so it isn't silently dropped and the agent left blocked on its dialog.
   */
  function answerHitl(req: HitlRequest, payload: Record<string, unknown>): void {
    pendingModals.delete(active);
    document.querySelector(".hitl-overlay")?.remove();
    if (ws?.readyState === WebSocket.OPEN) {
      wsSend({ type: "hitl_response", id: req.id, ...payload });
    } else {
      pendingAnswers.set(active, { id: req.id, payload });
    }
  }

  /** Transient, non-blocking notice for a notify request (auto-dismisses). */
  function showNotice(req: HitlRequest): void {
    const notice = el("div", req.message ?? "notification", `hitl-notice ${req.notifyType ?? "info"}`);
    notice.append(el("button", "×", "hitl-notice-close"));
    notice.querySelector(".hitl-notice-close")!.addEventListener("click", () => notice.remove());
    document.body.append(notice);
    setTimeout(() => notice.remove(), 5000);
  }

  /** Render the active session's pending HITL dialog as a modal, or nothing. */
  function renderHitlModal(): void {
    const old = document.querySelector<HTMLElement>(".hitl-overlay");
    if (old) old.remove();
    const req = active ? pendingModals.get(active) : undefined;
    if (!req) return;

    const overlay = el("div", undefined, "hitl-overlay");
    const box = el("div", undefined, `hitl-box ${req.method}`);
    box.append(el("div", req.title ?? "Question from agent", "hitl-title"));
    if (req.message) box.append(el("div", req.message, "hitl-message"));

    const cancel = el("button", "Cancel", "btn");
    cancel.addEventListener("click", () => answerHitl(req, { cancelled: true }));

    if (req.method === "confirm") {
      // confirm = Yes / No (rpc.md: extension_ui_response confirmed:true/false)
      const actions = el("div", undefined, "hitl-actions");
      const yes = el("button", "Yes", "btn primary");
      const no = el("button", "No", "btn");
      yes.addEventListener("click", () => answerHitl(req, { confirmed: true }));
      no.addEventListener("click", () => answerHitl(req, { confirmed: false }));
      actions.append(yes, no, cancel);
      box.append(actions);
    } else if (req.method === "select") {
      // select = list of options (rpc.md: value = the chosen option string)
      const select = el("select", undefined, "hitl-select") as HTMLSelectElement;
      const options = req.options?.length ? req.options : [];
      if (!options.length) select.append(el("option", "(no options)"));
      for (const o of options) select.append(el("option", o) as HTMLOptionElement);
      const actions = el("div", undefined, "hitl-actions");
      const ok = el("button", "OK", "btn primary");
      ok.addEventListener("click", () => answerHitl(req, { value: select.value }));
      actions.append(cancel, ok);
      box.append(el("label", "Select an option", "hitl-label"), select, actions);
    } else {
      // input = single text field; editor = larger text area (rpc.md: value = text)
      const field =
        req.method === "editor"
          ? el("textarea", req.prefill ?? "", "hitl-textarea")
          : (el("input", undefined, "hitl-input") as HTMLInputElement);
      if (req.method === "input") {
        const inp = field as HTMLInputElement;
        if (req.placeholder) inp.placeholder = req.placeholder;
      }
      const actions = el("div", undefined, "hitl-actions");
      const ok = el("button", "OK", "btn primary");
      ok.addEventListener("click", () =>
        answerHitl(req, {
          value: field instanceof HTMLTextAreaElement ? field.value : (field as HTMLInputElement).value,
        }),
      );
      box.append(field, actions);
      actions.append(cancel, ok);
    }

    overlay.append(box);
    document.body.append(overlay);
    (overlay.querySelector("input, select, textarea") as HTMLElement | null)?.focus();
  }

  /**
   * Light picker over the fork messages (get_fork_messages) so the user chooses which
   * message to fork from; sends the fork command for the chosen entryId (rpc.md).
   */
  function showForkPicker(): void {
    if (!forkMessages.length) {
      status.set("no messages to fork from — send a prompt first");
      return;
    }
    const overlay = el("div", undefined, "fork-overlay");
    const box = el("div", undefined, "fork-box");
    box.append(el("div", "Fork from which message?", "fork-title"));
    const select = el("select", undefined, "fork-select") as HTMLSelectElement;
    for (const m of forkMessages) {
      const opt = el("option", m.text.slice(0, 80) || "(empty)") as HTMLOptionElement;
      opt.value = m.entryId;
      select.append(opt);
    }
    box.append(select);
    const actions = el("div", undefined, "fork-actions");
    const cancel = el("button", "Cancel", "btn");
    const fork = el("button", "Fork", "btn primary");
    cancel.addEventListener("click", () => overlay.remove());
    fork.addEventListener("click", () => {
      wsSend({ type: "fork", entryId: select.value });
      overlay.remove();
    });
    actions.append(cancel, fork);
    box.append(actions);
    overlay.append(box);
    document.body.append(overlay);
  }

  // --- boot ----------------------------------------------------------------
  (async () => {
    try {
      await refreshSessions();
    } catch {
      status.set("failed to load sessions");
      return;
    }
    if (sessions.size === 0) showEmptyState();
    else selectSession([...sessions.keys()].sort()[0]);
  })();
}

if (document.readyState === "loading") document.addEventListener("DOMContentLoaded", setup);
else setup();
