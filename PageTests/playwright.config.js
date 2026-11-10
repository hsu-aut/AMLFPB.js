// Tests of the plugin page (Aml.Editor.Plugin.FPB/fpbjs-assets/index.html) in
// Chromium, the engine of WebView2. The page loads FPB.js from the built
// library (FPBJS_DIST, default ../../../FPB.JS/dist); build it first with
// `npm run build:lib` in FPB.JS.
const { defineConfig } = require('@playwright/test');

module.exports = defineConfig({
  testDir: '.',
  testMatch: '*.spec.js',
  timeout: 30000,
  reporter: 'line',
  outputDir: 'test-results',
  use: {
    browserName: 'chromium',
    viewport: { width: 1100, height: 800 }
  }
});
