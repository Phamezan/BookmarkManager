<script>
  import { onMount, onDestroy } from "svelte";
  import TagEditor from "./TagEditor.svelte";
  import { SIDEPANEL_PORT_NAME } from "./sidepanel-port";

  const POLL_INTERVAL_MS = 1500;
  const POLL_TIMEOUT_MS = 15000;
  const PLAN_TO_READ_STATUS = "PlanToRead";
  const FOLDER_ICON_PATH =
    "M10 4H4c-1.1 0-2 .9-2 2v12c0 1.1.9 2 2 2h16c1.1 0 2-.9 2-2V8c0-1.1-.9-2-2-2h-8l-2-2z";

  let loading = $state(true);
  let loadError = $state(null);
  let enrichment = $state(null);
  let allTags = $state([]);
  let saveState = $state("idle"); // idle | saving | saved | error
  let autotagState = $state("idle"); // idle | busy | error
  let shortcutLabel = $state("");

  let pollTimer = null;
  let pollDeadline = 0;

  function sendMessage(message) {
    return chrome.runtime.sendMessage(message);
  }

  async function refreshCurrent() {
    try {
      const result = await sendMessage({ type: "sidepanel/getCurrent" });
      enrichment = result ?? null;
      loadError = enrichment ? null : "No bookmark to show yet.";
    } catch (e) {
      loadError = e instanceof Error ? e.message : String(e);
    } finally {
      loading = false;
    }
  }

  async function loadTags() {
    try {
      const result = await sendMessage({ type: "sidepanel/getTags" });
      allTags = (result ?? []).map((t) => t.tag);
    } catch {
      allTags = [];
    }
  }

  async function loadShortcut() {
    try {
      const commands = await chrome.commands.getAll();
      const binding = commands.find((c) => c.name === "toggle-sidepanel");
      const shortcut = binding?.shortcut?.trim();
      shortcutLabel = shortcut && shortcut.length > 0 ? shortcut : "not set";
    } catch {
      shortcutLabel = "not set";
    }
  }

  function stillIncomplete() {
    if (!enrichment) return true;
    const hasTags = (enrichment.tags ?? []).length > 0;
    const hasCover = Boolean(enrichment.coverImageUrl);
    return !hasTags && !hasCover;
  }

  function startPolling() {
    pollDeadline = Date.now() + POLL_TIMEOUT_MS;
    pollTimer = setInterval(async () => {
      if (!stillIncomplete() || Date.now() >= pollDeadline) {
        stopPolling();
        return;
      }
      await refreshCurrent();
    }, POLL_INTERVAL_MS);
  }

  function stopPolling() {
    if (pollTimer !== null) {
      clearInterval(pollTimer);
      pollTimer = null;
    }
  }

  async function handleTagsChange(tags) {
    if (!enrichment) return;
    enrichment = { ...enrichment, tags };
    if (!enrichment.id) return;
    saveState = "saving";
    try {
      await sendMessage({
        type: "sidepanel/saveTags",
        serverId: enrichment.id,
        tags,
      });
      saveState = "saved";
      setTimeout(() => {
        if (saveState === "saved") saveState = "idle";
      }, 1500);
    } catch {
      saveState = "error";
    }
  }

  /** Case-insensitive union that keeps existing chip order and appends any
   *  genuinely new suggestions in the order the server returned them. */
  function mergeTags(current, suggested) {
    const seen = new Set(current.map((t) => t.toLowerCase()));
    const merged = [...current];
    for (const tag of suggested) {
      const key = tag.toLowerCase();
      if (seen.has(key)) continue;
      seen.add(key);
      merged.push(tag);
    }
    return merged;
  }

  async function handleAutotag() {
    if (!enrichment?.id || autotagState === "busy") return;
    autotagState = "busy";
    try {
      const suggestions = await sendMessage({
        type: "sidepanel/aiRetag",
        serverId: enrichment.id,
      });
      const merged = mergeTags(enrichment.tags ?? [], suggestions ?? []);
      autotagState = "idle";
      await handleTagsChange(merged);
    } catch {
      autotagState = "error";
    }
  }

  function closePanel() {
    window.close();
  }

  onMount(async () => {
    const port = chrome.runtime.connect({ name: SIDEPANEL_PORT_NAME });
    port.onMessage.addListener((message) => {
      if (message?.type === "close") {
        window.close();
      }
    });

    await refreshCurrent();
    await loadTags();
    await loadShortcut();
    if (stillIncomplete()) startPolling();
  });

  onDestroy(() => {
    stopPolling();
  });
