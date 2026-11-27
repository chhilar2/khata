import React, { useEffect, useRef, useState } from 'react';
import { createRoot } from 'react-dom/client';
import ManagedProfilesPage from './ManagedProfilesPage';
import ProfileStoryPage from './ProfileStoryPage';
import './style.css';

const accountFields = [
  { name: 'displayName', label: 'Display name', maxLength: 100, autoComplete: 'name' },
  { name: 'contactEmail', label: 'Contact email', type: 'email', maxLength: 254, autoComplete: 'email' },
  { name: 'phone', label: 'Phone number', type: 'tel', maxLength: 30, autoComplete: 'tel' },
  { name: 'country', label: 'Country code', maxLength: 2, autoComplete: 'country', hint: 'Two-letter code, for example US.' },
  { name: 'currency', label: 'Preferred currency', maxLength: 3, hint: 'Three-letter code, for example USD.' },
];

function AccountPage({ session, signOut, signingOut }) {
  const [account, setAccount] = useState(null);
  const [fields, setFields] = useState({});
  const [errors, setErrors] = useState({});
  const [message, setMessage] = useState('');
  const [loading, setLoading] = useState(true);
  const [saving, setSaving] = useState(false);
  const [missing, setMissing] = useState(false);
  const [conflict, setConflict] = useState(false);

  async function loadAccount(signal) {
    const response = await fetch('/api/account', { cache: 'no-store', signal });
    if (response.status === 401) {
      window.location.replace('/');
      return;
    }
    if (response.status === 404) {
      setMissing(true);
      return;
    }
    if (!response.ok) throw new Error('Account request failed');
    const data = await response.json();
    setAccount(data);
    setFields(Object.fromEntries(accountFields.map(({ name }) => [name, data[name] ?? ''])));
    setMissing(false);
    setConflict(false);
    setErrors({});
  }

  useEffect(() => {
    const controller = new AbortController();
    loadAccount(controller.signal)
      .catch(exception => {
        if (exception.name !== 'AbortError') setMessage('Unable to load your account. Please try again.');
      })
      .finally(() => {
        if (!controller.signal.aborted) setLoading(false);
      });
    return () => controller.abort();
  }, []);

  async function reload() {
    setLoading(true);
    setMessage('');
    try {
      await loadAccount();
    } catch {
      setMessage('Unable to load your account. Please try again.');
    } finally {
      setLoading(false);
    }
  }

  async function save(event) {
    event.preventDefault();
    setSaving(true);
    setErrors({});
    setMessage('');
    try {
      const response = await fetch('/api/account', {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json', 'X-CSRF-Token': session.csrfToken, 'If-Match': account.version },
        body: JSON.stringify(Object.fromEntries(accountFields.map(({ name }) => [name, fields[name]]))),
      });
      if (response.status === 401) {
        window.location.replace('/');
        return;
      }
      if (response.status === 404) {
        setMissing(true);
        return;
      }
      if (response.status === 409) {
        setConflict(true);
        setMessage('Your account changed elsewhere. Reload the latest information before saving; your unsaved changes will be lost.');
        return;
      }
      if (response.status === 400) {
        const data = await response.json().catch(() => null);
        if (data?.errors) {
          setErrors(data.errors);
          setMessage('Please check the highlighted fields.');
          return;
        }
      }
      if (!response.ok) throw new Error('Account update failed');
      try {
        await loadAccount();
        setMessage('Your account has been saved.');
      } catch {
        setConflict(true);
        setMessage('Your account was saved, but the latest version could not be loaded. Reload before editing again.');
      }
    } catch {
      setMessage('Unable to save your account. Please try again.');
    } finally {
      setSaving(false);
    }
  }

  return (
    <section className="portal wrap account-page">
      <a className="back-link" href="/portal">← Back to portal</a>
      <p className="eyebrow">YOUR ACCOUNT</p>
      <h1>Your details.</h1>
      <p className="intro">Manage the information you share with MoneySprout. All fields are optional.</p>
      {message && <p className="notice" role="status">{message}</p>}
      {loading ? <p>Loading your account...</p> : missing ? (
        <div className="account-panel">
          <p>Your account profile needs to be set up. Sign out and sign in again to continue.</p>
          <button className="button secondary" onClick={signOut} disabled={signingOut}>{signingOut ? 'Signing out...' : 'Sign out'}</button>
        </div>
      ) : account ? (
        <div className="account-panel">
          <div className="account-signin">
            <div><span>Sign-in provider</span><strong>{account.provider === 'google' ? 'Google' : account.provider}</strong></div>
            <div><span>Sign-in email</span><strong>{account.email || 'Not available'}</strong></div>
          </div>
          <form onSubmit={save} noValidate>
            <div className="account-fields">
              {accountFields.map(({ name, label, type, maxLength, autoComplete, hint }) => (
                <div className="field" key={name}>
                  <label htmlFor={name}>{label}</label>
                  {hint && <span className="field-hint" id={`${name}-hint`}>{hint}</span>}
                  <input id={name} name={name} type={type || 'text'} maxLength={maxLength}
                    autoComplete={autoComplete} value={fields[name] ?? ''} disabled={saving}
                    aria-invalid={Boolean(errors[name])}
                    aria-describedby={[hint && `${name}-hint`, errors[name] && `${name}-error`].filter(Boolean).join(' ') || undefined}
                    onChange={event => setFields(current => ({ ...current, [name]: event.target.value }))} />
                  {errors[name] && <span className="field-error" id={`${name}-error`}>{errors[name]}</span>}
                </div>
              ))}
            </div>
            <p className="field-hint">Contact email and phone are not verified and are not used to sign in.</p>
            <div className="actions">
              <button className="button" type="submit" disabled={saving || conflict}>{saving ? 'Saving...' : 'Save changes'}</button>
              {conflict && <button className="button button-outline" type="button" onClick={reload}>Reload latest details</button>}
            </div>
          </form>
        </div>
      ) : <button className="button button-outline" onClick={reload}>Try again</button>}
    </section>
  );
}

