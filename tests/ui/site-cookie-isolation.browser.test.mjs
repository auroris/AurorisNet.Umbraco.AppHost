// Build UmbracoAppHost.Web, then run: node --test tests/ui/site-cookie-isolation.browser.test.mjs
// Uses PowerShell 7 and the same Playwright installation as people-picker.browser.test.mjs.
import assert from "node:assert/strict";
import { execFileSync, spawn } from "node:child_process";
import { once } from "node:events";
import { existsSync } from "node:fs";
import { mkdir, mkdtemp, readFile, rm, writeFile } from "node:fs/promises";
import { createServer, request } from "node:https";
import { createRequire } from "node:module";
import { tmpdir } from "node:os";
import { join, resolve } from "node:path";
import { randomBytes } from "node:crypto";
import { test } from "node:test";
import { fileURLToPath } from "node:url";

const repo = fileURLToPath(new URL("../../", import.meta.url));
const require = createRequire(resolve(process.env.INTERACTIVE_REPORTS_ROOT || resolve(repo, "../AurorisNet.InteractiveReports"), "package.json"));
const { chromium } = require("playwright");
const dll = resolve(process.env.APPHOST_WEB_DLL || resolve(repo, "src/UmbracoAppHost.Web/bin/Debug/net10.0/UmbracoAppHost.Web.dll"));

function createTestCertificate(path, password) {
    execFileSync("pwsh", ["-NoProfile", "-NonInteractive", "-Command", `
        $ErrorActionPreference = 'Stop'
        $rsa = [Security.Cryptography.RSA]::Create(2048)
        $request = [Security.Cryptography.X509Certificates.CertificateRequest]::new('CN=localhost', $rsa,
            [Security.Cryptography.HashAlgorithmName]::SHA256, [Security.Cryptography.RSASignaturePadding]::Pkcs1)
        $certificate = $request.CreateSelfSigned([DateTimeOffset]::UtcNow.AddMinutes(-1), [DateTimeOffset]::UtcNow.AddDays(1))
        [IO.File]::WriteAllBytes($env:COOKIE_TEST_PFX, $certificate.Export('Pfx', $env:COOKIE_TEST_PASSWORD))
    `], { windowsHide: true, env: { ...process.env, COOKIE_TEST_PFX: path, COOKIE_TEST_PASSWORD: password } });
}

async function startSite(site) {
    const child = spawn("dotnet", [dll, "--urls=https://127.0.0.1:0"], {
        cwd: site.root, windowsHide: true, env: { ...process.env, ASPNETCORE_ENVIRONMENT: "Development" },
        stdio: ["ignore", "pipe", "pipe"],
    });
    site.child = child;
    site.exit = once(child, "exit");
    site.port = undefined;
    await new Promise((accept, reject) => {
        let output = "";
        const timer = setTimeout(() => reject(new Error(`Timed out starting ${site.name}: ${output.slice(-4000)}`)), 180_000);
        function read(chunk) {
            output = (output + chunk).slice(-12_000);
            const listening = /Now listening on:\s*https:\/\/127\.0\.0\.1:(\d+)/.exec(output);
            if (listening) site.port = Number(listening[1]);
            if (output.includes("Unattended upgrade completed successfully")) site.installed = true;
            if (site.port && site.installed) {
                clearTimeout(timer);
                accept();
            }
        }
        child.stdout.on("data", read);
        child.stderr.on("data", read);
        site.exit.then(() => { clearTimeout(timer); reject(new Error(`${site.name} stopped: ${output.slice(-4000)}`)); }, reject);
    });
}

async function stopSite(site) {
    if (!site.child) return;
    if (site.child.exitCode === null && site.child.signalCode === null) site.child.kill();
    await site.exit;
}

async function login(page, site, origin, password, path = site.name) {
    await page.goto(`${origin}/${path}/umbraco`);
    await page.locator("#username-input").fill("admin");
    await page.locator("#password-input").fill(password);
    await page.locator("#umb-login-button").click();
    await page.locator("umb-backoffice-header-logo").waitFor();
    await currentUser(page, site);
}

async function currentUser(page, site) {
    const user = await page.evaluate(async name => {
        const response = await fetch(`/${name}/umbraco/management/api/v1/user/current`, { headers: { Authorization: "Bearer [redacted]" } });
        if (!response.ok) throw new Error(`Current user returned ${response.status}`);
        return response.json();
    }, site.name);
    assert.equal(user.email, `${site.name}@example.test`);
}

async function refresh(page, site, url) {
    const [token] = await Promise.all([
        page.waitForResponse(response => new URL(response.url()).pathname === `/${site.name}/umbraco/management/api/v1/security/back-office/token`
            && response.request().postData()?.includes("grant_type=refresh_token")),
        url ? page.goto(url) : page.reload(),
    ]);
    assert.equal(token.status(), 200);
    await page.locator("umb-backoffice-header-logo").waitFor();
    await currentUser(page, site);
}

