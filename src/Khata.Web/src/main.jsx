import React from 'react';
import { createRoot } from 'react-dom/client';
import './style.css';

function App() {
  return (
    <main>
      <h1>Khata</h1>
      <p>A banking application for kids.</p>
      <p>Balances, investments, and gig jobs are coming soon.</p>
    </main>
  );
}

createRoot(document.getElementById('root')).render(<App />);
