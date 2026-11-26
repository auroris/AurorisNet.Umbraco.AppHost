// Umbraco loads this public entry point on both its provider chooser and its login page.

const stylesheet = new URL('./login-screen.css', import.meta.url).href;
export function onInit(_host, registry) {
    const image = registry?.getByAlias('UmbracoAppHost.LoginScreen')?.meta?.backdropImage;
    if (image) {
        const url = new URL(image, new URL('../../umbraco/', import.meta.url));
        document.documentElement.style.setProperty('--apphost-login-backdrop', `url("${url.href}")`);
    }
}

if (!document.querySelector(`link[href="${stylesheet}"]`)) {
    const link = document.createElement('link');
    link.rel = 'stylesheet';
    link.href = stylesheet;
    document.head.appendChild(link);
}

// Both Umbraco views render #content-container inside a shadow root. Adopt the same
// stylesheet in each root so switching views preserves the panel and photograph widths.
const shadow = new CSSStyleSheet();
shadow.replaceSync(`
    /* The standalone login page (umb-auth) reads --umb-login-background itself; the sign-in view
       inside the backoffice app (umb-auth-view) paints its own grey, so the host is set here too. */
    :host {
        background: var(--umb-login-background, #fff);
    }

    #layout, #main, #main-no-image {
        padding: clamp(12px, 3vw, 32px);
        gap: clamp(12px, 3vw, 32px);
    }
    #graphic, #image-container {
        flex: 1 1 0%;
        min-width: 0;
    }
    #content-container {
        align-self: center;
        box-sizing: border-box;
        flex: 0 0 auto;
        width: min(360px, 100%);
        height: auto;
        max-height: 100%;
        margin: 0;
        padding: 48px 32px;
        background: #fff;
        border-radius: 24px;
        box-shadow: 0 12px 40px rgba(6, 28, 74, 0.22);
    }
`);

const passwordStyle = new CSSStyleSheet();
passwordStyle.replaceSync(`
    #greeting { display: none; }
    ::slotted(uui-button[slot="subheadline"]) {
        align-self: flex-start;
        color: var(--uui-color-interactive);
        margin-bottom: var(--uui-size-space-4);
    }
`);

function addPasswordNavigation(page) {
    if (page.shadowRoot.adoptedStyleSheets.includes(passwordStyle)) return;
    page.shadowRoot.adoptedStyleSheets = [...page.shadowRoot.adoptedStyleSheets, passwordStyle];
    const back = document.createElement('uui-button');
    back.slot = 'subheadline';
    back.type = 'button';
    back.look = 'secondary';
    back.label = page.localize.termOrDefault('general_back', 'Back');
    // The logged-out backoffice shows the provider chooser even when SSO normally redirects automatically.
    back.addEventListener('click', () => location.assign(location.pathname.replace(/\/login\/?$/i, '/logout')));
    page.append(back);
}

function stylePanel(panel) {
    const root = panel.shadowRoot;
    if (!root.adoptedStyleSheets.includes(shadow)) root.adoptedStyleSheets = [...root.adoptedStyleSheets, shadow];
}

// A session timeout can create a chooser long after startup. Lit initializers also cover a new
// password form after a reset or MFA flow. Visit any instance already mounted when the plugin loads.
async function onRendered(tag, apply) {
    const component = await customElements.whenDefined(tag);
    const afterRender = element => element.updateComplete.then(() => apply(element));
    component.addInitializer(afterRender);
    function visit(root) {
        for (const element of root.querySelectorAll('*')) {
            if (element.localName === tag) afterRender(element);
            if (element.shadowRoot) visit(element.shadowRoot);
        }
    }
    visit(document);
}

onRendered('umb-auth-view', stylePanel);
onRendered('umb-auth-layout', stylePanel);
onRendered('umb-login-page', addPasswordNavigation);
