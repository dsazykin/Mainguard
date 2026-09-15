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
import { NotFound } from './pages/NotFound';
import { CookieConsent } from './components/CookieConsent';

const TITLES: Record<string, string> = {
  '/': 'Mainguard — the native Git client for the agent era',
  '/client': 'Git Client — free, native, no login · Mainguard',
  '/pro': 'Mainguard Pro — where agent work becomes trustworthy commits',
  '/cloud': 'Mainguard Cloud — describe it, ship it verified',
  '/contact': 'Contact · Mainguard',
  '/waitlist': 'Join the waitlist · Mainguard',
  '/privacy': 'Privacy policy · Mainguard',
  '/terms': 'Terms of service · Mainguard',
};

export default function App() {
  const { pathname } = useLocation();

  useEffect(() => {
    window.scrollTo(0, 0);
    document.title = TITLES[pathname] ?? 'Mainguard';
  }, [pathname]);

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
