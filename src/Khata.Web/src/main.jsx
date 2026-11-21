import React, { useEffect, useRef, useState } from 'react';
import { createRoot } from 'react-dom/client';
import './style.css';

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
      <header className="site-header">
        <a className="brand" href="/">khata<span>.</span></a>
        <span className="header-note">Little steps. Big possibilities.</span>
      </header>
      <main>
        {error && <p className="notice" role="alert">{error}</p>}
        {isPortal ? (
          <section className="hero">
            {loading ? <p>Checking your session...</p> : session ? (
              <>
                <p className="eyebrow">YOUR PORTAL</p>
                <h1>Welcome to Khata.</h1>
                <p className="intro">Your space to manage a ledger for your kids is ready. More features are coming soon.</p>
                <button className="button secondary" onClick={signOut} disabled={signingOut}>
                  {signingOut ? 'Signing out...' : 'Sign out'}
                </button>
              </>
            ) : <p>Sign-in is required to access the portal.</p>}
          </section>
        ) : (
          <>
            <section className="hero">
              <p className="eyebrow">A BRIGHTER START WITH MONEY</p>
              <h1>Small savings.<br /><span>Big dreams.</span></h1>
              <p className="intro">Khata is a place for parents to manage a ledger for their kids and keep track of their goals and earnings.</p>
              {loading ? <p>Checking your session...</p> : session ? (
                <a className="button" href="/portal">Go to your portal <span aria-hidden="true">→</span></a>
              ) : !googleReady && (
                <button className="button" onClick={prepareLogin} disabled={preparingLogin}>
                  {preparingLogin ? 'Preparing sign-in...' : 'Continue with Google'}
                </button>
              )}
              <div ref={googleButton} />
            </section>
            <section className="features" aria-label="What Khata is about">
              <article><span>01</span><h2>Dream it</h2><p>Make room for the things worth saving for.</p></article>
              <article><span>02</span><h2>Earn it</h2><p>Discover how effort can help dreams grow.</p></article>
              <article><span>03</span><h2>Grow it</h2><p>Build thoughtful money habits, one step at a time.</p></article>
            </section>
            <p className="footnote">Balances, investments, and gig jobs are coming soon.</p>
          </>
        )}
      </main>
    </div>
  );
}

createRoot(document.getElementById('root')).render(<App />);
