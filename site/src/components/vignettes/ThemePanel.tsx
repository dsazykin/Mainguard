import { WindowFrame } from './WindowFrame';
import { useTheme } from '../../theme/ThemeProvider';
import { THEMES } from '../../theme/themes';

/** The app's appearance settings — and the rows really switch this site's theme. */
export function ThemePanelVignette() {
  const { theme, setTheme } = useTheme();

  return (
    <WindowFrame title="mainguard · settings · appearance">
      <div className="vg-grid" role="radiogroup" aria-label="Site theme (live)">
        {THEMES.map((t, i) => (
          <button
            key={t.id}
            type="button"
            role="radio"
            aria-checked={theme === t.id}
            className={`vg-row ${theme === t.id ? 'vg-selected' : ''}`}
            onClick={() => setTheme(t.id)}
          >
            <span style={{ display: 'inline-flex', alignItems: 'center', gap: 10, fontSize: 13 }}>
              <span style={{ width: 14, height: 14, borderRadius: 4, background: `var(--lane-${i + 1})`, flexShrink: 0 }} />
              {t.label}
            </span>
            {theme === t.id && (
              <span className="mono" style={{ fontSize: 10.5, color: 'var(--accent)' }}>
                active ✓
              </span>
            )}
          </button>
        ))}
        <p className="vg-hint">these rows are wired to the real switcher, so the whole page follows</p>
      </div>
    </WindowFrame>
  );
}
