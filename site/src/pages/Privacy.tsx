import { Link } from 'react-router';
import { LEGAL_CONTACT_EMAIL, LEGAL_ENTITY, LEGAL_JURISDICTION, LEGAL_LAST_UPDATED } from '../config';

/**
 * Privacy policy for mainguard.dev.
 *
 * Every factual claim here is checked against the code that implements it:
 * site/worker/src/index.ts (what is collected and sent), worker/schema.sql
 * (what is stored), components/Turnstile.tsx (the one third-party script),
 * and theme/ThemeProvider.tsx + lib/consent.tsx (browser storage). If you
 * change what the worker collects, change this page in the same commit —
 * a privacy policy that lags the code is the one kind of stale copy that
 * carries legal consequences.
 */
export function Privacy() {
  return (
    <div className="container legal">
      <p className="legal-updated">Last updated · {LEGAL_LAST_UPDATED}</p>
      <h1>Privacy policy</h1>
      <p className="lede">
        Mainguard collects as little as it can get away with. There is no advertising, no tracking
        pixel, no third-party analytics and no third-party font. What there is: what you type into
        a form, the minimum needed to stop that form being abused by bots, and — only if you allow
        it — first-party page counts that set nothing on your device.
      </p>

      <nav className="legal-toc" aria-label="Contents">
        <h2>On this page</h2>
        <ol>
          <li><a href="#who">Who is responsible</a></li>
          <li><a href="#what">What is collected</a></li>
          <li><a href="#why">Why, and on what legal basis</a></li>
          <li><a href="#cookies">Cookies and browser storage</a></li>
          <li><a href="#processors">Who else sees it</a></li>
          <li><a href="#transfers">International transfers</a></li>
          <li><a href="#retention">How long it is kept</a></li>
          <li><a href="#rights">Your rights</a></li>
          <li><a href="#never">What is never done</a></li>
          <li><a href="#app">The desktop application</a></li>
          <li><a href="#changes">Changes</a></li>
        </ol>
      </nav>

      <section id="who">
        <h2>1. Who is responsible</h2>
        <p>
          This site, <strong>mainguard.dev</strong>, is operated by {LEGAL_ENTITY}, based in{' '}
          {LEGAL_JURISDICTION}. For the purposes of the General Data Protection Regulation (GDPR),
          that is the <strong>data controller</strong> for everything described here.
        </p>
        <p>
          The way to reach a human about any of this — including a request to see or delete your
          data — is either the <Link to="/contact">contact form</Link> or email:
        </p>
        <p>
          <a href={`mailto:${LEGAL_CONTACT_EMAIL}`} className="mono">
            {LEGAL_CONTACT_EMAIL}
          </a>
        </p>
        <p>
          That is a personal address rather than a company one, because Mainguard is currently one
          person and a domain mailbox does not exist yet. It is read. Requests about your own data
          are answered within one month, as the GDPR requires.
        </p>
      </section>

      <section id="what">
        <h2>2. What is collected</h2>
        <p>
          Nothing at all, unless you submit one of the two forms — or allow analytics, which is off
          until you say otherwise.
        </p>

        <h3>If you allow analytics</h3>
        <p>
          Aggregate counts, recorded by Mainguard's own server. No third party is involved, and
          nothing is stored on your device — no analytics cookie, no identifier. Each event holds:
        </p>
        <ul>
          <li><strong>Which page</strong> was viewed, and <strong>which button</strong> was clicked.</li>
          <li>
            <strong>How far into the contact form</strong> you got, if you started it — the step
            number only, never what you typed into it. A form people abandon halfway is usually a
            form with a bad question in it.
          </li>
          <li>
            <strong>The address you asked for, when a page does not exist</strong>, so broken links
            pointing here can be found and fixed.
          </li>
          <li>
            <strong>The site that linked you here</strong> — the host only, such as
            "news.ycombinator.com", never the full address, which can carry search terms.
          </li>
          <li><strong>A campaign tag</strong>, if the link you followed carried one.</li>
          <li><strong>Your country</strong> and <strong>device type</strong> (desktop, mobile or tablet).</li>
          <li><strong>Which colour theme</strong> the page was being read in.</li>
          <li>
            <strong>A visitor key that expires daily.</strong> It is a hash including the calendar
            date, so it can group one day's page views together and is structurally useless for
            recognising you tomorrow. Your IP address is not stored.
          </li>
        </ul>
        <p>
          That is the whole list. There is no scroll recording, no mouse tracking, no profile, and
          nothing that joins these counts to your email address if you later sign up.
        </p>

        <h3>If you join the waitlist</h3>
        <ul>
          <li>
            <strong>Your email address.</strong> Required — it is the entire point of a waitlist.
          </li>
          <li>
            <strong>Which products you are interested in</strong> (client, Pro, Cloud), if you tick
            any.
          </li>
        </ul>

        <h3>If you use the contact form</h3>
        <ul>
          <li><strong>Your name</strong> and <strong>email address</strong>.</li>
          <li><strong>A topic</strong> and <strong>the message you write.</strong> Whatever you put in the message is up to you — there is no need to include anything sensitive, so please don't.</li>
        </ul>

        <h3>Collected automatically with either submission</h3>
        <ul>
          <li>
            <strong>A one-way hash of your IP address.</strong> The raw address is never written to
            the database: it is passed through SHA-256 first, and only that hash is stored, purely
            so the same source cannot flood the form. Treat it as pseudonymous personal data, not
            anonymous — a hash of an IP can still, with effort, be linked back.
          </li>
          <li><strong>Your browser's user-agent string</strong> and <strong>the time of submission.</strong></li>
        </ul>

        <div className="legal-callout">
          <p>
            <strong>Not collected:</strong> your raw IP address is never stored. There is no
            account system, so there is no password, no profile and nothing to breach. No cookie
            follows you between sites, because none is set for that purpose.
          </p>
        </div>
      </section>

      <section id="why">
        <h2>3. Why, and on what legal basis</h2>
        <div className="legal-table-wrap">
          <table className="legal-table">
            <thead>
              <tr>
                <th scope="col">Data</th>
                <th scope="col">Purpose</th>
                <th scope="col">Legal basis (GDPR Art. 6)</th>
              </tr>
            </thead>
            <tbody>
              <tr>
                <td>Waitlist email and interests</td>
                <td>To tell you when the client, Pro or Cloud is available</td>
                <td>Your consent — 6(1)(a)</td>
              </tr>
              <tr>
                <td>Contact name, email, topic, message</td>
                <td>To read and answer what you sent</td>
                <td>Legitimate interest in replying to you — 6(1)(f)</td>
              </tr>
              <tr>
                <td>Hashed IP, user agent, timestamp</td>
                <td>Rate limiting and spam prevention</td>
                <td>Legitimate interest in a working form — 6(1)(f)</td>
              </tr>
              <tr>
                <td>Analytics counts (page, referrer host, country, device, theme, daily key)</td>
                <td>To learn which pages are read and which links bring people here</td>
                <td>Your consent — 6(1)(a)</td>
              </tr>
            </tbody>
          </table>
        </div>
        <p>
          Where the basis is consent, you can withdraw it at any time and the effect is immediate
          going forward; withdrawing does not undo anything already done lawfully. Where the basis
          is legitimate interest, you can object — see <a href="#rights">your rights</a>.
        </p>
      </section>

      <section id="cookies">
        <h2>4. Cookies and browser storage</h2>
        <p>
          This site sets <strong>no advertising cookies, and no analytics cookie either</strong>.
          The analytics described above deliberately stores nothing on your device, which is why
          you will not find it in the table below — consent for it controls whether anything is
          <em> sent</em>, not whether anything is stored.
        </p>
        <div className="legal-table-wrap">
          <table className="legal-table">
            <thead>
              <tr>
                <th scope="col">Name</th>
                <th scope="col">Kind</th>
                <th scope="col">Purpose</th>
                <th scope="col">Lifetime</th>
              </tr>
            </thead>
            <tbody>
              <tr>
                <td><code className="mono">mainguard-theme</code></td>
                <td>localStorage</td>
                <td>Remembers the colour theme you picked, so the site does not flash back to the default</td>
                <td>Until you clear it</td>
              </tr>
              <tr>
                <td><code className="mono">gitloom-theme</code></td>
                <td>localStorage</td>
                <td>The same preference under the pre-rename name. Only ever read, never written</td>
                <td>Until you clear it</td>
              </tr>
              <tr>
                <td><code className="mono">mainguard-consent</code></td>
                <td>localStorage</td>
                <td>Records your analytics choice, so you are not asked on every page</td>
                <td>Until you clear it</td>
              </tr>
              <tr>
                <td>Cloudflare Turnstile</td>
                <td>Script and storage</td>
                <td>Tells a person from a bot on the waitlist and contact pages. Loaded only on those two pages, never on the rest of the site</td>
                <td>Session</td>
              </tr>
            </tbody>
          </table>
        </div>
        <p>
          None of the above is shared with anyone, and none of it is used to profile you. You can
          change your analytics choice at any time through <strong>Cookie settings</strong> in the
          footer, and withdrawing is exactly as easy as agreeing was.
        </p>
        <div className="legal-callout">
          <p>
            <strong>Global Privacy Control is respected.</strong> If your browser sends the GPC
            signal, analytics is treated as declined before you are asked anything — you will not
            see a consent banner, and nothing is sent. Turning the signal on is enough; you do not
            have to tell this site twice.
          </p>
        </div>
      </section>

      <section id="processors">
        <h2>5. Who else sees it</h2>
        <p>
          Your data is not sold, rented, or handed to advertisers — there is no business model here
          that would want it. It is processed by a small number of service providers, each acting
          on instructions:
        </p>
        <div className="legal-table-wrap">
          <table className="legal-table">
            <thead>
              <tr>
                <th scope="col">Provider</th>
                <th scope="col">What it does</th>
                <th scope="col">What it sees</th>
              </tr>
            </thead>
            <tbody>
              <tr>
                <td>Cloudflare</td>
                <td>Runs the backend (Workers), stores submissions and analytics counts (D1), and provides the Turnstile bot check</td>
                <td>Everything you submit; your IP address at the moment of the bot check and when an analytics event is received, which is hashed rather than stored</td>
              </tr>
              <tr>
                <td>Resend</td>
                <td>Delivers the notification email announcing your submission</td>
                <td>The contents of your submission</td>
              </tr>
              <tr>
                <td>GitHub (Pages)</td>
                <td>Hosts and serves these pages</td>
                <td>Standard web-server request data, including your IP address</td>
              </tr>
            </tbody>
          </table>
        </div>
        <p>
          Data may also be disclosed where the law actually requires it. If that ever happens, it
          will be the narrowest disclosure that satisfies the request.
        </p>
      </section>

      <section id="transfers">
        <h2>6. International transfers</h2>
        <p>
          The providers above are US-headquartered and may process data outside the European
          Economic Area. Those transfers rely on the safeguards the GDPR provides for them —
          Standard Contractual Clauses, and the EU–US Data Privacy Framework where a provider is
          certified under it. In practice the volume is small: a table of email addresses and
          messages.
        </p>
      </section>

      <section id="retention">
        <h2>7. How long it is kept</h2>
        <ul>
          <li>
            <strong>Waitlist entries</strong> are kept until the product you signed up for has
            launched and you have been told, or until you ask for removal — whichever comes first.
          </li>
          <li>
            <strong>Contact messages</strong> are kept while the conversation is live and for a
            reasonable period after, so that a follow-up has context.
          </li>
          <li>
            <strong>Hashed IPs and user agents</strong> exist for rate limiting and are of no
            further interest once a submission is old.
          </li>
          <li>
            <strong>Analytics counts</strong> are kept as aggregate history. They contain no
            identifier that survives the day they were recorded, so there is nothing in them to
            delete on request — and nothing that could be traced back to you if there were.
          </li>
        </ul>
        <p>
          If you want any of it gone sooner, ask through the <Link to="/contact">contact form</Link>{' '}
          or email{' '}
          <a href={`mailto:${LEGAL_CONTACT_EMAIL}`} className="mono">
            {LEGAL_CONTACT_EMAIL}
          </a>{' '}
          and it will be deleted.
        </p>
      </section>

      <section id="rights">
        <h2>8. Your rights</h2>
        <p>Under the GDPR you can ask, free of charge, to:</p>
        <ul>
          <li><strong>Access</strong> — get a copy of what is held about you.</li>
          <li><strong>Rectify</strong> — correct it if it is wrong.</li>
          <li><strong>Erase</strong> — have it deleted.</li>
          <li><strong>Restrict</strong> — have it kept but not used, while something is disputed.</li>
          <li><strong>Object</strong> — to processing based on legitimate interest.</li>
          <li><strong>Port</strong> — receive it in a machine-readable form.</li>
          <li><strong>Withdraw consent</strong> — at any time, where consent was the basis.</li>
        </ul>
        <p>
          Use the <Link to="/contact">contact form</Link> or email{' '}
          <a href={`mailto:${LEGAL_CONTACT_EMAIL}`} className="mono">
            {LEGAL_CONTACT_EMAIL}
          </a>{' '}
          for any of these — no particular wording is needed, and you will not be asked to justify
          the request. If the answer is unsatisfactory, you have the right to complain to your
          local supervisory authority; in the Netherlands that is the Autoriteit Persoonsgegevens.
        </p>
      </section>

      <section id="never">
        <h2>9. What is never done</h2>
        <ul>
          <li>No advertising networks, no tracking pixels, no fingerprinting.</li>
          <li>
            No third-party analytics. The page counts described above are first-party, opt-in, and
            never leave Mainguard's own infrastructure.
          </li>
          <li>No session recording, no heatmaps, no mouse or scroll tracking.</li>
          <li>No cross-site or cross-day tracking — the visitor key is useless the next day.</li>
          <li>No linking analytics to your email address, or to anything you submitted.</li>
          <li>No third-party font CDN — the typefaces are served from this site.</li>
          <li>No selling, renting or trading personal data. Ever, under any circumstances.</li>
          <li>No automated decision-making or profiling that produces legal effects.</li>
          <li>No sending you mail you did not ask for.</li>
        </ul>
      </section>

      <section id="app">
        <h2>10. The desktop application</h2>
        <p>
          This policy covers the website. The Mainguard desktop client is a separate matter: it
          runs on your machine, and it sends no usage telemetry. When you connect your own model
          API keys, those stay in your operating system's keyring and traffic goes to the provider
          you chose, not through Mainguard. A fuller statement will ship alongside the application
          itself.
        </p>
      </section>

      <section id="changes">
        <h2>11. Changes</h2>
        <p>
          If this policy changes, the date at the top changes with it, and the history of every
          revision is public in the{' '}
          <a href="https://github.com/dsazykin/Mainguard" target="_blank" rel="noreferrer noopener">
            repository
          </a>{' '}
          — this page is a file in the same source tree as the code it describes. A change that
          materially affects you will be announced to anyone on the waitlist.
        </p>
      </section>
    </div>
  );
}
