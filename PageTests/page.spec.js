// The plugin page on FPB.js 2.0 (016f E3): what it loads, what it posts to the
// plugin and how it answers the plugin's messages. The protocol is in
// FPB.JS_Docs/Geplante-Features/Plaene/2026-11-05-016f-E3-Plugin.md
// (Festlegung 1).

const { test, expect } = require('@playwright/test');
const { openPluginPage, send, posted, clearPosted } = require('./fakeWebview');
const model = require('./fixtures/Temperieren.json');

const clone = () => JSON.parse(JSON.stringify(model));
const MAIN = model[0].entryPoint;
const CHILD = model[2].process.id;
// Rohstoff: a boundary state on both layers; Prüfen an operator above
const BOUNDARY = 'c50336d6-9a7f-46e3-a7f5-e6187618dd69';
const PRUEFEN = '9833d9a9-1b88-4bfb-91b3-7ab5e2617045';

/** Import through the bridge and wait for the answer with this number */
async function importModel(page, seq = 1, data = clone()) {
  await send(page, { type: 'importJSON', seq, data });
  await expect.poll(async () => (await posted(page, 'imported')).some((m) => m.seq === seq)).toBe(true);
}

/** Move Prüfen out of the system limit: one error (B2) */
const breakRule = (page) => page.evaluate((id) => {
  const operator = window.fpb.get('elementRegistry').get(id);
  window.fpb.get('modeling').moveElements([operator], { x: 900, y: 0 });
}, PRUEFEN);

test('the page loads FPB.js 2.0 without React, Bootstrap or an import map', async ({ page }) => {
  const { requests, errors } = await openPluginPage(page);
  expect(requests.filter((r) => r.includes('/vendor/'))).toEqual([]);
  // since 2.0 the styles of diagram-js come with the bundle (Umstieg-2.0, "Stile und Dateien")
  expect(requests).not.toContain('/css/diagram-js.css');
  await expect(page.locator('script[type=importmap]')).toHaveCount(0);
  expect((await posted(page, 'ready')).length).toBe(1);
  expect(errors).toEqual([]);
});

test('an import is answered with its number and the model; changes during it are not reported', async ({ page }) => {
  await openPluginPage(page);
  await importModel(page, 7);
  const answer = (await posted(page, 'imported'))[0];
  expect(answer.seq).toBe(7);
  expect(Array.isArray(answer.data)).toBe(true);
  await page.waitForTimeout(500);
  const changes = await posted(page, 'changed');
  expect(changes.filter((c) => JSON.stringify(c.data) !== JSON.stringify(answer.data))).toEqual([]);
});

test('two quick imports are answered for the newer one only', async ({ page }) => {
  await openPluginPage(page);
  await page.evaluate((data) => {
    window.__hostSend({ type: 'importJSON', seq: 1, data });
    window.__hostSend({ type: 'importJSON', seq: 2, data });
  }, clone());
  await expect.poll(async () => (await posted(page, 'imported')).map((m) => m.seq)).toContain(2);
  await page.waitForTimeout(500);
  expect((await posted(page, 'imported')).map((m) => m.seq)).toEqual([2]);
});

test('an edit posts changed with the model', async ({ page }) => {
  await openPluginPage(page);
  await importModel(page);
  await clearPosted(page);
  await page.evaluate((id) => {
    const operator = window.fpb.get('elementRegistry').get(id);
    window.fpb.get('modeling').updateLabel(operator, 'Kontrollieren');
  }, PRUEFEN);
  await expect.poll(async () => JSON.stringify(await posted(page, 'changed'))).toContain('Kontrollieren');
});

// 032a V6: changed comes with model.changed of FPB.js 2.1, which a layer switch is not
test('a layer switch posts no changed', async ({ page }) => {
  await openPluginPage(page);
  await importModel(page);
  await clearPosted(page);
  await page.evaluate((child) => window.fpb.switchProcess(child), CHILD);
  await expect.poll(() => page.evaluate(() => window.fpb.getCurrentProcess())).toBe(CHILD);
  await page.waitForTimeout(500);
  expect(await posted(page, 'changed')).toEqual([]);
});

// 032a V6: the page waits for the promise of the import; a refused one is an error
test('an import the importer refuses is reported as error and answered by no imported', async ({ page }) => {
  await openPluginPage(page);
  await importModel(page, 1);
  await clearPosted(page);
  await send(page, { type: 'importJSON', seq: 2, data: {} });
  await expect.poll(async () => (await posted(page, 'error')).length).toBe(1);
  expect((await posted(page, 'error'))[0].message).toContain('import failed');
  await page.waitForTimeout(500);
  expect(await posted(page, 'imported')).toEqual([]);
});

