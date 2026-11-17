import { request, withQuery } from "../ApplicationBlocks/data-api.js";

const selectionMessage = "apphost:person-picked";

// Each instance binds only its configured fields and accepts messages only from its own popup.
export function mountPeoplePicker(box) {
    const form = box?.closest("[data-app-form]");
    if (!form || box.dataset.mounted) return;
    const settings = JSON.parse(box.dataset.settings), endpoint = new URL(box.dataset.endpoint, location.origin);
    if (endpoint.origin !== location.origin) return;
    const field = key => form.querySelector(`[data-field="${CSS.escape(key)}"]`);
    const name = field(settings.nameField), sid = field(settings.identityField);
    const mappings = Object.entries(settings.fieldMappings || {}), fallbacks = settings.fallbackFields || {};
    if (!name || !sid || mappings.some(([, key]) => !field(key))) return;
    box.dataset.mounted = "true";
    const find = box.querySelector("[data-find]"), manual = box.querySelector("[data-manual]"), status = box.querySelector("[data-status]");
    const normalize = (key, value) => (settings.compactUpperFields || []).includes(key) ? (value || "").replace(/\s/g, "").toUpperCase() : value || "";
    const slot = box.closest('[data-content-element-type-alias="appPeoplePicker"]');
    const row = document.createElement("div"); row.className = "app-person-control";
    name.before(row); row.append(name, box);
    if (slot && !slot.contains(name)) slot.hidden = true;
    let popup, pending, generation = 0;
    const editable = () => !name.matches(":disabled") && !form.classList.contains("app-people-readonly");
    const changed = input => {
        input.dispatchEvent(new Event("input", { bubbles: true }));
        input.dispatchEvent(new Event("change", { bubbles: true }));
    };
    function lock() {
        name.readOnly = !!sid.value || !settings.allowManualEntry;
        for (const [, key] of mappings) {
            const mirror = fallbacks[key];
            field(key).readOnly = !!sid.value ? !mirror || !!field(mirror)?.value : !settings.allowManualEntry;
        }
        manual.hidden = !settings.allowManualEntry || !sid.value;
        find.disabled = manual.disabled = !editable();
    }
    function close() { ++generation; pending?.abort(); pending = null; if (popup && !popup.closed) popup.close(); popup = null; }
    function loaded() { close(); lock(); }
    form.addEventListener("app-form:loaded", loaded);
    form.addEventListener("app-form:saved", loaded);
    window.addEventListener("pagehide", close);
    window.addEventListener("message", async event => {
        if (event.origin !== location.origin || !popup || event.source !== popup || event.data?.type !== selectionMessage) return;
        if (!editable()) { close(); return; }
        const selectedSid = event.data.person?.sid;
        if (typeof selectedSid !== "string" || !selectedSid) return;
        const current = ++generation; pending?.abort(); pending = new AbortController();
        status.textContent = "Checking the directory selection…";
        try {
            // The popup's label and attributes are never used as canonical values.
            const selected = await request(withQuery(endpoint, { sid: selectedSid }), { signal: pending.signal });
            if (current !== generation || !editable() || !popup || popup.closed) return;
            const person = selected.person;
            if (!person || person.sid !== selectedSid || typeof person.displayName !== "string") throw new Error("Invalid selection");
            const attributes = Object.fromEntries(Object.entries(person.attributes || {}).map(([key, value]) => [key.toLowerCase(), value]));
            sid.value = person.sid; name.value = person.displayName;
            for (const [attribute, key] of mappings) {
                const value = attribute.toLowerCase() === "displayname" ? person.displayName : attributes[attribute.toLowerCase()];
                field(key).value = normalize(key, value);
                if (fallbacks[key] && field(fallbacks[key])) field(fallbacks[key]).value = field(key).value;
            }
            // Dispatch only after every field is updated, so observers see one complete identity.
            for (const input of [sid, name, ...mappings.map(([, key]) => field(key))]) changed(input);
            lock(); status.classList.remove("app-picker-error");
            status.textContent = `${person.displayName} selected from the directory.${selected.cached ? " Using a recent cached result." : ""}`;
            const missing = mappings.map(([, key]) => field(key)).find(input => !input.readOnly);
            close(); window.focus(); (missing || find).focus();
        } catch (error) {
            if (current !== generation || error.name === "AbortError") return;
            status.textContent = "This selection could not be validated. Try again; the existing details have been kept.";
            status.classList.add("app-picker-error");
        }
    });
    for (const [, key] of mappings) field(key).addEventListener("change", () => {
        if (!field(key).readOnly) field(key).value = normalize(key, field(key).value);
    });
    manual.addEventListener("click", () => {
        if (!editable() || !settings.allowManualEntry) return;
        close(); sid.value = "";
        for (const mirror of Object.values(fallbacks)) if (field(mirror)) field(mirror).value = "";
        changed(sid); lock(); status.textContent = "Directory link cleared. Enter the person's details.";
        status.classList.remove("app-picker-error"); name.focus();
    });
    find.addEventListener("click", () => {
        if (!editable()) return;
        if (popup && !popup.closed) { popup.focus(); return; }
        const address = new URL("people-picker.html", import.meta.url);
        address.searchParams.set("endpoint", endpoint.href);
        address.searchParams.set("minTermLength", settings.minTermLength);
        address.searchParams.set("maxResults", settings.maxResults);
        popup = window.open(address.href, "", "popup,width=680,height=620,resizable=yes,scrollbars=yes");
        status.textContent = popup ? "" : "Allow popups for this site, then choose Find person again.";
        status.classList.toggle("app-picker-error", !popup);
    });
    lock();
}

