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
        {isPortal ? (
          <section className="portal wrap">
            {loading ? <p>Checking your session...</p> : session ? (
              <>
                <p className="eyebrow">YOUR PORTAL</p>
                <h1>Welcome to MoneySprout.</h1>
                <p className="intro">Your space to manage a ledger for your kids is ready. More features are coming soon.</p>
                <button className="button secondary" onClick={signOut} disabled={signingOut}>
                  {signingOut ? 'Signing out...' : 'Sign out'}
                </button>
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
