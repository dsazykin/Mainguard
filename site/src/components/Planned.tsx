import { Link } from 'react-router';

/**
 * The mark that says "designed, not built yet".
 *
 * Every claim on this site is supposed to be true today. Where something is
 * described before it exists, it carries this mark, the page carries
 * <PlannedFootnote /> at the bottom, and both point at /roadmap.
 *
 * The rule is simple and worth keeping: if a feature is named anywhere in the
 * marketing copy and is not in the product, it gets the mark in the same
 * commit that names it. A reader should never have to guess which half of a
 * page is real.
 *
 * The dagger is hidden from assistive tech and replaced with real words, since
 * "dagger" read aloud tells a screen-reader user nothing.
 */
export function Planned({ what, linked = true }: { what?: string; linked?: boolean }) {
  const label = what ? `${what} is planned and not available yet` : 'Planned and not available yet';
  const body = (
    <>
      <span aria-hidden>†</span>
      <span className="visually-hidden">({label}{linked ? '. See the roadmap.' : '.'})</span>
    </>
  );

  // Inside an existing link (a whole card that is itself an anchor) the mark
  // must not be one too: nested anchors are invalid HTML and browsers recover
  // from them badly. The card's own destination explains the mark well enough.
  if (!linked) {
    return (
      <span className="planned-mark" title={`${label}.`}>
        {body}
      </span>
    );
  }

  return (
    <Link to="/roadmap" className="planned-mark" title={`${label}. See the roadmap.`}>
      {body}
    </Link>
  );
}

/** The footnote that explains the mark. One per page that uses it. */
export function PlannedFootnote() {
  return (
    <p className="planned-note">
      <span aria-hidden className="planned-note-mark">
        †
      </span>{' '}
      Marked items are designed but not built yet, and are not part of what you would get today.
      The <Link to="/roadmap">roadmap</Link> lists what exists, what is being wired together now,
      and what is still an intention.
    </p>
  );
}
