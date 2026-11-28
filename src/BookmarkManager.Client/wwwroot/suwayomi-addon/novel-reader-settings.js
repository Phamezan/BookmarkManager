import { el, button, storageGet, storageSet } from './explore-util.js?v=3';

export const SETTINGS_KEY = 'novel.readerSettings.v2';
const LEGACY_KEY = 'novel.readerSettings';

export const READER_THEMES = [
  // Light themes
  { id: 'white', label: 'White', group: 'light', bg: '#FFFFFF', fg: '#18181B', muted: '#52525B', surface: 'rgba(255, 255, 255, .94)', surfaceSolid: '#F4F4F5', border: 'rgba(24, 24, 27, .12)', accent: '#18181B', accentFg: '#FFFFFF' },
  { id: 'paper', label: 'Paper', group: 'light', bg: '#FBF8F1', fg: '#26231D', muted: '#6B6456', surface: 'rgba(246, 241, 230, .94)', surfaceSolid: '#F3EDE0', border: 'rgba(38, 35, 29, .14)', accent: '#26231D', accentFg: '#FBF8F1' },
  { id: 'sepia', label: 'Sepia', group: 'light', bg: '#F4ECD8', fg: '#3B2F22', muted: '#6E5D4B', surface: 'rgba(239, 227, 198, .94)', surfaceSolid: '#EBE0C5', border: 'rgba(59, 47, 34, .16)', accent: '#3B2F22', accentFg: '#F4ECD8' },
  { id: 'warm', label: 'Warm', group: 'light', bg: '#F7EFE5', fg: '#33271E', muted: '#705E50', surface: 'rgba(240, 230, 217, .94)', surfaceSolid: '#EFE3D4', border: 'rgba(51, 39, 30, .14)', accent: '#33271E', accentFg: '#F7EFE5' },
  { id: 'gray', label: 'Gray', group: 'light', bg: '#ECECED', fg: '#1F2024', muted: '#5C5E66', surface: 'rgba(229, 229, 232, .94)', surfaceSolid: '#E2E2E6', border: 'rgba(31, 32, 36, .14)', accent: '#1F2024', accentFg: '#ECECED' },
  { id: 'slate', label: 'Slate', group: 'light', bg: '#E6EAEE', fg: '#1E252D', muted: '#52606F', surface: 'rgba(219, 225, 232, .94)', surfaceSolid: '#DBE2E9', border: 'rgba(30, 37, 45, .14)', accent: '#1E252D', accentFg: '#E6EAEE' },
  { id: 'mint', label: 'Mint', group: 'light', bg: '#EAF4EE', fg: '#1D3026', muted: '#4E6B5B', surface: 'rgba(221, 237, 228, .94)', surfaceSolid: '#DCECE2', border: 'rgba(29, 48, 38, .14)', accent: '#1D3026', accentFg: '#EAF4EE' },
  { id: 'sky', label: 'Sky', group: 'light', bg: '#EAF2F8', fg: '#1C2A36', muted: '#4F6577', surface: 'rgba(221, 234, 244, .94)', surfaceSolid: '#DCEAF4', border: 'rgba(28, 42, 54, .14)', accent: '#1C2A36', accentFg: '#EAF2F8' },
  { id: 'rose', label: 'Rose', group: 'light', bg: '#F8ECEE', fg: '#351F24', muted: '#76545B', surface: 'rgba(243, 225, 229, .94)', surfaceSolid: '#F2DFE3', border: 'rgba(53, 31, 36, .14)', accent: '#351F24', accentFg: '#F8ECEE' },
  { id: 'lavender', label: 'Lavender', group: 'light', bg: '#F1EDF8', fg: '#282236', muted: '#625878', surface: 'rgba(232, 226, 243, .94)', surfaceSolid: '#E7E0F3', border: 'rgba(40, 34, 54, .14)', accent: '#282236', accentFg: '#F1EDF8' },
  // Dark themes
  { id: 'charcoal', label: 'Charcoal', group: 'dark', bg: '#121214', fg: '#EAEAEF', muted: 'rgba(234, 234, 239, .62)', surface: 'rgba(24, 24, 28, .92)', surfaceSolid: '#1C1C20', border: 'rgba(255, 255, 255, .12)', accent: '#F4F4F5', accentFg: '#121214' },
  { id: 'dim', label: 'Dim', group: 'dark', bg: '#1E2024', fg: '#E4E6EB', muted: 'rgba(228, 230, 235, .64)', surface: 'rgba(38, 41, 47, .92)', surfaceSolid: '#272A30', border: 'rgba(255, 255, 255, .12)', accent: '#F4F4F5', accentFg: '#1E2024' },
  { id: 'night', label: 'Night', group: 'dark', bg: '#080809', fg: '#D8D8DC', muted: 'rgba(216, 216, 220, .58)', surface: 'rgba(18, 18, 20, .94)', surfaceSolid: '#141416', border: 'rgba(255, 255, 255, .11)', accent: '#F4F4F5', accentFg: '#080809' },
  { id: 'mocha', label: 'Mocha', group: 'dark', bg: '#1C1714', fg: '#EBE0D6', muted: 'rgba(235, 224, 214, .64)', surface: 'rgba(39, 32, 27, .94)', surfaceSolid: '#2A221D', border: 'rgba(235, 224, 214, .14)', accent: '#F3EAE1', accentFg: '#1C1714' },
  { id: 'forest', label: 'Forest', group: 'dark', bg: '#101A15', fg: '#DCECE2', muted: 'rgba(220, 236, 226, .64)', surface: 'rgba(22, 36, 29, .94)', surfaceSolid: '#182820', border: 'rgba(220, 236, 226, .14)', accent: '#E5F3EA', accentFg: '#101A15' },
  { id: 'ocean', label: 'Ocean', group: 'dark', bg: '#10181F', fg: '#DCE8F0', muted: 'rgba(220, 232, 240, .64)', surface: 'rgba(22, 33, 43, .94)', surfaceSolid: '#17232E', border: 'rgba(220, 232, 240, .14)', accent: '#E6F0F7', accentFg: '#10181F' }
];