</script>

<main>
  <div class="panel-header">
    <span class="panel-header-label">Bookmark saved</span>
    <button
      type="button"
      class="close-btn"
      aria-label="Close panel"
      onclick={closePanel}
    >
      <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round">
        <path d="M4 4l16 16M20 4L4 20" />
      </svg>
    </button>
  </div>

  {#if loading}
    <div class="skeleton">
      <div class="skel-cover"></div>
      <div class="skel-line" style="width: 70%"></div>
      <div class="skel-line" style="width: 45%"></div>
    </div>
  {:else if !enrichment}
    <p class="empty-state">{loadError ?? "No bookmark to show yet."}</p>
  {:else}
    <div class="bookmark">
      {#if enrichment.coverImageUrl}
        <div class="cover-frame">
          <img class="cover" src={enrichment.coverImageUrl} alt="" />
          <div class="cover-fade"></div>
        </div>
      {/if}

      <h1 class="title">{enrichment.title}</h1>

      {#if enrichment.url}
        <a class="url" href={enrichment.url} target="_blank" rel="noopener noreferrer">
          {enrichment.url}
        </a>
      {/if}

      {#if enrichment.folderPath || enrichment.status === PLAN_TO_READ_STATUS}
        <div class="meta-row">
          {#if enrichment.folderPath}
            <div class="folder">
              <svg class="folder-icon" width="16" height="16" viewBox="0 0 24 24" aria-hidden="true">
                <path d={FOLDER_ICON_PATH} />
              </svg>
              <span>{enrichment.folderPath}</span>
            </div>
          {/if}
          {#if enrichment.status === PLAN_TO_READ_STATUS}
            <span class="status-pill">Saved for later</span>
          {/if}
        </div>
      {/if}

      <section class="tags-section">
        <div class="tags-header">
          <label for="tags">Tags</label>
          <button
            type="button"
            class="btn-primary autotag-btn"
            disabled={!enrichment.id || autotagState === "busy"}
            onclick={handleAutotag}
          >
            {autotagState === "busy" ? "Autotagging…" : "Autotag"}
          </button>
        </div>
        <TagEditor
          tags={enrichment.tags ?? []}
          {allTags}
          disabled={!enrichment.id}
          onChange={handleTagsChange}
        />
        {#if !enrichment.id}
          <p class="hint">Tag editing unlocks once the bookmark finishes syncing.</p>
        {:else if saveState === "saving"}
          <p class="hint saving">Saving…</p>
        {:else if saveState === "saved"}
          <p class="hint saved">Saved</p>
        {:else if saveState === "error"}
          <p class="hint error">Failed to save — try again.</p>
        {:else if autotagState === "error"}
          <p class="hint error">Autotag failed — try again.</p>
        {/if}
      </section>
    </div>
  {/if}

  <footer class="panel-footer">
    <p>Toggle panel: {shortcutLabel}</p>
    <p>Change at brave://extensions/shortcuts</p>
  </footer>
</main>

<style>
  :global(html),
  :global(body) {
    margin: 0;
    background: hsl(220 16% 10%);
    color: hsl(220 15% 92%);
    font-family: "Inter", system-ui, -apple-system, "Segoe UI", Roboto, sans-serif;
  }

  main {
    padding: 16px;
    min-height: 100vh;
    box-sizing: border-box;
    display: flex;
    flex-direction: column;
  }

  .panel-header {
    display: flex;
    align-items: center;
    justify-content: space-between;
    margin-bottom: 14px;
  }

  .panel-header-label {
    font-size: 11px;
    font-weight: 500;
    color: hsl(220 10% 55%);
    text-transform: uppercase;
    letter-spacing: 0.04em;
  }

  .close-btn {
    display: flex;
    align-items: center;
    justify-content: center;
    width: 26px;
    height: 26px;
    background: transparent;
    border: none;
    border-radius: 8px;
    color: hsl(220 10% 55%);
    cursor: pointer;
    transition: color 0.18s ease, background 0.18s ease;
  }

  .close-btn:hover {
    color: hsl(220 15% 92%);
    background: hsl(220 14% 17%);
  }

  .skeleton {
    display: flex;
    flex-direction: column;
    gap: 10px;
  }

  .skel-cover {
    width: 100%;
    height: 160px;
    border-radius: 12px;
    background: hsl(220 14% 17%);
    animation: pulse 1.4s ease-in-out infinite;
  }

  .skel-line {
    height: 12px;
    border-radius: 6px;
    background: hsl(220 14% 17%);
    animation: pulse 1.4s ease-in-out infinite;
  }

  @keyframes pulse {
    0%, 100% { opacity: 0.6; }
    50% { opacity: 1; }
  }

  .empty-state {
    color: hsl(220 10% 55%);
    font-size: 13px;
  }

  .bookmark {
    display: flex;
    flex-direction: column;
    gap: 14px;
  }

  .cover-frame {
    position: relative;
    width: 100%;
    max-height: 260px;
    border-radius: 12px;
    overflow: hidden;
  }

  .cover {
    display: block;
    width: 100%;
    max-width: 100%;
    max-height: 260px;
    object-fit: cover;
  }

  .cover-fade {
    position: absolute;
    inset: 0;
    background: linear-gradient(transparent 60%, hsl(220 16% 10% / 0.85));
    pointer-events: none;
  }

  .title {
    font-size: 18px;
    line-height: 1.3;
    font-weight: 650;
    letter-spacing: -0.01em;
    margin: 0;
    word-break: break-word;
  }

  .url {
    display: block;
    font-size: 12px;
    color: hsl(212 80% 68%);
    text-decoration: none;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }

  .url:hover {
    text-decoration: underline;
  }

  .meta-row {
    display: flex;
    align-items: center;
    gap: 8px;
    flex-wrap: wrap;
  }

  .folder {
    display: inline-flex;
    align-items: center;
    gap: 6px;
    font-size: 12px;
    color: hsl(220 10% 55%);
    background: hsl(220 14% 14%);
    border: 1px solid hsl(220 12% 22%);
    border-radius: 8px;
    padding: 6px 10px;
    width: fit-content;
  }

  .folder-icon {
    flex-shrink: 0;
    fill: #a5b4fc;
  }

  .status-pill {
    display: inline-flex;
    align-items: center;
    font-size: 11px;
    font-weight: 600;
    color: #7dd3fc;
    background: hsl(199 89% 64% / 0.14);
    border-radius: 999px;
    padding: 4px 10px;
    width: fit-content;
  }

  .tags-section {
    display: flex;
    flex-direction: column;
    gap: 8px;
    margin-top: 0;
    padding-top: 14px;
    border-top: 1px solid hsl(220 12% 22%);
  }

  .tags-header {
    display: flex;
    align-items: center;
    justify-content: space-between;
    gap: 8px;
  }

  .tags-header label {
    font-size: 11px;
    font-weight: 500;
    color: hsl(220 10% 55%);
    text-transform: uppercase;
    letter-spacing: 0.04em;
  }

  .btn-primary {
    background: hsl(212 80% 58%);
    border: none;
    border-radius: 8px;
    color: #fff;
    font-family: inherit;
    font-size: 12px;
    font-weight: 600;
    padding: 6px 12px;
    cursor: pointer;
    transition: background 0.18s ease, box-shadow 0.18s ease;
  }

  .btn-primary:hover:not(:disabled) {
    background: hsl(212 60% 45%);
    box-shadow: 0 2px 8px hsl(212 80% 58% / 0.25);
  }

  .btn-primary:disabled {
    opacity: 0.4;
    cursor: not-allowed;
    box-shadow: none;
  }

  .autotag-btn {
    flex-shrink: 0;
    white-space: nowrap;
  }

  .hint {
    font-size: 11.5px;
    color: hsl(220 8% 40%);
    margin: 2px 0 0;
  }

  .hint.saving { color: hsl(38 90% 55%); }
  .hint.saved { color: hsl(145 60% 45%); }
  .hint.error { color: hsl(0 65% 55%); }

  .panel-footer {
    margin-top: 14px;
    padding-top: 12px;
    border-top: 1px solid hsl(220 12% 22%);
    display: flex;
    flex-direction: column;
    gap: 2px;
  }

  .panel-footer p {
    margin: 0;
    font-size: 11px;
    color: hsl(220 8% 40%);
  }
</style>
