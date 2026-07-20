<script>
  let { tags = [], allTags = [], disabled = false, onChange = () => {} } = $props();

  let draft = $state("");

  function commitDraft() {
    const value = draft.trim();
    draft = "";
    if (!value) return;
    if (tags.some((t) => t.toLowerCase() === value.toLowerCase())) return;
    onChange([...tags, value]);
  }

  function removeTag(tag) {
    onChange(tags.filter((t) => t !== tag));
  }

  function handleKeydown(event) {
    if (event.key === "Enter") {
      event.preventDefault();
      commitDraft();
    }
  }
</script>

<div class="tag-editor">
  <div class="chips">
    {#each tags as tag (tag)}
      <span class="chip">
        {tag}
        {#if !disabled}
          <button
            type="button"
            class="chip-remove"
            aria-label={`Remove ${tag}`}
            onclick={() => removeTag(tag)}
          >
            ×
          </button>
        {/if}
      </span>
    {/each}
    {#if tags.length === 0}
      <span class="chip-empty">No tags yet</span>
    {/if}
  </div>

  {#if !disabled}
    <div class="tag-input-row">
      <input
        type="text"
        list="tag-suggestions"
        placeholder="Add a tag…"
        bind:value={draft}
        onkeydown={handleKeydown}
      />
      <datalist id="tag-suggestions">
        {#each allTags as t (t)}
          <option value={t}></option>
        {/each}
      </datalist>
      <button type="button" class="add-btn" onclick={commitDraft}>Add</button>
    </div>
  {/if}
</div>

<style>
  .tag-editor {
    display: flex;
    flex-direction: column;
    gap: 10px;
  }

  .chips {
    display: flex;
    flex-wrap: wrap;
    gap: 7px;
  }

  .chip {
    display: inline-flex;
    align-items: center;
    gap: 2px;
    background: hsl(258 80% 66% / 0.12);
    color: hsl(258 90% 84%);
    border: 1px solid hsl(258 80% 66% / 0.3);
    border-radius: 999px;
    padding: 4px 6px 4px 11px;
    font-size: 12px;
    font-weight: 550;
    transition: background 0.4s cubic-bezier(0.32, 0.72, 0, 1),
      border-color 0.4s cubic-bezier(0.32, 0.72, 0, 1),
      transform 0.4s cubic-bezier(0.32, 0.72, 0, 1);
  }

  .chip:hover {
    background: hsl(258 80% 66% / 0.2);
    border-color: hsl(258 80% 66% / 0.48);
    transform: translateY(-1px);
  }

  .chip-empty {
    font-size: 12px;
    color: hsl(250 10% 44%);
    font-style: italic;
  }

  .chip-remove {
    display: inline-flex;
    align-items: center;
    justify-content: center;
    width: 16px;
    height: 16px;
    background: none;
    border: none;
    color: inherit;
    cursor: pointer;
    font-size: 14px;
    line-height: 1;
    border-radius: 999px;
    opacity: 0.65;
    transition: opacity 0.3s ease, background 0.3s ease;
  }

  .chip-remove:hover {
    opacity: 1;
    background: hsl(258 80% 66% / 0.32);
  }

  .tag-input-row {
    display: flex;
    gap: 7px;
  }

  .tag-input-row input {
    flex: 1;
    min-width: 0;
    background: hsl(250 22% 5% / 0.6);
    border: 1px solid hsl(250 40% 100% / 0.08);
    border-radius: 999px;
    color: hsl(250 22% 96%);
    font-family: inherit;
    font-size: 12.5px;
    padding: 8px 13px;
    outline: none;
    transition: border-color 0.4s cubic-bezier(0.32, 0.72, 0, 1),
      box-shadow 0.4s cubic-bezier(0.32, 0.72, 0, 1);
  }

  .tag-input-row input::placeholder {
    color: hsl(250 10% 44%);
  }

  .tag-input-row input:focus {
    border-color: hsl(258 88% 66% / 0.6);
    box-shadow: 0 0 0 3px hsl(258 88% 66% / 0.18);
  }

  .add-btn {
    flex-shrink: 0;
    background: hsl(250 40% 100% / 0.06);
    border: 1px solid hsl(250 40% 100% / 0.14);
    border-radius: 999px;
    color: hsl(250 22% 96%);
    font-family: inherit;
    font-size: 12.5px;
    font-weight: 600;
    padding: 0 16px;
    cursor: pointer;
    transition: background 0.4s cubic-bezier(0.32, 0.72, 0, 1),
      border-color 0.4s cubic-bezier(0.32, 0.72, 0, 1),
      transform 0.3s cubic-bezier(0.32, 0.72, 0, 1);
  }

  .add-btn:hover {
    background: hsl(258 80% 66% / 0.18);
    border-color: hsl(258 80% 66% / 0.42);
  }

  .add-btn:active {
    transform: scale(0.96);
  }
</style>