function App() {
  const [session, setSession] = useState(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState(
    new URLSearchParams(window.location.search).has('login')
      ? 'Sign-in could not be completed. Please try again.'
      : '',
  );
  const [signingOut, setSigningOut] = useState(false);
  const [preparingLogin, setPreparingLogin] = useState(false);
  const [googleReady, setGoogleReady] = useState(false);
  const googleButton = useRef(null);
  const isPortal = window.location.pathname === '/portal';
  const isAccount = isPortal && new URLSearchParams(window.location.search).get('view') === 'account';
  const isProfiles = isPortal && new URLSearchParams(window.location.search).get('view') === 'profiles';
  const isProfile = isPortal && new URLSearchParams(window.location.search).get('view') === 'profile';
  const isInvestments = isPortal && new URLSearchParams(window.location.search).get('view') === 'investments';
  const profileId = new URLSearchParams(window.location.search).get('id');

  useEffect(() => {
    const controller = new AbortController();
    async function loadSession() {
      try {
        const response = await fetch('/api/auth/session', {
          cache: 'no-store',
          signal: controller.signal,
        });
        if (response.status === 401) {
          if (isPortal) window.location.replace('/');
        } else if (response.ok) {
          setSession(await response.json());
        } else {
          throw new Error('Session request failed');
        }
      } catch (exception) {
        if (exception.name !== 'AbortError') {
          setError('Unable to check your session. Please try again later.');
        }
      } finally {
        if (!controller.signal.aborted) setLoading(false);
      }
    }
    loadSession();
    return () => controller.abort();
  }, [isPortal]);

  async function signOut() {
    setSigningOut(true);
    setError('');
    try {
      const response = await fetch('/api/auth/logout', {
        method: 'POST',
        headers: { 'X-CSRF-Token': session.csrfToken },
      });
      if (!response.ok) throw new Error('Sign-out failed');
      window.location.assign('/');
    } catch {
      setError('Unable to sign out. Please try again.');
      setSigningOut(false);
    }
  }

  async function prepareLogin() {
    setPreparingLogin(true);
    setError('');
    try {
      const start = await fetch('/api/auth/google/start', { method: 'POST' });
      if (!start.ok) throw new Error('Unable to start sign-in');
      const { state, nonce, clientId } = await start.json();
      if (!window.google?.accounts?.id) {
        await new Promise((resolve, reject) => {
          const script = document.createElement('script');
          script.src = 'https://accounts.google.com/gsi/client';
          script.async = true;
          script.onload = resolve;
          script.onerror = () => {
            script.remove();
            reject(new Error('Unable to load Google sign-in'));
          };
          document.head.appendChild(script);
        });
      }
      window.google.accounts.id.initialize({
        client_id: clientId,
        nonce,
        callback: async ({ credential }) => {
          try {
            const response = await fetch('/api/auth/google/complete', {
              method: 'POST',
              headers: { 'Content-Type': 'application/json' },
              body: JSON.stringify({ state, credential }),
            });
            if (!response.ok) throw new Error('Unable to complete sign-in');
            window.location.assign('/portal');
          } catch {
            googleButton.current?.replaceChildren();
            setGoogleReady(false);
            setError('Sign-in could not be completed. Please try again.');
          }
        },
      });
      window.google.accounts.id.renderButton(googleButton.current, { type: 'standard', theme: 'outline', size: 'large' });
      setGoogleReady(true);
    } catch {
      setError('Unable to start Google sign-in. Please try again later.');
    } finally {
      setPreparingLogin(false);
    }
  }

  return (
    <div className="page">
      <header className="site-header wrap">
        <a className="brand" href="/"><span className="brand-icon" aria-hidden="true">🌱</span> MoneySprout</a>
        {!isPortal && (
          <nav className="nav-links" aria-label="Main navigation">
            <a href="#ideas">The big ideas</a>
            <a href="#how">How it works</a>
            <a href="#parents">For parents</a>
          </nav>
        )}
        {!isPortal && <a className="button nav-button" href={session ? '/portal' : '#signin'}>{session ? 'Your portal' : 'Parent sign in'}</a>}
      </header>
      <main>
        {error && <p className="notice wrap" role="alert">{error}</p>}
        {(isProfile || isInvestments) && session ? (
          <ProfileStoryPage profileId={profileId} investmentsPage={isInvestments} />
        ) : isProfiles && session ? (
          <ManagedProfilesPage session={session} />
        ) : isAccount && session ? (
          <AccountPage session={session} signOut={signOut} signingOut={signingOut} />
        ) : isPortal ? (
          <section className="portal wrap">
            {loading ? <p>Checking your session...</p> : session ? (
              <>
                <p className="eyebrow">YOUR PORTAL</p>
                <h1>Welcome to MoneySprout.</h1>
                <p className="intro">Create managed profiles for yourself or people you manage. Ledger features are coming soon.</p>
                <div className="actions">
                  <a className="button" href="/portal?view=account">Your account</a>
                  <a className="button button-outline" href="/portal?view=profiles">Managed profiles</a>
                  <button className="button secondary" onClick={signOut} disabled={signingOut}>
                    {signingOut ? 'Signing out...' : 'Sign out'}
                  </button>
                </div>
              </>
            ) : <p>Sign-in is required to access the portal.</p>}
          </section>
        ) : (
          <>
            <section className="hero wrap" id="signin">
              <div className="hero-copy">
                <p className="eyebrow">✦ &nbsp; A smarter start to money</p>
                <h1>Little steps.<br />Big <span>money</span><br />confidence.</h1>
                <p className="intro">Money skills grow with practice. MoneySprout is a place for parents to guide their kids through saving, goals, and everyday money moments.</p>
                <div className="actions">
                  {loading ? <p className="session-status">Checking your session...</p> : session ? (
                    <a className="button" href="/portal">Go to your portal <span aria-hidden="true">→</span></a>
                  ) : !googleReady && (
                    <button className="button" onClick={prepareLogin} disabled={preparingLogin}>
                      {preparingLogin ? 'Preparing sign-in...' : 'Continue with Google'}
                    </button>
                  )}
                  <div ref={googleButton} />
                  <a className="button button-outline" href="#how">See how it works</a>
                </div>
                <p className="trust"><span aria-hidden="true">🌿</span> Designed for families. Managed by parents.</p>
              </div>
              <div className="hero-art" aria-hidden="true">
                <div className="arch" />
                <span className="sun">☀️</span>
                <span className="plant">🪴</span>
                <span className="book">📚</span>
                <span className="pig">🐷</span>
                <span className="car">🚗</span>
                <span className="coin">$</span>
                <div className="float float-goal">🎯 Savings goals<strong>Dream big!</strong></div>
                <div className="float float-learning">💡 Money moments<strong>Learn together</strong></div>
                <div className="float float-habits">✨ Small habits, big wins</div>
              </div>
            </section>

            <section className="section wrap center" id="ideas">
              <h2>Money is more than math.</h2>
              <p className="section-intro">It is about choices, confidence, and discovering what matters. Small money moments can become lessons that last.</p>
              <div className="cards">
                <article className="card"><span className="card-icon" aria-hidden="true">📖</span><h3>Learn</h3><p>Talk about money basics and the choices we make every day.</p></article>
                <article className="card"><span className="card-icon" aria-hidden="true">🐷</span><h3>Save</h3><p>Build the habit of setting money aside for something special.</p></article>
                <article className="card"><span className="card-icon" aria-hidden="true">🛍️</span><h3>Choose</h3><p>Explore needs, wants, and thoughtful spending together.</p></article>
                <article className="card"><span className="card-icon" aria-hidden="true">🚀</span><h3>Achieve</h3><p>Turn small steps into progress worth celebrating.</p></article>
              </div>
            </section>

            <section className="section section-pale" id="how">
              <div className="wrap center">
                <h2>One family. One journey.</h2>
                <p className="section-intro">A little guidance today can help good habits take root tomorrow.</p>
                <div className="steps">
                  <article className="step"><span>01</span><h3>Parents lead the way</h3><p>Sign in to your parent portal, the home for your family's money journey.</p></article>
                  <article className="step"><span>02</span><h3>Dream together</h3><p>Talk with your kids about the things they want to save for and why they matter.</p></article>
                  <article className="step"><span>03</span><h3>Watch habits grow</h3><p>Celebrate progress and use everyday moments to talk about money.</p></article>
                </div>
                <p className="availability">The parent portal is available now. Ledgers, balances, and goals are coming soon.</p>
              </div>
            </section>

            <section className="section wrap" id="parents">
              <div className="parents">
                <div>
                  <p className="eyebrow eyebrow-dark">Made for parents</p>
                  <h2>Grow money confidence, together.</h2>
                  <p>Every family's journey starts with a parent. MoneySprout is being built to help you keep track of your kids' ledgers and encourage thoughtful money habits.</p>
                  <a className="button button-light" href={session ? '/portal' : '#signin'}>{session ? 'Go to your portal' : 'Get started'} <span aria-hidden="true">→</span></a>
                </div>
                <div className="parent-ideas">
                  <div><span aria-hidden="true">🌱</span><p><strong>Parent-led</strong><br />A space for parents to manage their children's ledgers.</p></div>
                  <div><span aria-hidden="true">🎯</span><p><strong>Goals worth sharing</strong><br />Make room for conversations about saving and dreaming.</p></div>
                  <div><span aria-hidden="true">✨</span><p><strong>Little steps</strong><br />Celebrate the small wins along the way.</p></div>
                </div>
              </div>
            </section>
          </>
        )}
      </main>
      <footer className="footer">
        <div className="wrap footer-inner">
          <div>
            <a className="brand footer-brand" href="/"><span className="brand-icon" aria-hidden="true">🌱</span> MoneySprout</a>
            <p>Helping families grow a healthy relationship with money, one small step at a time.</p>
          </div>
          {!isPortal && <a href="#signin">Back to top ↑</a>}
        </div>
      </footer>
    </div>
  );
}

createRoot(document.getElementById('root')).render(<App />);
