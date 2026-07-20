/**
 * Fallback toast page shown in a small Brave popup window when in-page
 * injection is unavailable (no host permission / restricted URL).
 */

const HOLD_MS = 5200;

const params = new URLSearchParams(window.location.search);
const title = params.get("title") ?? "Bookmark saved";
const folder = params.get("folder");
const lines = params.getAll("line");

const titleEl = document.getElementById("title");
const folderEl = document.getElementById("folder");
const folderRow = document.getElementById("folder-row");
const toastEl = document.getElementById("toast");
if (titleEl) {
  titleEl.textContent = title;
}
if (folder && folderEl && folderRow) {
  folderEl.textContent = folder;
  folderRow.hidden = false;
} else if (folderRow) {
  folderRow.hidden = true;
}
if (toastEl) {
  for (const line of lines) {
    const el = document.createElement("div");
    if (line === "Saved for later" || line.startsWith("Saved for later")) {
      el.className = "line-later";
    } else if (line.startsWith("Tags:")) {
      el.className = "line-tags";
    } else {
      el.className = "line";
    }
    el.textContent = line;
    toastEl.appendChild(el);
  }
}

window.setTimeout(() => {
  window.close();
}, HOLD_MS);
