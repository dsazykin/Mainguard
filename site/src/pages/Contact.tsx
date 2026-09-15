import { useEffect, useRef, useState, type FormEvent, type KeyboardEvent } from 'react';
import { Link } from 'react-router';
import { Turnstile } from '../components/Turnstile';
import { SuccessGate } from '../components/SuccessGate';
import { postJson } from '../lib/api';
import { trackStep } from '../lib/analytics';

const TOPICS = ['General', 'Early access & beta', 'Partnerships', 'Press', 'Support'];
const EMAIL_RE = /^[^\s@]+@[^\s@]+\.[^\s@]{2,}$/;

const STEPS = ['Name', 'Email', 'Topic', 'Message', 'Send'] as const;

/** One question at a time — Enter or Next to advance, Back never loses input. */
export function Contact() {
  const [name, setName] = useState('');
  const [email, setEmail] = useState('');
  const [topic, setTopic] = useState(TOPICS[0]);
  const [message, setMessage] = useState('');
  const [token, setToken] = useState<string | null>(null);
  const [hp, setHp] = useState('');
  const [step, setStep] = useState(0);
  const [dir, setDir] = useState<'fwd' | 'back'>('fwd');
  const [fieldError, setFieldError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [done, setDone] = useState(false);
  const [error, setError] = useState<string | null>(null);

  // Report the furthest step reached, once each. Without this, walking back and
  // forward would report the same step repeatedly and a drop-off funnel would
  // read as enthusiasm. -1 so step 0 still counts as newly reached.
  const furthestStep = useRef(-1);
  useEffect(() => {
    if (step <= furthestStep.current) return;
    furthestStep.current = step;
    trackStep(`${step + 1}-${STEPS[step].toLowerCase()}`, '/contact');
  }, [step]);

  function validate(s: number): string | null {
    if (s === 0 && !name.trim()) return 'Please enter your name.';
    if (s === 1 && !EMAIL_RE.test(email)) return 'Please enter a valid email address.';
    if (s === 3 && !message.trim()) return 'Please write a message.';
    if (s === 3 && message.length > 5000) return 'Message is too long (max 5000 characters).';
    return null;
  }

  function goNext() {
    const err = validate(step);
    if (err) {
      setFieldError(err);
      return;
    }
    setFieldError(null);
    setDir('fwd');
    setStep((s) => Math.min(s + 1, STEPS.length - 1));
  }

  function goBack() {
    setFieldError(null);
    setError(null);
    setDir('back');
    setStep((s) => Math.max(s - 1, 0));
  }

  function onEnter(e: KeyboardEvent) {
    if (e.key === 'Enter') {
      e.preventDefault();
      goNext();
    }
  }

  async function submit(e: FormEvent) {
    e.preventDefault();
    setError(null);
    // Belt and braces — every step was validated on the way here.
    for (let s = 0; s < STEPS.length - 1; s++) {
      const err = validate(s);
      if (err) {
        setStep(s);
        setFieldError(err);
        return;
      }
    }
    if (!token) {
      setError('Please complete the anti-spam check above.');
      return;
    }
    setBusy(true);
    const res = await postJson('/api/contact', {
      name,
      email,
      topic,
      message,
      turnstileToken: token,
      website: hp,
    });
    setBusy(false);
    if (res.ok) setDone(true);
    else
      setError(
        (res.error ?? 'Something went wrong.') +
          ' Your message is still here — nothing was lost. Please try again.',
      );
  }

  if (done) {
    return (
      <div className="container form-page">
        <SuccessGate title="Message sent.">
          <p className="muted">Thanks for writing, {name.trim().split(/\s+/)[0]} — expect a reply within a couple of days.</p>
        </SuccessGate>
      </div>
    );
  }

  return (
    <div className="container form-page">
      <h1>Contact</h1>
      <p className="lede" style={{ marginBottom: 'var(--space-8)' }}>
        Questions, beta access, partnerships, or just opinions about Git clients — all welcome. One
        question at a time; nothing you type gets lost.
      </p>

      <div className="wiz-progress" aria-hidden>
        <span className="wiz-count">
          {step + 1} / {STEPS.length}
        </span>
        <div className="wiz-threads">
          {STEPS.map((s, i) => (
            <span
              key={s}
              className={i <= step ? 'done' : ''}
              style={{ '--lane': `var(--lane-${i + 1})` } as React.CSSProperties}
            />
          ))}
        </div>
      </div>

      <form onSubmit={submit} noValidate aria-label={`Contact form, step ${step + 1} of ${STEPS.length}: ${STEPS[step]}`}>
        <div key={step} className={`wiz-step ${dir === 'back' ? 'back' : ''}`}>
          {step === 0 && (
            <div className="field">
              <label htmlFor="ct-name">What's your name?</label>
              <input
                id="ct-name"
                type="text"
                autoComplete="name"
                placeholder="Ada Lovelace"
                value={name}
                onChange={(e) => setName(e.target.value)}
                onKeyDown={onEnter}
                autoFocus
                required
              />
            </div>
          )}

          {step === 1 && (
            <div className="field">
              <label htmlFor="ct-email">Where should the reply go?</label>
              <input
                id="ct-email"
                type="email"
                autoComplete="email"
                placeholder="you@company.com"
                value={email}
                onChange={(e) => setEmail(e.target.value)}
                onKeyDown={onEnter}
                autoFocus
                required
              />
            </div>
          )}

          {step === 2 && (
            <div className="field">
              <label id="ct-topic-label">What's it about?</label>
              <div className="interest-chips" role="radiogroup" aria-labelledby="ct-topic-label">
                {TOPICS.map((t) => (
                  <button
                    key={t}
                    type="button"
                    role="radio"
                    aria-checked={topic === t}
                    className="chip-toggle"
                    aria-pressed={topic === t}
                    onClick={() => setTopic(t)}
                  >
                    {t}
                  </button>
                ))}
              </div>
            </div>
          )}

          {step === 3 && (
            <div className="field">
              <label htmlFor="ct-message">What's on your mind?</label>
              <textarea
                id="ct-message"
                placeholder="Take your time — Back won't eat this."
                value={message}
                maxLength={5000}
                onChange={(e) => setMessage(e.target.value)}
                onKeyDown={(e) => {
                  if (e.key === 'Enter' && (e.ctrlKey || e.metaKey)) {
                    e.preventDefault();
                    goNext();
                  }
                }}
                autoFocus
                required
              />
              <span className="muted" style={{ fontSize: '0.75rem', alignSelf: 'flex-end' }}>
                {message.length} / 5000
              </span>
            </div>
          )}

          {step === 4 && (
            <>
              <div className="wiz-summary" aria-label="Review your message">
                <div>
                  <span className="k">from</span>
                  <span className="v">
                    {name} · {email}
                  </span>
                </div>
                <div>
                  <span className="k">topic</span>
                  <span className="v">{topic}</span>
                </div>
                <div>
                  <span className="k">message</span>
                  <span className="v" style={{ whiteSpace: 'pre-wrap' }}>
                    {message.length > 400 ? message.slice(0, 400) + '…' : message}
                  </span>
                </div>
              </div>
              <Turnstile onToken={setToken} />
            </>
          )}

          {fieldError && (
            <p className="field-error" role="alert">
              {fieldError}
            </p>
          )}
        </div>

        <div className="hp-field" aria-hidden="true">
          <label htmlFor="ct-website">Leave this field empty</label>
          <input
            id="ct-website"
            type="text"
            tabIndex={-1}
            autoComplete="off"
            value={hp}
            onChange={(e) => setHp(e.target.value)}
          />
        </div>

        {error && (
          <p className="form-alert" role="alert">
            {error}
          </p>
        )}

        <div className="wiz-nav">
          {step > 0 && (
            <button type="button" className="btn btn-quiet btn-lg" onClick={goBack} disabled={busy}>
              ← Back
            </button>
          )}
          {step < STEPS.length - 1 ? (
            <button type="button" className="btn btn-accent btn-lg" onClick={goNext}>
              Next →
            </button>
          ) : (
            <button type="submit" className="btn btn-accent btn-lg" disabled={busy}>
              {busy ? 'Sending…' : 'Send message'}
            </button>
          )}
        </div>
        {step < 3 && <p className="wiz-hint">press Enter to continue</p>}
        {step === 3 && <p className="wiz-hint">Ctrl+Enter to continue</p>}

        {/* GDPR Art. 13: shown on the step where the message is actually sent,
            so the notice arrives before the data does. */}
        {step === STEPS.length - 1 && (
          <p className="form-consent">
            Sending stores your name, email and message so it can be read and answered. Nothing
            else, never sold or shared, and you can ask for it to be deleted. Full detail in the{' '}
            <Link to="/privacy">privacy policy</Link>.
          </p>
        )}
      </form>
    </div>
  );
}
