import React, { useEffect, useState } from 'react';

async function getJson(url, signal) {
  const response = await fetch(url, { cache: 'no-store', signal });
  if (response.status === 401) {
    window.location.replace('/');
    throw new Error('Session expired');
  }
  if (!response.ok) throw new Error(response.status === 404 ? 'not-found' : 'request-failed');
  return response.json();
}

function useResource(url) {
  const [state, setState] = useState({ status: 'loading', data: null });
  const [attempt, setAttempt] = useState(0);
  useEffect(() => {
    if (!url) return;
    const controller = new AbortController();
    setState({ status: 'loading', data: null });
    getJson(url, controller.signal)
      .then(data => setState({ status: 'ready', data }))
      .catch(error => {
        if (error.name !== 'AbortError') setState({ status: error.message === 'not-found' ? 'missing' : 'error', data: null });
      });
    return () => controller.abort();
  }, [url, attempt]);
  return { ...state, retry: () => setAttempt(value => value + 1) };
}

function usePage(url) {
  const [state, setState] = useState({ status: 'loading', items: [], token: null, failedToken: null });
  const [attempt, setAttempt] = useState(0);
  useEffect(() => {
    if (!url) return;
    const controller = new AbortController();
    setState({ status: 'loading', items: [], token: null, failedToken: null });
    getPage(url, controller.signal)
      .then(page => setState({ status: 'ready', items: page.items, token: page.continuationToken, failedToken: null }))
      .catch(error => {
        if (error.name !== 'AbortError') setState({ status: 'error', items: [], token: null, failedToken: null });
      });
    return () => controller.abort();
  }, [url, attempt]);

  async function loadMore(token) {
    setState(current => ({ ...current, status: 'loading-more', failedToken: null }));
    try {
      const query = new URLSearchParams({ continuationToken: token });
      const page = await getPage(`${url}?${query}`);
      setState(current => ({ status: 'ready', items: [...current.items, ...page.items], token: page.continuationToken, failedToken: null }));
    } catch {
      setState(current => ({ ...current, status: 'more-error', failedToken: token }));
    }
  }

  return {
    ...state,
    retry: () => setAttempt(value => value + 1),
    loadMore: () => loadMore(state.failedToken ?? state.token),
  };
}

async function getPage(url, signal) {
  const page = await getJson(url, signal);
  if (!Array.isArray(page.items) || (page.continuationToken != null && typeof page.continuationToken !== 'string'))
    throw new Error('Invalid list response');
  return page;
}

function money(value, currency) {
  if (!currency) return 'Currency not set';
  if (typeof value !== 'string' || !/^-?\d+\.\d{2}$/.test(value)) return 'Unavailable';
  const negative = value.startsWith('-');
  const [whole, fraction] = (negative ? value.slice(1) : value).split('.');
  const grouped = whole.replace(/\B(?=(\d{3})+(?!\d))/g, ',');
  return `${negative ? '−' : ''}${currency === 'USD' ? '$' : `${currency} `}${grouped}.${fraction}`;
}

function dateLabel(value) {
  if (!value) return null;
  const date = new Date(value.length === 10 ? `${value}T00:00:00Z` : value);
  return Number.isNaN(date.getTime()) ? null : new Intl.DateTimeFormat('en-US', {
    month: 'short', day: 'numeric', year: 'numeric', timeZone: 'UTC',
  }).format(date);
}

function label(value) {
  const labels = {
    inprogress: 'In progress', planned: 'Planned', completed: 'Completed', cancelled: 'Cancelled',
    unfunded: 'Unfunded', active: 'Active', closed: 'Closed',
    deposit: 'Cash deposit', withdrawal: 'Cash withdrawal', 'gig-payout': 'Gig payout',
    'gig-payout-reversal': 'Gig payout reversal', 'investment-funding': 'Investment funding',
    daily: 'Daily', monthly: 'Monthly', quarterly: 'Quarterly', annually: 'Annual', simple: 'Simple interest',
  };
  return labels[value] ?? value;
}

