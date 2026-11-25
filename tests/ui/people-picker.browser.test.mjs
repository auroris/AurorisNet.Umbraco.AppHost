import assert from "node:assert/strict";
import { test, before, after } from "node:test";
import { createRequire } from "node:module";
import { readFile, mkdir } from "node:fs/promises";
import { existsSync } from "node:fs";
import { resolve, join } from "node:path";
import { fileURLToPath } from "node:url";
import { tmpdir } from "node:os";

const root = fileURLToPath(new URL("../../", import.meta.url));
const require = createRequire(resolve(process.env.INTERACTIVE_REPORTS_ROOT || resolve(root, "../AurorisNet.InteractiveReports"), "package.json"));
const { chromium } = require("playwright");
const { expect } = require("@playwright/test");
const assets = resolve(root, "src/UmbracoAppHost/wwwroot/App_Plugins/PeoplePicker");
const shared = resolve(root, "../AurorisNet.Umbraco.AppBlocks/src/AppBlocks/wwwroot/App_Plugins/ApplicationBlocks");
const view = await readFile(resolve(root, "src/UmbracoAppHost/Views/Partials/PeoplePicker.cshtml"), "utf8");
const markupTemplate = view.slice(view.indexOf('<div id="@id"'), view.indexOf('<script type="module">')).replaceAll("@id", "people-test");
const options = {identityField:'incumbentSid',nameField:'incumbentName',fieldMappings:{extensionattribute10:'incumbentSn'},fallbackFields:{incumbentSn:'incumbentDirectorySn'},compactUpperFields:['incumbentSn'],allowManualEntry:true,minTermLength:2,maxResults:20};
const people = [
    { sid: "sid-1", displayName: "Example A@Finance@TEST", snPri: "k12 345 678 ", email: "example.a@example.invalid" },
    { sid: "sid-2", displayName: "Example B@Operations@TEST", snPri: "K87654321", email: "example.b@example.invalid" }
];
let browser;
before(async () => {
    browser = await chromium.launch({ headless: true, ...(existsSync(chromium.executablePath()) ? {} : { executablePath: process.env.BROWSER_PATH || "C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe" }) });
});
after(async () => browser?.close());

