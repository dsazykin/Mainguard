import { Link } from 'react-router';
import {
  LEGAL_CONTACT_EMAIL,
  LEGAL_ENTITY,
  LEGAL_GOVERNING_LAW,
  LEGAL_JURISDICTION,
  LEGAL_LAST_UPDATED,
  LEGAL_VENUE,
} from '../config';

/**
 * Terms of service for mainguard.dev.
 *
 * Scope is deliberately narrow: this covers the website, the waitlist and the
 * contact form. It does NOT license the desktop application or the source —
 * those carry their own licence, and conflating the two would be wrong in
 * both directions. Nothing is sold through this site, so there is no purchase,
 * subscription or refund clause here; if that changes, this page needs a
 * lawyer before it needs an edit.
 */
export function Terms() {
  return (
    <div className="container legal">
      <p className="legal-updated">Last updated · {LEGAL_LAST_UPDATED}</p>
      <h1>Terms of service</h1>
      <p className="lede">
        These terms cover this website and the two things you can do on it — join the waitlist and
        send a message. They are short, because the site does little: nothing is sold here, and no
        account exists.
      </p>

      <nav className="legal-toc" aria-label="Contents">
        <h2>On this page</h2>
        <ol>
          <li><a href="#scope">What these terms cover</a></li>
          <li><a href="#use">Using the site</a></li>
          <li><a href="#waitlist">The waitlist</a></li>
          <li><a href="#forward">Statements about the product</a></li>
          <li><a href="#nothing-sold">Nothing is sold here</a></li>
          <li><a href="#software">The application and its source</a></li>
          <li><a href="#ip">Intellectual property</a></li>
          <li><a href="#feedback">Feedback you send</a></li>
          <li><a href="#availability">Availability and warranties</a></li>
          <li><a href="#liability">Liability</a></li>
          <li><a href="#changes">Changes to these terms</a></li>
          <li><a href="#law">Governing law</a></li>
        </ol>
      </nav>

      <section id="scope">
        <h2>1. What these terms cover</h2>
        <p>
          This site is operated by {LEGAL_ENTITY}, based in {LEGAL_JURISDICTION}, reachable at{' '}
          <a href={`mailto:${LEGAL_CONTACT_EMAIL}`} className="mono">
            {LEGAL_CONTACT_EMAIL}
          </a>
          . By using mainguard.dev you agree to what follows. If you do not, the remedy is simple:
          stop using the site.
        </p>
        <p>
          These terms govern <strong>the website only</strong>. They do not license the Mainguard
          application, and they are not a contract to supply any software. How your personal data
          is handled is covered separately in the <Link to="/privacy">privacy policy</Link>.
        </p>
      </section>

      <section id="use">
        <h2>2. Using the site</h2>
        <p>You may read anything here, link to it, and quote it with attribution. You may not:</p>
        <ul>
          <li>submit the forms automatically, in bulk, or in a way designed to defeat the bot check;</li>
          <li>submit anything unlawful, deliberately false, or someone else's personal data;</li>
          <li>attempt to gain access to the backend, the database, or any part not meant to be public;</li>
          <li>scrape the site in a way that degrades it for other people;</li>
          <li>use the name or branding in a way that implies endorsement or affiliation that does not exist.</li>
        </ul>
        <p>
          The forms are rate-limited. Submissions that look automated are rejected, which is not a
          judgement about you personally.
        </p>
        <p>
          The site is not directed at children. If you are under 16, use it with the involvement of
          a parent or guardian.
        </p>
      </section>

      <section id="waitlist">
        <h2>3. The waitlist</h2>
        <p>Joining the waitlist means you will be emailed when there is something to be emailed about. It is not:</p>
        <ul>
          <li>a purchase, a reservation, or a contract for anything;</li>
          <li>a promise that a given product will ship, or ship by a given date;</li>
          <li>a guarantee of access, a price, or any particular founding terms.</li>
        </ul>
        <p>
          Where founding terms or early access are mentioned on this site, they describe an
          intention. They become binding only if and when they are offered to you in writing with
          actual terms attached.
        </p>
        <p>
          You can leave at any time — ask through the <Link to="/contact">contact form</Link> or
          email{' '}
          <a href={`mailto:${LEGAL_CONTACT_EMAIL}`} className="mono">
            {LEGAL_CONTACT_EMAIL}
          </a>{' '}
          and your entry is deleted. No dark patterns, no retention offer.
        </p>
      </section>

      <section id="forward">
        <h2>4. Statements about the product</h2>
        <p>
          This site describes software under active development. Considerable care is taken to
          separate what exists from what does not: features described as planned are labelled as
          planned, and the Pro page keeps its unbuilt items in a section titled{' '}
          <em>"Honestly, not yet"</em> for exactly this reason.
        </p>
        <p>
          Even so, everything here is a description of intent at a moment in time, not a
          specification and not a warranty. Plans change, features get cut, and timelines move.
          Nothing on this site should be relied on as a commitment when making a decision that
          matters to you.
        </p>
      </section>

      <section id="nothing-sold">
        <h2>5. Nothing is sold here</h2>
        <p>
          There is no checkout, no subscription, no billing and no payment method on this site.
          Prices mentioned anywhere are indicative and are not an offer capable of acceptance. If
          paid products are introduced, they will come with their own terms, and those terms — not
          this page — will govern the purchase.
        </p>
      </section>

      <section id="software">
        <h2>6. The application and its source</h2>
        <p>
          The Mainguard desktop application is licensed separately. Whatever licence accompanies a
          download, or the licence file in the source repository, governs your use of that software
          — not these terms. Nothing here grants you any right to the application.
        </p>
      </section>

      <section id="ip">
        <h2>7. Intellectual property</h2>
        <p>
          The text, layout, design system and imagery of this site, and the Mainguard name and
          wordmark, belong to {LEGAL_ENTITY} except where stated otherwise. Fair quotation with
          attribution is fine. Wholesale reproduction, or use of the name and mark for another
          product, is not.
        </p>
        <p>
          The site links to third-party sites. Those are not under this site's control, and linking
          to one is not an endorsement of it.
        </p>
      </section>

      <section id="feedback">
        <h2>8. Feedback you send</h2>
        <p>
          If you send an idea, suggestion, bug report or feature request through the contact form,
          it may be used to improve the product without obligation, payment or attribution to you.
          This is the normal arrangement for unsolicited feedback, and it is stated plainly here so
          it is not a surprise. It does not apply to your personal data, which is governed by the{' '}
          <Link to="/privacy">privacy policy</Link>.
        </p>
        <p>
          If you have something to share that needs to stay confidential, say so before sending it,
          and agree terms first.
        </p>
      </section>

      <section id="availability">
        <h2>9. Availability and warranties</h2>
        <p>
          The site is provided as it is. There is no guarantee that it will be available, accurate,
          complete, or free of errors, and it may change or disappear without notice. To the fullest
          extent the law allows, implied warranties are excluded.
        </p>
        <div className="legal-callout">
          <p>
            <strong>This does not take away your consumer rights.</strong> If you are a consumer in
            the EU or the UK, you keep every right that mandatory law gives you, and nothing on this
            page limits them.
          </p>
        </div>
      </section>

      <section id="liability">
        <h2>10. Liability</h2>
        <p>
          To the extent permitted by law, {LEGAL_ENTITY} is not liable for indirect or consequential
          loss, lost profits, lost data, or business interruption arising from your use of this
          website.
        </p>
        <p>
          Nothing in these terms excludes or limits liability for death or personal injury caused by
          negligence, for fraud or fraudulent misrepresentation, or for anything else that cannot
          lawfully be excluded.
        </p>
      </section>

      <section id="changes">
        <h2>11. Changes to these terms</h2>
        <p>
          These terms can change. The date at the top changes with them, and because this page is a
          file in the project's source tree, every revision is visible in the{' '}
          <a href="https://github.com/dsazykin/Mainguard" target="_blank" rel="noreferrer noopener">
            repository
          </a>
          . Continuing to use the site after a change means you accept the current version.
        </p>
      </section>

      <section id="law">
        <h2>12. Governing law</h2>
        <p>
          These terms are governed by {LEGAL_GOVERNING_LAW}, and disputes go to {LEGAL_VENUE}. If
          you are a consumer resident elsewhere in the EU, you also keep the protection of the
          mandatory law of your own country, and you may bring proceedings there.
        </p>
        <p>
          Questions about any of this go through the <Link to="/contact">contact form</Link> or to{' '}
          <a href={`mailto:${LEGAL_CONTACT_EMAIL}`} className="mono">
            {LEGAL_CONTACT_EMAIL}
          </a>
          .
        </p>
      </section>
    </div>
  );
}
