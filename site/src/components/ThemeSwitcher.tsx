import { THEMES } from '../theme/themes';
import { useTheme } from '../theme/ThemeProvider';

/**
 * The four-theme switcher — the site's live demo of the app's design system.
 * Each swatch is that theme's window surface with its accent thread across it.
 */
export function ThemeSwitcher({ large = false }: { large?: boolean }) {
  const { theme, setTheme } = useTheme();
  const size = large ? 34 : 20;

  return (
    <div
      role="radiogroup"
      aria-label="Color theme"
      style={{ display: 'inline-flex', gap: large ? 12 : 7, alignItems: 'center' }}
    >
      {THEMES.map((t) => {
        const active = t.id === theme;
        return (
          <button
            key={t.id}
            role="radio"
            aria-checked={active}
            title={t.label}
            aria-label={t.label}
            onClick={() => setTheme(t.id)}
            style={{
              width: size,
              height: size,
              borderRadius: '50%',
              cursor: 'pointer',
              padding: 0,
              background: t.surface,
              border: active ? `2px solid ${t.accent}` : '1px solid var(--border-hairline)',
              boxShadow: active ? `0 0 0 3px var(--accent-selection)` : 'none',
              position: 'relative',
              overflow: 'hidden',
              transition: 'box-shadow var(--dur-micro) var(--ease-out), border-color var(--dur-micro) var(--ease-out)',
            }}
          >
            <span
              aria-hidden
              style={{
                position: 'absolute',
                left: '15%',
                right: '15%',
                top: '48%',
                height: large ? 3 : 2,
                borderRadius: 2,
                background: t.accent,
                transform: 'rotate(-18deg)',
              }}
            />
          </button>
        );
      })}
    </div>
  );
}
