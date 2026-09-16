import { useEffect } from 'react';
import { Navigate, Route, Routes, useLocation } from 'react-router';
import { Nav } from './components/Nav';
import { Footer } from './components/Footer';
import { Home } from './pages/Home';
import { Client } from './pages/Client';
import { Pro } from './pages/Pro';
import { Cloud } from './pages/Cloud';
import { Contact } from './pages/Contact';
import { Waitlist } from './pages/Waitlist';
import { Privacy } from './pages/Privacy';
import { Terms } from './pages/Terms';
import { Verification } from './pages/Verification';
import { NotFound } from './pages/NotFound';
import { CookieConsent } from './components/CookieConsent';
import { useConsent } from './lib/consent';
import { setAnalyticsAllowed, trackCta, trackPageview } from './lib/analytics';

const TITLES: Record<string, string> = {
  '/': 'Mainguard · the native Git client for the agent era',
  '/client': 'Git Client · free, native, no login · Mainguard',
  '/pro': 'Mainguard Pro · where agent work becomes trustworthy commits',
  '/cloud': 'Mainguard Cloud · describe it, ship it verified',
  '/contact': 'Contact · Mainguard',
  '/waitlist': 'Join the waitlist · Mainguard',
  '/verification': 'How verification works · Mainguard',
  '/privacy': 'Privacy policy · Mainguard',
  '/terms': 'Terms of service · Mainguard',
};

export default function App() {
  const { pathname } = useLocation();
  const { hasConsent } = useConsent();
  const analyticsOn = hasConsent('analytics');

  useEffect(() => {
    window.scrollTo(0, 0);
    document.title = TITLES[pathname] ?? 'Mainguard';
  }, [pathname]);

  // Keep the beacon's gate in step with the visitor's choice, including the
  // moment they accept or withdraw — analytics defaults to off until this runs.
  useEffect(() => {
    setAnalyticsAllowed(analyticsOn);
  }, [analyticsOn]);

  // One pageview per route change, and only once consent is in hand. Listing
  // analyticsOn as a dependency means accepting mid-visit still records the
  // page being read at that moment, rather than silently missing it.
  useEffect(() => {
    if (!analyticsOn) return;
    trackPageview(pathname);
  }, [pathname, analyticsOn]);

  // CTA clicks, by delegation: any element carrying data-cta reports its label.
  // A listener beats threading a callback through every button on the site.
  useEffect(() => {
    if (!analyticsOn) return;
    const onClick = (e: MouseEvent) => {
      const el = (e.target as HTMLElement | null)?.closest<HTMLElement>('[data-cta]');
      if (el?.dataset.cta) trackCta(el.dataset.cta, window.location.pathname);
    };
    document.addEventListener('click', onClick);
    return () => document.removeEventListener('click', onClick);
  }, [analyticsOn]);

  return (
    <>
      <a href="#main" className="visually-hidden">
        Skip to content
      </a>
      <Nav />
      <main id="main">
        <Routes>
          <Route path="/" element={<Home />} />
          <Route path="/client" element={<Client />} />
          <Route path="/pro" element={<Pro />} />
          <Route path="/cloud" element={<Cloud />} />
          {/* Pre-rename URL — keep old links working. */}
          <Route path="/weave" element={<Navigate to="/cloud" replace />} />
          <Route path="/contact" element={<Contact />} />
          <Route path="/waitlist" element={<Waitlist />} />
          <Route path="/verification" element={<Verification />} />
          <Route path="/privacy" element={<Privacy />} />
          <Route path="/terms" element={<Terms />} />
          <Route path="*" element={<NotFound />} />
        </Routes>
      </main>
      <Footer />
      <CookieConsent />
    </>
  );
}