const THEME_MAP = new Map(READER_THEMES.map(t => [t.id, t]));

export const READER_FONTS = [
  { id: 'montserrat', label: 'Montserrat', css: 'Montserrat, Inter, system-ui, sans-serif' },
  { id: 'literata', label: 'Literata', css: 'Literata, Georgia, "Times New Roman", serif' },
  { id: 'inter', label: 'Inter', css: 'Inter, "Segoe UI Variable", Roboto, system-ui, sans-serif' },
  { id: 'georgia', label: 'Georgia', css: 'Georgia, "Times New Roman", Cambria, serif' },
  { id: 'system-serif', label: 'System serif', css: 'ui-serif, Georgia, Cambria, "Times New Roman", Times, serif' }
];

const FONT_MAP = new Map(READER_FONTS.map(f => [f.id, f]));

export const SPACING_OPTIONS = [
  { id: 'compact', label: 'Compact', value: 1.45 },
  { id: 'normal', label: 'Normal', value: 1.75 },
  { id: 'relaxed', label: 'Relaxed', value: 2.05 }
];

export const WIDTH_OPTIONS = [
  { id: 'auto', label: 'Auto', value: '740px' },
  { id: 'narrow', label: 'Narrow', value: '580px' },
  { id: 'medium', label: 'Medium', value: '740px' },
  { id: 'wide', label: 'Wide', value: '920px' }
];

export const ALIGN_OPTIONS = [
  { id: 'left', label: 'Left' },
  { id: 'center', label: 'Center' },
  { id: 'right', label: 'Right' },
  { id: 'justify', label: 'Justify' }
];

export const DEFAULT_SETTINGS = {
  theme: 'charcoal',
  size: 20,
  font: 'literata',
  spacing: 'normal',
  width: 'auto',
  align: 'left',
  wakeLock: false,
  progressBar: true,
  autoScroll: false,
  autoScrollSpeed: 3
};

function clampInt(val, min, max, fallback) {
  const n = Math.round(Number(val));
  if (!Number.isFinite(n)) return fallback;
  return Math.min(max, Math.max(min, n));
}

function sanitizeSettings(raw) {
  if (!raw || typeof raw !== 'object') return { ...DEFAULT_SETTINGS };
  return {
    theme: THEME_MAP.has(raw.theme) ? raw.theme : DEFAULT_SETTINGS.theme,
    size: clampInt(raw.size, 14, 32, DEFAULT_SETTINGS.size),
    font: FONT_MAP.has(raw.font) ? raw.font : DEFAULT_SETTINGS.font,
    spacing: SPACING_OPTIONS.some(o => o.id === raw.spacing) ? raw.spacing : DEFAULT_SETTINGS.spacing,
    width: WIDTH_OPTIONS.some(o => o.id === raw.width) ? raw.width : DEFAULT_SETTINGS.width,
    align: ALIGN_OPTIONS.some(o => o.id === raw.align) ? raw.align : DEFAULT_SETTINGS.align,
    wakeLock: Boolean(raw.wakeLock),
    progressBar: raw.progressBar === undefined ? DEFAULT_SETTINGS.progressBar : Boolean(raw.progressBar),
    autoScroll: Boolean(raw.autoScroll),
    autoScrollSpeed: clampInt(raw.autoScrollSpeed, 1, 10, DEFAULT_SETTINGS.autoScrollSpeed)
  };
}

