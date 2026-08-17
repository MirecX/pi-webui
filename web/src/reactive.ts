/**
 * A deliberately tiny reactive layer — no framework.
 *
 * - `el()`  : typed DOM element factory
 * - `Signal`: a minimal observable (subscribe / set / get) for live DOM bindings
 *
 * This is the "light reactive layer" chosen in DESIGN.md §9 (locked).
 */
export type ClassMap = Record<string, boolean>;

export function el<K extends keyof HTMLElementTagNameMap>(
  tag: K,
  text?: string,
  cls?: string,
): HTMLElementTagNameMap[K] {
  const node = document.createElement(tag);
  if (text !== undefined) node.textContent = text;
  if (cls) node.className = cls;
  return node;
}

/** Set a class on an element from a lookup map (e.g. {active:true}). */
export function applyClasses(node: HTMLElement, classes: ClassMap): void {
  for (const name of Object.keys(classes)) node.classList.toggle(name, !!classes[name]);
}

/** Minimal observable value used to bind live DOM in one direction. */
export class Signal<T> {
  private value: T;
  private readonly listeners = new Set<(v: T) => void>();

  constructor(initial: T) {
    this.value = initial;
  }

  get(): T {
    return this.value;
  }

  set(next: T): void {
    if (Object.is(next, this.value)) return;
    this.value = next;
    for (const fn of [...this.listeners]) fn(next);
  }

  subscribe(fn: (v: T) => void): () => void {
    this.listeners.add(fn);
    return () => this.listeners.delete(fn);
  }
}

/**
 * Two-way binding between a signal and a textarea/input. Textarea changes push to
 * the signal; external sets update the DOM (unless the element is focused while
 * the user is typing).
 */
export function bindInput(signal: Signal<string>, input: HTMLTextAreaElement | HTMLInputElement): () => void {
  input.value = signal.get();
  const onDom = () => signal.set(input.value);
  input.addEventListener("input", onDom);
  const unsub = signal.subscribe((v) => {
    if (document.activeElement !== input) input.value = v;
  });
  return () => {
    input.removeEventListener("input", onDom);
    unsub();
  };
}

/** Connect a signal to an element's text content (one-way live text). */
export function bindText<T>(signal: Signal<T>, node: HTMLElement): () => void {
  node.textContent = String(signal.get());
  return signal.subscribe((v) => {
    node.textContent = String(v);
  });
}
