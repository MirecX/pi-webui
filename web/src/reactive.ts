/**
 * A deliberately tiny reactive layer — no framework.
 *
 * - `el()`  : typed DOM element factory
 * - `Signal`: a minimal observable (subscribe / set / get) for live DOM bindings
 *
 * This is the "light reactive layer" chosen in DESIGN.md §9 (locked).
 */
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