// 2.1.1: of two imports in a row the newer is refused; the error names its
// number, and the older import, whose model is on the canvas, is answered
test('a refused newer import names its number and the older one is answered', async ({ page }) => {
  await openPluginPage(page);
  await page.evaluate((data) => {
    window.__hostSend({ type: 'importJSON', seq: 1, data });
    window.__hostSend({ type: 'importJSON', seq: 2, data: {} });
  }, clone());
  await expect.poll(async () => (await posted(page, 'imported')).map((m) => m.seq)).toEqual([1]);
  const errors = await posted(page, 'error');
  expect(errors.map((m) => m.seq)).toEqual([2]);
});

test('Ctrl+S posts save with the text just typed in a field', async ({ page }) => {
  let downloads = 0;
  page.on('download', () => { downloads++; });
  await openPluginPage(page);
  await importModel(page);
  const toggle = page.locator('#propertiesPanelToggle');
  if ((await toggle.getAttribute('aria-expanded')) !== 'true') {
    await toggle.click();
  }
  await page.evaluate((id) => window.fpb.select(id), PRUEFEN);
  const name = page.locator('#bio-properties-panel-shortName');
  await expect(name).toHaveValue('Prüfen');
  await name.click();
  await page.keyboard.press('End');
  await page.keyboard.type('X');
  await page.keyboard.press('Control+s');
  await expect.poll(async () => (await posted(page, 'save')).length).toBe(1);
  expect(JSON.stringify((await posted(page, 'save'))[0].data)).toContain('PrüfenX');
  expect(downloads).toBe(0);
});

// final review 016f E3: the label editor of diagram-js stops every key
test('Ctrl+S while a label is edited on the canvas saves the new label', async ({ page }) => {
  await openPluginPage(page);
  await importModel(page);
  await page.locator(`.djs-element[data-element-id="${PRUEFEN}"]`).dblclick();
  await expect(page.locator('.djs-direct-editing-parent')).toBeVisible();
  await page.keyboard.type('ZZ');
  await page.keyboard.press('Control+s');
  await expect.poll(async () => (await posted(page, 'save')).length).toBe(1);
  expect(JSON.stringify((await posted(page, 'save'))[0].data)).toContain('ZZ');
});

test('findings come with the rules FPB.js checks and the layer of each', async ({ page }) => {
  await openPluginPage(page);
  await importModel(page);
  await clearPosted(page);
  await breakRule(page);
  await expect.poll(async () => JSON.stringify(await posted(page, 'findings'))).toContain('"B2"');
  const last = (await posted(page, 'findings')).pop();
  expect(last.rules).toEqual(await page.evaluate(() => window.fpb.getCheckedRules()));
  const b2 = last.items.find((item) => item.rule === 'B2');
  expect(b2).toMatchObject({ severity: 'error', elementId: PRUEFEN, processId: MAIN });
  expect(typeof b2.message).toBe('string');
});

test('a click on an element posts it with its layer', async ({ page }) => {
  await openPluginPage(page);
  await importModel(page);
  await clearPosted(page);
  await page.locator(`.djs-element[data-element-id="${PRUEFEN}"]`).click();
  await expect.poll(async () => (await posted(page, 'selected')).pop()).toEqual({ type: 'selected', id: PRUEFEN, processId: MAIN });
});

test('select from the host switches the layer and leaves the focus', async ({ page }) => {
  await openPluginPage(page);
  await importModel(page);
  await page.evaluate(() => document.activeElement && document.activeElement.blur());
  await send(page, { type: 'select', id: BOUNDARY, processId: CHILD });
  await expect.poll(() => page.evaluate(() => window.fpb.getCurrentProcess())).toBe(CHILD);
  expect(await page.evaluate(() => window.fpb.getSelected().map((e) => e.id))).toEqual([BOUNDARY]);
  expect(await page.evaluate(() => document.activeElement === document.body)).toBe(true);
});

test('selecting a layer itself from the host only switches to it', async ({ page }) => {
  await openPluginPage(page);
  await importModel(page);
  await send(page, { type: 'select', id: CHILD, processId: CHILD });
  await expect.poll(() => page.evaluate(() => window.fpb.getCurrentProcess())).toBe(CHILD);
});

test('setTheme dark darkens the page and hides the light and dark button', async ({ page }) => {
  await openPluginPage(page);
  await importModel(page);
  await expect(page.getByRole('button', { name: /Switch to (Dark|Light) Mode/ })).toHaveCount(1);
  await send(page, { type: 'setTheme', theme: 'dark' });
  await expect(page.locator('html')).toHaveAttribute('data-theme', 'dark');
  await expect(page.locator('#modeler-container')).toHaveAttribute('data-theme', 'dark');
  await expect(page.getByRole('button', { name: /Switch to (Dark|Light) Mode/ })).toHaveCount(0);
  // dark-mode.css fades the colours over 0.3 s
  await expect.poll(() => page.evaluate(() => getComputedStyle(document.body).backgroundColor)).not.toBe('rgb(255, 255, 255)');
});

