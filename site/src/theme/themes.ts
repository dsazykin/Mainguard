/**
 * The four Mainguard themes, in the app's canonical order (DESIGN.md §1).
 *
 * Command Deck and Loom Aurora were retired in the 2026-08 restyle — their
 * saturated palettes read as generic-AI-website rather than instrument. Their
 * ids are deliberately not reused; a visitor still holding one in localStorage
 * falls back to DEFAULT_THEME (see ThemeProvider.readStoredTheme).
 */
export interface ThemeInfo {
  id: string;
  label: string;
  scheme: 'dark' | 'light';
  /** Swatch colors shown in the switcher: window surface + accent. */
  surface: string;
  accent: string;
}

export const THEMES: ThemeInfo[] = [
  { id: 'midnight', label: 'Midnight Loom', scheme: 'dark', surface: '#0F1115', accent: '#8487F0' },
  { id: 'daylight', label: 'Daylight Loom', scheme: 'light', surface: '#EDEFF4', accent: '#6467E8' },
  { id: 'graphite', label: 'Graphite', scheme: 'dark', surface: '#1D1D1F', accent: '#409CFF' },
  { id: 'atelier', label: 'Atelier', scheme: 'dark', surface: '#171512', accent: '#D8A25A' },
];

export const DEFAULT_THEME = 'midnight';
export const THEME_STORAGE_KEY = 'mainguard-theme';
/**
 * Pre-rename storage key — still read (never written) so visitors who chose a
 * theme before the 2026-07-16 GitLoom→Mainguard rename keep it. This one string
 * stays spelled "gitloom" on purpose: it names a key already sitting in those
 * browsers, so renaming it would silently drop their preference.
 */
export const LEGACY_THEME_STORAGE_KEY = 'gitloom-theme';
