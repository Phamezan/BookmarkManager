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
    gap: 8px;
  }

  .chips {
    display: flex;
    flex-wrap: wrap;
    gap: 6px;
  }

  .chip {
    display: inline-flex;
    align-items: center;
    gap: 4px;
    background: hsl(212 80% 58% / 0.14);
    color: hsl(212 80% 72%);
    border: 1px solid hsl(212 80% 58% / 0.3);
    border-radius: 999px;
    padding: 3px 6px 3px 10px;
    font-size: 12px;
    font-weight: 500;
  }

  .chip-empty {
    font-size: 12px;
    color: hsl(220 8% 40%);
  }

  .chip-remove {
    background: none;
    border: none;
    color: inherit;
    cursor: pointer;
    font-size: 14px;
    line-height: 1;
    padding: 0 2px;
    opacity: 0.75;
  }

  .chip-remove:hover {
    opacity: 1;
  }

  .tag-input-row {
    display: flex;
    gap: 6px;
  }

  .tag-input-row input {
    flex: 1;
    background: hsl(220 16% 10%);
    border: 1px solid hsl(220 12% 22%);
    border-radius: 6px;
    color: hsl(220 15% 92%);
    font-family: inherit;
    font-size: 12.5px;
    padding: 7px 10px;
    outline: none;
  }

  .tag-input-row input:focus {
    border-color: hsl(212 80% 58%);
    box-shadow: 0 0 0 3px hsl(212 80% 58% / 0.25);
  }

  .add-btn {
    background: hsl(212 80% 58%);
    border: none;
    border-radius: 6px;
    color: #fff;
    font-family: inherit;
    font-size: 12.5px;
    font-weight: 600;
    padding: 0 12px;
    cursor: pointer;
  }

  .add-btn:hover {
    background: hsl(212 60% 45%);
  }
</style>