function migrateLegacySettings(raw) {
  const themeMap = { light: 'white', dark: 'charcoal', sepia: 'sepia' };
  const fontMap = { sans: 'inter', serif: 'georgia' };
  const lh = Number(raw.lineHeight);
  const spacing = Number.isFinite(lh)
    ? (lh <= 1.6 ? 'compact' : lh >= 1.9 ? 'relaxed' : 'normal')
    : DEFAULT_SETTINGS.spacing;
  const w = Number(raw.width);
  const width = Number.isFinite(w)
    ? (w <= 640 ? 'narrow' : w >= 820 ? 'wide' : 'medium')
    : DEFAULT_SETTINGS.width;

  return sanitizeSettings({
    theme: themeMap[raw.theme] || DEFAULT_SETTINGS.theme,
    font: fontMap[raw.font] || DEFAULT_SETTINGS.font,
    size: raw.size ?? DEFAULT_SETTINGS.size,
    spacing,
    width
  });
}

export function loadReaderSettings() {
  try {
    const rawV2 = storageGet(SETTINGS_KEY);
    if (rawV2) return sanitizeSettings(JSON.parse(rawV2));
  } catch { /* fall through */ }

  try {
    const rawV1 = storageGet(LEGACY_KEY);
    if (rawV1) {
      const migrated = migrateLegacySettings(JSON.parse(rawV1));
      saveReaderSettings(migrated);
      return migrated;
    }
  } catch { /* ignore */ }

  return { ...DEFAULT_SETTINGS };
}

export function saveReaderSettings(settings) {
  try {
    storageSet(SETTINGS_KEY, JSON.stringify(sanitizeSettings(settings)));
  } catch { /* storage unavailable */ }
}

export function applyReaderSettings(settings) {
  const theme = THEME_MAP.get(settings.theme) || THEME_MAP.get('charcoal');
  const font = FONT_MAP.get(settings.font) || FONT_MAP.get('literata');
  const spacing = SPACING_OPTIONS.find(o => o.id === settings.spacing) || SPACING_OPTIONS[1];
  const width = WIDTH_OPTIONS.find(o => o.id === settings.width) || WIDTH_OPTIONS[0];

  document.body.classList.add('is-reading');
  document.body.setAttribute('data-reader-theme', theme.id);
  document.body.setAttribute('data-reader-mode', theme.group);

  const root = document.documentElement.style;
  root.setProperty('--reader-bg', theme.bg);
  root.setProperty('--reader-fg', theme.fg);
  root.setProperty('--reader-muted', theme.muted);
  root.setProperty('--reader-surface', theme.surface);
  root.setProperty('--reader-surface-solid', theme.surfaceSolid);
  root.setProperty('--reader-border', theme.border);
  root.setProperty('--reader-accent', theme.accent);
  root.setProperty('--reader-accent-fg', theme.accentFg);
  root.setProperty('--reader-font', font.css);
  root.setProperty('--reader-size', `${settings.size}px`);
  root.setProperty('--reader-lh', String(spacing.value));
  root.setProperty('--reader-width', width.value);
  root.setProperty('--reader-align', settings.align);
}

export function clearReaderTheme() {
  document.body.classList.remove('is-reading');
  document.body.removeAttribute('data-reader-theme');
  document.body.removeAttribute('data-reader-mode');
}

export function supportsWakeLock() {
  return typeof navigator !== 'undefined' && 'wakeLock' in navigator && typeof navigator.wakeLock?.request === 'function';
}

