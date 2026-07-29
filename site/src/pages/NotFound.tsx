import { Link } from 'react-router';

export function NotFound() {
  return (
    <div className="container form-page" style={{ textAlign: 'center' }}>
      <svg width="120" height="90" viewBox="0 0 120 90" fill="none" aria-hidden style={{ margin: '0 auto var(--space-6)' }}>
        <path
          d="M10 10 C 40 10, 50 30, 60 45 S 80 75, 84 82"
          stroke="var(--accent)"
          strokeWidth="2.5"
          strokeLinecap="round"
          strokeDasharray="1 0"
        />
        <path d="M84 82c1.5 3-1 6-4 5" stroke="var(--accent)" strokeWidth="2.5" strokeLinecap="round" opacity="0.6" />
        <circle cx="10" cy="10" r="5" fill="var(--surface-panel)" stroke="var(--accent)" strokeWidth="2.5" />
      </svg>
      <h1 style={{ fontSize: 'var(--text-2xl)' }}>This trail leads nowhere.</h1>
      <p className="muted" style={{ marginInline: 'auto' }}>
        The page you're after was never committed — or it's been rebased away.
      </p>
      <Link to="/" className="btn btn-accent btn-lg" style={{ marginTop: 'var(--space-4)' }}>
        Back to the gatehouse
      </Link>
    </div>
  );
}