test("two installations share an HTTPS origin and keep independent backoffice sessions", { timeout: 300_000 }, async () => {
    assert.ok(existsSync(dll), `Build the reference website first: ${dll}`);
    const root = await mkdtemp(join(tmpdir(), "apphost-site-cookies-"));
    const [portal, app] = ["portal", "app"].map(name => ({ name, root: join(root, name) }));
    const sites = [portal, app];
    const password = "Test!" + randomBytes(20).toString("hex");
    let browser, proxy;
    const diagnostics = [];
    try {
        const pfx = join(root, "localhost.pfx");
        createTestCertificate(pfx, password);
        for (const site of sites) {
            await mkdir(join(site.root, "wwwroot"), { recursive: true });
            await writeFile(join(site.root, "appsettings.json"), JSON.stringify({
                Kestrel: { Certificates: { Default: { Path: pfx, Password: password } } },
                Umbraco: { CMS: { Hosting: { ApplicationVirtualPath: "/" + site.name, SiteName: "Shared Test Site" } } },
                IdentityServer: { Enabled: false }, EmbeddedIdentityServer: { Enabled: false }, BackOfficeLogin: { DefaultFlow: "Form", DebugFlow: "Form" },
                BreakGlass: { AdminUserName: "admin", AdminPassword: password, AdminEmail: `${site.name}@example.test` },
                uSync: { Settings: { FirstBootGroup: "Settings" } },
            }));
        }
        await Promise.all(sites.map(startSite));
        proxy = createServer({ pfx: await readFile(pfx), passphrase: password }, (incoming, outgoing) => {
            const site = sites.find(candidate => incoming.url.toLowerCase().startsWith(`/${candidate.name}/`));
            if (!site) { outgoing.writeHead(404).end(); return; }
            // Preserve the external Host so both sites issue redirects for the browser's single origin.
            const upstream = request({ hostname: "127.0.0.1", port: site.port, rejectUnauthorized: false,
                path: incoming.url, method: incoming.method, headers: incoming.headers }, response => {
                outgoing.writeHead(response.statusCode, response.headers);
                response.pipe(outgoing);
                outgoing.on("close", () => response.destroy());
            });
            upstream.on("error", () => { if (!outgoing.headersSent) outgoing.writeHead(502); outgoing.end(); });
            incoming.pipe(upstream);
        });
        proxy.listen(0, "127.0.0.1");
        await once(proxy, "listening");
        const origin = `https://127.0.0.1:${proxy.address().port}`;
        browser = await chromium.launch({ headless: true, ...(process.env.BROWSER_PATH && { executablePath: process.env.BROWSER_PATH }) });
        const context = await browser.newContext({ ignoreHTTPSErrors: true });
        context.on("page", page => {
            page.on("pageerror", error => diagnostics.push(error.message));
            page.on("console", message => {
                if (message.type() === "error") diagnostics.push(message.text());
            });
            page.on("response", response => {
                if (response.status() >= 400) diagnostics.push(`${response.status()} ${new URL(response.url()).pathname}`);
            });
        });
        context.setDefaultTimeout(45_000);
        const legacy = ["UMB_UCONTEXT", "UMB_PREVIEW"];
        await context.addCookies(legacy.map(name => ({ name, value: "other-installation", url: origin })));
        const tabA = await context.newPage(), tabB = await context.newPage();
        await login(tabA, portal, origin, password, "PORTAL");
        const bookmark = await context.request.get(`${origin}/PORTAL/umbraco/reports/whoami?view=full`);
        assert.equal(bookmark.url(), `${origin}/portal/umbraco/reports/whoami?view=full`);
        assert.equal((await bookmark.json()).authenticated, true);
        const refreshNames = async () => (await context.cookies()).filter(cookie => cookie.name.includes("umbRefreshToken")).map(cookie => cookie.name).sort();
        const [portalCookie] = await refreshNames();
        assert.match(portalCookie, /^__Host-umbRefreshToken-shared-test-site-[0-9a-f]{12}$/);
        await login(tabB, app, origin, password);
        const names = await refreshNames();
        assert.equal(names.length, 2);
        const assetHash = page => page.evaluate(() =>
            JSON.parse(document.querySelector('script[type="importmap"]').textContent).imports["@umbraco-cms/backoffice/app"].match(/\/backoffice\/([\da-f]+)\//)[1]);
        assert.notEqual(await assetHash(tabA), await assetHash(tabB));
        await refresh(tabA, portal, `${origin}/portal/umbraco`);
        await refresh(tabB, app);

        // Both tabs have live auth contexts: signing out of A must leave B's open tab and cookies valid.
        const peer = await context.newPage();
        await peer.goto(`${origin}/portal/umbraco`);
        await peer.locator("umb-backoffice-header-logo").waitFor();
        await Promise.all([
            tabA.waitForURL(`${origin}/portal/umbraco/logout`), peer.waitForURL(`${origin}/portal/umbraco/logout`),
            tabA.evaluate(async () => {
                const { UMB_AUTH_CONTEXT } = await import("@umbraco-cms/backoffice/auth");
                await (await document.querySelector("umb-app").getContext(UMB_AUTH_CONTEXT)).signOut();
            }),
        ]);
        await tabB.locator("umb-backoffice-header-logo").waitFor();
        await currentUser(tabB, app);
        assert.deepEqual(await refreshNames(), names.filter(name => name !== portalCookie));
        const afterLogout = await context.cookies();
        assert.ok(!afterLogout.some(cookie => cookie.name.startsWith("UMB_UCONTEXT") && cookie.name.includes(portalCookie.slice(-12))),
            "Logging out clears the installation's login cookie even when the initial bookmark used different casing");
        assert.ok(legacy.every(name => afterLogout.some(cookie => cookie.name === name && cookie.value === "other-installation")));
        await refresh(tabB, app);

        await stopSite(portal);
        await startSite(portal);
        await login(tabA, portal, origin, password);
        assert.deepEqual(await refreshNames(), names, "An installation keeps its cookie names after restart");
        await currentUser(tabB, app);
    } catch (error) {
        if (diagnostics.length) error.message += "\n" + diagnostics.slice(-15).join("\n");
        throw error;
    } finally {
        await browser?.close();
        if (proxy) { proxy.closeAllConnections(); await new Promise(done => proxy.close(done)); }
        await Promise.all(sites.map(stopSite));
        await rm(root, { recursive: true, force: true, maxRetries: 3 });
    }
});