function PageStatus({ page, name, empty }) {
  if (page.status === 'loading') return <p className="story-muted">Loading {name}...</p>;
  if (page.status === 'error') return (
    <div role="alert"><p>Unable to load {name}.</p><button className="story-link" type="button" onClick={page.retry}>Try again</button></div>
  );
  if (!page.items.length && !page.token && page.status === 'ready') return <p className="story-muted">{empty}</p>;
  return null;
}

function MoreButton({ page, name }) {
  if (!page.token && page.status !== 'more-error') return null;
  return (
    <div className="story-more">
      {page.status === 'more-error' && <p role="alert">Unable to load more {name}.</p>}
      <button className="story-link" type="button" disabled={page.status === 'loading-more'} onClick={page.loadMore}>
        {page.status === 'loading-more' ? 'Loading...' : page.status === 'more-error' ? 'Retry' : `Load more ${name}`}
      </button>
    </div>
  );
}

function Investments({ balanceId, currency, detailed, profileId }) {
  const page = usePage(`/api/balances/${encodeURIComponent(balanceId)}/investments`);
  return (
    <section className="story-panel" aria-labelledby="investments-title">
      <div className="story-panel-top"><span className="story-icon" aria-hidden="true">🌿</span>
        <h2 id="investments-title">{detailed ? 'Your investments' : 'Growing savings'}</h2></div>
      <PageStatus page={page} name="investments" empty="No investments yet." />
      {page.items.length > 0 && <ul className="story-list">{page.items.map(investment => (
        <li key={investment.id}>
          <div className="story-row"><strong>{investment.kind === 'fixed-rate' ? 'Fixed-rate investment' : 'Market-linked investment'}</strong>
            <span className="story-badge">{label(investment.state)}</span></div>
          <p className="story-muted story-small">
            {investment.state === 'unfunded' ? 'Not funded' : `Original amount: ${money(investment.principal, currency)}`}
            {investment.fundedOn && ` · Funded ${dateLabel(investment.fundedOn)}`}
          </p>
          {investment.state === 'active' && <p className="story-small">Current value unavailable</p>}
          {investment.state === 'closed' && investment.closingValue != null &&
            <p className="story-small">Closing value: {money(investment.closingValue, currency)}</p>}
          {detailed && investment.kind === 'fixed-rate' && (
            <p className="story-small">{investment.annualRatePercent}% annual rate · {label(investment.compounding)}</p>
          )}
        </li>
      ))}</ul>}
      <MoreButton page={page} name="investments" />
      {!detailed && <a className="story-link story-panel-link" href={`/portal?view=investments&id=${encodeURIComponent(profileId)}`}>Explore investments →</a>}
    </section>
  );
}

function Gigs({ balanceId, currency }) {
  const page = usePage(`/api/balances/${encodeURIComponent(balanceId)}/gigs`);
  return (
    <section className="story-panel" aria-labelledby="gigs-title">
      <div className="story-panel-top"><span className="story-icon story-icon-sand" aria-hidden="true">✦</span><h2 id="gigs-title">Small jobs</h2></div>
      <PageStatus page={page} name="gigs" empty="No gigs yet." />
      {page.items.length > 0 && <ul className="story-list">{page.items.map(gig => (
        <li key={gig.id}>
          <div className="story-row"><strong>{gig.name}</strong><span className="story-badge story-badge-sand">{label(gig.state)}</span></div>
          <p className="story-muted story-small">Virtual payout: {money(gig.payout, currency)}{gig.startDate && ` · Started ${dateLabel(gig.startDate)}`}</p>
          {gig.description && <p className="story-small">{gig.description}</p>}
        </li>
      ))}</ul>}
      <MoreButton page={page} name="gigs" />
    </section>
  );
}

function Ledger({ balanceId, currency }) {
  const page = usePage(`/api/balances/${encodeURIComponent(balanceId)}/ledger`);
  return (
    <section className="story-panel" aria-labelledby="ledger-title">
      <h2 id="ledger-title">Latest money moments</h2>
      <PageStatus page={page} name="activity" empty="No activity yet." />
      {page.items.length > 0 && <ul className="story-list">{page.items.map(entry => (
        <li className="story-row" key={entry.id}>
          <div><strong>{label(entry.operation)}</strong><p className="story-muted story-small">{dateLabel(entry.createdAt)}</p></div>
          <strong>{money(entry.amount, currency)}</strong>
        </li>
      ))}</ul>}
      <MoreButton page={page} name="activity" />
    </section>
  );
}