async function mount(t, search = async () => ({ items: people }), apiRoot = "/WComptDoA/api", extra = {}) {
    const settings = {...options, ...extra.settings};
    const markup = markupTemplate.replace("@endpoint", apiRoot+"/people-picker/test-page/test-picker").replace("@options", JSON.stringify(settings).replaceAll('"', "&quot;")).replace("@settings.Label", "Find person");
    const known = new Map();
    const context = await browser.newContext();
    t.after(() => context.close());
    const page = await context.newPage(), errors = [], searches = [];
    context.on("page", p => p.on("pageerror", error => errors.push(error.message)));
    page.on("pageerror", error => errors.push(error.message));
    await context.route("**/*", async route => {
        const url = new URL(route.request().url());
        if (url.pathname === `${apiRoot}/people-picker/test-page/test-picker`) {
            if(url.searchParams.has('sid')) {
                const person=known.get(url.searchParams.get('sid'));
                const body=extra.select ? await extra.select(person) : person ? {person:{...person,attributes:{extensionattribute10:person.snPri || null, mail:person.email}}} : {status:422};
                return route.fulfill({status:body.status||200,contentType:'application/json',body:JSON.stringify(body)});
            }
            searches.push(url.searchParams.get("s"));
            const body = await search(url.searchParams.get("s"));
            for(const person of body.items || []) known.set(person.sid,person);
            return route.fulfill({ status: body.status || 200, contentType: "application/json", body: JSON.stringify(body) });
        }
        if (url.pathname === "/WComptDoA/form") return route.fulfill({ contentType: "text/html", body: `<!doctype html><html lang="en"><head>
            <meta name="viewport" content="width=device-width,initial-scale=1"><meta name="appblocks-api-root" content="${apiRoot}"><link rel="stylesheet" href="/WComptDoA/App_Plugins/ApplicationBlocks/runtime.css">
            <link rel="stylesheet" href="/WComptDoA/App_Plugins/PeoplePicker/people-picker.css">
            <style>body{font:16px/1.5 system-ui;margin:24px}.app-form{max-width:900px}.app-form-field{margin-bottom:20px}</style></head><body>
            <h1>Delegation of Authority</h1><form data-app-form class="app-form"><h2>Who the delegation is for</h2>
            <fieldset class="app-form-body" disabled>
            <div data-content-element-type-alias="appPeoplePicker">${markup}</div>
            <div class="app-form-field"><label for="incumbent">Incumbent</label><input id="incumbent" data-field="incumbentName" required></div>
            <label>Email <input data-field="contactEmail"></label>
            <div class="app-form-field"><label for="number">Service number</label><input id="number" data-field="incumbentSn" required><small id="number-help">From the directory, or entered manually.</small></div>
            <input data-field="incumbentSid" type="hidden"><input data-field="incumbentDirectorySn" type="hidden" data-readonly="true"><button type="submit">Submit request</button></fieldset></form>
            <script type="module">
                import { mountPeoplePicker } from '/WComptDoA/App_Plugins/PeoplePicker/people-picker.js';
                mountPeoplePicker(document.getElementById('people-test'));
                window.submits=0; window.changes=[];
                const form=document.querySelector('form');
                form.addEventListener('submit', e=>{e.preventDefault(); window.submits++;});
                form.addEventListener('change', ()=>window.changes.push(['incumbentName','incumbentSn','incumbentSid'].map(key=>form.querySelector('[data-field="'+key+'"]').value)));
                form.querySelector('fieldset').disabled=false;
                form.dispatchEvent(new CustomEvent('app-form:loaded'));
            </script></body></html>` });
        const local = url.pathname.startsWith("/WComptDoA/App_Plugins/PeoplePicker/") ? assets
            : url.pathname.startsWith("/WComptDoA/App_Plugins/ApplicationBlocks/") ? shared : null;
        if (local) return route.fulfill({ contentType: url.pathname.endsWith(".html") ? "text/html" : url.pathname.endsWith(".css") ? "text/css" : "text/javascript", body: await readFile(join(local, url.pathname.split("/").at(-1)), "utf8") });
        return route.abort();
    });
    await page.goto("http://picker.test/WComptDoA/form");
    await expect(page.getByRole("button", { name: "Find person" })).toBeEnabled();
    async function open() {
        const pending = page.waitForEvent("popup");
        await page.getByRole("button", { name: "Find person" }).click();
        const popup = await pending;
        await expect(popup.getByLabel("Name", { exact: true })).toBeFocused();
        await expect.poll(() => popup.locator('body').evaluate(body => getComputedStyle(body).fontFamily)).toContain("system-ui");
        return popup;
    }
    t.after(() => assert.deepEqual(errors, []));
    return { page, context, open, searches };
}