test('read-only takes no edits', async ({ page }) => {
  await openPluginPage(page);
  await importModel(page);
  await send(page, { type: 'setReadOnly', readOnly: true });
  await expect.poll(() => page.evaluate(() => window.fpb.isReadOnly())).toBe(true);
  await page.locator(`.djs-element[data-element-id="${PRUEFEN}"]`).click();
  await page.keyboard.press('Delete');
  expect(await page.evaluate((id) => !!window.fpb.get('elementRegistry').get(id), PRUEFEN)).toBe(true);
});

test('no start card, no Open, Save, New model or model check; Export is there', async ({ page }) => {
  await openPluginPage(page);
  await page.waitForTimeout(300);
  await expect(page.locator('.fpb-start-card')).toHaveCount(0);
  await importModel(page);
  for (const id of ['#openFileButton', '#saveFileButton', '#newModelButton', '#openValidationButton']) {
    await expect(page.locator(id)).toHaveCount(0);
  }
  await expect(page.locator('#exportButton')).toBeVisible();
});

test('setValidationMarkers false hides the markers', async ({ page }) => {
  await openPluginPage(page);
  await importModel(page);
  await breakRule(page);
  await expect(page.locator('.fpb-validation-marker')).toHaveCount(1);
  await send(page, { type: 'setValidationMarkers', show: false });
  await expect(page.locator('.fpb-validation-marker')).toHaveCount(0);
  await send(page, { type: 'setValidationMarkers', show: true });
  await expect(page.locator('.fpb-validation-marker')).toHaveCount(1);
});

test('in 535 x 585 nothing sticks out and toolbar and panel do not overlap', async ({ page }, testInfo) => {
  await page.setViewportSize({ width: 535, height: 585 });
  await openPluginPage(page);
  await importModel(page);
  const toggle = page.locator('#propertiesPanelToggle');
  if ((await toggle.getAttribute('aria-expanded')) !== 'true') {
    await toggle.click();
  }
  await page.evaluate((id) => window.fpb.select(id), PRUEFEN);
  const box = (selector) => page.locator(selector).boundingBox();
  const panel = await box('#properties-container');
  const toolbar = await box('#layer-container');
  const canvas = await box('#modeler-container');
  for (const b of [panel, toolbar, canvas]) {
    expect(b.x).toBeGreaterThanOrEqual(0);
    expect(b.y).toBeGreaterThanOrEqual(0);
    expect(b.x + b.width).toBeLessThanOrEqual(535 + 0.5);
    expect(b.y + b.height).toBeLessThanOrEqual(585 + 0.5);
  }
  const overlap = !(panel.x + panel.width <= toolbar.x + 0.5 || toolbar.x + toolbar.width <= panel.x + 0.5);
  expect(overlap).toBe(false);
  expect(canvas.width).toBeGreaterThan(100);
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
  await page.screenshot({ path: testInfo.outputPath('narrow-light.png') });
  await send(page, { type: 'setTheme', theme: 'dark' });
  // dark-mode.css fades the colours over 0.3 s
  await page.waitForTimeout(600);
  await page.screenshot({ path: testInfo.outputPath('narrow-dark.png') });
});

// 016f E4: the links of the AML file the plugin sends; the delete question names them
test('setAmlLinks from the plugin makes the delete question name the links', async ({ page }) => {
  await openPluginPage(page);
  await importModel(page);
  await send(page, {
    type: 'setAmlLinks',
    links: [{ processId: MAIN, elementId: PRUEFEN, name: 'Prüfen', link: 'L1', owner: 'Plant' }]
  });
  await page.evaluate((id) => {
    window.fpb.get('fpbDelete').remove([window.fpb.get('elementRegistry').get(id)]);
  }, PRUEFEN);
  const question = page.locator('dialog.fpb-dialog[open]');
  await expect(question).toContainText('L1 (Plant) to Prüfen');
  await expect(question).toContainText('Applying the diagram to the AML file removes them.');
  await question.locator('[data-answer="cancel"]').click();
  expect(await page.evaluate((id) => !!window.fpb.get('elementRegistry').get(id), PRUEFEN)).toBe(true);
});

// 016h F2: the plugin tells the page its language in the address
test('the page speaks German with ?lang=de and English without', async ({ page }) => {
  const { errors } = await openPluginPage(page, '?lang=de');
  await expect(page.locator('#exportButton')).toHaveAttribute('aria-label', 'Exportieren');
  expect(await page.evaluate(() => window.fpb.getLanguage())).toBe('de');
  await openPluginPage(page);
  await expect(page.locator('#exportButton')).toHaveAttribute('aria-label', 'Export');
  expect(await page.evaluate(() => window.fpb.getLanguage())).toBe('en');
  expect(errors).toEqual([]);
});

// run in the real editor: the page itself said "en" while FPB.js spoke German
test('the page names its language for screen readers', async ({ page }) => {
  await openPluginPage(page, '?lang=de');
  await expect(page.locator('html')).toHaveAttribute('lang', 'de');
  await openPluginPage(page);
  await expect(page.locator('html')).toHaveAttribute('lang', 'en');
});
