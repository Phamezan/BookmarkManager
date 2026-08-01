/**
 * Minimal fake DOM used to unit-test small DOM-building functions in a plain
 * "node" vitest environment (this repo has no jsdom/happy-dom dependency).
 * Implements only the surface `buildPaletteErrorMessage` needs:
 * createElement, createTextNode, appendChild, className, textContent.
 */

export class FakeNode {
  parentNode: FakeNode | null = null;
}

export class FakeTextNode extends FakeNode {
  constructor(public textContent: string) {
    super();
  }
}

export class FakeElement extends FakeNode {
  className = "";
  private children: FakeNode[] = [];

  constructor(public tagName: string) {
    super();
  }

  appendChild<T extends FakeNode>(child: T): T {
    child.parentNode = this;
    this.children.push(child);
    return child;
  }

  set textContent(value: string) {
    this.children = [new FakeTextNode(value)];
  }

  get textContent(): string {
    return this.children
      .map((child) => (child instanceof FakeElement ? child.textContent : (child as FakeTextNode).textContent))
      .join("");
  }

  // No child element is ever created with a matching tag by the code under
  // test, so a real query engine isn't needed — always report "not found".
  querySelector(_selector: string): FakeElement | null {
    return null;
  }
}

export class FakeDocument {
  body = new FakeElement("body");

  createElement(tagName: string): FakeElement {
    return new FakeElement(tagName);
  }

  createTextNode(text: string): FakeTextNode {
    return new FakeTextNode(text);
  }
}

/** Installs a fresh fake `document` (and a no-op `window`/`chrome`) on `globalThis`. */
export function installFakeDom(): void {
  (globalThis as Record<string, unknown>).document = new FakeDocument();
  (globalThis as Record<string, unknown>).window = {
    parent: { postMessage: () => undefined },
    addEventListener: () => undefined,
    location: { search: "" },
  };
  (globalThis as Record<string, unknown>).chrome = {
    runtime: {
      sendMessage: async () => ({ paletteBaseUrl: null }),
    },
  };
}