test("LOV beside the name opens a window, searches under the IIS path, and commits one identity", async t => {
    const { page, open, searches } = await mount(t, undefined, undefined, {settings: {prefillMappings: {mail: 'contactEmail'}}});
    assert.equal(await page.locator('.app-person-control [data-field="incumbentName"]').count(), 1);
    await expect(page.locator('[data-content-element-type-alias="appPeoplePicker"]')).toBeHidden();
    await page.getByLabel("Incumbent", { exact: true }).fill("Manual person");
    await page.getByLabel("Service number").fill("K11111111");
    const popup = await open();
    await popup.getByLabel("Name", { exact: true }).fill("E");
    await popup.getByRole("button", { name: "Search", exact: true }).click();
    assert.deepEqual(searches, []);
    await popup.getByLabel("Name", { exact: true }).fill("Example");
    await popup.getByLabel("Name", { exact: true }).press("Enter");
    await expect(popup.locator('[data-results] button')).toHaveCount(2);
    assert.deepEqual(searches, ["Example"]);
    await expect(page.getByLabel("Incumbent", { exact: true })).toHaveValue("Manual person");
    // A different window (including the form itself) cannot forge a picker selection.
    await page.evaluate(() => window.postMessage({ type: "appblocks:account-picked", key: "forged" }, location.origin));
    await expect(page.locator('[data-field="incumbentSid"]')).toHaveValue("");
    await popup.getByLabel("Name", { exact: true }).press("ArrowDown");
    await popup.keyboard.press("ArrowDown");
    await expect(popup.locator('[data-results] button').nth(1)).toBeFocused();
    await popup.keyboard.press("ArrowUp");
    const closed = popup.waitForEvent("close");
    await popup.keyboard.press("Enter").catch(error => { if (!popup.isClosed()) throw error; }); await closed;
    await expect(page.getByLabel("Incumbent", { exact: true })).toHaveValue(people[0].displayName);
    await expect(page.getByLabel("Service number")).toHaveValue("K12345678");
    await expect(page.getByLabel("Email")).toHaveValue(people[0].email);
    await expect(page.locator('[data-field="incumbentSid"]')).toHaveValue("sid-1");
    assert.equal(await page.getByLabel("Incumbent", { exact: true }).getAttribute("readonly"), "");
    await expect(page.getByRole("button", { name: "Find person" })).toBeFocused();
    assert.equal(await page.evaluate(() => window.submits), 0);
    assert.deepEqual((await page.evaluate(() => window.changes)).slice(-3), Array(3).fill([people[0].displayName, "K12345678", "sid-1"]));
    await page.getByLabel("Email").fill("filed@example.invalid");
    await page.getByRole("button", { name: "Enter manually" }).click();
    await expect(page.locator('[data-field="incumbentSid"]')).toHaveValue("");
    await expect(page.getByLabel("Incumbent", { exact: true })).toBeEditable();
    await expect(page.getByLabel("Service number")).toBeEditable();
    await page.getByLabel("Service number").fill("k12 345 678 ");
    await page.getByLabel("Service number").press("Tab");
    await expect(page.getByLabel("Service number")).toHaveValue("K12345678");
});

test("Cancel, Escape, empty results and failed searches preserve an existing selection", async t => {
    const { page, open } = await mount(t, async term => term === "failure" ? { status: 503 } : { items: [] });
    await page.evaluate(() => {
        document.querySelector('[data-field="incumbentName"]').value = "Existing person";
        document.querySelector('[data-field="incumbentSn"]').value = "K11111111";
        document.querySelector('[data-field="incumbentSid"]').value = "existing-sid";
        document.querySelector('[data-field="incumbentDirectorySn"]').value = "K11111111";
        document.querySelector('form').dispatchEvent(new CustomEvent('app-form:loaded'));
    });
    let popup = await open();
    await popup.getByLabel("Name", { exact: true }).fill("nobody");
    await expect(popup.getByRole("status")).toContainText("No matching people");
    await popup.getByLabel("Name", { exact: true }).fill("failure");
    await expect(popup.getByRole("status")).toContainText("could not be completed");
    let closed = popup.waitForEvent("close");
    await popup.getByRole("button", { name: "Cancel" }).click(); await closed;
    popup = await open(); closed = popup.waitForEvent("close");
    await popup.keyboard.press("Escape").catch(error => { if (!popup.isClosed()) throw error; }); await closed;
    await expect(page.getByLabel("Incumbent", { exact: true })).toHaveValue("Existing person");
    await expect(page.getByLabel("Service number")).toHaveValue("K11111111");
    await expect(page.locator('[data-field="incumbentSid"]')).toHaveValue("existing-sid");
});

