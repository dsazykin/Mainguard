import { Link } from 'react-router';
import { VERIFICATION_REVIEWED } from '../config';

/**
 * How verification works: the one technical page.
 *
 * Scope is deliberate and should stay narrow. This page describes the
 * mechanism, the threat model, and the limits. It carries NO version numbers,
 * NO performance figures, NO API surface and NO dates for unshipped work,
 * because those are the parts that rot and a technical page that is wrong
 * costs more trust than no technical page at all.
 *
 * Everything here is checked against PRODUCT.md. When the mechanism changes,
 * this page changes in the same commit and VERIFICATION_REVIEWED moves.
 *
 * Not exempt from the copy guard: no em dashes.
 */
export function Verification() {
  return (
    <div className="container legal">
      <p className="legal-updated">Last reviewed · {VERIFICATION_REVIEWED}</p>
      <h1>How verification works</h1>
      <p className="lede">
        Mainguard claims that agent work is safe to merge. That claim is worth nothing unless you
        can see the mechanism behind it, so here it is in full, including what it does not do.
      </p>

      <nav className="legal-toc" aria-label="Contents">
        <h2>On this page</h2>
        <ol>
          <li><a href="#problem">Why self-reporting fails</a></li>
          <li><a href="#properties">The four properties</a></li>
          <li><a href="#lifecycle">What happens to a branch</a></li>
          <li><a href="#threats">What this stops</a></li>
          <li><a href="#limits">What this does not do</a></li>
          <li><a href="#currency">Keeping this page honest</a></li>
        </ol>
      </nav>

      <section id="problem">
        <h2>1. Why self-reporting fails</h2>
        <p>
          Ask a coding agent whether its work is correct and it will tell you. It may even be
          right. The trouble is that you cannot tell the difference between an agent that ran your
          tests and an agent that printed a line saying it did, because both arrive as text in the
          same stream.
        </p>
        <p>
          The rule Mainguard is built on is short: <strong>a value supplied by the thing being
          judged is not evidence.</strong> Every guarantee on this page is readable from outside
          the thing it describes. Where something has to be taken on report rather than observed,
          it is labelled as such rather than quietly counted.
        </p>
      </section>

      <section id="properties">
        <h2>2. The four properties</h2>
        <p>
          Verification in Mainguard means four specific things. Each one closes a way that a green
          check can lie to you.
        </p>

        <h3>It is observed from outside the container</h3>
        <p>
          The verdict is the container's real exit code, read by the daemon from the container
          runtime. The agent inside cannot write to it, cannot intercept it, and has no path to the
          component that reads it. Printing "all tests passed" produces a green line in a log and
          changes nothing about whether the branch is eligible.
        </p>

        <h3>It is measured against current main</h3>
        <p>
          A branch is verified against the <span className="mono">main</span> that exists now, not
          the one that existed when the agent started. When anything merges, every other verified
          branch is invalidated and re-verified before it can land. There is no window where a
          branch is still wearing a green check it earned against a main that has since moved.
        </p>

        <h3>It is pinned to a command</h3>
        <p>
          The resolved test command and the hash of its configuration are recorded immutably at the
          moment of the run. An agent cannot earn a pass by editing the test config, narrowing the
          suite or swapping the command for something cheaper, because the record of what was
          actually run travels with the result.
        </p>

        <h3>It is never automatic</h3>
        <p>
          Passing does not merge anything. It earns a branch the right to be considered, and then a
          human decides. The merge itself is an atomic compare-and-swap, so two agents finishing at
          the same moment cannot both land, and neither can land on top of a main that changed
          underneath them.
        </p>
      </section>

      <section id="lifecycle">
        <h2>3. What happens to a branch</h2>
        <p>The whole path, in order:</p>
        <ol>
          <li>
            <strong>A plan, before any code.</strong> A coordinator breaks the work into a plan and
            stops. No worker starts until you have read it and let it through. The coordinator's
            own tool surface is a small contract with nothing in common with a worker's, so the
            thing that plans the work cannot quietly begin doing it.
          </li>
          <li>
            <strong>Work happens in a jail.</strong> Each agent gets a hardened container and its
            own worktree. Your working directory is not in play at any point, uncommitted changes
            included.
          </li>
          <li>
            <strong>Verification runs in the worker's own sandbox</strong>, and the daemon reads the
            result from outside it.
          </li>
          <li>
            <strong>The branch becomes eligible</strong>, not merged. If it can no longer merge
            cleanly it is parked rather than silently dropped.
          </li>
          <li>
            <strong>You review.</strong> Diffs are ranked by risk so attention goes where it
            matters, every hunk carries which agent and which run produced it, and a change flagged
            as sensitive needs an explicit acknowledgement before it can be approved.
          </li>
          <li>
            <strong>You merge</strong>, atomically. Every other verified branch is invalidated and
            goes back through verification against the main you just created.
          </li>
        </ol>
        <p>
          Every step in that list lands in a hash-chained, tamper-evident audit log with typed
          events and timestamp anchoring, and there is a command that checks the chain.
        </p>
      </section>

      <section id="threats">
        <h2>4. What this stops</h2>
        <p>
          The useful way to read a security design is by what it refuses, so here are the attempts
          and why each fails.
        </p>
        <div className="legal-table-wrap">
          <table className="legal-table">
            <thead>
              <tr>
                <th scope="col">The attempt</th>
                <th scope="col">Why it fails</th>
              </tr>
            </thead>
            <tbody>
              <tr>
                <td>An agent claims the tests passed</td>
                <td>The verdict is the exit code, read by the daemon from outside the container. Output is not evidence.</td>
              </tr>
              <tr>
                <td>An agent weakens the test command or its config</td>
                <td>The resolved command and a hash of its config are pinned immutably to the result.</td>
              </tr>
              <tr>
                <td>A branch passes, then main moves underneath it</td>
                <td>Merging invalidates every other verified branch, which is re-verified before it can land.</td>
              </tr>
              <tr>
                <td>Two agents finish and race to merge</td>
                <td>The merge is an atomic compare-and-swap. Exactly one lands.</td>
              </tr>
              <tr>
                <td>An agent tries to clone your other repositories, or push</td>
                <td>Default-deny egress. Model APIs and package registries resolve from a jail, your git host does not.</td>
              </tr>
              <tr>
                <td>An agent reaches for your working directory</td>
                <td>Agents work their own worktrees inside jails. The host checkout is never mounted into one.</td>
              </tr>
              <tr>
                <td>A dependency feed is poisoned mid-run</td>
                <td>Toolchains are built ahead of time. A jail does not fetch at runtime.</td>
              </tr>
              <tr>
                <td>A misbehaving worker ignores a stop</td>
                <td>Stop freezes the queue first, then pauses agents, under a ceiling a worker cannot stretch.</td>
              </tr>
              <tr>
                <td>Someone edits the record afterwards</td>
                <td>The audit log is hash-chained and tamper-evident, and the chain can be checked with one command.</td>
              </tr>
            </tbody>
          </table>
        </div>
      </section>

      <section id="limits">
        <h2>5. What this does not do</h2>
        <p>
          A verification system that is described only by its strengths is a sales page. These are
          the limits, and they are real.
        </p>
        <ul>
          <li>
            <strong>It does not tell you the code is good.</strong> It tells you your gates passed.
            If your test suite is thin, verification will faithfully certify thin work. Mainguard
            raises the floor under agent output to the standard you already hold your own work to,
            and no higher.
          </li>
          <li>
            <strong>It is not a code review.</strong> Deterministic gates have no opinion about
            design, naming, architecture or whether the change was a good idea. That judgement is
            yours, which is why nothing merges without you.
          </li>
          <li>
            <strong>It does not protect you from your own gates.</strong> If a test command is
            wrong, pinning it immutably records exactly which wrong command ran.
          </li>
          <li>
            <strong>It reduces the cost of a bad merge, not the possibility of one.</strong> A
            change can pass every gate, be approved, and still be wrong. What you get is a clean
            history, a full record of who approved what, and a fast way back.
          </li>
          <li>
            <strong>The stop control is bounded by design and has not been measured.</strong> When
            a number for it appears on this page, it will be one that was measured rather than
            estimated.
          </li>
        </ul>
      </section>

      <section id="currency">
        <h2>6. Keeping this page honest</h2>
        <p>
          This page carries no version numbers, no performance figures and no dates for unshipped
          work, because those are the parts that go stale first and a technical page that is wrong
          costs more than no technical page at all. It describes the mechanism, which changes
          rarely, and when it does change this page changes with it.
        </p>
        <p>
          Features that are designed but not built are listed as such on the{' '}
          <Link to="/pro">Pro page</Link>, under a heading that says so plainly. If you find
          something here that does not match what the product does, that is a bug worth reporting
          through the <Link to="/contact">contact form</Link>.
        </p>
      </section>
    </div>
  );
}
