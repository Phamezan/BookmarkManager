/** Short badge flash on the extension action icon. */
export function showBadge(text: string, color: string): void {
  chrome.action.setBadgeText({ text }).catch(() => {});
  chrome.action.setBadgeBackgroundColor({ color }).catch(() => {});
  setTimeout(() => {
    chrome.action.setBadgeText({ text: "" }).catch(() => {});
  }, 2000);
}

/**
 * Opens the extension popup when supported. Falls back to a short badge
 * flash when openPopup is unavailable or rejected by the host browser.
 */
export async function openPopupOrBadge(): Promise<void> {
  try {
    const action = chrome.action as typeof chrome.action & {
      openPopup?: (() => Promise<void>) | undefined;
    };
    if (typeof action.openPopup === "function") {
      await action.openPopup();
      return;
    }
  } catch (e) {
    console.warn("[worker] openPopup unavailable, using badge fallback", e);
  }
  showBadge("✓", "#10B981");
}
