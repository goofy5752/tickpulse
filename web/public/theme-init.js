// Applies the remembered theme before first paint, so light-mode visitors never see a dark flash. A classic,
// render-blocking script loaded from index.html's <head>: a file rather than an inline script, so the dashboard's
// Content-Security-Policy (script-src 'self') needs no hash or 'unsafe-inline'.
try {
  if (localStorage.getItem('tickpulse.theme') === 'light') {
    document.documentElement.dataset.theme = 'light'
    document.querySelector('meta[name="theme-color"]').setAttribute('content', '#f3f2fa')
  }
} catch {
  // Storage blocked (privacy mode): keep the default dark theme.
}