export default function ProfileStoryPage({ profileId, investmentsPage }) {
  const profile = useResource(profileId ? `/api/managed-profiles/${encodeURIComponent(profileId)}` : null);
  const balance = useResource(profile.status === 'ready' && !profile.data.deletionPending
    ? `/api/managed-profiles/${encodeURIComponent(profileId)}/balance` : null);
  const profileUrl = `/portal?view=profile&id=${encodeURIComponent(profileId ?? '')}`;
  const editUrl = `/portal?view=profiles&edit=${encodeURIComponent(profileId ?? '')}`;
  const currency = balance.data?.currency;

  return (
    <section className="portal wrap story-page">
      <a className="back-link" href={investmentsPage ? profileUrl : '/portal?view=profiles'}>
        ← {investmentsPage ? 'Back to profile' : 'Back to profiles'}
      </a>
      {!profileId || profile.status === 'missing' ? <p role="alert">This profile no longer exists or is unavailable.</p>
        : profile.status === 'error' ? <div role="alert"><p>Unable to load this profile.</p><button className="button button-outline" onClick={profile.retry}>Try again</button></div>
          : profile.status === 'loading' ? <p>Loading profile...</p> : (
            <>
              <div className="story-heading">
                <div><p className="story-eyebrow">{investmentsPage ? 'Plant a seed' : 'A little progress, every day'}</p>
                  <h1>{investmentsPage ? 'Growing savings' : `Growing with ${profile.data.displayName}`} <span aria-hidden="true">🌿</span></h1>
                  <p className="story-muted">{investmentsPage ? 'Explore what steady growth can look like.' : `A snapshot of ${profile.data.displayName}'s virtual money journey.`}</p>
                </div>
                {!investmentsPage && <a className="button button-outline" href={editUrl}>{profile.data.deletionPending ? 'Continue deletion' : 'Edit profile'}</a>}
              </div>
              {profile.data.deletionPending ? <p className="notice">Deletion is in progress. <a href={editUrl}>Continue deletion</a> to remove this profile and its records.</p>
                : balance.status === 'loading' ? <p>Loading balance...</p>
                  : balance.status !== 'ready' ? <div className="notice" role="alert"><p>Unable to load this profile's balance.</p><button className="story-link" onClick={balance.retry}>Try again</button></div>
                    : (
                      <>
                        <div className="story-hero">
                          <div className="story-row"><div>
                            <p className="story-eyebrow">{investmentsPage ? 'Growth so far' : 'The big picture'}</p>
                            <p className="story-amount">{!investmentsPage && balance.data.netWorthAvailable && balance.data.netWorth != null
                              ? money(balance.data.netWorth, currency) : 'Unavailable'}</p>
                            <p className="story-muted">{investmentsPage ? 'Current investment values and growth are not available yet.' : 'Total net worth'}
                              {!investmentsPage && !balance.data.netWorthAvailable && ' · investment valuation is not available yet.'}</p>
                          </div><span className="story-sprout" aria-hidden="true">🌱</span></div>
                          {!investmentsPage && <div className="story-balance"><div><span>Ready to use</span>
                            <strong>{balance.data.currency ? `${money(balance.data.cash, currency)} cash` : 'Currency not set'}</strong></div>
                            <div><span>Growing</span><strong>Investment value unavailable</strong></div></div>}
                        </div>
                        <p className="story-footnote">These are virtual records, not real accounts or money. Financial actions are not available yet.</p>
                        <div className={investmentsPage ? 'story-stack' : 'story-grid'}>
                          <Investments balanceId={balance.data.id} currency={currency} detailed={investmentsPage} profileId={profileId} />
                          {!investmentsPage && <Gigs balanceId={balance.data.id} currency={currency} />}
                        </div>
                        {!investmentsPage && <Ledger balanceId={balance.data.id} currency={currency} />}
                        {investmentsPage && <p className="story-note"><strong>About transfers:</strong> Closing an investment transfers its full current value to cash or an unfunded investment. Market-linked funding and prices are not available yet.</p>}
                      </>
                    )}
            </>
          )}
    </section>
  );
}