test("a selected person without a directory number keeps their SID while the number is entered", async t => {
    const { page, open } = await mount(t, async () => ({ items: [{ sid: "sid-no-number", displayName: "Ordinary Person" }] }),
        undefined, {settings: {prefillMappings: {mail: 'contactEmail'}}});
    await page.getByLabel("Email").fill("previous@example.invalid");
    const popup = await open();
    await popup.getByLabel("Name", { exact: true }).fill("Ordinary");
    const result = popup.getByRole("button", { name: "Ordinary Person", exact: true });
    await expect(result).toBeVisible();
    const closed = popup.waitForEvent("close");
    await result.click().catch(error => { if (!popup.isClosed()) throw error; }); await closed;
    await expect(page.getByLabel("Incumbent", { exact: true })).toHaveValue("Ordinary Person");
    assert.equal(await page.getByLabel("Incumbent", { exact: true }).getAttribute("readonly"), "");
    await expect(page.getByLabel("Service number")).toBeEditable();
    await expect(page.getByLabel("Service number")).toBeFocused();
    await expect(page.getByLabel("Email")).toHaveValue("");
    await expect(page.getByLabel("Email")).toBeEditable();
    await page.getByLabel("Service number").fill("k12 345 678");
    await page.getByLabel("Service number").press("Tab");
    await expect(page.getByLabel("Service number")).toHaveValue("K12345678");
    await expect(page.locator('[data-field="incumbentSid"]')).toHaveValue("sid-no-number");
    // Reloading/saving the fallback number must not make it look like a number supplied by AD.
    await page.evaluate(() => document.querySelector('form').dispatchEvent(new CustomEvent('app-form:saved')));
    await expect(page.getByLabel("Service number")).toBeEditable();
    await page.evaluate(() => {
        document.querySelector('[data-field="incumbentDirectorySn"]').value = "K22223333";
        document.querySelector('[data-field="incumbentSn"]').value = "K22223333";
        document.querySelector('form').dispatchEvent(new CustomEvent('app-form:loaded'));
    });
    assert.equal(await page.getByLabel("Service number").getAttribute("readonly"), "");
});

test("new searches replace older results and names are rendered as text", async t => {
    let releaseOld;
    const old = new Promise(resolve => { releaseOld = resolve; });
    const { open, searches } = await mount(t, async term => {
        if (term === "old") { await old; return { items: [people[0]] }; }
        return { items: [{ ...people[1], displayName: '<em>New person</em>' }] };
    }, "/WComptDoA/custom-api");
    const popup = await open();
    await popup.getByLabel("Name", { exact: true }).fill("old");
    await expect.poll(() => searches).toContain("old");
    await popup.getByLabel("Name", { exact: true }).fill("new");
    await expect(popup.locator('[data-results] button')).toHaveCount(1);
    releaseOld();
    await expect(popup.locator('[data-results] button')).toContainText('<em>New person</em>');
    assert.equal(await popup.locator('[data-results] em').count(), 0);
    await popup.getByLabel("Name", { exact: true }).fill("");
    await expect(popup.locator('[data-results] button')).toHaveCount(0);
});

test("one popup is reused, a form reload closes it, and disabled forms cannot choose a person", async t => {
    const { page, context, open } = await mount(t);
    const popup = await open();
    await page.getByRole("button", { name: "Find person" }).click();
    assert.equal(context.pages().length, 2);
    const closed = popup.waitForEvent("close");
    await page.evaluate(() => {
        document.querySelector('[data-field="incumbentName"]').disabled = true;
        document.querySelector('form').dispatchEvent(new CustomEvent('app-form:loaded'));
    });
    await closed;
    await expect(page.getByRole("button", { name: "Find person" })).toBeDisabled();
});

test("a blocked popup explains recovery and both layouts fit a narrow viewport", async t => {
    const { page, open } = await mount(t);
    await page.setViewportSize({ width: 360, height: 780 });
    assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true);
    const popup = await open();
    await popup.setViewportSize({ width: 360, height: 640 });
    await popup.getByLabel("Name", { exact: true }).fill("Example");
    await expect(popup.locator('[data-results] button')).toHaveCount(2);
    assert.equal(await popup.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true);
    if (process.env.APPHOST_PICKER_SCREENSHOTS) {
        const out = join(tmpdir(), "app-picker-preview"); await mkdir(out, { recursive: true });
        await page.setViewportSize({ width: 1024, height: 650 });
        await popup.setViewportSize({ width: 680, height: 620 });
        await page.screenshot({ path: join(out, "form.png") });
        await popup.screenshot({ path: join(out, "search.png") });
    }
    await popup.close();
    await page.evaluate(() => { window.open = () => null; });
    await page.getByRole("button", { name: "Find person" }).click();
    await expect(page.locator('.app-picker-error')).toContainText("Allow popups");
});

