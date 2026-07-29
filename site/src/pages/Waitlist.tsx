import { useState, type FormEvent } from 'react';
import { useSearchParams } from 'react-router';
import { Turnstile } from '../components/Turnstile';
import { SuccessGate } from '../components/SuccessGate';
import { postJson } from '../lib/api';

// `weave` is Mainguard Cloud's wire id — the deployed worker whitelists it, so
// it stays until the worker accepts `cloud` (see docs/rebrand plan).
const PRODUCTS = [
  { id: 'client', label: 'Git Client (free)' },
  { id: 'pro', label: 'Mainguard Pro' },
  { id: 'weave', label: 'Mainguard Cloud' },
];

export function Waitlist() {
  const [params] = useSearchParams();
  const rawPreselect = params.get('p');
  const preselect = rawPreselect === 'cloud' ? 'weave' : rawPreselect;
  const [email, setEmail] = useState('');
  const [interests, setInterests] = useState<string[]>(
    PRODUCTS.some((p) => p.id === preselect) ? [preselect as string] : ['client'],
  );
  const [token, setToken] = useState<string | null>(null);
  const [hp, setHp] = useState('');
  const [busy, setBusy] = useState(false);
  const [done, setDone] = useState(false);
  const [error, setError] = useState<string | null>(null);

  function toggle(id: string) {
    setInterests((cur) => (cur.includes(id) ? cur.filter((i) => i !== id) : [...cur, id]));
  }

  async function submit(e: FormEvent) {
    e.preventDefault();
    setError(null);
    if (!/^[^\s@]+@[^\s@]+\.[^\s@]{2,}$/.test(email)) {
      setError('Please enter a valid email address.');
      return;
    }
    if (!token) {
      setError('Please complete the anti-spam check below.');
      return;
    }
    setBusy(true);
    const res = await postJson('/api/waitlist', {
      email,
      interests,
      turnstileToken: token,
      website: hp,
    });
    setBusy(false);
    if (res.ok) setDone(true);
    else setError((res.error ?? 'Something went wrong.') + ' Your details are still here — please try again.');
  }

  if (done) {
    return (
      <div className="container form-page">
        <SuccessGate title="You're on the list.">
          <p className="muted">
            We'll email you the moment there's something to download — and nothing else. No
            newsletters, no drip campaigns.
          </p>
        </SuccessGate>
      </div>
    );
  }

  return (
    <div className="container form-page">
      <h1>Join the waitlist</h1>
      <p className="lede" style={{ marginBottom: 'var(--space-8)' }}>
        First access to the free client this fall, and founding-member terms for Pro and Cloud.
      </p>
      <form onSubmit={submit} noValidate>
        <div className="field">
          <label htmlFor="wl-email">Email</label>
          <input
            id="wl-email"
            type="email"
            autoComplete="email"
            placeholder="you@company.com"
            value={email}
            onChange={(e) => setEmail(e.target.value)}
            required
          />
        </div>

        <div className="field">
          <label id="wl-interest-label">I'm interested in</label>
          <div className="interest-chips" role="group" aria-labelledby="wl-interest-label">
            {PRODUCTS.map((p) => (
              <button
                key={p.id}
                type="button"
                className="chip-toggle"
                aria-pressed={interests.includes(p.id)}
                onClick={() => toggle(p.id)}
              >
                {p.label}
              </button>
            ))}
          </div>
        </div>

        <div className="hp-field" aria-hidden="true">
          <label htmlFor="wl-website">Leave this field empty</label>
          <input
            id="wl-website"
            type="text"
            tabIndex={-1}
            autoComplete="off"
            value={hp}
            onChange={(e) => setHp(e.target.value)}
          />
        </div>

        <Turnstile onToken={setToken} />

        {error && (
          <p className="form-alert" role="alert">
            {error}
          </p>
        )}

        <button type="submit" className="btn btn-accent btn-lg" disabled={busy}>
          {busy ? 'Joining…' : 'Join the waitlist'}
        </button>
      </form>
    </div>
  );
}
