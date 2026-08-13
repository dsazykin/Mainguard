/**
 * The Mainguard mark: an M drawn as a gatehouse — two watchtowers flanking a
 * gate, with the main branch running safely through the opening beneath the
 * keystone. Drawn with the current theme's accent + lane colors.
 *
 * Interim programmatic version — swap the <svg> body for the crafted asset
 * when it lands, keeping the viewBox and CSS-variable strokes.
 */
export function Wordmark({ size = 26, withText = true }: { size?: number; withText?: boolean }) {
  return (
    <span
      style={{ display: 'inline-flex', alignItems: 'center', gap: '0.55rem', lineHeight: 1 }}
      aria-label="Mainguard"
    >
      <svg width={size} height={size} viewBox="0 0 26 26" fill="none" aria-hidden>
        <path
          d="M3.5 22V7.5L13 15.5L22.5 7.5V22"
          stroke="var(--accent)"
          strokeWidth="2.6"
          strokeLinecap="round"
          strokeLinejoin="round"
        />
        <path d="M8 19.5H18" stroke="var(--lane-3)" strokeWidth="1.7" strokeLinecap="round" />
      </svg>
      {withText && (
        <span
          style={{
            font: '650 1.18rem/1 var(--font-sans)',
            letterSpacing: '-0.02em',
            color: 'var(--text-primary)',
          }}
        >
          Mainguard
        </span>
      )}
    </span>
  );
}