export function buildSettingsPopover(settings, { onUpdate, onReset, onClose }) {
  const panel = el('section', 'nset-popover');
  panel.setAttribute('role', 'dialog');
  panel.setAttribute('aria-label', 'Reader settings');

  const head = el('div', 'nset-head');
  head.appendChild(el('h3', null, 'Reading settings'));
  const headActions = el('div', 'nset-head-actions');
  headActions.append(
    button('nset-reset', 'Reset', () => onReset()),
    button('nset-close', '×', () => onClose(), 'Close settings')
  );
  head.appendChild(headActions);
  panel.appendChild(head);

  const body = el('div', 'nset-body');

  function swatchRow(groupLabel, groupId) {
    const group = el('div', 'nset-group');
    group.appendChild(el('span', 'nset-label', groupLabel));
    const grid = el('div', 'nset-swatches');
    for (const theme of READER_THEMES.filter(t => t.group === groupId)) {
      const sw = button('nset-swatch', 'Aa', () => {
        settings.theme = theme.id;
        onUpdate();
      }, `${theme.label} theme`);
      sw.dataset.theme = theme.id;
      sw.title = theme.label;
      sw.style.background = theme.bg;
      sw.style.color = theme.fg;
      sw.setAttribute('aria-pressed', String(settings.theme === theme.id));
      grid.appendChild(sw);
    }
    group.appendChild(grid);
    return group;
  }

  body.append(swatchRow('Light themes', 'light'), swatchRow('Dark themes', 'dark'));

  // Font size stepper + Font dropdown row
  const typoRow = el('div', 'nset-split');

  const sizeGroup = el('div', 'nset-group');
  sizeGroup.appendChild(el('span', 'nset-label', 'Font size'));
  const stepper = el('div', 'nset-stepper');
  const decBtn = button('nset-step-btn', '−', () => {
    if (settings.size > 14) { settings.size -= 1; onUpdate(); }
  }, 'Decrease font size');
  decBtn.disabled = settings.size <= 14;
  const sizeVal = el('span', 'nset-step-val', String(settings.size));
  const incBtn = button('nset-step-btn', '+', () => {
    if (settings.size < 32) { settings.size += 1; onUpdate(); }
  }, 'Increase font size');
  incBtn.disabled = settings.size >= 32;
  stepper.append(decBtn, sizeVal, incBtn);
  sizeGroup.appendChild(stepper);

  const fontGroup = el('div', 'nset-group');
  const fontLabel = el('label', 'nset-label', 'Font');
  fontLabel.htmlFor = 'nset-font-select';
  const fontSelect = el('select', 'nset-select');
  fontSelect.id = 'nset-font-select';
  fontSelect.setAttribute('aria-label', 'Font family');
  for (const f of READER_FONTS) {
    const opt = el('option', null, f.label);
    opt.value = f.id;
    if (f.id === settings.font) opt.selected = true;
    fontSelect.appendChild(opt);
  }
  fontSelect.addEventListener('change', () => {
    settings.font = fontSelect.value;
    onUpdate();
  });
  fontGroup.append(fontLabel, fontSelect);

  typoRow.append(sizeGroup, fontGroup);
  body.appendChild(typoRow);

  function segmentedGroup(label, options, current, onPick) {
    const group = el('div', 'nset-group');
    group.appendChild(el('span', 'nset-label', label));
    const seg = el('div', 'nset-seg');
    for (const opt of options) {
      const btn = button('nset-seg-btn', opt.label, () => {
        onPick(opt.id);
        onUpdate();
      });
      btn.setAttribute('aria-pressed', String(current === opt.id));
      seg.appendChild(btn);
    }
    group.appendChild(seg);
    return group;
  }

  body.append(
    segmentedGroup('Line spacing', SPACING_OPTIONS, settings.spacing, v => { settings.spacing = v; }),
    segmentedGroup('Page width', WIDTH_OPTIONS, settings.width, v => { settings.width = v; }),
    segmentedGroup('Text alignment', ALIGN_OPTIONS, settings.align, v => { settings.align = v; })
  );

  // Toggles section
  const toggles = el('div', 'nset-toggles');

  function makeToggle(label, checked, onToggle) {
    const row = button('nset-toggle', '', () => {
      onToggle(!checked);
      onUpdate();
    });
    row.setAttribute('role', 'switch');
    row.setAttribute('aria-checked', String(checked));
    row.appendChild(el('span', 'nset-toggle-label', label));
    const track = el('span', 'nset-track');
    track.appendChild(el('span', 'nset-thumb'));
    row.appendChild(track);
    return row;
  }

  if (supportsWakeLock()) {
    toggles.appendChild(makeToggle('Keep screen on', settings.wakeLock, v => { settings.wakeLock = v; }));
  }
  toggles.appendChild(makeToggle('Progress bar', settings.progressBar, v => { settings.progressBar = v; }));
  toggles.appendChild(makeToggle('Auto-scroll', settings.autoScroll, v => { settings.autoScroll = v; }));

  const speedWrap = el('div', 'nset-speed');
  const speedTop = el('div', 'nset-speed-top');
  speedTop.append(
    el('span', 'nset-label', 'Auto-scroll speed'),
    el('span', 'nset-speed-val', String(settings.autoScrollSpeed))
  );
  const slider = el('input', 'nset-slider');
  slider.type = 'range';
  slider.min = '1';
  slider.max = '10';
  slider.step = '1';
  slider.value = String(settings.autoScrollSpeed);
  slider.setAttribute('aria-label', 'Auto-scroll speed');
  slider.addEventListener('input', () => {
    settings.autoScrollSpeed = clampInt(slider.value, 1, 10, DEFAULT_SETTINGS.autoScrollSpeed);
    speedTop.querySelector('.nset-speed-val').textContent = String(settings.autoScrollSpeed);
    saveReaderSettings(settings);
    onUpdate({ rerender: false });
  });
  speedWrap.append(speedTop, slider);
  toggles.appendChild(speedWrap);

  body.appendChild(toggles);
  panel.appendChild(body);
  return panel;
}
