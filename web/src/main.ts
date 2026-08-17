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

/** Server-side lifecycle frame pushed by the WS bridge (ticket #05). */
interface SessionInfo {
  name: string;
  status: "running" | "recycled" | "deleted";
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

/** A single assistant message being streamed (text + optional thinking). */
interface ActiveAssistant {
  text: HTMLElement;
  thinking: HTMLElement | null;
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
        const msg = ev.message as { role?: string } | undefined;
        const role = msg?.role ?? "message";
        if (role === "assistant") this.startAssistant();
        else this.appendRow(role === "user" ? "You" : role, String((msg as { content?: unknown })?.content ?? ""));
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
        const row = this.appendToolRow(t.toolName ?? "tool");
        if (t.toolCallId) this.toolRows.set(t.toolCallId, row);
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

  const token = readToken();

  // --- controller ----------------------------------------------------------
  const sessions = new Map<string, string>(); // name -> status
  let active = "";

  // --- model + thinking pickers (ticket #04) --------------------------------
  const modelSelect = document.querySelector<HTMLSelectElement>("#model-select")!;
  const thinkingSelect = document.querySelector<HTMLSelectElement>("#thinking-select")!;
  let availableModels: ModelInfo[] = [];
  let availableThinkingLevels: string[] = [];
  let currentModel: ModelInfo | null = null;
  let currentThinkingLevel: string | null = null;

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

  async function createSession(name: string): Promise<void> {
    await api("/api/sessions", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ name }),
    });
    sessions.set(name, "running");
  }

  async function refreshSessions(): Promise<void> {
    const res = await api("/api/sessions");
    const list = (await res.json()) as SessionInfo[];
    sessions.clear();
    for (const s of list) sessions.set(s.name, s.status);
  }

  // --- rendering -----------------------------------------------------------
  function statusClass(s: string): string {
    return s === "running" ? "running" : "recycled";
  }

  function renderSessions(): void {
    sessionListEl.textContent = "";
    const names = [...sessions.keys()].sort();
    for (const name of names) {
      const item = el("li", undefined, "session-item");
      if (name === active) item.classList.add("active");
      item.append(el("span", undefined, `dot ${statusClass(sessions.get(name) ?? "")}`));
      const label = el("span", name, "name");
      label.title = name;
      const del = el("button", "×", "del");
      del.title = "Delete session";
      del.addEventListener("click", (e) => {
        e.stopPropagation();
        wsSend({ type: "delete", name });
      });
      item.append(label, del);
      item.addEventListener("click", () => selectSession(name));
      sessionListEl.append(item);
    }
    activeEl.textContent = active ? `session: ${active}` : "";
    recycleBtn.disabled = !active;
    deleteBtn.disabled = !active;
  }

  function selectSession(name: string): void {
    if (name === active) return;
    active = name;
    if (!sessions.has(name)) sessions.set(name, "running");
    renderSessions();
    connect();
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
        }
        break;
      }
      case "set_thinking_level":
        // plain set returns no data; the level we selected is already current.
        renderPickers();
        break;
    }
  }

  function handleFrame(obj: RpcEvent): void {
    if (obj.type === "session_event") {
      const s = obj.session as SessionInfo;
      if (s.status === "deleted") sessions.delete(s.name);
      else sessions.set(s.name, s.status);

      if (s.name === active && s.status === "deleted") fallbackAfterActiveDeleted();
      else renderSessions();
      return;
    }
    if (obj.type === "error") {
      status.set(`⚠ ${String((obj as { message?: string }).message ?? "error")}`);
      return;
    }
    if (obj.type === "result") {
      handleResult(obj as ResultFrame);
      return; // model/thinking pickers only, not a transcript event
    }
    if (obj.type === "agent_start") {
      agentRunning = true;
      aborting = false;
      stopped = false;
      renderAgentState();
    } else if (obj.type === "agent_settled") {
      agentRunning = false;
      aborting = false;
      stopped = false;
      renderAgentState();
    } else if (obj.type === "queue_update") {
      const q = obj as { steering?: unknown[]; followUp?: unknown[] };
      queuedSteer = Array.isArray(q.steering) ? q.steering.length : 0;
      queuedFollow = Array.isArray(q.followUp) ? q.followUp.length : 0;
      renderAgentState();
      return; // queue status only, not a transcript event
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
