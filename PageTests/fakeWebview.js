// A stand-in for window.chrome.webview of WebView2, so the bridge between the
// page and the plugin is tested in a browser without the editor (the same
// harness as in NodeSet.js). What the page posts lands in window.__posted;
// send() delivers a message of the plugin as WebView2 does after
// PostWebMessageAsJson: as a parsed object in event.data.

const fs = require('fs');
const path = require('path');

const ASSETS = path.resolve(__dirname, '..', 'Aml.Editor.Plugin.FPB', 'fpbjs-assets');
const DIST = process.env.FPBJS_DIST || path.resolve(__dirname, '..', '..', '..', 'FPB.JS', 'dist');
const ORIGIN = 'https://fpbjs.local';

const TYPES = { '.html': 'text/html; charset=utf-8', '.js': 'application/javascript; charset=utf-8', '.css': 'text/css; charset=utf-8', '.svg': 'image/svg+xml', '.woff2': 'font/woff2' };

/** The file of a request: the page from the assets, FPB.js from the build */
function fileFor(pathname) {
  if (pathname === '/' || pathname === '/index.html') {
    return path.join(ASSETS, 'index.html');
  }
  if (pathname === '/fpbjs.esm.js' || pathname.startsWith('/css/')) {
    return path.join(DIST, decodeURIComponent(pathname));
  }
  return path.join(ASSETS, decodeURIComponent(pathname));
}

async function installFakeWebview(page) {
  await page.addInitScript(() => {
    const listeners = [];
    window.__posted = [];
    window.chrome = window.chrome || {};
    window.chrome.webview = {
      postMessage(message) {
        window.__posted.push(JSON.parse(JSON.stringify(message)));
      },
      addEventListener(type, listener) {
        if (type === 'message') {
          listeners.push(listener);
        }
      },
      removeEventListener(type, listener) {
        const at = listeners.indexOf(listener);
        if (at !== -1) {
          listeners.splice(at, 1);
        }
      }
    };
    window.__hostSend = (data) => listeners.slice().forEach((listener) => listener({ data }));
  });
}

/**
 * Open the page on its virtual host. Returns the requests and page errors
 * seen, so tests can check what was loaded and that nothing blew up.
 */
async function openPluginPage(page) {
  const requests = [];
  const errors = [];
  page.on('pageerror', (error) => errors.push(String((error && error.message) || error)));
  await installFakeWebview(page);
  await page.route(ORIGIN + '/**', async (route) => {
    const url = new URL(route.request().url());
    requests.push(url.pathname);
    const file = fileFor(url.pathname);
    if (!fs.existsSync(file) || fs.statSync(file).isDirectory()) {
      return route.fulfill({ status: 404, body: 'not found' });
    }
    return route.fulfill({
      status: 200,
      contentType: TYPES[path.extname(file)] || 'application/octet-stream',
      body: fs.readFileSync(file)
    });
  });
  await page.goto(ORIGIN + '/index.html');
  await page.waitForFunction(() => window.__posted.some((m) => m.type === 'ready'));
  return { requests, errors };
}

const send = (page, message) => page.evaluate((m) => window.__hostSend(m), message);
const posted = (page, type) => page.evaluate((t) => window.__posted.filter((m) => m.type === t), type);
const clearPosted = (page) => page.evaluate(() => { window.__posted.length = 0; });

module.exports = { installFakeWebview, openPluginPage, send, posted, clearPosted, DIST, ORIGIN };
