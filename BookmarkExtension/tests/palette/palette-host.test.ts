import { describe, expect, it } from "vitest";
import { installFakeDom } from "../helpers/fake-document";

// palette-host.ts calls `void bootstrap()` at module scope, which touches
// `document`, `window`, and `chrome`. Install a minimal fake DOM/chrome
// before importing so the module can load in vitest's plain "node"
// environment (this repo has no jsdom/happy-dom dependency). bootstrap()
// then short-circuits immediately because the faked config has no
// paletteBaseUrl, matching the file's existing "no palette base URL
// configured" path.
installFakeDom();
const { buildPaletteErrorMessage } = await import("../../src/palette/palette-host");

describe("buildPaletteErrorMessage", () => {
  it("shows a failure message naming the origin when the palette frame does not load", () => {
    const element = buildPaletteErrorMessage("https://bookmarks.lan:8443");

    expect(element.textContent).toContain("https://bookmarks.lan:8443");
  });

  it("mentions checking server reachability and the TLS certificate", () => {
    const element = buildPaletteErrorMessage("https://bookmarks.lan:8443");

    expect(element.textContent).toMatch(/reachable/i);
    expect(element.textContent).toMatch(/certificate/i);
  });

  it("builds the message with DOM APIs rather than innerHTML interpolation", () => {
    const maliciousOrigin = "<img src=x onerror=alert(1)>";

    const element = buildPaletteErrorMessage(maliciousOrigin);

    // If the origin were ever interpolated via innerHTML, this would parse
    // into a real <img> element instead of remaining literal text.
    expect(element.querySelector("img")).toBeNull();
    expect(element.textContent).toContain(maliciousOrigin);
  });

  it("renders a container element with the host error class for styling", () => {
    const element = buildPaletteErrorMessage("https://bookmarks.lan");

    expect(element.className).toContain("bm-palette-error");
  });
});
