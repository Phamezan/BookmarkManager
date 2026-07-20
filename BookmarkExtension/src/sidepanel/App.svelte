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

  function closePanel() {
    window.close();
  }

  /** Reloads the panel for whichever bookmark the worker now points at.
   *  Used on mount and when the worker signals a new save while open. */
  async function reload() {
    stopPolling();
    loading = true;
    await refreshCurrent();
    await loadTags();
    if (stillIncomplete()) startPolling();
  }

  onMount(async () => {
    const port = chrome.runtime.connect({ name: SIDEPANEL_PORT_NAME });
    port.onMessage.addListener((message) => {
      if (message?.type === "close") {
        window.close();
      } else if (message?.type === "refresh") {
        void reload();
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
    <span class="eyebrow">
      <span class="eyebrow-dot" aria-hidden="true"></span>
      Bookmark saved
    </span>
    <button
      type="button"
      class="close-btn"
      aria-label="Close panel"
      onclick={closePanel}
    >
      <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round">
        <path d="M5 5l14 14M19 5L5 19" />
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
      <div class="card hero">
        <span class="hero-glow" aria-hidden="true"></span>
        {#if enrichment.coverImageUrl}
          <div class="cover-frame">
            <img class="cover" src={enrichment.coverImageUrl} alt="" />
            <div class="cover-fade"></div>
          </div>
        {/if}

        <div class="hero-body">
          <h1 class="title">{enrichment.title}</h1>

          {#if enrichment.url}
            <a class="url-pill" href={enrichment.url} target="_blank" rel="noopener noreferrer">
              <span class="url-dot" aria-hidden="true"></span>
              <span class="url-text">{enrichment.url}</span>
            </a>
          {/if}

          {#if enrichment.folderPath || enrichment.status === PLAN_TO_READ_STATUS}
            <div class="meta-row">
              {#if enrichment.folderPath}
                <div class="folder">
                  <svg class="folder-icon" width="15" height="15" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">
                    <path d="M3 7a2 2 0 0 1 2-2h4l2 2h6a2 2 0 0 1 2 2v7a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2z" />
                  </svg>
                  <span>{enrichment.folderPath}</span>
                </div>
              {/if}
              {#if enrichment.status === PLAN_TO_READ_STATUS}
                <span class="status-pill">Saved for later</span>
              {/if}
            </div>
          {/if}
        </div>
      </div>

      <section class="card tags-section">
        <div class="tags-header">
          <label for="tags">Tags</label>
        </div>
        <div class="tags-core">
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
          {/if}
        </div>
      </section>
    </div>
  {/if}

  <footer class="panel-footer">
    <p>Toggle panel <kbd class="kbd">{shortcutLabel}</kbd></p>
    <p class="footer-dim">Change at brave://extensions/shortcuts</p>
  </footer>
</main>

<style>
  :global(:root) {
    --bg: #060608;
    --card-shell: hsl(250 20% 100% / 0.03);
    --card-core: hsl(250 22% 9% / 0.72);
    --hairline: hsl(250 40% 100% / 0.08);
    --hairline-strong: hsl(250 40% 100% / 0.14);
    --inner-glow: inset 0 1px 0 hsl(0 0% 100% / 0.06);
    --text: hsl(250 22% 96%);
    --text-muted: hsl(250 12% 62%);
    --text-dim: hsl(250 10% 44%);
    --violet: hsl(258 90% 74%);
    --violet-deep: hsl(262 84% 62%);
    --emerald: hsl(162 72% 60%);
    --ease-fluid: cubic-bezier(0.32, 0.72, 0, 1);
    --radius-shell: 22px;
    --radius-core: 16px;
  }

  :global(html),
  :global(body) {
    margin: 0;
    background: var(--bg);
    color: var(--text);
    font-family: "Geist", "Plus Jakarta Sans", "Space Grotesk", ui-sans-serif,
      system-ui, "Segoe UI", sans-serif;
    -webkit-font-smoothing: antialiased;
    text-rendering: optimizeLegibility;
  }

  /* Fixed radial-mesh ambient orbs — GPU-cheap, never scrolls */
  :global(body)::before {
    content: "";
    position: fixed;
    inset: 0;
    z-index: 0;
    pointer-events: none;
    background:
      radial-gradient(52% 34% at 82% -4%, hsl(262 84% 58% / 0.28), transparent 60%),
      radial-gradient(46% 30% at 6% 8%, hsl(200 90% 55% / 0.14), transparent 60%),
      radial-gradient(60% 40% at 50% 108%, hsl(280 80% 55% / 0.12), transparent 62%);
  }

  main {
    position: relative;
    z-index: 1;
    padding: 18px 16px 16px;
    min-height: 100dvh;
    box-sizing: border-box;
    display: flex;
    flex-direction: column;
  }

  .panel-header {
    display: flex;
    align-items: center;
    justify-content: space-between;
    margin-bottom: 18px;
  }

  /* Eyebrow tag */
  .eyebrow {
    display: inline-flex;
    align-items: center;
    gap: 7px;
    font-size: 10px;
    font-weight: 600;
    color: var(--text-muted);
    text-transform: uppercase;
    letter-spacing: 0.2em;
    background: var(--card-shell);
    border: 1px solid var(--hairline);
    border-radius: 999px;
    padding: 5px 11px 5px 9px;
  }

  .eyebrow-dot {
    width: 6px;
    height: 6px;
    border-radius: 999px;
    background: var(--emerald);
    box-shadow: 0 0 8px 1px hsl(162 72% 60% / 0.8);
    animation: breathe 2.6s var(--ease-fluid) infinite;
  }

  @keyframes breathe {
    0%, 100% { opacity: 1; }
    50% { opacity: 0.4; }
  }

  .close-btn {
    display: flex;
    align-items: center;
    justify-content: center;
    width: 30px;
    height: 30px;
    background: var(--card-shell);
    border: 1px solid var(--hairline);
    border-radius: 999px;
    color: var(--text-muted);
    cursor: pointer;
    transition: color 0.4s var(--ease-fluid), background 0.4s var(--ease-fluid),
      transform 0.4s var(--ease-fluid);
  }

  .close-btn:hover {
    color: var(--text);
    background: hsl(250 40% 100% / 0.08);
    transform: rotate(90deg);
  }

  .close-btn:active {
    transform: rotate(90deg) scale(0.92);
  }

  /* Double-bezel card: outer shell + inner core */
  .card {
    background: var(--card-shell);
    border: 1px solid var(--hairline);
    border-radius: var(--radius-shell);
    padding: 6px;
  }

  .skeleton {
    display: flex;
    flex-direction: column;
    gap: 12px;
    padding: 6px;
  }

  .skel-cover {
    width: 100%;
    height: 150px;
    border-radius: var(--radius-core);
    background: linear-gradient(100deg, hsl(250 20% 12%) 30%, hsl(250 18% 18%) 50%, hsl(250 20% 12%) 70%);
    background-size: 200% 100%;
    animation: shimmer 1.6s var(--ease-fluid) infinite;
  }

  .skel-line {
    height: 12px;
    border-radius: 6px;
    background: linear-gradient(100deg, hsl(250 20% 12%) 30%, hsl(250 18% 18%) 50%, hsl(250 20% 12%) 70%);
    background-size: 200% 100%;
    animation: shimmer 1.6s var(--ease-fluid) infinite;
  }

  @keyframes shimmer {
    0% { background-position: 200% 0; }
    100% { background-position: -200% 0; }
  }

  .empty-state {
    color: var(--text-muted);
    font-size: 13px;
    text-align: center;
    padding: 36px 16px;
    background: var(--card-core);
    border: 1px solid var(--hairline);
    border-radius: var(--radius-shell);
  }

  .bookmark {
    display: flex;
    flex-direction: column;
    gap: 14px;
    animation: rise 0.7s var(--ease-fluid) both;
  }

  @keyframes rise {
    from { opacity: 0; transform: translateY(16px); filter: blur(6px); }
    to { opacity: 1; transform: translateY(0); filter: blur(0); }
  }

  /* HERO card */
  .hero {
    position: relative;
    overflow: hidden;
  }

  .hero-glow {
    position: absolute;
    top: -60px;
    right: -40px;
    width: 180px;
    height: 140px;
    background: radial-gradient(closest-side, hsl(262 84% 60% / 0.55), transparent);
    filter: blur(8px);
    pointer-events: none;
  }

  .hero-body {
    position: relative;
    background: var(--card-core);
    border: 1px solid var(--hairline);
    border-radius: var(--radius-core);
    box-shadow: var(--inner-glow);
    padding: 16px 15px 15px;
    display: flex;
    flex-direction: column;
    gap: 12px;
  }

  .cover-frame {
    position: relative;
    width: 100%;
    max-height: 240px;
    border-radius: var(--radius-core);
    overflow: hidden;
    margin-bottom: 12px;
  }

  .cover {
    display: block;
    width: 100%;
    max-width: 100%;
    max-height: 240px;
    object-fit: cover;
  }

  .cover-fade {
    position: absolute;
    inset: 0;
    background: linear-gradient(transparent 55%, hsl(250 22% 6% / 0.8));
    pointer-events: none;
  }

  .title {
    font-size: 20px;
    line-height: 1.25;
    font-weight: 640;
    letter-spacing: -0.02em;
    margin: 0;
    word-break: break-word;
  }

  /* URL as an inset pill */
  .url-pill {
    display: inline-flex;
    align-items: center;
    gap: 8px;
    max-width: 100%;
    font-size: 11.5px;
    color: var(--violet);
    text-decoration: none;
    background: hsl(258 80% 66% / 0.1);
    border: 1px solid hsl(258 80% 66% / 0.22);
    border-radius: 999px;
    padding: 5px 12px 5px 10px;
    width: fit-content;
    transition: background 0.4s var(--ease-fluid), border-color 0.4s var(--ease-fluid);
  }

  .url-pill:hover {
    background: hsl(258 80% 66% / 0.18);
    border-color: hsl(258 80% 66% / 0.4);
  }

  .url-dot {
    flex-shrink: 0;
    width: 6px;
    height: 6px;
    border-radius: 999px;
    background: var(--violet);
    box-shadow: 0 0 7px 0 var(--violet);
  }

  .url-text {
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
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
    gap: 7px;
    font-size: 11.5px;
    color: var(--text-muted);
    background: hsl(250 20% 100% / 0.03);
    border: 1px solid var(--hairline);
    border-radius: 999px;
    padding: 5px 11px;
    width: fit-content;
  }

  .folder-icon {
    flex-shrink: 0;
    color: var(--violet);
  }

  .status-pill {
    display: inline-flex;
    align-items: center;
    font-size: 10.5px;
    font-weight: 600;
    letter-spacing: 0.02em;
    color: var(--emerald);
    background: hsl(162 72% 55% / 0.12);
    border: 1px solid hsl(162 72% 55% / 0.28);
    border-radius: 999px;
    padding: 5px 11px;
    width: fit-content;
  }

  /* TAGS card */
  .tags-section {
    display: flex;
    flex-direction: column;
  }

  .tags-header {
    display: flex;
    align-items: center;
    justify-content: space-between;
    gap: 8px;
    padding: 6px 8px 12px 10px;
  }

  .tags-core {
    background: var(--card-core);
    border: 1px solid var(--hairline);
    border-radius: var(--radius-core);
    box-shadow: var(--inner-glow);
    padding: 13px;
    display: flex;
    flex-direction: column;
    gap: 10px;
  }

  .tags-header label {
    font-size: 10px;
    font-weight: 600;
    color: var(--text-muted);
    text-transform: uppercase;
    letter-spacing: 0.2em;
  }

  .hint {
    font-size: 11.5px;
    color: var(--text-dim);
    margin: 0;
  }

  .hint.saving { color: hsl(40 92% 66%); }
  .hint.saved { color: var(--emerald); }
  .hint.error { color: hsl(2 80% 70%); }

  .panel-footer {
    margin-top: 18px;
    padding: 14px 6px 4px;
    border-top: 1px solid var(--hairline);
    display: flex;
    flex-direction: column;
    align-items: center;
    gap: 6px;
    text-align: center;
  }

  .panel-footer p {
    margin: 0;
    font-size: 12px;
    color: var(--text-muted);
    display: flex;
    align-items: center;
    justify-content: center;
    gap: 6px;
  }

  .footer-dim {
    color: var(--text-dim) !important;
  }

  .kbd {
    font-family: ui-monospace, "SFMono-Regular", "Cascadia Code", monospace;
    font-size: 10px;
    color: var(--text);
    background: hsl(250 20% 100% / 0.05);
    border: 1px solid var(--hairline-strong);
    border-bottom-width: 2px;
    border-radius: 7px;
    padding: 2px 7px;
    line-height: 1.4;
  }

  @media (prefers-reduced-motion: reduce) {
    *, ::before, ::after {
      animation-duration: 0.01ms !important;
      animation-iteration-count: 1 !important;
      transition-duration: 0.01ms !important;
    }
  }
</style>
