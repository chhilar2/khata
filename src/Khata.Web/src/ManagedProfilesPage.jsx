import React, { useEffect, useRef, useState } from 'react';

const emptyFields = { displayName: '', dateOfBirth: '' };

export default function ManagedProfilesPage({ session }) {
  const [profiles, setProfiles] = useState([]);
  const [continuation, setContinuation] = useState(null);
  const [selected, setSelected] = useState(null);
  const [fields, setFields] = useState(emptyFields);
  const [errors, setErrors] = useState({});
  const [mode, setMode] = useState('list');
  const [loading, setLoading] = useState(true);
  const [listLoaded, setListLoaded] = useState(false);
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState('');
  const [conflict, setConflict] = useState(false);
  const [confirmDelete, setConfirmDelete] = useState(false);
  const createRequest = useRef(null);

  async function fetchProfiles(token, signal) {
    const query = token ? `?${new URLSearchParams({ continuationToken: token })}` : '';
    const response = await fetch(`/api/managed-profiles${query}`, { cache: 'no-store', signal });
    if (response.status === 401) {
      window.location.replace('/');
      return;
    }
    if (!response.ok) throw new Error('Unable to load profiles');
    const page = await response.json();
    setProfiles(current => token ? [...current, ...page.items] : page.items);
    setContinuation(page.continuationToken);
    setListLoaded(true);
  }

  useEffect(() => {
    const controller = new AbortController();
    fetchProfiles(null, controller.signal)
      .catch(error => {
        if (error.name !== 'AbortError') setMessage('Unable to load profiles. Please try again.');
      })
      .finally(() => {
        if (!controller.signal.aborted) setLoading(false);
      });
    return () => controller.abort();
  }, []);

  useEffect(() => {
    const editId = new URLSearchParams(window.location.search).get('edit');
    if (editId) openProfile(editId);
  }, []);

  async function loadList(token = null) {
    setBusy(true);
    setMessage('');
    try {
      await fetchProfiles(token);
    } catch {
      setMessage('Unable to load profiles. Please try again.');
    } finally {
      setBusy(false);
      setLoading(false);
    }
  }

  async function openProfile(id) {
    setBusy(true);
    setMessage('');
    try {
      const response = await fetch(`/api/managed-profiles/${id}`, { cache: 'no-store' });
      if (response.status === 401) {
        window.location.replace('/');
        return;
      }
      if (response.status === 404) {
        setMessage('This profile no longer exists or is unavailable. Refresh the list.');
        return;
      }
      if (!response.ok) throw new Error('Unable to load profile');
      const profile = await response.json();
      setSelected(profile);
      setFields({ displayName: profile.displayName, dateOfBirth: profile.dateOfBirth ?? '' });
      setErrors({});
      setConflict(false);
      setConfirmDelete(false);
      setMode('edit');
    } catch {
      setMessage('Unable to load this profile. Please try again.');
    } finally {
      setBusy(false);
    }
  }

  function startCreate() {
    createRequest.current = null;
    setFields(emptyFields);
    setErrors({});
    setMessage('');
    setMode('create');
  }

  function returnToList() {
    setMode('list');
    setSelected(null);
    setConfirmDelete(false);
    setConflict(false);
    setMessage('');
    if (new URLSearchParams(window.location.search).has('edit')) {
      window.location.assign('/portal?view=profiles');
      return;
    }
    loadList();
  }

  async function save(event) {
    event.preventDefault();
    setBusy(true);
    setErrors({});
    setMessage('');
    const body = JSON.stringify({ displayName: fields.displayName, dateOfBirth: fields.dateOfBirth || null });
    if (mode === 'create' && createRequest.current?.body !== body)
      createRequest.current = { body, key: crypto.randomUUID() };
    const headers = { 'Content-Type': 'application/json', 'X-CSRF-Token': session.csrfToken };
    if (mode === 'create') headers['Idempotency-Key'] = createRequest.current.key;
    else headers['If-Match'] = selected.version;
    try {
      const response = await fetch(mode === 'create' ? '/api/managed-profiles' : `/api/managed-profiles/${selected.id}`, {
        method: mode === 'create' ? 'POST' : 'PUT', headers, body,
      });
      if (response.status === 401) {
        window.location.replace('/');
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
      if (response.status === 409 && mode === 'edit') {
        setConflict(true);
        setMessage('This profile changed elsewhere. Reload the latest version before saving; your unsaved changes will be lost.');
        return;
      }
      if (response.status === 404 || response.status === 410) {
        setMessage('This profile no longer exists or is unavailable. Return to the list to refresh.');
        return;
      }
      if (!response.ok) throw new Error('Unable to save profile');
      const profile = await response.json();
      createRequest.current = null;
      setSelected(profile);
      setFields({ displayName: profile.displayName, dateOfBirth: profile.dateOfBirth ?? '' });
      setMode('edit');
      setMessage('Profile saved.');
    } catch {
      setMessage(mode === 'create'
        ? 'Unable to confirm creation. Retry without changing the details, or return to the list to check.'
        : 'Unable to save this profile. Please try again.');
    } finally {
      setBusy(false);
    }
  }

  async function remove() {
    setBusy(true);
    setMessage('');
    try {
      let version = selected.version;
      while (true) {
        const response = await fetch(`/api/managed-profiles/${selected.id}`, {
          method: 'DELETE', headers: { 'X-CSRF-Token': session.csrfToken, 'If-Match': version, Prefer: 'respond-async' },
        });
        if (response.status === 401) {
          window.location.replace('/');
          return;
        }
        if (response.status === 409) {
          setConflict(true);
          setConfirmDelete(false);
          setMessage('This profile changed elsewhere. Reload it before deleting.');
          return;
        }
        if (response.status === 404) {
          setMessage('This profile no longer exists or is unavailable. Return to the list to refresh.');
          return;
        }
        if (response.status === 202) {
          const pending = await response.json();
          setSelected(pending);
          setConfirmDelete(false);
          setMessage('Deleting this profile and its records...');
          version = pending.version;
          continue;
        }
        if (response.status !== 204) throw new Error('Unable to delete profile');
        break;
      }
      setMode('list');
      setSelected(null);
      setConfirmDelete(false);
      setConflict(false);
      if (new URLSearchParams(window.location.search).has('edit'))
        window.history.replaceState(null, '', '/portal?view=profiles');
      try {
        await fetchProfiles(null);
        setMessage('Profile deleted.');
      } catch {
        setProfiles([]);
        setListLoaded(false);
        setMessage('Profile deleted, but the list could not be refreshed. Please try again.');
      }
    } catch {
      setConflict(true);
      setConfirmDelete(false);
      setMessage('Unable to confirm deletion. Reload the profile to check or continue deletion.');
    } finally {
      setBusy(false);
    }
  }

  return (
    <section className="portal wrap account-page">
      <a className="back-link" href="/portal">← Back to portal</a>
      <p className="eyebrow">YOUR PROFILES</p>
      <h1>Managed profiles.</h1>
      <p className="intro">Create profiles for yourself or people you manage. Only you can access them.</p>
      {message && <p className="notice" role="status">{message}</p>}
      {mode === 'list' ? (
        <div className="account-panel">
          {loading ? <p>Loading profiles...</p> : profiles.length === 0 ? (listLoaded && <p>No profiles yet. Add one to get started.</p>) : (
            <ul className="profile-list">
              {profiles.map(profile => (
                <li key={profile.id}>
                  <button className="profile-link" type="button" disabled={busy} onClick={() => window.location.assign(`/portal?view=profile&id=${encodeURIComponent(profile.id)}`)}>
                    <span>{profile.displayName}</span><span aria-hidden="true">→</span>
                  </button>
                </li>
              ))}
            </ul>
          )}
          <div className="actions">
            <button className="button" type="button" disabled={busy || loading} onClick={startCreate}>Add profile</button>
            {continuation && <button className="button button-outline" type="button" disabled={busy} onClick={() => loadList(continuation)}>Load more</button>}
            {!loading && message && <button className="button button-outline" type="button" disabled={busy} onClick={() => loadList()}>Refresh list</button>}
          </div>
        </div>
      ) : (
        <div className="account-panel">
          <h2>{mode === 'create' ? 'Add a profile' : 'Profile details'}</h2>
          <form onSubmit={save} noValidate>
            <div className="account-fields">
              <div className="field">
                <label htmlFor="profile-name">Display name</label>
                <input id="profile-name" value={fields.displayName} maxLength={100} disabled={busy || selected?.deletionPending}
                  aria-invalid={Boolean(errors.displayName)} aria-describedby={errors.displayName ? 'profile-name-error' : undefined}
                  onChange={event => setFields(current => ({ ...current, displayName: event.target.value }))} />
                {errors.displayName && <span className="field-error" id="profile-name-error">{errors.displayName}</span>}
              </div>
              <div className="field">
                <label htmlFor="profile-birthdate">Date of birth (optional)</label>
                <input id="profile-birthdate" type="date" value={fields.dateOfBirth} max={new Date().toISOString().slice(0, 10)} disabled={busy || selected?.deletionPending}
                  aria-invalid={Boolean(errors.dateOfBirth)} aria-describedby={errors.dateOfBirth ? 'profile-birthdate-error' : undefined}
                  onChange={event => setFields(current => ({ ...current, dateOfBirth: event.target.value }))} />
                {errors.dateOfBirth && <span className="field-error" id="profile-birthdate-error">{errors.dateOfBirth}</span>}
              </div>
            </div>
            <div className="actions profile-actions">
              <button className="button" type="submit" disabled={busy || conflict || selected?.deletionPending}>{busy ? 'Working...' : 'Save profile'}</button>
              <button className="button button-outline" type="button" disabled={busy} onClick={returnToList}>Back to profiles</button>
              {conflict && <button className="button button-outline" type="button" disabled={busy} onClick={() => openProfile(selected.id)}>Reload latest profile</button>}
            </div>
          </form>
          {mode === 'edit' && (
            <div className="profile-delete">
              {selected.deletionPending ? (
                <>
                  <p>Deletion is in progress. Resume it to remove this profile and all its records.</p>
                  <button className="button button-danger" type="button" disabled={busy || conflict} onClick={remove}>Continue deletion</button>
                </>
              ) : !confirmDelete ? (
                <button className="button button-outline" type="button" disabled={busy || conflict} onClick={() => setConfirmDelete(true)}>Delete profile</button>
              ) : (
                <>
                  <p>Permanently delete this profile? This cannot be undone.</p>
                  <div className="actions">
                    <button className="button button-danger" type="button" disabled={busy} onClick={remove}>Yes, delete profile</button>
                    <button className="button button-outline" type="button" disabled={busy} onClick={() => setConfirmDelete(false)}>Cancel</button>
                  </div>
                </>
              )}
            </div>
          )}
        </div>
      )}
    </section>
  );
}