export function mountPeopleSearch() {
    const query = document.querySelector("input"), results = document.querySelector("[data-results]");
    const message = document.querySelector("[data-search-status]");
    const parameters = new URL(location.href).searchParams;
    let endpoint;
    try { endpoint = new URL(parameters.get("endpoint"), location.origin); if (endpoint.origin !== location.origin || !parameters.get("endpoint")) throw new Error(); }
    catch { message.textContent = "Open this search from a people picker."; return; }
    const minTermLength = Math.min(32, Math.max(2, Number(parameters.get("minTermLength")) || 2));
    const maxResults = Math.min(50, Math.max(1, Number(parameters.get("maxResults")) || 20));
    document.getElementById("search-help").textContent = 'Enter at least '+minTermLength+' characters per word.';
    let timer, controller, generation = 0, composing = false;
    function stop() {
        clearTimeout(timer); controller?.abort(); controller = null; ++generation;
        results.removeAttribute("aria-busy");
    }
    window.addEventListener("pagehide", stop, { once: true });
    const dismiss = () => { window.opener?.focus(); window.close(); };
    document.querySelector("[data-cancel]").addEventListener("click", dismiss);
    document.addEventListener("keydown", event => {
        if (event.key === "Escape") { event.preventDefault(); dismiss(); }
    });
    async function search() {
        stop(); results.replaceChildren();
        const text = query.value.trim();
        if (text.length < minTermLength) { message.textContent = `Enter at least ${minTermLength} characters to search.`; return; }
        const current = generation;
        controller = new AbortController();
        message.textContent = "Searching…"; results.setAttribute("aria-busy", "true");
        try {
            const response = await request(withQuery(endpoint, { s: text }), { signal: controller.signal });
            if (current !== generation) return;
            const people = response.items;
            for (const person of people) {
                const item = document.createElement("li"), choose = document.createElement("button");
                choose.type = "button"; choose.className = "app-directory-person";
                const label = document.createElement("span"); label.textContent = person.displayName; choose.append(label);
                if (person.email) { const email = document.createElement("small"); email.textContent = person.email; choose.append(email); }
                choose.addEventListener("click", () => {
                    if (!window.opener || window.opener.closed) { message.textContent = "The request form has closed. Close this window and open the form again."; return; }
                    window.opener.postMessage({ type: selectionMessage, person }, location.origin);
                });
                item.append(choose); results.append(item);
            }
            message.textContent = people.length === 0 ? "No matching people. Try another name, or cancel and enter the person manually."
                : people.length === maxResults ? `Showing up to ${maxResults} people. Refine the name if needed, then select a person.`
                : `${people.length} ${people.length === 1 ? "person found" : "people found"}. Select a person.`;
        } catch (error) {
            if (current !== generation || error.name === "AbortError") return;
            message.textContent = "The directory search could not be completed. Try again.";
        } finally { if (current === generation) results.removeAttribute("aria-busy"); }
    }
    const changed = () => {
        stop(); results.replaceChildren();
        if (composing) return;
        message.textContent = query.value.trim().length < minTermLength ? `Enter at least ${minTermLength} characters to search.` : "Searching…";
        if (query.value.trim().length >= minTermLength) timer = setTimeout(search, 300);
    };
    query.addEventListener("input", changed);
    query.addEventListener("compositionstart", () => { composing = true; stop(); });
    query.addEventListener("compositionend", () => { composing = false; changed(); });
    document.querySelector("[data-search-form]").addEventListener("submit", event => { event.preventDefault(); if (!composing) search(); });
    query.addEventListener("keydown", event => {
        if (event.key === "ArrowDown" && results.firstElementChild) { event.preventDefault(); results.querySelector("button").focus(); }
    });
    results.addEventListener("keydown", event => {
        const buttons = [...results.querySelectorAll("button")], index = buttons.indexOf(document.activeElement);
        if (index < 0) return;
        const next = event.key === "ArrowDown" ? Math.min(index + 1, buttons.length - 1) : event.key === "ArrowUp" ? index - 1
            : event.key === "Home" ? 0 : event.key === "End" ? buttons.length - 1 : null;
        if (next !== null) { event.preventDefault(); (next < 0 ? query : buttons[next]).focus(); }
    });
    query.focus();
}
