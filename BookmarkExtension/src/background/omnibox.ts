/** Omnibox (address-bar) search over browser bookmarks. */
export function registerOmnibox(): void {
  chrome.omnibox.onInputChanged.addListener(async (text, suggest) => {
    try {
      const results = await chrome.bookmarks.search(text);
      const suggestions = results
        .filter((bm) => bm.url !== undefined)
        .slice(0, 5)
        .map((bm) => ({
          content: bm.url!,
          description: escapeHtml(bm.title || bm.url!),
        }));
      suggest(suggestions);
    } catch (error) {
      console.error("[omnibox] failed to search bookmarks:", error);
    }
  });

  chrome.omnibox.onInputEntered.addListener((text, disposition) => {
    if (!text.startsWith("http://") && !text.startsWith("https://")) {
      chrome.bookmarks.search(text, (results) => {
        const match = results.find((bm) => bm.url !== undefined);
        if (match?.url) {
          navigate(match.url, disposition);
        }
      });
    } else {
      navigate(text, disposition);
    }
  });
}

function escapeHtml(str: string): string {
  return str
    .replace(/&/g, "&amp;")
    .replace(/</g, "&lt;")
    .replace(/>/g, "&gt;")
    .replace(/"/g, "&quot;")
    .replace(/'/g, "&#039;");
}

function navigate(url: string, disposition: string): void {
  if (disposition === "currentTab") {
    chrome.tabs.update({ url });
  } else if (disposition === "newForegroundTab") {
    chrome.tabs.create({ url, active: true });
  } else {
    chrome.tabs.create({ url, active: false });
  }
}
