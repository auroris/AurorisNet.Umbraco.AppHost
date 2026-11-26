import { currentInstall } from './migrations.mjs';
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFile, readdir } from 'node:fs/promises';
import { resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { JSDOM } from 'jsdom';
import { renderAppBlock, fieldTypes, values, listItems } from '../../../AurorisNet.Umbraco.AppBlocks/src/AppBlocks/wwwroot/App_Plugins/ApplicationBlocks/preview-model.js';
import { appBlockTypes } from '../../../AurorisNet.Umbraco.AppBlocks/src/AppBlocks/wwwroot/App_Plugins/ApplicationBlocks/block-types.js';

const root = fileURLToPath(new URL('../../', import.meta.url));
// These pure layouts already have the required preview: Umbraco's own card and editable areas.
const nativeLayouts = new Set(['appFullWidth', 'appThreeColumns', 'appFormFullWidth', 'appFormThreeColumns', 'blockTwoColumn1', 'blockTwoColumn2', 'blockTwoColumn3']);
// Form rules and their conditions use labels in Umbraco's native Block List editor.
const ruleLabels = new Map([
    ['appFieldRule', '{=action} {=field}'],
    ['appFieldCondition', '{=field} {=comparison} {=otherField} {=literal}'],
]);
const blocksInstall = currentInstall(resolve(root, '../AurorisNet.Umbraco.AppBlocks'));
const hostInstall = currentInstall(root);
const owners = [
    ['../AurorisNet.Umbraco.AppBlocks/src/AppBlocks', resolve(blocksInstall, 'AppBlocks/Schema')], ['../AurorisNet.Umbraco.AppBlocks/src/AppBlocks.InteractiveReports', resolve(blocksInstall, 'AppBlocks.InteractiveReports/Schema')],
    ['../AurorisNet.Umbraco.AppBlocks/src/AppBlocks.Documents', resolve(blocksInstall, 'AppBlocks.Documents/Schema')], ['src/UmbracoAppHost', resolve(hostInstall, 'uSync'), resolve(hostInstall, 'Schema')],
];
async function files(directory, suffix) {
    let entries;
    try { entries = await readdir(directory, { withFileTypes: true }); } catch (error) { if (error.code === 'ENOENT') return []; throw error; }
    return (await Promise.all(entries.map(entry => entry.isDirectory() ? files(resolve(directory, entry.name), suffix) : entry.name.endsWith(suffix) ? [resolve(directory, entry.name)] : []))).flat();
}
test('every owned element offered by the block editors has a preview in its owning library', async () => {
    // Conventional host partials now render through Umbraco.Community.BlockPreview.
    const razorAliases = new Set((await files(resolve(root, 'src/UmbracoAppHost/Views/Partials/blockgrid/Components'), '.cshtml'))
        .map(path => path.split(/[\\/]/).at(-1).replace(/\.cshtml$/, '')));
    const allTypes = new Map();
    const configs = [];
    const previewAliases = new Set();
    const previewScopes = new Set();
    for (const [project, ...schemas] of owners) {
        const manifests = await files(resolve(root, project, 'wwwroot/App_Plugins'), 'umbraco-package.json');
        const aliases = new Set();
        for (const path of manifests) {
            const manifest = JSON.parse(await readFile(path, 'utf8'));
            for (const ext of manifest.extensions ?? []) if (ext.type === 'blockEditorCustomView') {
                aliases.add(ext.forContentTypeAlias);
                const scope = `${ext.forContentTypeAlias} in ${ext.forBlockEditor ?? 'every block editor'}`;
                assert(!previewScopes.has(scope), `Duplicate preview: ${scope}`);
                previewScopes.add(scope);
                previewAliases.add(ext.forContentTypeAlias);
                await readFile(resolve(root, project, 'wwwroot', ext.element.replace(/^\//, '')));
            }
        }
        for (const path of (await Promise.all(schemas.map(schema => files(resolve(root, project, schema), '.config')))).flat()) {
            const doc = new JSDOM(await readFile(path, 'utf8'), { contentType: 'text/xml' }).window.document;
            const type = doc.querySelector('ContentType');
            if (type) {
                const alias = type.getAttribute('Alias');
                allTypes.set(type.getAttribute('Key'), alias);
                const nestedType = appBlockTypes.find(item => item.alias === alias);
                if (nestedType) assert.equal(nestedType.key, type.getAttribute('Key'), `Nested preview identity differs: ${alias}`);
                if (doc.querySelector('Info > IsElement')?.textContent === 'true' && !['appColumnsSettings', 'appRegionSettings'].includes(alias)) {
                    assert(aliases.has(alias) || nativeLayouts.has(alias) || ruleLabels.has(alias) || razorAliases.has(alias), `${project} does not preview ${alias}`);
                }
            }
            const config = doc.querySelector('DataType > Config')?.textContent;
            if (config) configs.push({ editor: doc.querySelector('DataType > Info > EditorAlias').textContent, blocks: JSON.parse(config).blocks ?? [] });
        }
    }
    const checkedRules = new Set();
    for (const { editor, blocks } of configs) for (const block of blocks) {
        const alias = allTypes.get(block.contentElementTypeKey);
        assert(alias, `Unknown configured type ${block.contentElementTypeKey}`);
        if (ruleLabels.has(alias)) {
            assert.equal(editor, 'Umbraco.BlockList', alias);
            assert.equal(block.label, ruleLabels.get(alias), alias);
            checkedRules.add(alias);
        } else {
            assert(previewAliases.has(alias) || nativeLayouts.has(alias) || razorAliases.has(alias), `Configured block has no preview: ${alias}`);
        }
        if (nativeLayouts.has(alias)) assert(block.areas?.length > 0, `Native layout has no configured areas: ${alias}`);
    }
    assert.deepEqual(checkedRules, new Set(ruleLabels.keys()));
});

test('AppBlocks renders its custom previews, including nested forms', () => {
    for (const { alias } of appBlockTypes) {
        if (ruleLabels.has(alias)) continue;
        const result = renderAppBlock(alias, {}, {}, () => '<p>Nested form content</p>');
        assert(!result.includes('Preview unavailable'), alias);
        if (!nativeLayouts.has(alias) && !['appColumns', 'appFormColumns', 'appRegion', 'appFormRegion', 'appFieldset'].includes(alias)) assert(result.length > 0, alias);
    }
    assert.match(renderAppBlock('appFormsContainer', {}, {}, () => '<p>Nested form content</p>'), /Nested form content/);
});

test('field previews encode authored values and have no working controls', () => {
    for (const alias of Object.keys(fieldTypes)) {
        const markup = renderAppBlock(alias, { label: '<script>bad()</script>', defaultValue: '" autofocus onfocus="bad()', placeholder: '<unsafe>', helpText: '<img src=x>', required: true });
        const doc = new JSDOM(markup).window.document;
        assert.equal(doc.querySelector('script,img,[autofocus],[onfocus]'), null, alias);
        for (const control of doc.querySelectorAll('input,select,textarea,button')) assert(control.disabled, alias);
        assert(doc.querySelector('label').textContent.endsWith(' *'));
    }
});

test('nested lists follow layout order and select the active culture without leaking other variants', () => {
    const data = { layout: { 'Umbraco.BlockList': [{ contentKey: 'b' }, { contentKey: 'a' }] }, contentData: [
        { key: 'a', values: [{ alias: 'title', culture: null, value: 'A' }] },
        { key: 'b', values: [{ alias: 'title', culture: 'en-US', value: 'English' }, { alias: 'title', culture: 'fr-FR', value: 'Français' }] },
    ] };
    assert.deepEqual(listItems(data, 'fr-FR').map(item => item.title), ['Français', 'A']);
    assert.deepEqual(values(data.contentData[1]), {});
});

test('code and interactive previews do not execute page code or submit forms', () => {
    for (const alias of ['appScript', 'appCss', 'appForm', 'appActions', 'appAttachments', 'appTabular', 'appSchedule', 'appAntiSpam']) {
        const doc = new JSDOM(renderAppBlock(alias, { script: '<script>bad()</script>', css: '</style><script>bad()</script>' })).window.document;
        assert.equal(doc.querySelector('script,form,iframe'), null, alias);
        for (const button of doc.querySelectorAll('button')) assert(button.disabled, alias);
    }
});

test('source previews count logical lines and leave source in the block editor', () => {
    for (const [alias, property, name] of [['appCss', 'css', 'CSS'], ['appScript', 'script', 'JavaScript']]) {
        for (const [source, count] of [['', '0 lines'], ['secret();\n', '1 line'], ['one\r\n\r\ntwo', '3 lines']]) {
            const doc = new JSDOM(renderAppBlock(alias, { [property]: source })).window.document;
            assert.equal(doc.body.textContent, `${name} · ${count}`);
            assert.equal(doc.querySelector('pre,code,script,style'), null);
        }
    }
});
