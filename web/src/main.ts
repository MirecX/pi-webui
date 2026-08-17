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
  /** Rows created by tool_execution_start, keyed by toolCallId so updates and
   * bash output can land in the same row instead of "the last row". */
  private readonly toolRows = new Map<string, HTMLElement>();

  constructor(root: HTMLElement) {
    this.root = root;
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
        // Bash output renders inside the same row as its bash tool call (correlated
        // by toolCallId/id); fall back to the last tool row / dedicated bash row.
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

  const input = document.querySelector<HTMLTextAreaElement>("#prompt")!;
  const send = document.querySelector<HTMLButtonElement>("#send")!;
  const draft = new Signal<string>("");
  const sendBox = document.querySelector<HTMLFormElement>("#composer")!;

  input.addEventListener("input", () => draft.set(input.value));
  const submit = (): void => {
    const text = input.value.trim();
    if (!text) return;
    wsSend({ type: "prompt", message: text });
    input.value = "";
    draft.set("");
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

  // --- WebSocket ----------------------------------------------------------
  const proto = location.protocol === "https:" ? "wss:" : "ws:";
  // Ticket #02: the service gate-keeps every request behind the config token. Take
  // it from the page URL (http://host:port/?token=…) or the cookie the server set
  // after that first authenticated hit, and send it on the WS handshake.
  const token = readToken();
  const wsUrl = `${proto}//${location.host}/ws${token ? `?token=${encodeURIComponent(token)}` : ""}`;
  let ws: WebSocket | null = null;

  function wsSend(obj: unknown): void {
    if (ws?.readyState === WebSocket.OPEN) ws.send(JSON.stringify(obj));
  }

  function connect(): void {
    status.set("connecting…");
    ws = new WebSocket(wsUrl);
    ws.onopen = () => status.set("● connected");
    ws.onmessage = (evt) => {
      try {
        const event = JSON.parse(String(evt.data)) as RpcEvent;
        transcript.handle(event);
      } catch {
        /* ignore malformed frames */
      }
    };
    ws.onclose = () => {
      status.set("disconnected — retrying…");
      setTimeout(connect, 1000);
    };
    ws.onerror = () => ws?.close();
  }
  connect();
}

if (document.readyState === "loading") document.addEventListener("DOMContentLoaded", setup);
else setup();