test("a failed live selection preserves the existing fields and leaves the popup open", async t => {
    const { page, open } = await mount(t, undefined, undefined, { select: async () => ({ status: 422 }) });
    await page.getByLabel("Incumbent", { exact: true }).fill("Existing person");
    await page.getByLabel("Service number").fill("K11111111");
    const popup = await open();
    await popup.getByLabel("Name", { exact: true }).fill("Example");
    await popup.locator('[data-results] button').first().click();
    await expect(page.locator('[data-status]')).toContainText("could not be validated");
    await expect(page.getByLabel("Incumbent", { exact: true })).toHaveValue("Existing person");
    await expect(page.getByLabel("Service number")).toHaveValue("K11111111");
    await expect(page.locator('[data-field="incumbentSid"]')).toHaveValue("");
    assert.equal(popup.isClosed(), false);
});

test("a late validation response cannot change a form that has reloaded", async t => {
    let release, started = false;
    const wait = new Promise(resolve => { release = resolve; });
    const { page, open } = await mount(t, undefined, undefined, { select: async person => {
        started = true; await wait; return { person: { ...person, attributes: { extensionattribute10: person.snPri } } };
    } });
    const popup = await open();
    await popup.getByLabel("Name", { exact: true }).fill("Example");
    await popup.locator('[data-results] button').first().click();
    await expect.poll(() => started).toBe(true);
    await page.evaluate(() => {
        document.querySelector('[data-field="incumbentName"]').value = "Reloaded person";
        document.querySelector('form').dispatchEvent(new CustomEvent('app-form:loaded'));
    });
    release();
    await expect(page.getByLabel("Incumbent", { exact: true })).toHaveValue("Reloaded person");
    await expect(page.locator('[data-field="incumbentSid"]')).toHaveValue("");
    assert.equal(popup.isClosed(), true);
});

test("two pickers in one form retain independent fields and popup ownership", async t => {
    const { page, open } = await mount(t);
    await page.evaluate(async options => {
        const form = document.querySelector('form'), box = document.getElementById('people-test').cloneNode(true);
        box.id = 'second-picker'; delete box.dataset.mounted;
        box.dataset.settings = JSON.stringify({ ...options, identityField: 'secondSid', nameField: 'secondName',
            fieldMappings: { extensionattribute10: 'secondNumber' }, fallbackFields: { secondNumber: 'secondMirror' }, compactUpperFields: ['secondNumber'] });
        box.querySelector('[data-find]').textContent = 'Find second person';
        const fields = document.createElement('div');
        fields.innerHTML = '<label>Second person<input data-field="secondName"></label><input data-field="secondSid" type="hidden"><input data-field="secondNumber"><input data-field="secondMirror" type="hidden">';
        form.append(fields, box);
        const { mountPeoplePicker } = await import('/WComptDoA/App_Plugins/PeoplePicker/people-picker.js');
        mountPeoplePicker(box);
    }, options);
    const first = await open(), pending = page.waitForEvent('popup');
    await page.getByRole('button', { name: 'Find second person', exact: true }).click();
    const second = await pending;
    await second.getByLabel('Name', { exact: true }).fill('Example');
    await expect(second.locator('[data-results] button')).toHaveCount(2);
    const closed = second.waitForEvent('close');
    await second.locator('[data-results] button').first().click(); await closed;
    await expect(page.locator('[data-field="secondSid"]')).toHaveValue('sid-1');
    await expect(page.locator('[data-field="incumbentSid"]')).toHaveValue('');
    assert.equal(first.isClosed(), false);
});
