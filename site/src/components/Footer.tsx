import { Link } from 'react-router';
import { Wordmark } from './Wordmark';
import { IconGitHub } from './Icons';
import { GITHUB_REPO_LABEL, GITHUB_URL } from '../config';
import { useConsent } from '../lib/consent';

export function Footer() {
  const { consentApplies, openPreferences } = useConsent();

  return (
    <footer className="footer">
      <div className="container footer-inner">
        <div className="footer-brand">
          <Wordmark />
          <p className="muted" style={{ maxWidth: '32ch', marginTop: 'var(--space-3)' }}>
            A native Git client, and the guard that decides what agent work reaches main.
          </p>
          <a href={GITHUB_URL} className="footer-gh" aria-label="Mainguard on GitHub">
            <IconGitHub /> <span>{GITHUB_REPO_LABEL}</span>
          </a>
        </div>
        <nav className="footer-col" aria-label="Products">
          <h4>Products</h4>
          <Link to="/client">Git Client — free</Link>
          <Link to="/pro">Mainguard Pro</Link>
          <Link to="/cloud">Mainguard Cloud</Link>
        </nav>
        <nav className="footer-col" aria-label="Company">
          <h4>Get in touch</h4>
          <Link to="/waitlist">Join the waitlist</Link>
          <Link to="/contact">Contact</Link>
        </nav>
        <nav className="footer-col" aria-label="Legal">
          <h4>Legal</h4>
          <Link to="/privacy">Privacy policy</Link>
          <Link to="/terms">Terms of service</Link>
          {/* Only shown once something actually needs consent — see lib/consent.tsx. */}
          {consentApplies && (
            <button type="button" className="footer-linkish" onClick={openPreferences}>
              Cookie settings
            </button>
          )}
        </nav>
      </div>
      <div className="container footer-legal">
        <span className="muted mono" style={{ fontSize: '0.75rem' }}>
          © {new Date().getFullYear()} Mainguard · rendered natively, no web view was harmed
        </span>
      </div>
    </footer>
  );
}
